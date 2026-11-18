using Memoria.Online.Session;
using Memoria.Prime;
using SimpleJSON;
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using UnityEngine;

namespace Memoria.Online.Party
{
    /// <summary>
    /// Step 2 / M4: host-authoritative party state (docs/online/step2-m4-party-state.md).
    ///
    ///  - Guest: menus work as normal (optimistic). When its party state differs from what it expects (the host's
    ///    last update + its own unanswered requests), it sends a Request: the differences (<see cref="PartyState.Diff"/>).
    ///  - Host: applies requests in order of arrival, after checking them (<see cref="Validate"/>), and sends every
    ///    guest a StateUpdate: the whole party state + the answer to each guest's last request. Its own changes (its
    ///    menus, shops, chests, story) go out the same way.
    ///  - Guest on an update: the host's state, with its unanswered requests re-applied on top. A rejected request
    ///    simply disappears: the guest's numbers become the host's.
    ///  - Blocked on guests: saving, arranging items, discarding cards, changing the party order (rows: own character
    ///    only); a short message says why.
    /// Messages ride on BlobTransfer (chunked, reliable), tagged with <see cref="MagicRequest"/> / <see cref="MagicUpdate"/>.
    /// </summary>
    internal sealed class PartySync
    {
        private static readonly Byte[] MagicRequest = Encoding.ASCII.GetBytes("PTR1");
        private static readonly Byte[] MagicUpdate = Encoding.ASCII.GetBytes("PTU1");
        private const Double MinUpdateIntervalMs = 100;

        /// <summary>Keys of a character record a player may change on their own character.</summary>
        private static readonly HashSet<String> OwnKeys = new HashSet<String> { "equip", "sa", "sa_extended", "cur", "status" };
        /// <summary>Keys recomputed from the others when the state is applied (FF9Play_Update): changes are ignored.</summary>
        private static readonly HashSet<String> DerivedKeys = new HashSet<String> { "max", "elem", "defence", "basis", "web_bone", "maxHpLimit", "maxMpLimit", "maxDamageLimit", "maxMpDamageLimit", "trance" };

        public static PartySync Instance { get; private set; }

        private readonly BlobTransfer _blobs;
        private readonly Func<Boolean> _isHost;
        private readonly Func<UInt64> _hostId;
        private readonly Func<IList<UInt64>> _peers;
        private readonly Func<UInt64, Int32> _characterOf;
        private readonly Func<Int32> _myCharacter;

        // Host
        private Int32 _version;
        private UInt32 _hostFingerprint;
        private Boolean _hostBaselineSet;
        private Double _lastUpdateMs = Double.MinValue;
        private Boolean _updateDue;
        private readonly Dictionary<UInt64, KeyValuePair<Int32, String>> _lastAnswer = new Dictionary<UInt64, KeyValuePair<Int32, String>>(); // peer -> (seq, "" accepted / reason)

        // Guest
        private JSONClass _expected;
        private UInt32 _expectedFingerprint;
        private Int32 _nextSeq = 1;
        private readonly List<KeyValuePair<Int32, JSONClass>> _pending = new List<KeyValuePair<Int32, JSONClass>>();
        private JSONClass _deferredUpdate;
        private Int32 _lastVersion;

        public Boolean Active { get; private set; }
        public Int32 Requests { get; private set; }
        public Int32 Rejected { get; private set; }
        public Int32 UpdatesSent { get; private set; }
        public Int32 UpdatesApplied { get; private set; }

        private static String _toast;
        private static Single _toastUntil;

        public PartySync(BlobTransfer blobs, Func<Boolean> isHost, Func<UInt64> hostId, Func<IList<UInt64>> peers, Func<UInt64, Int32> characterOf, Func<Int32> myCharacter)
        {
            _blobs = blobs;
            _isHost = isHost;
            _hostId = hostId;
            _peers = peers;
            _characterOf = characterOf;
            _myCharacter = myCharacter;
            Instance = this;
        }

        public static Boolean IsPartyBlob(Byte[] blob) => StartsWith(blob, MagicRequest) || StartsWith(blob, MagicUpdate);

        private static Boolean StartsWith(Byte[] blob, Byte[] magic)
        {
            if (blob == null || blob.Length < magic.Length)
                return false;
            for (Int32 i = 0; i < magic.Length; i++)
                if (blob[i] != magic[i])
                    return false;
            return true;
        }

