using Assets.Scripts.Common;
using Memoria.Online.Ghosts;
using Memoria.Online.Party;
using Memoria.Prime;
using System;
using System.Collections.Generic;
using UnityEngine;

namespace Memoria.Online.Session
{
    /// <summary>
    /// Step 2 / M5: free roaming. Guests go where they like and run their own fields; when a guest and the host are on
    /// the same field, the guest asks the host for its whole game (the M1 blob) and loads it, keeping its own spot.
    /// "Join host" is the same resync without keeping the spot: the guest lands where the host is.
    ///
    /// Guest → host (control channel): MsgMeet [type, kind, field lo, field hi], or 8 bytes with a CRC32 of the guest's
    /// shared event bytes (<see cref="EventWatch.SharedDigest"/>) when it arrives on the host's field.
    /// Host → guest: MsgResync, 12 bytes with mode <see cref="ModeKeepSpot"/> and the field (meet), or the usual
    /// 10-byte MsgResync (join). If the arriving guest's CRC matches the host's own, nothing is sent: the guest's world
    /// already is the host's, and the reload (a blink) is skipped.
    /// The host answers once it has control on a field (never in the middle of a cutscene), and drops a meet request
    /// if it has left that field meanwhile.
    /// </summary>
    internal sealed partial class SessionLobby
    {
        public const Byte MsgMeet = 6;
        private const Byte MeetKindMeet = 0;
        private const Byte MeetKindJoin = 1;
        private const Byte ModeKeepSpot = 2;
        private const Double RestoreSpotTimeoutMs = 20000;

        private sealed class MeetRequest
        {
            public Byte Kind;
            public UInt16 Field;
            public Boolean HasDigest; // an arrival: skip the reload if the shared event bytes already match
            public UInt32 Digest;
        }

        private sealed class SavedSpot
        {
            public Int32 Field;
            public FieldMap OldMap;
            public Vector3 Pos;
            public Single Facing;
            public Double Until;
        }

        // Guest
        private Int32 _metField = -1; // the field this guest last resynced on with the host there (-1: not together)
        private SavedSpot _restoreSpot;
        private Double _lastMeetNowMs;

        // Host
        private readonly Dictionary<UInt64, MeetRequest> _meetRequests = new Dictionary<UInt64, MeetRequest>();

        /// <summary>Guest, playing: on the host's field now (after the meeting resync).</summary>
        public Boolean WithHost => !IsHost && _metField >= 0 && _metField == LocalPlayableField(false);

        private void UpdateMeet(Double nowMs)
        {
            _lastMeetNowMs = nowMs;
            if (Phase != SessionPhase.Playing)
            {
                SetFrozen(null);
                GuestGuard.ActedOnThisField = false;
                return;
            }
            TrackActed(nowMs);
            WatchEvents(nowMs, false);
            Boolean meet = !SceneSync.Active; // Spike E: no meeting resync (a whole-game reload) in the middle of a party scene; it happens after
            if (IsHost)
            {
                if (meet)
                    HostMeet();
                HostPublishState(nowMs);
            }
            else
            {
                ReleaseHeldFlags();
                if (meet)
                    GuestMeet(nowMs);
                GuestFreeze(nowMs);
            }
        }

        // ---- Event bytes (tier 2) and "acted on this field" -------------------------------------------------------

        public const Byte MsgFlags = 8;
        private const Int32 MaxFlagsPerMessage = 300;
        private const String WorldMapPlace = "world map";
        private System.Object _actedOn; // the field map (or the world map) GuestGuard.ActedOnThisField is about
        public Int32 FlagsSent { get; private set; }
        public Int32 FlagsApplied { get; private set; }

