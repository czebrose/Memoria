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
    /// Guest → host (control channel): MsgMeet [type, kind, field lo, field hi].
    /// Host → guest: MsgResync, 12 bytes with mode <see cref="ModeKeepSpot"/> and the field (meet), or the usual
    /// 10-byte MsgResync (join).
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
                return;
            EventWatch.Update(nowMs, IsHost ? "host" : WithHost ? "guest, with the host" : "guest, alone");
            if (IsHost)
                HostMeet();
            else
                GuestMeet(nowMs);
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
            SendMeet(MeetKindMeet, (UInt16)mine);
            Log.Message($"[Online] Meet: you and the host are both on field {mine}; asking for the host's world (you keep your spot)");
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

        private void SendMeet(Byte kind, UInt16 field)
        {
            Byte[] message = { MsgMeet, kind, (Byte)field, (Byte)(field >> 8) };
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
            if (data.Length != 4 || data[0] != MsgMeet)
                return false;
            if (!IsHost || Phase != SessionPhase.Playing)
                return true;
            UInt16 field = (UInt16)(data[2] | (data[3] << 8));
            _meetRequests[peer] = new MeetRequest { Kind = data[1], Field = field };
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
            _restoreSpot = null;
            _meetRequests.Clear();
            EventWatch.Reset();
        }
    }
}