        private static Byte[] Pack(Byte[] magic, JSONNode body)
        {
            Byte[] payload = GameSnapshot.ToBytes(body);
            Byte[] blob = new Byte[magic.Length + payload.Length];
            Buffer.BlockCopy(magic, 0, blob, 0, magic.Length);
            Buffer.BlockCopy(payload, 0, blob, magic.Length, payload.Length);
            return blob;
        }

        private static JSONClass Unpack(Byte[] blob, Int32 magicLength)
        {
            Byte[] payload = new Byte[blob.Length - magicLength];
            Buffer.BlockCopy(blob, magicLength, payload, 0, payload.Length);
            return GameSnapshot.FromBytes(payload);
        }

        // ---- Per frame -----------------------------------------------------------------------------------------

        /// <summary>Called every frame by OnlineSession while a session is playing.</summary>
        public void Update(Boolean playing, Double nowMs)
        {
            try
            {
                if (!playing)
                {
                    if (Active)
                        Reset("the session isn't playing");
                    return;
                }
                if (!CanTouchPartyNow())
                    return;
                if (!Active)
                {
                    Active = true;
                    Log.Message($"[Online] Party: sync on ({(_isHost() ? "host: the authority" : "guest")})");
                }
                if (_isHost())
                    HostUpdate(nowMs);
                else
                    GuestUpdate();
            }
            catch (Exception err)
            {
                Log.Error(err, "[Online] Party: update failed");
            }
        }

        /// <summary>A whole-game load (M1 snapshot, resync) replaces everything: start over from it.</summary>
        public void OnGameLoaded()
        {
            _expected = null;
            _pending.Clear();
            _deferredUpdate = null;
            _hostBaselineSet = false;
        }

        private void Reset(String reason)
        {
            Active = false;
            OnGameLoaded();
            _lastAnswer.Clear();
            Log.Message($"[Online] Party: sync off ({reason})");
        }

        /// <summary>On a field or the world map, not loading, not in a battle (battles are M6).</summary>
        private static Boolean CanTouchPartyNow()
        {
            EventEngine engine = PersistenSingleton<EventEngine>.Instance;
            UIManager ui = PersistenSingleton<UIManager>.Instance;
            if (engine == null || ui == null || FF9StateSystem.Common?.FF9?.party == null)
                return false;
            if (engine.gMode != 1 && engine.gMode != 3)
                return false;
            switch (ui.State)
            {
                case UIManager.UIState.Initial:
                case UIManager.UIState.Title:
                case UIManager.UIState.BattleHUD:
                case UIManager.UIState.BattleResult:
                case UIManager.UIState.GameOver:
                case UIManager.UIState.Serialize:
                    return false;
            }
            return true;
        }

        // ---- Host ----------------------------------------------------------------------------------------------

        private void HostUpdate(Double nowMs)
        {
            UInt32 fingerprint = PartyState.Fingerprint();
            if (!_hostBaselineSet || fingerprint != _hostFingerprint)
            {
                _hostFingerprint = fingerprint;
                _hostBaselineSet = true;
                _updateDue = true; // its own menus, shops, chests, story... (the first one: the starting state)
            }
            if (_updateDue && nowMs - _lastUpdateMs >= MinUpdateIntervalMs && _peers().Count > 0)
            {
                _updateDue = false;
                _lastUpdateMs = nowMs;
                Broadcast();
            }
        }

        private void Broadcast()
        {
            JSONClass state = PartyState.Capture();
            _version++;
            foreach (UInt64 peer in _peers())
            {
                JSONClass update = new JSONClass();
                update.Add("version", new JSONData(_version));
                update.Add("state", state);
                if (_lastAnswer.TryGetValue(peer, out KeyValuePair<Int32, String> answer))
                {
                    update.Add("ack", new JSONData(answer.Key));
                    update.Add("reason", new JSONData(answer.Value));
                }
                _blobs.Send(peer, Pack(MagicUpdate, update));
            }
            UpdatesSent++;
        }

        private void OnRequest(UInt64 peer, JSONClass request)
        {
            Requests++;
            Int32 seq = request["seq"].AsInt;
            Int32 character = _characterOf(peer);
            JSONClass state = PartyState.Capture();
            String reason = Validate(request, character, state);
            if (reason == null)
            {
                ApplyChanges(state, request, character);
                PartyState.Apply(state);
                _hostFingerprint = PartyState.Fingerprint();
                Log.Message($"[Online] Party: request {seq} from {SteamNative.Tag(peer)} ({Describe(request)}) applied");
            }
            else
            {
                Rejected++;
                Log.Message($"[Online] Party: request {seq} from {SteamNative.Tag(peer)} ({Describe(request)}) rejected: {reason}");
            }
            _lastAnswer[peer] = new KeyValuePair<Int32, String>(seq, reason ?? String.Empty);
            _updateDue = false;
            Broadcast(); // at once: the guest's menu waits on it
        }

