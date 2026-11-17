using Memoria.Prime;
using System;
using System.Runtime.InteropServices;
using System.Text;

namespace Memoria.Online
{
    /// <summary>
    /// Bindings to the game's own steam_api64.dll (Steamworks SDK ~1.35, flat API).
    ///
    /// Functions are resolved with GetProcAddress on the steam_api64.dll module that is *already loaded*
    /// (by Square Enix's steamwrapper.dll, which called SteamAPI_Init). A plain [DllImport("steam_api64")]
    /// could make Mono load the second, identical copy in FF9_Data\Plugins as a separate module whose
    /// SteamAPI context was never initialised. SteamAPI_Init is never called here.
    ///
    /// Marshalling notes (x64 MSVC): CSteamID and SteamAPICall_t are passed as UInt64; the flat functions
    /// that return a CSteamID return a uint64. C++ bool is 1 byte, hence every bool is marshalled as I1.
    /// </summary>
    internal static class SteamNative
    {
        public const UInt32 FF9AppId = 377840;

        // Interface versions present in the shipped DLL (strings found in x64\steam_api64.dll)
        private const String ClientVersion = "SteamClient017";
        private const String NetworkingVersion = "SteamNetworking005";
        private const String MatchmakingVersion = "SteamMatchMaking009";
        private const String UserVersion = "SteamUser018";
        private const String FriendsVersion = "SteamFriends015";
        private const String UtilsVersion = "SteamUtils007";

        public static Boolean IsLoaded { get; private set; }
        public static String ModulePath { get; private set; }
        public static IntPtr Client, Networking, Matchmaking, User, Friends, Utils;

        #region kernel32
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr GetModuleHandleW(String moduleName);

        [DllImport("kernel32.dll", CharSet = CharSet.Ansi, ExactSpelling = true)]
        private static extern IntPtr GetProcAddress(IntPtr module, String procName);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        private static extern UInt32 GetModuleFileNameW(IntPtr module, StringBuilder fileName, UInt32 size);

        [DllImport("kernel32.dll")]
        private static extern IntPtr VirtualAlloc(IntPtr address, UIntPtr size, UInt32 allocationType, UInt32 protect);

