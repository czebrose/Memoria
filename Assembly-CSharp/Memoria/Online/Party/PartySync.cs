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
    ///  - Guests change who's in the party only with the party screen (CheckPartyScreen).
    ///  - Equipment: a player changes their own character's, and anyone changes a follow character's (one nobody
    ///    plays). Nobody, the host included, changes the equipment of a character another player plays
    ///    (<see cref="MayEquip"/>; the Equip menu blocks it, <see cref="BlockEquipChange"/>).
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
        /// <summary>
        /// Keys anyone may change on a follow character (nobody plays it): its equipment. Its support abilities only
        /// along with an equipment change: taking off a gear can switch off the abilities it was teaching
        /// (ff9feqp.FF9FEqp_UpdateSA).
        /// </summary>
        private static readonly HashSet<String> FollowerKeys = new HashSet<String> { "equip", "cur", "status" };
        private static readonly HashSet<String> FollowerKeysWithEquip = new HashSet<String> { "sa", "sa_extended" };
        /// <summary>Keys recomputed from the others when the state is applied (FF9Play_Update): changes are ignored.</summary>
        private static readonly HashSet<String> DerivedKeys = new HashSet<String> { "max", "elem", "defence", "basis", "web_bone", "maxHpLimit", "maxMpLimit", "maxDamageLimit", "maxMpDamageLimit", "trance" };

        public static PartySync Instance { get; private set; }

        private readonly BlobTransfer _blobs;
        private readonly Func<Boolean> _isHost;
        private readonly Func<UInt64> _hostId;
        private readonly Func<IList<UInt64>> _peers;
        private readonly Func<UInt64, Int32> _characterOf;
        private readonly Func<Int32> _myCharacter;
        private readonly Func<Int32, Boolean> _isPlayed; // a player (the host or a guest) plays this character

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

        public PartySync(BlobTransfer blobs, Func<Boolean> isHost, Func<UInt64> hostId, Func<IList<UInt64>> peers, Func<UInt64, Int32> characterOf, Func<Int32> myCharacter, Func<Int32, Boolean> isPlayed)
        {
            _isPlayed = isPlayed;
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
            GuestFinds.Clear();
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
            if (Memoria.Online.Battle.BattleEntry.PartyAltered)
                return false; // Spike D: the party slots hold a battle party
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
            String reason = Validate(request, character, state, c => c != character && _isPlayed(c));
            if (reason == null)
            {
                ApplyChanges(state, request, character);
                ApplyState(state);
                if (request["found"] != null && request["found"].AsArray != null)
                    GuestFinds.Apply(request["found"].AsArray); // M5: a guest's chests, gifts, quest items, gil
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

        /// <summary>Null if the request may be applied to <paramref name="state"/> (the host's state now), else why not.
        /// <paramref name="playedByOther"/>: whether another player (not this request's) plays a character.</summary>
        private static String Validate(JSONClass request, Int32 character, JSONClass state, Func<Int32, Boolean> playedByOther)
        {
            JSONArray other = request["other"].AsArray;
            if (other != null && other.Count > 0)
            {
                // The one exception: the party screen's new party (a moogle's party menu, a Party() call in a field script)
                if (!IsPartyScreenRequest(request) || other.Count != 1 || other[0].Value != "slot")
                    return $"only the host can change: {Join(other)}";
                String why = CheckPartyScreen(request, state, playedByOther);
                if (why != null)
                    return why;
            }
            Boolean shop = IsShopRequest(request);
            Int64 gilDelta = request["gil"] != null ? request["gil"].AsLong : 0;
            if (gilDelta != 0 && !shop)
                return "only the host can change gil";
            if (shop)
            {
                // A guest's shop: buying, selling, synthesis. The host's gil must cover it, and selling can't bring in
                // more than the sold items are worth here.
                Int64 hostGil = state[PartyState.Common]["gil"].AsLong;
                if (hostGil + gilDelta < 0)
                    return $"not enough gil (the party has {hostGil}, {-gilDelta} needed)";
                if (gilDelta > 0 && gilDelta > SellValue(request["items"].AsArray))
                    return $"{gilDelta} gil is more than the sold items are worth";
            }

            Dictionary<Int32, JSONNode> players = PartyState.PlayersById(state[PartyState.Common]);
            Dictionary<Int32, Int32> freedByUnequip = new Dictionary<Int32, Int32>();
            JSONArray changes = request["players"].AsArray;
            if (changes != null)
            {
                foreach (JSONNode change in changes)
                {
                    Int32 id = change["id"].AsInt;
                    Boolean own = id == character;
                    Boolean follower = !own && !playedByOther(id); // nobody plays it: anyone may change its equipment
                    if (!players.ContainsKey(id))
                        return $"character {id} isn't in the host's party";
                    if (change["row"] != null && !own)
                        return "only a character's player can change its row";
                    JSONNode newEquip = change["keys"]["equip"];
                    foreach (KeyValuePair<String, JSONNode> key in (JSONClass)change["keys"])
                    {
                        if (DerivedKeys.Contains(key.Key))
                            continue;
                        if (own ? OwnKeys.Contains(key.Key) : follower ? FollowerKeys.Contains(key.Key) || newEquip != null && FollowerKeysWithEquip.Contains(key.Key) : (key.Key == "cur" || key.Key == "status"))
                            continue;
                        if (own)
                            return $"'{key.Key}' can't be changed from a menu";
                        return follower ? "only a character's player can change its abilities" : "only a character's player can change its equipment or abilities";
                    }
                    // Unequipping puts items back in the bag
                    if ((own || follower) && newEquip != null && newEquip.AsArray != null)
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
                    if (d > 0 && !shop && (!freedByUnequip.TryGetValue(id, out Int32 freed) || d > freed))
                        return $"item {id} can't be gained from a menu";
                }
            }
            return null;
        }

        /// <summary>A guest's request made while its shop screen was open (buy, sell, synthesis): it may change gil.</summary>
        private static Boolean IsShopRequest(JSONClass request) => request["shop"] != null && request["shop"].AsInt == 1;

        // ---- Guest party changes from the party screen -----------------------------------------------------------
        // A guest may change who's in the party with the game's party screen (PartySettingUI: a moogle's party menu, a
        // field script's Party()). The screen itself enforces the story's rules: it offers only the characters the story
        // has made available, and keeps the ones the script locks. The host adds the multiplayer rules below. Everything
        // else in the party list (story changes, the order) stays the host's.

        private static Single _partyScreenAt = -100f;

        /// <summary>[Online] hook, PartySettingUI: the player confirmed a new party (just before it's written).</summary>
        public static void OnPartyScreenConfirmed() => _partyScreenAt = Time.realtimeSinceStartup;

        private static Boolean IsPartyScreenRequest(JSONClass request) => request["partyScreen"] != null && request["partyScreen"].AsInt == 1 && request["slot"]?.AsArray != null;

        /// <summary>Guest: a party-list change made with the party screen (still open, or confirmed a moment ago).</summary>
        private static Boolean FromPartyScreen(JSONClass changes)
        {
            JSONArray other = changes["other"].AsArray;
            if (other == null || other.Count != 1 || other[0].Value != "slot")
                return false;
            return PersistenSingleton<UIManager>.Instance.State == UIManager.UIState.PartySetting
                || Time.realtimeSinceStartup - _partyScreenAt < 5f;
        }

        /// <summary>Host: null if a guest's new party from the party screen may be applied now, else why not.</summary>
        private static String CheckPartyScreen(JSONClass request, JSONClass state, Func<Int32, Boolean> playedByOther)
        {
            if (!CanTouchPartyNow() || IsMenuBusy() || SceneSync.Active)
                return "the host's game is busy (a cutscene, battle or menu): try again in a moment";
            JSONArray now = state[PartyState.Common]["slot"].AsArray, want = request["slot"].AsArray;
            if (now == null || want.Count != now.Count)
                return "the new party didn't come through";
            Dictionary<Int32, JSONNode> players = PartyState.PlayersById(state[PartyState.Common]);
            HashSet<Int32> wanted = new HashSet<Int32>();
            foreach (JSONNode n in want)
            {
                Int32 id = n.AsInt;
                if (id == EmptySlot)
                    continue;
                if (!players.TryGetValue(id, out JSONNode p) || p["info"]["party"].AsInt == 0)
                    return $"{CharacterName(id)} can't join the party now";
                if (!wanted.Add(id))
                    return "a character is in the party twice";
            }
            if (wanted.Count == 0)
                return "the party can't be empty";
            if (FirstMember(now) != FirstMember(want))
                return "the party's first member leads on the field for everyone: only the host can change it";
            foreach (JSONNode n in now)
            {
                Int32 id = n.AsInt;
                if (id != EmptySlot && !wanted.Contains(id) && playedByOther(id))
                    return $"{CharacterName(id)} is another player's character";
            }
            return null;
        }

        private const Int32 EmptySlot = 255;

        private static Int32 FirstMember(JSONArray slots)
        {
            foreach (JSONNode n in slots)
                if (n.AsInt != EmptySlot)
                    return n.AsInt;
            return EmptySlot;
        }

        private static String CharacterName(Int32 id)
        {
            try { return FF9StateSystem.Common.FF9.GetPlayer((Memoria.Data.CharacterId)id)?.Name ?? $"character {id}"; }
            catch (Exception) { return $"character {id}"; }
        }

        /// <summary>Puts a state into the game. If that changed who's in the party, the field's party entries are
        /// updated too (what a script's UpdatePartyUID() does after Party()): the change came from another PC, so no
        /// script here did it.</summary>
        private static void ApplyState(JSONClass state)
        {
            String before = FF9StateSystem.Common.FF9.party.member == null ? null : SlotKey();
            PartyState.Apply(state);
            if (before == null || before == SlotKey())
                return;
            try
            {
                EventEngine engine = PersistenSingleton<EventEngine>.Instance;
                if (engine != null && engine.gMode == 1)
                    engine.SetupPartyUID();
                Log.Message($"[Online] Party: the party is now {SlotKey()} (was {before})");
            }
            catch (Exception err)
            {
                Log.Error(err, "[Online] Party: the field's party entries couldn't be updated");
            }
        }

        private static String SlotKey()
        {
            PLAYER[] members = FF9StateSystem.Common.FF9.party.member;
            String[] ids = new String[members.Length];
            for (Int32 i = 0; i < members.Length; i++)
                ids[i] = members[i] == null ? "-" : ((Int32)members[i].info.slot_no).ToString();
            return String.Join(" ", ids);
        }

        /// <summary>What the items a request removes sell for (the most gil a shop request can bring in).</summary>
        private static Int64 SellValue(JSONArray items)
        {
            Int64 value = 0;
            if (items == null)
                return value;
            foreach (JSONNode item in items)
            {
                Int32 d = item["d"].AsInt;
                if (d < 0 && ff9item._FF9Item_Data.TryGetValue((Memoria.Data.RegularItem)item["id"].AsInt, out FF9ITEM_DATA data) && data.selling_price > 0)
                    value += (Int64)data.selling_price * -d;
            }
            return value;
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
            if (IsShopRequest(request) && request["gil"] != null && request["gil"].AsLong != 0)
            {
                Int64 gil = Math.Max(0L, Math.Min(9999999L, state[PartyState.Common]["gil"].AsLong + request["gil"].AsLong));
                state[PartyState.Common]["gil"] = new JSONData(gil.ToString());
            }
            if (IsPartyScreenRequest(request))
                state[PartyState.Common]["slot"] = PartyState.Clone(request["slot"]);
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
            if (fingerprint == _expectedFingerprint && !GuestFinds.Any)
                return;
            JSONClass now = PartyState.Capture();
            JSONClass changes = PartyState.Diff(_expected, now);
            _expected = now;
            _expectedFingerprint = fingerprint;
            if (GuestFinds.Any)
            {
                // M5: what the scripts gave or took is sent as finds (the host replays them); the item, gil, key item
                // and card differences they caused aren't requests of their own. Other story changes stay the host's.
                if (changes == null)
                    changes = new JSONClass();
                changes.Remove("items");
                changes.Remove("gil");
                JSONArray other = changes["other"].AsArray;
                if (other != null)
                {
                    List<String> dropped = new List<String>();
                    foreach (JSONNode o in other)
                        dropped.Add(o.Value);
                    changes.Remove("other");
                    dropped.RemoveAll(o => o == "rareItems" || o == "rareItemsEx" || o == "cards");
                    if (dropped.Count > 0)
                        Log.Message($"[Online] Party: with the finds, not sent (the host's game decides): {String.Join(", ", dropped.ToArray())}");
                }
                changes.Add("found", GuestFinds.Take());
            }
            if (changes != null && GuestGuard.HoldLiveFlags)
            {
                // [Online] Spike E: in a party scene this PC runs the owner's scene in step; what its copy of the scripts
                // changes in the party (members, slots, finds) is the owner's to send, not a request of this guest's
                Log.Message($"[Online] Party: changed by this PC's copy of a party scene, not sent (the scene owner's game decides): {Describe(changes)}");
                return;
            }
            if (changes == null)
                return;
            if (changes["found"] == null && (changes["gil"] != null || changes["items"] != null)
                && PersistenSingleton<UIManager>.Instance.State == UIManager.UIState.Shop)
                changes.Add("shop", new JSONData(1)); // bought, sold or synthesized: the host lets it change gil
            if (changes["found"] == null && FromPartyScreen(changes))
            {
                // the party screen's new party: the host lets it change who's in the party (CheckPartyScreen)
                changes.Add("partyScreen", new JSONData(1));
                changes.Add("slot", PartyState.Clone(now[PartyState.Common]["slot"]));
            }
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
            ApplyState(state);
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
        internal static Boolean IsGuestNow => IsGuest;

        /// <summary>[Online] hook, SaveLoadUI (choosing a file to save): guests can't save.</summary>
        public static Boolean BlockGuestSave() => Block("Only the host can save.");

        /// <summary>[Online] hook, ItemUI arrange and CardUI discard.</summary>
        public static Boolean BlockGuestAction(String message) => Block(message);

        /// <summary>
        /// Whether this PC may change this character's equipment: its own character, or a follow character (nobody
        /// plays it). Not a character another player plays, on the host either. True off a session.
        /// </summary>
        public static Boolean MayEquip(Int32 character)
        {
            PartySync sync = Instance;
            if (sync == null || !sync.Active || character < 0)
                return true;
            return character == sync._myCharacter() || !sync._isPlayed(character);
        }

        /// <summary>[Online] hook, EquipUI (Equip, Optimize, Remove, and choosing a gear slot): only this character's
        /// player changes its equipment, but anyone a follow character's.</summary>
        public static Boolean BlockEquipChange(PLAYER player)
        {
            if (player == null || MayEquip((Int32)player.info.slot_no))
                return false;
            FF9Sfx.FF9SFX_Play(102);
            Toast($"Only {player.Name}'s player can change their equipment.");
            return true;
        }

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
            if (IsShopRequest(request))
                parts.Add("in a shop");
            if (IsPartyScreenRequest(request))
                parts.Add($"party screen: {request["slot"].ToString()}");
            JSONArray other = request["other"].AsArray;
            if (other != null && other.Count > 0)
                parts.Add($"other: {Join(other)}");
            if (request["found"] != null && request["found"].AsArray != null)
                parts.Add(GuestFinds.Describe(request["found"].AsArray));
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