        /// <summary>
        /// GuestGuard.ActedOnThisField: false from a field (or world map) load until this PC first has control on it.
        /// Changes are flushed at both edges, so a field's last changes (an exit scene) are still forwarded, and its
        /// opening scripts' changes never are. A battle keeps the field's state.
        /// </summary>
        private void TrackActed(Double nowMs)
        {
            EventEngine engine = PersistenSingleton<EventEngine>.Instance;
            UIManager ui = PersistenSingleton<UIManager>.Instance;
            if (engine == null || ui == null)
                return;
            System.Object place = engine.gMode == 1 ? engine.fieldmap : engine.gMode == 3 ? WorldMapPlace : _actedOn;
            if (!ReferenceEquals(place, _actedOn))
            {
                WatchEvents(nowMs, true); // the old place's last changes, forwarded if we had acted there
                _actedOn = place;
                GuestGuard.ActedOnThisField = false;
            }
            if (GuestGuard.ActedOnThisField)
                return;
            Boolean control = (engine.gMode == 1 && engine.fieldmap?.playerController != null && engine.GetUserControl() && ui.State == UIManager.UIState.FieldHUD)
                              || (engine.gMode == 3 && ui.State == UIManager.UIState.WorldHUD);
            if (!control)
                return;
            WatchEvents(nowMs, true); // the opening scripts' changes: logged, not forwarded
            GuestGuard.ActedOnThisField = true;
        }

        private void WatchEvents(Double nowMs, Boolean force)
        {
            String who = IsHost ? "host" : $"guest, {(WithHost ? "with the host" : "alone")}{(GuestGuard.ActedOnThisField ? "" : ", field opening: not sent")}";
            List<KeyValuePair<Int32, Byte>> changes = EventWatch.Update(nowMs, who, force);
            if (changes == null || !GuestGuard.ActedOnThisField)
                return;
            changes.RemoveAll(c => !EventWatch.IsForwarded(c.Key));
            if (changes.Count == 0)
                return;
            if (IsHost)
            {
                // Live flags: the host's own changes go to the guests on its field at once (a chest it opened, an ice
                // wall it broke), not only with their next meeting resync
                Int32 field = LocalPlayableField(false);
                if (field >= 0)
                    RelayFlags(changes, field, 0, "the host");
                return;
            }
            SendFlags(_lobby.OwnerId, changes);
            FlagsSent += changes.Count;
        }

        private void SendFlags(UInt64 peer, List<KeyValuePair<Int32, Byte>> changes)
        {
            for (Int32 start = 0; start < changes.Count; start += MaxFlagsPerMessage)
            {
                Int32 count = Math.Min(MaxFlagsPerMessage, changes.Count - start);
                Byte[] message = new Byte[3 + count * 3];
                message[0] = MsgFlags;
                message[1] = (Byte)count;
                message[2] = (Byte)(count >> 8);
                for (Int32 i = 0; i < count; i++)
                {
                    KeyValuePair<Int32, Byte> c = changes[start + i];
                    message[3 + i * 3] = (Byte)c.Key;
                    message[4 + i * 3] = (Byte)(c.Key >> 8);
                    message[5 + i * 3] = c.Value;
                }
                _transport.Send(peer, TransportChannel.Control, message, message.Length, true);
            }
        }

        /// <summary>Host: send changes made on a field to every guest standing on that field (except the one that made them).</summary>
        private void RelayFlags(List<KeyValuePair<Int32, Byte>> changes, Int32 field, UInt64 except, String from)
        {
            List<String> sentTo = new List<String>();
            foreach (UInt64 peer in _transport.Peers)
            {
                if (peer == except || peer == _lobby.LocalId)
                    continue;
                if (!GhostManager.TryGetLastField(peer, out UInt16 peerField) || peerField != field)
                    continue;
                SendFlags(peer, changes);
                FlagsRelayed += changes.Count;
                sentTo.Add(SteamNative.Tag(peer));
            }
            if (sentTo.Count > 0)
                Log.Message($"[Online] Event bytes: {changes.Count} from {from} on field {field} sent live to {String.Join(", ", sentTo.ToArray())}");
        }

        public Int32 FlagsRelayed { get; private set; }