        /// <summary>Null if the request may be applied to <paramref name="state"/> (the host's state now), else why not.</summary>
        private static String Validate(JSONClass request, Int32 character, JSONClass state)
        {
            JSONArray other = request["other"].AsArray;
            if (other != null && other.Count > 0)
                return $"only the host can change: {Join(other)}";
            if (request["gil"] != null && request["gil"].AsLong != 0)
                return "only the host can change gil";

            Dictionary<Int32, JSONNode> players = PartyState.PlayersById(state[PartyState.Common]);
            Dictionary<Int32, Int32> freedByUnequip = new Dictionary<Int32, Int32>();
            JSONArray changes = request["players"].AsArray;
            if (changes != null)
            {
                foreach (JSONNode change in changes)
                {
                    Int32 id = change["id"].AsInt;
                    Boolean own = id == character;
                    if (!players.ContainsKey(id))
                        return $"character {id} isn't in the host's party";
                    if (change["row"] != null && !own)
                        return "only a character's player can change its row";
                    foreach (KeyValuePair<String, JSONNode> key in (JSONClass)change["keys"])
                    {
                        if (DerivedKeys.Contains(key.Key))
                            continue;
                        if (own ? OwnKeys.Contains(key.Key) : (key.Key == "cur" || key.Key == "status"))
                            continue;
                        return own ? $"'{key.Key}' can't be changed from a menu" : "only a character's player can change its equipment or abilities";
                    }
                    // Unequipping puts items back in the bag
                    JSONNode newEquip = change["keys"]["equip"];
                    if (own && newEquip != null && newEquip.AsArray != null)
                    {
                        Dictionary<Int32, Int32> was = Counts(players[id]["equip"]), now = Counts(newEquip);
                        foreach (KeyValuePair<Int32, Int32> kv in was)
                        {
                            now.TryGetValue(kv.Key, out Int32 n);
                            if (kv.Value > n)
                                freedByUnequip[kv.Key] = (freedByUnequip.TryGetValue(kv.Key, out Int32 f) ? f : 0) + kv.Value - n;
                        }
                    }
                }
            }

            JSONArray items = request["items"].AsArray;
            if (items != null)
            {
                Dictionary<Int32, Int32> have = PartyState.ItemCounts(state[PartyState.Common]);
                foreach (JSONNode item in items)
                {
                    Int32 id = item["id"].AsInt, d = item["d"].AsInt;
                    have.TryGetValue(id, out Int32 count);
                    if (d < 0 && count + d < 0)
                        return $"not enough of item {id} (the host has {count}, {-d} needed)";
                    if (d > 0 && (!freedByUnequip.TryGetValue(id, out Int32 freed) || d > freed))
                        return $"item {id} can't be gained from a menu";
                }
            }
            return null;
        }

        private static Dictionary<Int32, Int32> Counts(JSONNode array)
        {
            Dictionary<Int32, Int32> result = new Dictionary<Int32, Int32>();
            if (array?.AsArray == null)
                return result;
            foreach (JSONNode n in array.AsArray)
            {
                Int32 id = n.AsInt;
                result[id] = (result.TryGetValue(id, out Int32 c) ? c : 0) + 1;
            }
            return result;
        }

        /// <summary>Puts a request's changes into a state (validated on the host; re-applied as-is on a guest).</summary>
        private static void ApplyChanges(JSONClass state, JSONClass request, Int32 character)
        {
            Dictionary<Int32, JSONNode> players = PartyState.PlayersById(state[PartyState.Common]);
            JSONArray changes = request["players"].AsArray;
            if (changes != null)
            {
                foreach (JSONNode change in changes)
                {
                    if (!players.TryGetValue(change["id"].AsInt, out JSONNode player))
                        continue;
                    Boolean own = change["id"].AsInt == character;
                    foreach (KeyValuePair<String, JSONNode> key in (JSONClass)change["keys"])
                    {
                        if (DerivedKeys.Contains(key.Key))
                            continue;
                        if (own && OwnKeys.Contains(key.Key))
                        {
                            player[key.Key] = PartyState.Clone(key.Value);
                        }
                        else if (key.Key == "cur")
                        {
                            player["cur"]["hp"] = PartyState.Clone(key.Value["hp"]);
                            player["cur"]["mp"] = PartyState.Clone(key.Value["mp"]);
                        }
                        else if (key.Key == "status")
                        {
                            player["status"] = PartyState.Clone(key.Value);
                        }
                    }
                    if (change["row"] != null && own)
                        player["info"]["row"] = new JSONData(change["row"].AsInt);
                }
            }
            JSONArray items = request["items"].AsArray;
            if (items != null)
                foreach (JSONNode item in items)
                    PartyState.AddItem(state, item["id"].AsInt, item["d"].AsInt);
        }