        [DllImport("kernel32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern Boolean VirtualProtect(IntPtr address, UIntPtr size, UInt32 newProtect, out UInt32 oldProtect);

        [DllImport("kernel32.dll")]
        private static extern IntPtr GetCurrentProcess();

        [DllImport("kernel32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern Boolean FlushInstructionCache(IntPtr process, IntPtr address, UIntPtr size);
        #endregion

        #region Delegate types
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] public delegate IntPtr D_Ptr();
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] public delegate Int32 D_Int();
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] public delegate void D_Void();
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] public delegate void D_RegisterCallback(IntPtr callback, Int32 iCallback);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] public delegate void D_UnregisterCallback(IntPtr callback);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] public delegate IntPtr D_GetInterface(IntPtr self, Int32 hUser, Int32 hPipe, Byte[] version);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] public delegate IntPtr D_GetClientInterface(IntPtr self, Int32 hPipe, Byte[] version);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] [return: MarshalAs(UnmanagedType.I1)]
        public delegate Boolean D_SendP2PPacket(IntPtr self, UInt64 steamIdRemote, Byte[] data, UInt32 size, Int32 sendType, Int32 channel);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] [return: MarshalAs(UnmanagedType.I1)]
        public delegate Boolean D_IsP2PPacketAvailable(IntPtr self, out UInt32 size, Int32 channel);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] [return: MarshalAs(UnmanagedType.I1)]
        public delegate Boolean D_ReadP2PPacket(IntPtr self, [In, Out] Byte[] dest, UInt32 destSize, out UInt32 size, out UInt64 steamIdRemote, Int32 channel);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] [return: MarshalAs(UnmanagedType.I1)]
        public delegate Boolean D_SteamIdBool(IntPtr self, UInt64 steamId);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] [return: MarshalAs(UnmanagedType.I1)]
        public delegate Boolean D_GetP2PSessionState(IntPtr self, UInt64 steamId, out P2PSessionState state);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] [return: MarshalAs(UnmanagedType.I1)]
        public delegate Boolean D_BoolArgBool(IntPtr self, [MarshalAs(UnmanagedType.I1)] Boolean value);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] public delegate UInt64 D_CreateLobby(IntPtr self, Int32 lobbyType, Int32 maxMembers);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] public delegate UInt64 D_SteamIdToU64(IntPtr self, UInt64 steamId);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] public delegate void D_SteamIdVoid(IntPtr self, UInt64 steamId);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] public delegate Int32 D_SteamIdInt(IntPtr self, UInt64 steamId);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] public delegate UInt64 D_GetLobbyMemberByIndex(IntPtr self, UInt64 lobby, Int32 index);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] [return: MarshalAs(UnmanagedType.I1)]
        public delegate Boolean D_SetLobbyData(IntPtr self, UInt64 lobby, Byte[] key, Byte[] value);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] public delegate IntPtr D_GetLobbyData(IntPtr self, UInt64 lobby, Byte[] key);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] public delegate void D_SetLobbyMemberData(IntPtr self, UInt64 lobby, Byte[] key, Byte[] value);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] public delegate IntPtr D_GetLobbyMemberData(IntPtr self, UInt64 lobby, UInt64 user, Byte[] key);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] [return: MarshalAs(UnmanagedType.I1)]
        public delegate Boolean D_SteamIdIntBool(IntPtr self, UInt64 steamId, Int32 value);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] [return: MarshalAs(UnmanagedType.I1)]
        public delegate Boolean D_SteamId2Bool(IntPtr self, UInt64 steamId1, UInt64 steamId2);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] public delegate UInt64 D_SelfToU64(IntPtr self);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] public delegate UInt32 D_SelfToU32(IntPtr self);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] [return: MarshalAs(UnmanagedType.I1)]
        public delegate Boolean D_SelfToBool(IntPtr self);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] public delegate IntPtr D_SelfToPtr(IntPtr self);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] public delegate IntPtr D_SteamIdToPtr(IntPtr self, UInt64 steamId);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] public delegate Int32 D_IntToInt(IntPtr self, Int32 value);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] public delegate UInt64 D_IntIntToU64(IntPtr self, Int32 a, Int32 b);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] [return: MarshalAs(UnmanagedType.I1)]
        public delegate Boolean D_GetFriendGamePlayed(IntPtr self, UInt64 steamId, out FriendGameInfo info);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] [return: MarshalAs(UnmanagedType.I1)]
        public delegate Boolean D_IsAPICallCompleted(IntPtr self, UInt64 call, [MarshalAs(UnmanagedType.I1)] out Boolean failed);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] [return: MarshalAs(UnmanagedType.I1)]
        public delegate Boolean D_GetAPICallResult(IntPtr self, UInt64 call, IntPtr result, Int32 resultSize, Int32 expectedCallback, [MarshalAs(UnmanagedType.I1)] out Boolean failed);
        #endregion

        #region Structs
        [StructLayout(LayoutKind.Sequential, Pack = 8)]
        public struct P2PSessionState
        {
            public Byte ConnectionActive;
            public Byte Connecting;
            public Byte P2PSessionError;
            public Byte UsingRelay;
            public Int32 BytesQueuedForSend;
            public Int32 PacketsQueuedForSend;
            public UInt32 RemoteIP;
            public UInt16 RemotePort;
        }

        [StructLayout(LayoutKind.Sequential, Pack = 8)]
        public struct FriendGameInfo
        {
            public UInt64 GameId;
            public UInt32 GameIP;
            public UInt16 GamePort;
            public UInt16 QueryPort;
            public UInt64 SteamIdLobby;
        }
        #endregion

        #region Enums / constants
        public const Int32 P2PSendUnreliable = 0;
        public const Int32 P2PSendUnreliableNoDelay = 1;
        public const Int32 P2PSendReliable = 2;
        public const Int32 P2PSendReliableWithBuffering = 3;

        public const Int32 LobbyTypePrivate = 0;
        public const Int32 LobbyTypeFriendsOnly = 1;
        public const Int32 LobbyTypePublic = 2;

        public const Int32 FriendFlagImmediate = 0x04;

        // Callback ids (k_iSteam*Callbacks + n)
        public const Int32 CB_GameOverlayActivated = 331;
        public const Int32 CB_GameLobbyJoinRequested = 333;
        public const Int32 CB_LobbyEnter = 504;
        public const Int32 CB_LobbyChatUpdate = 506;
        public const Int32 CB_LobbyCreated = 513;
        public const Int32 CB_P2PSessionRequest = 1202;
        public const Int32 CB_P2PSessionConnectFail = 1203;
        #endregion

        #region Resolved functions
        public static D_Void RunCallbacks;
        public static D_RegisterCallback RegisterCallback;
        public static D_UnregisterCallback UnregisterCallback;

        public static D_SendP2PPacket SendP2PPacket;
        public static D_IsP2PPacketAvailable IsP2PPacketAvailable;
        public static D_ReadP2PPacket ReadP2PPacket;
        public static D_SteamIdBool AcceptP2PSessionWithUser;
        public static D_SteamIdBool CloseP2PSessionWithUser;
        public static D_GetP2PSessionState GetP2PSessionState;
        public static D_BoolArgBool AllowP2PPacketRelay;

        public static D_CreateLobby CreateLobby;
        public static D_SteamIdToU64 JoinLobby;
        public static D_SteamIdVoid LeaveLobby;
        public static D_SteamIdInt GetNumLobbyMembers;
        public static D_GetLobbyMemberByIndex GetLobbyMemberByIndex;
        public static D_SteamIdToU64 GetLobbyOwner;
        public static D_SetLobbyData SetLobbyData;
        public static D_GetLobbyData GetLobbyData;
        public static D_SetLobbyMemberData SetLobbyMemberData;
        public static D_GetLobbyMemberData GetLobbyMemberData;
        public static D_SteamIdIntBool SetLobbyMemberLimit;
        public static D_SteamIdIntBool SetLobbyType;
        public static D_SteamId2Bool InviteUserToLobby;

        public static D_SelfToU64 GetSteamID;
        public static D_SelfToPtr GetPersonaName;
        public static D_SteamIdToPtr GetFriendPersonaName;
        public static D_IntToInt GetFriendCount;
        public static D_IntIntToU64 GetFriendByIndex;
        public static D_GetFriendGamePlayed GetFriendGamePlayed;
        public static D_SteamIdVoid ActivateGameOverlayInviteDialog;

        public static D_SelfToU32 GetAppID;
        public static D_SelfToBool IsOverlayEnabled;
        public static D_IsAPICallCompleted IsAPICallCompleted;
        public static D_GetAPICallResult GetAPICallResult;
        #endregion

        private static IntPtr _module;

        /// <summary>Resolves everything. Requires the game to have initialised Steam already (SteamSdkWrapper.IsInitialized).</summary>
        public static Boolean TryLoad()
        {
            if (IsLoaded)
                return true;
            _module = GetModuleHandleW("steam_api64.dll");
            if (_module == IntPtr.Zero)
            {
                Log.Warning("[Online] steam_api64.dll is not loaded in the process (has the game initialised Steam?)");
                return false;
            }
            StringBuilder path = new StringBuilder(1024);
            GetModuleFileNameW(_module, path, (UInt32)path.Capacity);
            ModulePath = path.ToString();
            Log.Message($"[Online] Using steam_api64.dll already loaded at {ModulePath}");

            D_Ptr steamClient = Get<D_Ptr>("SteamClient");
            D_Int getHSteamUser = Get<D_Int>("SteamAPI_GetHSteamUser");
            D_Int getHSteamPipe = Get<D_Int>("SteamAPI_GetHSteamPipe");
            RunCallbacks = Get<D_Void>("SteamAPI_RunCallbacks");
            RegisterCallback = Get<D_RegisterCallback>("SteamAPI_RegisterCallback");
            UnregisterCallback = Get<D_UnregisterCallback>("SteamAPI_UnregisterCallback");

            Client = steamClient();
            Int32 hUser = getHSteamUser();
            Int32 hPipe = getHSteamPipe();
            Log.Message($"[Online] SteamClient()=0x{Client.ToInt64():X} hUser={hUser} hPipe={hPipe}");
            if (Client == IntPtr.Zero || hUser == 0 || hPipe == 0)
            {
                Log.Warning("[Online] Steam is not initialised in this module instance; aborting");
                return false;
            }

            D_GetInterface getNetworking = Get<D_GetInterface>("SteamAPI_ISteamClient_GetISteamNetworking");
            D_GetInterface getMatchmaking = Get<D_GetInterface>("SteamAPI_ISteamClient_GetISteamMatchmaking");
            D_GetInterface getUser = Get<D_GetInterface>("SteamAPI_ISteamClient_GetISteamUser");
            D_GetInterface getFriends = Get<D_GetInterface>("SteamAPI_ISteamClient_GetISteamFriends");
            D_GetClientInterface getUtils = Get<D_GetClientInterface>("SteamAPI_ISteamClient_GetISteamUtils");
            Networking = getNetworking(Client, hUser, hPipe, Utf8(NetworkingVersion));
            Matchmaking = getMatchmaking(Client, hUser, hPipe, Utf8(MatchmakingVersion));
            User = getUser(Client, hUser, hPipe, Utf8(UserVersion));
            Friends = getFriends(Client, hUser, hPipe, Utf8(FriendsVersion));
            Utils = getUtils(Client, hPipe, Utf8(UtilsVersion));
            Log.Message($"[Online] Interfaces: Networking=0x{Networking.ToInt64():X} Matchmaking=0x{Matchmaking.ToInt64():X} User=0x{User.ToInt64():X} Friends=0x{Friends.ToInt64():X} Utils=0x{Utils.ToInt64():X}");
            if (Networking == IntPtr.Zero || Matchmaking == IntPtr.Zero || User == IntPtr.Zero || Friends == IntPtr.Zero || Utils == IntPtr.Zero)
            {
                Log.Warning("[Online] Could not get every Steam interface; aborting");
                return false;
            }

            SendP2PPacket = Get<D_SendP2PPacket>("SteamAPI_ISteamNetworking_SendP2PPacket");
            IsP2PPacketAvailable = Get<D_IsP2PPacketAvailable>("SteamAPI_ISteamNetworking_IsP2PPacketAvailable");
            ReadP2PPacket = Get<D_ReadP2PPacket>("SteamAPI_ISteamNetworking_ReadP2PPacket");
            AcceptP2PSessionWithUser = Get<D_SteamIdBool>("SteamAPI_ISteamNetworking_AcceptP2PSessionWithUser");
            CloseP2PSessionWithUser = Get<D_SteamIdBool>("SteamAPI_ISteamNetworking_CloseP2PSessionWithUser");
            GetP2PSessionState = Get<D_GetP2PSessionState>("SteamAPI_ISteamNetworking_GetP2PSessionState");
            AllowP2PPacketRelay = Get<D_BoolArgBool>("SteamAPI_ISteamNetworking_AllowP2PPacketRelay");

            CreateLobby = Get<D_CreateLobby>("SteamAPI_ISteamMatchmaking_CreateLobby");
            JoinLobby = Get<D_SteamIdToU64>("SteamAPI_ISteamMatchmaking_JoinLobby");
            LeaveLobby = Get<D_SteamIdVoid>("SteamAPI_ISteamMatchmaking_LeaveLobby");
            GetNumLobbyMembers = Get<D_SteamIdInt>("SteamAPI_ISteamMatchmaking_GetNumLobbyMembers");
            GetLobbyMemberByIndex = Get<D_GetLobbyMemberByIndex>("SteamAPI_ISteamMatchmaking_GetLobbyMemberByIndex");
            GetLobbyOwner = Get<D_SteamIdToU64>("SteamAPI_ISteamMatchmaking_GetLobbyOwner");
            SetLobbyData = Get<D_SetLobbyData>("SteamAPI_ISteamMatchmaking_SetLobbyData");
            GetLobbyData = Get<D_GetLobbyData>("SteamAPI_ISteamMatchmaking_GetLobbyData");
            SetLobbyMemberData = Get<D_SetLobbyMemberData>("SteamAPI_ISteamMatchmaking_SetLobbyMemberData");
            GetLobbyMemberData = Get<D_GetLobbyMemberData>("SteamAPI_ISteamMatchmaking_GetLobbyMemberData");
            SetLobbyMemberLimit = Get<D_SteamIdIntBool>("SteamAPI_ISteamMatchmaking_SetLobbyMemberLimit");
            SetLobbyType = Get<D_SteamIdIntBool>("SteamAPI_ISteamMatchmaking_SetLobbyType");
            InviteUserToLobby = Get<D_SteamId2Bool>("SteamAPI_ISteamMatchmaking_InviteUserToLobby");

            GetSteamID = Get<D_SelfToU64>("SteamAPI_ISteamUser_GetSteamID");
            GetPersonaName = Get<D_SelfToPtr>("SteamAPI_ISteamFriends_GetPersonaName");
            GetFriendPersonaName = Get<D_SteamIdToPtr>("SteamAPI_ISteamFriends_GetFriendPersonaName");
            GetFriendCount = Get<D_IntToInt>("SteamAPI_ISteamFriends_GetFriendCount");
            GetFriendByIndex = Get<D_IntIntToU64>("SteamAPI_ISteamFriends_GetFriendByIndex");
            GetFriendGamePlayed = Get<D_GetFriendGamePlayed>("SteamAPI_ISteamFriends_GetFriendGamePlayed");
            ActivateGameOverlayInviteDialog = Get<D_SteamIdVoid>("SteamAPI_ISteamFriends_ActivateGameOverlayInviteDialog");

            GetAppID = Get<D_SelfToU32>("SteamAPI_ISteamUtils_GetAppID");
            IsOverlayEnabled = Get<D_SelfToBool>("SteamAPI_ISteamUtils_IsOverlayEnabled");
            IsAPICallCompleted = Get<D_IsAPICallCompleted>("SteamAPI_ISteamUtils_IsAPICallCompleted");
            GetAPICallResult = Get<D_GetAPICallResult>("SteamAPI_ISteamUtils_GetAPICallResult");

            IsLoaded = true;
            UInt64 me = GetSteamID(User);
            // Sanity check of the CSteamID-as-uint64 return convention: an individual account in the public
            // universe has universe 1 (bits 56-63) and account type 1 (bits 52-55)
            Boolean idLooksValid = (me >> 52) == 0x011;
            Log.Message($"[Online] Local user {Tag(me)}, AppID {GetAppID(Utils)}, overlay enabled: {IsOverlayEnabled(Utils)}, SteamID check: {(idLooksValid ? "OK" : "UNEXPECTED (return-value marshalling?)")}");
            return true;
        }

        /// <summary>
        /// A short, log-safe tag for a Steam user: "#" + the last 4 digits of the account number. Use it in every log
        /// line; Steam names (GetName) are for the on-screen status only, since logs get shared and quoted in docs.
        /// </summary>
        public static String Tag(UInt64 steamId)
        {
            return "#" + ((steamId & 0xFFFFFFFF) % 10000).ToString("D4");
        }

        /// <summary>The user's Steam name. On screen only, never in logs (see <see cref="Tag"/>).</summary>
        public static String GetName(UInt64 steamId)
        {
            if (!IsLoaded)
                return steamId.ToString();
            if (steamId == GetSteamID(User))
                return FromUtf8(GetPersonaName(Friends));
            return FromUtf8(GetFriendPersonaName(Friends, steamId));
        }

        public static Byte[] Utf8(String s)
        {
            Byte[] bytes = Encoding.UTF8.GetBytes(s);
            Byte[] result = new Byte[bytes.Length + 1];
            Buffer.BlockCopy(bytes, 0, result, 0, bytes.Length);
            return result;
        }

        public static String FromUtf8(IntPtr ptr)
        {
            if (ptr == IntPtr.Zero)
                return String.Empty;
            Int32 length = 0;
            while (Marshal.ReadByte(ptr, length) != 0)
                length++;
            Byte[] bytes = new Byte[length];
            Marshal.Copy(ptr, bytes, 0, length);
            return Encoding.UTF8.GetString(bytes);
        }

        // RULE (docs/online/CONTRIBUTING.md, rule 1): every native Steam call goes through these bindings.
        // Unity 5.2's Mono caches the managed-to-native wrapper of GetDelegateForFunctionPointer by function
        // address only. In steam_api64.dll the flat functions are tiny "jump to vtable slot N" stubs, and the
        // linker folded identical ones together: e.g. SteamAPI_ISteamClient_GetISteamUtils and
        // SteamAPI_ISteamUtils_GetAppID (and 14 others) share one address. Binding both directly makes the
        // second call reuse the first one's marshalling (byte[] argument and all) and crashes the game.
        // So each binding gets its own address: a 16-byte stub "jmp qword ptr [rip+0]; dq target".
        private const Int32 StubSize = 16;
        private const Int32 StubPageSize = 4096;
        private static IntPtr _stubPage;
        private static Int32 _stubOffset = StubPageSize;

        private static IntPtr MakeStub(IntPtr target)
        {
            if (_stubOffset + StubSize > StubPageSize)
            {
                _stubPage = VirtualAlloc(IntPtr.Zero, (UIntPtr)StubPageSize, 0x3000 /* MEM_COMMIT|MEM_RESERVE */, 0x04 /* PAGE_READWRITE */);
                if (_stubPage == IntPtr.Zero)
                    throw new OutOfMemoryException("VirtualAlloc failed for the Steam call stubs");
                _stubOffset = 0;
            }
            IntPtr stub = new IntPtr(_stubPage.ToInt64() + _stubOffset);
            _stubOffset += StubSize;
            VirtualProtect(_stubPage, (UIntPtr)StubPageSize, 0x04 /* PAGE_READWRITE */, out _);
            Marshal.WriteByte(stub, 0, 0xFF); // jmp qword ptr [rip+0]
            Marshal.WriteByte(stub, 1, 0x25);
            Marshal.WriteInt32(stub, 2, 0);
            Marshal.WriteInt64(stub, 6, target.ToInt64());
            Marshal.WriteInt16(stub, 14, unchecked((Int16)0xCCCC)); // int3 padding
            VirtualProtect(_stubPage, (UIntPtr)StubPageSize, 0x20 /* PAGE_EXECUTE_READ */, out _);
            FlushInstructionCache(GetCurrentProcess(), stub, (UIntPtr)StubSize);
            return stub;
        }

        private static T Get<T>(String name) where T : class
        {
            IntPtr proc = GetProcAddress(_module, name);
            if (proc == IntPtr.Zero)
                throw new EntryPointNotFoundException($"steam_api64.dll has no export '{name}'");
            return Marshal.GetDelegateForFunctionPointer(MakeStub(proc), typeof(T)) as T;
        }
    }
}