        private Boolean OnFlagsMessage(UInt64 peer, Byte[] data)
        {
            if (data.Length < 3 || data[0] != MsgFlags)
                return false;
            Int32 count = data[1] | (data[2] << 8);
            if (data.Length != 3 + count * 3)
                return false;
            if (Phase != SessionPhase.Playing)
                return true;
            List<KeyValuePair<Int32, Byte>> changes = new List<KeyValuePair<Int32, Byte>>(count);
            for (Int32 i = 0; i < count; i++)
                changes.Add(new KeyValuePair<Int32, Byte>(data[3 + i * 3] | (data[4 + i * 3] << 8), data[5 + i * 3]));
            if (!IsHost)
            {
                if (peer != _lobby.OwnerId)
                    return true;
                if (GuestGuard.HoldLiveFlags)
                {
                    // In step with the host's scripts (a party scene): its own scripts set these; apply later what differs
                    _heldFlags.AddRange(changes);
                    return true;
                }
                ApplyHostFlags(changes);
                return true;
            }
            WatchEvents(_lastMeetNowMs, true); // the host's own changes so far are logged as the host's
            EventWatch.ApplyFromGuest(SteamNative.Tag(peer), changes);
            FlagsApplied += count;
            changes.RemoveAll(c => !EventWatch.IsForwarded(c.Key));
            if (changes.Count > 0 && GhostManager.TryGetLastField(peer, out UInt16 guestField) && guestField != GhostState.NotOnField)
                RelayFlags(changes, guestField, peer, SteamNative.Tag(peer));
            return true;
        }

        private readonly List<KeyValuePair<Int32, Byte>> _heldFlags = new List<KeyValuePair<Int32, Byte>>();

        /// <summary>Guest: the host's live flags into this game (its own changes so far go out first); a redraw follows.</summary>
        private void ApplyHostFlags(List<KeyValuePair<Int32, Byte>> changes)
        {
            WatchEvents(_lastMeetNowMs, true);
            Int32 applied = EventWatch.ApplyFromHost(SteamNative.Tag(_lobby.OwnerId), changes);
            FlagsApplied += applied;
            Int32 field = LocalPlayableField(false);
            if (applied > 0 && field >= 0)
            {
                // The field shows chests, ice and doors from the flags it loaded with: refresh it soon
                _refreshField = field;
                _refreshAfter = _lastMeetNowMs + RefreshQuietMs;
            }
        }

        /// <summary>Guest: live flags held during a party scene go in once it's over (only what still differs).</summary>
        private void ReleaseHeldFlags()
        {
            if (GuestGuard.HoldLiveFlags || _heldFlags.Count == 0)
                return;
            List<KeyValuePair<Int32, Byte>> held = new List<KeyValuePair<Int32, Byte>>(_heldFlags);
            _heldFlags.Clear();
            Log.Message($"[Online] Event bytes: {held.Count} live change(s) held during a party scene, applying what differs");
            ApplyHostFlags(held);
        }

        // Guest: live flags arrived for this field; once nothing new has come for a moment and this player has control,
        // a keep-spot meeting resync redraws the field (one blink)
        private const Double RefreshQuietMs = 1500;
        private Int32 _refreshField = -1;
        private Double _refreshAfter;

        // ---- Host state: guests on the host's field wait during its scenes and battles -------------------------------

        public const Byte MsgHostState = 9;
        public enum HostActivity : byte { Playing = 0, Scene = 1, Menu = 2, Battle = 3, WorldMap = 4, Other = 5 }
        private const Double SceneAfterMs = 1000; // control off this long on a field = a scene (not a door or a sign)
        private const Double HostStateEveryMs = 1000;
        private const Double HostStateStaleMs = 3500;

        // Host
        private HostActivity _published = HostActivity.Other;
        private Double _nextHostState;
        private Double _noControlSince = -1;

        // Guest
        private HostActivity _hostActivity = HostActivity.Other;
        private UInt16 _hostActivityField = GhostState.NotOnField;
        private Double _hostActivityAt = Double.MinValue;

        public HostActivity HostActivityNow => IsHost ? _published : _hostActivity;