        // ---- Guest ---------------------------------------------------------------------------------------------

        private void GuestUpdate()
        {
            if (_deferredUpdate != null)
            {
                JSONClass update = _deferredUpdate;
                _deferredUpdate = null;
                ApplyUpdate(update);
            }
            if (_expected == null)
            {
                _expected = PartyState.Capture();
                _expectedFingerprint = PartyState.Fingerprint();
                return;
            }
            UInt32 fingerprint = PartyState.Fingerprint();
            if (fingerprint == _expectedFingerprint)
                return;
            JSONClass now = PartyState.Capture();
            JSONClass changes = PartyState.Diff(_expected, now);
            _expected = now;
            _expectedFingerprint = fingerprint;
            if (changes == null)
                return;
            Int32 seq = _nextSeq++;
            changes.Add("seq", new JSONData(seq));
            _pending.Add(new KeyValuePair<Int32, JSONClass>(seq, changes));
            _blobs.Send(_hostId(), Pack(MagicRequest, changes));
            Requests++;
            Log.Message($"[Online] Party: request {seq} sent to the host ({Describe(changes)})");
        }

        private void OnUpdate(JSONClass update)
        {
            Int32 version = update["version"].AsInt;
            if (version <= _lastVersion)
                return;
            _lastVersion = version;
            if (!Active || !CanTouchPartyNow() || IsMenuBusy())
            {
                _deferredUpdate = update; // the newest one wins
                return;
            }
            ApplyUpdate(update);
        }

        private void ApplyUpdate(JSONClass update)
        {
            Int32 ack = update["ack"] != null ? update["ack"].AsInt : 0;
            String reason = update["reason"] != null ? update["reason"].Value : String.Empty;
            for (Int32 i = _pending.Count - 1; i >= 0; i--)
            {
                if (_pending[i].Key > ack)
                    continue;
                if (_pending[i].Key == ack && !String.IsNullOrEmpty(reason))
                {
                    Rejected++;
                    Toast($"The host's game didn't accept that: {reason}");
                    Log.Message($"[Online] Party: request {ack} rejected by the host: {reason}");
                }
                _pending.RemoveAt(i);
            }
            JSONClass state = PartySync.CloneState(update["state"]);
            Int32 character = _myCharacter();
            foreach (KeyValuePair<Int32, JSONClass> pending in _pending)
                ApplyChanges(state, pending.Value, character); // not answered yet: stays on top (optimistic)
            PartyState.Apply(state);
            _expected = PartyState.Capture();
            _expectedFingerprint = PartyState.Fingerprint();
            UpdatesApplied++;
            RefreshOpenMenu();
        }

        private static JSONClass CloneState(JSONNode node) => PartyState.CloneObject(node);

        // ---- Messages ------------------------------------------------------------------------------------------

        /// <summary>BlobTransfer.Completed: party requests (host) and updates (guests).</summary>
        public void OnBlob(BlobTransfer.Received received)
        {
            try
            {
                if (StartsWith(received.Blob, MagicRequest))
                {
                    if (_isHost() && Active)
                        OnRequest(received.Peer, Unpack(received.Blob, MagicRequest.Length));
                }
                else if (StartsWith(received.Blob, MagicUpdate))
                {
                    if (!_isHost() && received.Peer == _hostId())
                        OnUpdate(Unpack(received.Blob, MagicUpdate.Length));
                }
            }
            catch (Exception err)
            {
                Log.Error(err, "[Online] Party: a message couldn't be handled");
            }
        }

        // ---- Menus ---------------------------------------------------------------------------------------------

        /// <summary>While a choice is being made in a menu (a target, a confirm dialog), the update waits.</summary>
        private static Boolean IsMenuBusy()
        {
            UIManager ui = PersistenSingleton<UIManager>.Instance;
            switch (ui.State)
            {
                case UIManager.UIState.Shop:
                case UIManager.UIState.Card:
                case UIManager.UIState.NameSetting:
                case UIManager.UIState.PartySetting:
                case UIManager.UIState.QuadMist:
                case UIManager.UIState.QuadMistBattle:
                case UIManager.UIState.Chocograph:
                    return true;
            }
            return false;
        }

