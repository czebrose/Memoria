using Memoria.Prime;
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace Memoria.Online
{
    /// <summary>
    /// Steam lobby helper: create (friends-only, 4 members), join (overlay invite, command line, Steam ID),
    /// member list, host = lobby owner.
    /// Call results (LobbyCreated_t, LobbyEnter_t) are polled through ISteamUtils; membership is polled.
    /// Only GameLobbyJoinRequested_t really needs a registered callback; the others are registered for logging.
    /// </summary>
    internal sealed class SteamLobby : IDisposable
    {
        public const Int32 MaxMembers = 4;
        public const String ModKey = "mod";
        public const String ModValue = "ff9sequel";
        public const String ProtoKey = "proto";
        public const String ProtoValue = "1";

        public enum LobbyState { None, Creating, Joining, InLobby }

        public LobbyState State { get; private set; }
        public UInt64 LobbyId { get; private set; }
        public UInt64 OwnerId { get; private set; }
        public readonly UInt64 LocalId;
        public Boolean IsHost => State == LobbyState.InLobby && OwnerId == LocalId;
        /// <summary>All members, local user included.</summary>
        public readonly List<UInt64> Members = new List<UInt64>();
        public String LastEvent { get; private set; } = String.Empty;

        public event Action<UInt64> MemberJoined;
        public event Action<UInt64> MemberLeft;
        public event Action<UInt64> HostChanged;

        private UInt64 _pendingCall;
        private Int32 _pendingCallbackId;
        private Int32 _pendingSize;
        private Single _nextMemberPoll;
        private readonly List<SteamCallback> _callbacks = new List<SteamCallback>();

        // Sizes of the callback structs (pack 8 on Windows)
        private const Int32 SizeLobbyCreated = 16;          // EResult m_eResult; uint64 m_ulSteamIDLobby @8
        private const Int32 SizeLobbyEnter = 24;            // uint64 lobby; uint32 permissions @8; bool locked @12; uint32 response @16
        private const Int32 SizeLobbyChatUpdate = 32;       // uint64 lobby; uint64 changed @8; uint64 makingChange @16; uint32 state @24
        private const Int32 SizeGameLobbyJoinRequested = 16; // CSteamID lobby; CSteamID friend @8
        private const Int32 SizeGameOverlayActivated = 1;   // uint8 m_bActive

        public SteamLobby()
        {
            LocalId = SteamNative.GetSteamID(SteamNative.User);
            _callbacks.Add(new SteamCallback(SteamNative.CB_GameLobbyJoinRequested, SizeGameLobbyJoinRequested, OnGameLobbyJoinRequested));
            _callbacks.Add(new SteamCallback(SteamNative.CB_LobbyChatUpdate, SizeLobbyChatUpdate, OnLobbyChatUpdate));
            _callbacks.Add(new SteamCallback(SteamNative.CB_LobbyEnter, SizeLobbyEnter, OnLobbyEnterCallback));
            _callbacks.Add(new SteamCallback(SteamNative.CB_GameOverlayActivated, SizeGameOverlayActivated, OnGameOverlayActivated));
        }

        public Int32 CallbacksFired
        {
            get
            {
                Int32 n = 0;
                foreach (SteamCallback cb in _callbacks)
                    n += cb.FiredCount;
                return n;
            }
        }

        public void Create()
        {
            if (State != LobbyState.None)
                Leave();
            UInt64 call = SteamNative.CreateLobby(SteamNative.Matchmaking, SteamNative.LobbyTypeFriendsOnly, MaxMembers);
            SetEvent($"CreateLobby(friends-only, {MaxMembers}) -> call {call}");
            StartCall(call, SteamNative.CB_LobbyCreated, SizeLobbyCreated, LobbyState.Creating);
        }

        public void Join(UInt64 lobbyId)
        {
            if (State == LobbyState.InLobby && LobbyId == lobbyId)
                return;
            if (State != LobbyState.None)
                Leave();
            UInt64 call = SteamNative.JoinLobby(SteamNative.Matchmaking, lobbyId);
            SetEvent($"JoinLobby({lobbyId}) -> call {call}");
            StartCall(call, SteamNative.CB_LobbyEnter, SizeLobbyEnter, LobbyState.Joining);
        }

        public void Leave()
        {
            if (LobbyId != 0)
                SteamNative.LeaveLobby(SteamNative.Matchmaking, LobbyId);
            SetEvent($"Left lobby {LobbyId}");
            foreach (UInt64 member in Members.ToArray())
                if (member != LocalId)
                    MemberLeft?.Invoke(member);
            Members.Clear();
            LobbyId = 0;
            OwnerId = 0;
            _pendingCall = 0;
            State = LobbyState.None;
        }

        /// <summary>Opens the Steam overlay's "invite friends to lobby" dialog.</summary>
        public void OpenInviteOverlay()
        {
            if (State != LobbyState.InLobby)
            {
                SetEvent("Not in a lobby: create one first");
                return;
            }
            SteamNative.ActivateGameOverlayInviteDialog(SteamNative.Friends, LobbyId);
            SetEvent($"Opened the overlay invite dialog (overlay enabled: {SteamNative.IsOverlayEnabled(SteamNative.Utils)})");
        }

        /// <summary>Fallback for when the overlay doesn't work: join the lobby of a Steam friend who is playing FF9.</summary>
        public Boolean JoinFriendLobby()
        {
            Int32 count = SteamNative.GetFriendCount(SteamNative.Friends, SteamNative.FriendFlagImmediate);
            for (Int32 i = 0; i < count; i++)
            {
                UInt64 friend = SteamNative.GetFriendByIndex(SteamNative.Friends, i, SteamNative.FriendFlagImmediate);
                if (!SteamNative.GetFriendGamePlayed(SteamNative.Friends, friend, out SteamNative.FriendGameInfo info))
                    continue;
                UInt32 appId = (UInt32)(info.GameId & 0xFFFFFF);
                if (appId == SteamNative.FF9AppId && info.SteamIdLobby != 0)
                {
                    SetEvent($"Joining the lobby of {SteamNative.Tag(friend)}");
                    Join(info.SteamIdLobby);
                    return true;
                }
            }
            SetEvent("No friend in an FF9 lobby found");
            return false;
        }

        public void Update(Single now)
        {
            PollCallResult();
            if (State == LobbyState.InLobby && now >= _nextMemberPoll)
            {
                _nextMemberPoll = now + 0.25f;
                RefreshMembers();
            }
        }

        public String GetData(String key)
        {
            if (LobbyId == 0)
                return String.Empty;
            return SteamNative.FromUtf8(SteamNative.GetLobbyData(SteamNative.Matchmaking, LobbyId, SteamNative.Utf8(key)));
        }

        private void StartCall(UInt64 call, Int32 callbackId, Int32 size, LobbyState state)
        {
            if (call == 0) // k_uAPICallInvalid
            {
                SetEvent("Steam returned an invalid API call handle");
                State = LobbyState.None;
                return;
            }
            _pendingCall = call;
            _pendingCallbackId = callbackId;
            _pendingSize = size;
            State = state;
        }

        private void PollCallResult()
        {
            if (_pendingCall == 0)
                return;
            if (!SteamNative.IsAPICallCompleted(SteamNative.Utils, _pendingCall, out Boolean failed))
                return;
            UInt64 call = _pendingCall;
            _pendingCall = 0;
            IntPtr buffer = Marshal.AllocHGlobal(_pendingSize);
            try
            {
                Boolean ok = SteamNative.GetAPICallResult(SteamNative.Utils, call, buffer, _pendingSize, _pendingCallbackId, out failed);
                if (!ok || failed)
                {
                    SetEvent($"API call {call} (callback {_pendingCallbackId}) failed (ok={ok}, ioFailure={failed})");
                    State = LobbyState.None;
                    return;
                }
                if (_pendingCallbackId == SteamNative.CB_LobbyCreated)
                    OnLobbyCreated(buffer);
                else if (_pendingCallbackId == SteamNative.CB_LobbyEnter)
                    OnLobbyEntered(buffer);
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }

        private void OnLobbyCreated(IntPtr result)
        {
            Int32 eResult = Marshal.ReadInt32(result, 0);
            UInt64 lobby = (UInt64)Marshal.ReadInt64(result, 8);
            if (eResult != 1) // k_EResultOK
            {
                SetEvent($"LobbyCreated failed: EResult {eResult}");
                State = LobbyState.None;
                return;
            }
            LobbyId = lobby;
            SteamNative.SetLobbyMemberLimit(SteamNative.Matchmaking, lobby, MaxMembers);
            SteamNative.SetLobbyData(SteamNative.Matchmaking, lobby, SteamNative.Utf8(ModKey), SteamNative.Utf8(ModValue));
            SteamNative.SetLobbyData(SteamNative.Matchmaking, lobby, SteamNative.Utf8(ProtoKey), SteamNative.Utf8(ProtoValue));
            EnterLobby(lobby);
            SetEvent($"Lobby {lobby} created; you are the host. Lobby ID copied to the clipboard");
            try { UnityEngine.GUIUtility.systemCopyBuffer = lobby.ToString(); } catch { }
        }

        private void OnLobbyEntered(IntPtr result)
        {
            UInt64 lobby = (UInt64)Marshal.ReadInt64(result, 0);
            UInt32 response = (UInt32)Marshal.ReadInt32(result, 16);
            if (response != 1) // k_EChatRoomEnterResponseSuccess
            {
                SetEvent($"Joining lobby {lobby} failed: EChatRoomEnterResponse {response}");
                State = LobbyState.None;
                return;
            }
            LobbyId = lobby;
            String mod = GetData(ModKey);
            String proto = GetData(ProtoKey);
            if (mod != ModValue || proto != ProtoValue)
                Log.Warning($"[Online] Lobby {lobby} has mod='{mod}' proto='{proto}' (expected '{ModValue}'/'{ProtoValue}')");
            EnterLobby(lobby);
            SetEvent($"Joined lobby {lobby}; host is {SteamNative.Tag(OwnerId)}");
        }

        private void EnterLobby(UInt64 lobby)
        {
            LobbyId = lobby;
            State = LobbyState.InLobby;
            RefreshMembers();
        }

        public void RefreshMembers()
        {
            Int32 count = SteamNative.GetNumLobbyMembers(SteamNative.Matchmaking, LobbyId);
            List<UInt64> current = new List<UInt64>(count);
            for (Int32 i = 0; i < count; i++)
                current.Add(SteamNative.GetLobbyMemberByIndex(SteamNative.Matchmaking, LobbyId, i));
            foreach (UInt64 member in current)
            {
                if (Members.Contains(member))
                    continue;
                Members.Add(member);
                Log.Message($"[Online] Lobby member joined: {SteamNative.Tag(member)}");
                if (member != LocalId)
                    MemberJoined?.Invoke(member);
            }
            foreach (UInt64 member in Members.ToArray())
            {
                if (current.Contains(member))
                    continue;
                Members.Remove(member);
                Log.Message($"[Online] Lobby member left: {SteamNative.Tag(member)}");
                if (member != LocalId)
                    MemberLeft?.Invoke(member);
            }
            UInt64 owner = SteamNative.GetLobbyOwner(SteamNative.Matchmaking, LobbyId);
            if (owner != OwnerId)
            {
                OwnerId = owner;
                Log.Message($"[Online] Lobby owner (host) is now {SteamNative.Tag(owner)}{(owner == LocalId ? " (you)" : String.Empty)}");
                HostChanged?.Invoke(owner);
            }
        }

        private void OnGameLobbyJoinRequested(IntPtr param)
        {
            UInt64 lobby = (UInt64)Marshal.ReadInt64(param, 0);
            UInt64 friend = (UInt64)Marshal.ReadInt64(param, 8);
            Log.Message($"[Online] Callback GameLobbyJoinRequested_t: lobby {lobby} from {SteamNative.Tag(friend)}");
            Join(lobby);
        }

        private void OnLobbyChatUpdate(IntPtr param)
        {
            UInt64 lobby = (UInt64)Marshal.ReadInt64(param, 0);
            UInt64 changed = (UInt64)Marshal.ReadInt64(param, 8);
            UInt32 change = (UInt32)Marshal.ReadInt32(param, 24);
            Log.Message($"[Online] Callback LobbyChatUpdate_t: lobby {lobby}, {SteamNative.Tag(changed)} state change 0x{change:X}");
            if (lobby == LobbyId && State == LobbyState.InLobby)
                RefreshMembers();
        }

        private void OnLobbyEnterCallback(IntPtr param)
        {
            UInt64 lobby = (UInt64)Marshal.ReadInt64(param, 0);
            UInt32 response = (UInt32)Marshal.ReadInt32(param, 16);
            Log.Message($"[Online] Callback LobbyEnter_t: lobby {lobby}, response {response}");
        }

        private void OnGameOverlayActivated(IntPtr param)
        {
            Log.Message($"[Online] Callback GameOverlayActivated_t: active={Marshal.ReadByte(param, 0) != 0}");
        }

        private void SetEvent(String message)
        {
            LastEvent = message;
            Log.Message($"[Online] {message}");
        }

        public void Dispose()
        {
            if (State != LobbyState.None)
                Leave();
            foreach (SteamCallback cb in _callbacks)
                cb.Dispose();
            _callbacks.Clear();
        }
    }
}