        private void HostPublishState(Double nowMs)
        {
            EventEngine engine = PersistenSingleton<EventEngine>.Instance;
            UIManager ui = PersistenSingleton<UIManager>.Instance;
            HostActivity activity = HostActivity.Other;
            if (engine != null && ui != null)
            {
                if (engine.gMode == 2)
                {
                    activity = HostActivity.Battle;
                }
                else if (engine.gMode == 3)
                {
                    activity = ui.State == UIManager.UIState.WorldHUD ? HostActivity.WorldMap : HostActivity.Menu;
                }
                else if (engine.gMode == 1 && ui.State != UIManager.UIState.FieldHUD)
                {
                    activity = ui.State == UIManager.UIState.Title || ui.State == UIManager.UIState.GameOver ? HostActivity.Other : HostActivity.Menu;
                }
                else if (engine.gMode == 1)
                {
                    if (engine.GetUserControl())
                        _noControlSince = -1;
                    else if (_noControlSince < 0)
                        _noControlSince = nowMs;
                    activity = _noControlSince >= 0 && nowMs - _noControlSince >= SceneAfterMs ? HostActivity.Scene : HostActivity.Playing;
                }
            }
            if (activity == _published && nowMs < _nextHostState)
                return;
            if (activity != _published)
                Log.Message($"[Online] Host state: {activity}{(activity == HostActivity.Scene || activity == HostActivity.Battle ? $" (guests on field {FF9StateSystem.Common.FF9.fldMapNo} wait)" : "")}");
            _published = activity;
            _nextHostState = nowMs + HostStateEveryMs;
            UInt16 field = (UInt16)FF9StateSystem.Common.FF9.fldMapNo;
            Byte[] message = { MsgHostState, (Byte)activity, (Byte)field, (Byte)(field >> 8) };
            foreach (UInt64 peer in _transport.Peers)
                _transport.Send(peer, TransportChannel.Control, message, message.Length, true);
        }

        private Boolean OnHostStateMessage(UInt64 peer, Byte[] data)
        {
            if (data.Length != 4 || data[0] != MsgHostState)
                return false;
            if (IsHost || peer != _lobby.OwnerId)
                return true;
            _hostActivity = (HostActivity)data[1];
            _hostActivityField = (UInt16)(data[2] | (data[3] << 8));
            _hostActivityAt = _lastMeetNowMs;
            return true;
        }

        private void GuestFreeze(Double nowMs)
        {
            EventEngine engine = PersistenSingleton<EventEngine>.Instance;
            Boolean fresh = nowMs - _hostActivityAt < HostStateStaleMs;
            Boolean busy = _hostActivity == HostActivity.Scene || _hostActivity == HostActivity.Battle;
            Boolean here = engine != null && engine.gMode == 1 && _hostActivityField == FF9StateSystem.Common.FF9.fldMapNo;
            SetFrozen(fresh && busy && here ? (_hostActivity == HostActivity.Battle ? "The host is in a battle..." : "The host is in a scene...") : null);
        }

        private static void SetFrozen(String message)
        {
            Boolean frozen = message != null;
            if (frozen == GuestGuard.Frozen && (!frozen || message == GuestGuard.FrozenMessage))
                return;
            GuestGuard.Frozen = frozen;
            GuestGuard.FrozenMessage = message;
            Log.Message(frozen ? $"[Online] Freeze: {message} (field {FF9StateSystem.Common.FF9.fldMapNo})" : "[Online] Freeze: off");
        }

        // ---- Guest ------------------------------------------------------------------------------------------------

        private void GuestMeet(Double nowMs)
        {
            if (_restoreSpot != null)
                TryRestoreSpot(nowMs);
            if (_pending != null || _restoreSpot != null)
                return;
            Int32 mine = LocalPlayableField(true);
            if (mine < 0)
                return; // menu, battle, world map, cutscene, loading: nothing changes until we're back
            if (_refreshField >= 0 && _refreshField != mine)
                _refreshField = -1; // left that field: the next one loads with the new flags anyway
            if (_refreshField >= 0 && nowMs >= _refreshAfter && !GuestGuard.Frozen)
            {
                _refreshField = -1;
                SendMeet(MeetKindMeet, (UInt16)mine);
                Log.Message($"[Online] Meet: live flags changed field {mine}; asking for the host's world to redraw it (you keep your spot)");
                return;
            }
            if (!GhostManager.TryGetLastField(_lobby.OwnerId, out UInt16 hostField) || hostField == GhostState.NotOnField)
                return; // the host is in a menu, a battle or on the world map: keep what we had
            if (hostField != mine)
            {
                if (_metField >= 0)
                    Log.Message($"[Online] Meet: apart now (you on field {mine}, the host on field {hostField})");
                _metField = -1;
                return;
            }
            if (_metField == mine)
                return;
            _metField = mine;
            UInt32 digest = EventWatch.SharedDigest();
            SendMeet(MeetKindMeet, (UInt16)mine, digest);
            Log.Message($"[Online] Meet: you and the host are both on field {mine}; asking for the host's world unless it matches yours (flags {digest:X8}; you keep your spot)");
        }