        /// <summary>Redraws the open menu screen from the (new) game state, if it's one that shows the party.</summary>
        private static void RefreshOpenMenu()
        {
            UIManager ui = PersistenSingleton<UIManager>.Instance;
            try
            {
                switch (ui.State)
                {
                    case UIManager.UIState.Item:
                        Invoke(ui.ItemScene, "DisplayItem");
                        break;
                    case UIManager.UIState.Equip:
                        Invoke(ui.EquipScene, "DisplayEquipment", "DisplayParameter", "DisplayInventory");
                        break;
                    case UIManager.UIState.Ability:
                        Invoke(ui.AbilityScene, "DisplayAA", "DisplaySA");
                        break;
                    case UIManager.UIState.MainMenu:
                        Invoke(ui.MainMenuScene, "DisplayCharacter", "DisplayGeneralInfo");
                        break;
                }
            }
            catch (Exception err)
            {
                Log.Warning($"[Online] Party: redrawing the {ui.State} menu failed: {err.Message}");
            }
        }

        private static void Invoke(System.Object target, params String[] methods)
        {
            if (target == null)
                return;
            foreach (String name in methods)
            {
                MethodInfo method = target.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public, null, Type.EmptyTypes, null);
                if (method != null)
                    method.Invoke(target, null);
            }
        }

        // ---- Blocks on guests ([Online] hooks) -----------------------------------------------------------------

        private static Boolean IsGuest => Instance != null && Instance.Active && !Instance._isHost();

        /// <summary>[Online] hook, SaveLoadUI (choosing a file to save): guests can't save.</summary>
        public static Boolean BlockGuestSave() => Block("Only the host can save.");

        /// <summary>[Online] hook, ItemUI arrange and CardUI discard.</summary>
        public static Boolean BlockGuestAction(String message) => Block(message);

        /// <summary>[Online] hook, MainMenuUI.ToggleOrder: guests change only their own character's row, never the order.</summary>
        public static Boolean BlockOrderChange(PLAYER player, Boolean isRowToggle)
        {
            if (!IsGuest)
                return false;
            if (isRowToggle && player != null && Instance._myCharacter() == (Int32)player.info.slot_no)
                return false;
            return Block(isRowToggle ? "Only that character's player can change their row." : "Only the host can change the party order.");
        }

        private static Boolean Block(String message)
        {
            if (!IsGuest)
                return false;
            FF9Sfx.FF9SFX_Play(102);
            Toast(message);
            return true;
        }

        public static void Toast(String message)
        {
            _toast = message;
            _toastUntil = Time.realtimeSinceStartup + 4f;
        }

        /// <summary>The current message, if any (drawn by OnlineSession).</summary>
        public static String CurrentToast => _toast != null && Time.realtimeSinceStartup < _toastUntil ? _toast : null;

        // ---- Debug ---------------------------------------------------------------------------------------------

        public String Describe()
        {
            if (!Active)
                return "party sync: off";
            return _isHost()
                ? $"party sync: host, v{_version}, requests {Requests} ({Rejected} rejected), updates sent {UpdatesSent}"
                : $"party sync: guest, v{_lastVersion}, pending {_pending.Count}, requests {Requests} ({Rejected} rejected), updates applied {UpdatesApplied}{(_deferredUpdate != null ? ", 1 waiting" : "")}";
        }

        private static String Describe(JSONClass request)
        {
            List<String> parts = new List<String>();
            JSONArray players = request["players"].AsArray;
            if (players != null)
                foreach (JSONNode p in players)
                {
                    List<String> keys = new List<String>();
                    foreach (KeyValuePair<String, JSONNode> k in (JSONClass)p["keys"])
                        keys.Add(k.Key);
                    if (p["row"] != null)
                        keys.Add("row");
                    parts.Add($"character {p["id"].AsInt}: {String.Join("/", keys.ToArray())}");
                }
            JSONArray items = request["items"].AsArray;
            if (items != null)
                foreach (JSONNode i in items)
                    parts.Add($"item {i["id"].AsInt} {(i["d"].AsInt > 0 ? "+" : "")}{i["d"].AsInt}");
            if (request["gil"] != null)
                parts.Add($"gil {request["gil"].AsLong}");
            JSONArray other = request["other"].AsArray;
            if (other != null && other.Count > 0)
                parts.Add($"other: {Join(other)}");
            return String.Join(", ", parts.ToArray());
        }

        private static String Join(JSONArray array)
        {
            List<String> values = new List<String>();
            foreach (JSONNode n in array)
                values.Add(n.Value);
            return String.Join(", ", values.ToArray());
        }
    }
}