        /// <summary>Guest: go to the host (a resync that puts this player where the host is).</summary>
        public void JoinHost()
        {
            if (!IsGuest || Phase != SessionPhase.Playing)
                return;
            SendMeet(MeetKindJoin, GhostState.NotOnField);
            PartySync.Toast("Joining the host...");
            Log.Message("[Online] Meet: Join host asked");
        }

        private void SendMeet(Byte kind, UInt16 field, UInt32? digest = null)
        {
            Byte[] message = new Byte[digest.HasValue ? 8 : 4];
            message[0] = MsgMeet;
            message[1] = kind;
            message[2] = (Byte)field;
            message[3] = (Byte)(field >> 8);
            if (digest.HasValue)
                WriteU32(message, 4, digest.Value);
            _transport.Send(_lobby.OwnerId, TransportChannel.Control, message, message.Length, true);
        }

        /// <summary>The field this PC's player is on and can move in (or, <paramref name="needControl"/> false, just on), else -1.</summary>
        private static Int32 LocalPlayableField(Boolean needControl)
        {
            EventEngine engine = PersistenSingleton<EventEngine>.Instance;
            if (engine == null || engine.gMode != 1 || engine.fieldmap?.playerController == null)
                return -1;
            if (needControl && (!engine.GetUserControl() || PersistenSingleton<SceneDirector>.Instance.IsFading
                                || PersistenSingleton<UIManager>.Instance.State != UIManager.UIState.FieldHUD))
                return -1;
            return FF9StateSystem.Common.FF9.fldMapNo;
        }

        /// <summary>Before a keep-spot load: where this player stands. False if it isn't on that field any more.</summary>
        private Boolean TrySaveSpot(Int32 field, out SavedSpot spot)
        {
            spot = null;
            EventEngine engine = PersistenSingleton<EventEngine>.Instance;
            FieldMapActorController controller = engine?.fieldmap?.playerController;
            if (engine == null || engine.gMode != 1 || controller == null || controller.originalActor == null || FF9StateSystem.Common.FF9.fldMapNo != field)
                return false;
            spot = new SavedSpot
            {
                Field = field,
                OldMap = engine.fieldmap,
                Pos = controller.curPos,
                Facing = controller.originalActor.rotAngle[1],
                Until = _lastMeetNowMs + RestoreSpotTimeoutMs,
            };
            return true;
        }

        /// <summary>After a keep-spot load: once the reloaded field is playable, put the player back where it stood.</summary>
        private void TryRestoreSpot(Double nowMs)
        {
            SavedSpot spot = _restoreSpot;
            if (nowMs > spot.Until)
            {
                _restoreSpot = null;
                Log.Warning($"[Online] Meet: the reloaded field {spot.Field} wasn't playable within {RestoreSpotTimeoutMs / 1000:0}s; you stay where the host's world put you");
                return;
            }
            EventEngine engine = PersistenSingleton<EventEngine>.Instance;
            if (engine == null || engine.gMode != 1 || ReferenceEquals(engine.fieldmap, spot.OldMap) || LocalPlayableField(true) < 0)
                return;
            _restoreSpot = null;
            if (FF9StateSystem.Common.FF9.fldMapNo != spot.Field)
            {
                Log.Message($"[Online] Meet: the host's world loaded field {FF9StateSystem.Common.FF9.fldMapNo}, not {spot.Field}; you stay where it put you");
                return;
            }
            Actor actor = engine.fieldmap.playerController.originalActor;
            engine.SetActorPosition(actor, spot.Pos.x, spot.Pos.y, spot.Pos.z);
            actor.rotAngle[1] = spot.Facing;
            Log.Message($"[Online] Meet: back at your spot on field {spot.Field} ({spot.Pos.x:0},{spot.Pos.y:0},{spot.Pos.z:0}), tri {engine.fieldmap.playerController.activeTri}");
        }

        // ---- Host -------------------------------------------------------------------------------------------------

        private Boolean OnMeetMessage(UInt64 peer, Byte[] data)
        {
            if ((data.Length != 4 && data.Length != 8) || data[0] != MsgMeet)
                return false;
            if (!IsHost || Phase != SessionPhase.Playing)
                return true;
            UInt16 field = (UInt16)(data[2] | (data[3] << 8));
            Boolean hasDigest = data.Length == 8;
            UInt32 digest = hasDigest ? ReadU32(data, 4) : 0;
            _meetRequests[peer] = new MeetRequest { Kind = data[1], Field = field, HasDigest = hasDigest, Digest = digest };
            Log.Message($"[Online] Meet: {SteamNative.Tag(peer)} asks for {(data[1] == MeetKindJoin ? "Join host" : $"the world on field {field}")}");
            return true;
        }

        private void HostMeet()
        {
            if (_meetRequests.Count == 0)
                return;
            EventEngine engine = PersistenSingleton<EventEngine>.Instance;
            Boolean onField = IsPlayableOnField() && !PersistenSingleton<SceneDirector>.Instance.IsFading
                              && PersistenSingleton<UIManager>.Instance.State == UIManager.UIState.FieldHUD;
            Boolean onWorldMap = engine != null && engine.gMode == 3 && PersistenSingleton<UIManager>.Instance.State == UIManager.UIState.WorldHUD
                                 && !PersistenSingleton<SceneDirector>.Instance.IsFading;
            if (!onField && !onWorldMap)
                return; // a cutscene, a menu, a battle or a field change: answer once the host has control again
            Int32 hostField = onField ? FF9StateSystem.Common.FF9.fldMapNo : -1;
            Byte[] blob = null;
            UInt32 crc = 0;
            foreach (KeyValuePair<UInt64, MeetRequest> kv in _meetRequests)
            {
                MeetRequest request = kv.Value;
                if (!_transport.Peers.Contains(kv.Key))
                    continue;
                if (request.Kind == MeetKindMeet && request.Field != hostField)
                {
                    Log.Message($"[Online] Meet: {SteamNative.Tag(kv.Key)}'s request for field {request.Field} dropped (the host is {(hostField >= 0 ? $"on field {hostField}" : "on the world map")} now)");
                    continue;
                }
                if (request.Kind == MeetKindMeet && request.HasDigest)
                {
                    UInt32 own = EventWatch.SharedDigest();
                    if (own == request.Digest && own != 0)
                    {
                        Log.Message($"[Online] Meet: {SteamNative.Tag(kv.Key)} arrived on field {request.Field} with the host's flags already ({own:X8}); no reload");
                        continue;
                    }
                    Log.Message($"[Online] Meet: {SteamNative.Tag(kv.Key)}'s flags differ on field {request.Field} (guest {request.Digest:X8}, host {own:X8}); sending the host's world");
                }
                if (blob == null)
                {
                    blob = GameSnapshot.CaptureCurrent(out _);
                    crc = GameSnapshot.Crc32(blob, 0, blob.Length);
                }
                UInt32 transferId = _blobs.Send(kv.Key, blob);
                if (request.Kind == MeetKindJoin)
                {
                    SendControl(kv.Key, MsgResync, 0, transferId, crc);
                }
                else
                {
                    Byte[] message = new Byte[12];
                    message[0] = MsgResync;
                    message[1] = ModeKeepSpot;
                    WriteU32(message, 2, transferId);
                    WriteU32(message, 6, crc);
                    message[10] = (Byte)request.Field;
                    message[11] = (Byte)(request.Field >> 8);
                    _transport.Send(kv.Key, TransportChannel.Control, message, message.Length, true);
                }
                Log.Message($"[Online] Meet: sent the host's world to {SteamNative.Tag(kv.Key)} ({(request.Kind == MeetKindJoin ? "Join host" : $"keeps its spot on field {request.Field}")}, {blob.Length} bytes)");
            }
            _meetRequests.Clear();
        }

        private void ResetMeet()
        {
            _metField = -1;
            _refreshField = -1;
            _heldFlags.Clear();
            _restoreSpot = null;
            _meetRequests.Clear();
            EventWatch.Reset();
            _actedOn = null;
            GuestGuard.ActedOnThisField = false;
            SetFrozen(null);
            _published = HostActivity.Other;
            _noControlSince = -1;
            _hostActivity = HostActivity.Other;
            _hostActivityAt = Double.MinValue;
        }
    }
}
