# Spike A: Steam networking from Memoria — findings

Branch `steam-spike-a` (off `main` at `c716e1fa`). Code: `Assembly-CSharp/Memoria/Online/`.

**Status: single-PC smoke test passed (2026-11-16, third build); two-PC test pending.** Claims still marked
*(to verify in game)* are answered by the two-PC test in [How to test](#how-to-test). The verdict is at the end.

### Single-PC results

- Bindings: all interfaces non-null, `SteamID check: OK`, AppID 377840, `AllowP2PPacketRelay(true)` → true.
- The game's pump starts at the title screen ~1 s after Steam init; `IsOverlayEnabled` is `False` at startup and
  `True` from then on.
- `Ctrl+Shift+H`: `CreateLobby` call result polled fine; lobby created, owner = self, member list correct.
- **Callback emulation works:** `LobbyEnter_t` and `GameOverlayActivated_t` (active true/false) arrived through
  the emulated `CCallbackBase`, in slot 1 (no "call-result slot" warning), so the reversed vtable order is right and
  the game's pump does run `SteamAPI_RunCallbacks`.
- `Ctrl+Shift+I`: `ActivateGameOverlayInviteDialog` opened the Steam overlay's invite dialog.
- The overlay is injected (`gameoverlayrenderer64.dll` is loaded) and works both from code and from its hotkey.
  (Shift+Tab is only Steam's default; the tester's shortcut is Alt+Caps Lock. Use whatever Steam → Settings →
  In Game says.)

## A1. How FF9 already talks to Steam

- **There is no managed Steam wrapper.** `x64/FF9_Data/Managed` contains no `Steamworks.NET.dll` / `CSteamworks.dll`.
  The game's Steam code is `SteamSdkWrapper` (`Assembly-CSharp/Global/Steam/SteamSdkWrapper.cs`), which P/Invokes a
  small native library from Square Enix, `x64/FF9_Data/Plugins/steamwrapper.dll` (18 KB). Its 13 exports cover
  achievements, stats and cloud only:
  `Init`, `Shutdown_Internal`, `ProcessCallbacks_Internal`, `SteamAPIRestartAppIfNecessary`, `RequestStats`,
  `TriggerAchievement_Internal`, `UpdateStatProgress_Internal`, `ClearAchievement_Internal`, `IsCloudEnabled_Internal`,
  `RegisterStatsReceivedCallback`, `RegisterStatsStoredCallback`, `RegisterAchievementStoredCallback`,
  `SetSendAchievementToCS`. Nothing for networking or matchmaking, so A3 option 1 ("reuse a bundled wrapper") is out.
- **Initialisation:** `BundleSceneSelector.Awake` (`Global/Bundle/BundleSceneSelector.cs`) calls
  `SteamAPIRestartAppIfNecessary(377840)` then `SteamSdkWrapper.TryInitialize()` → `steamwrapper!Init`, which
  calls `SteamAPI_Init` inside `steam_api64.dll`. The spike never calls `SteamAPI_Init`; it waits for
  `SteamSdkWrapper.IsInitialized`.
- **Callback pump:** `SceneDirector.Update` (`Assets/Scripts/Common/SceneDirector.cs`) calls
  `SiliconStudio.Social.ProcessCallbacks()` → `SteamLocalUser.ProcessCallbacks()` →
  `steamwrapper!ProcessCallbacks_Internal` (presumably `SteamAPI_RunCallbacks`), `FPSManager.MainLoopUpdateCount`
  times per frame. **But only after `Social.InitializeSocialPlatform()`**, which `TitleUI` calls on the title screen
  (`Social._hasCreated`). Before that, nothing pumps callbacks, **and nothing may**: the second test run pumped
  `SteamAPI_RunCallbacks` from the spike before the title screen and crashed in `steamwrapper.dll+0x1555`. That code
  (`mov cl,1; call [rsi+10h]`) is Square Enix's stats-received handler calling the game's C# delegate with `true`,
  with no null check; the delegate is only registered by `Social.Authenticate` at the title screen. So the spike
  never pumps; it only logs when the game starts (`Social.IsCreated`, a one-line accessor added to
  `SiliconStudio/Social.cs`). Consequence: Steam callbacks (overlay invites, logging callbacks) only work from the
  title screen on. Call results, membership and packets are polled, so they work from startup.
  *(to verify in game: that `ProcessCallbacks_Internal` really runs `SteamAPI_RunCallbacks`; if no callback ever
  fires after the title screen, it doesn't.)*
- `ISteamUtils::IsOverlayEnabled` returned `False` at startup on the first successful run (the overlay hooks the
  renderer a little later); it's logged again when callbacks go live and when the invite dialog opens.
- **Two copies of `steam_api64.dll`**, byte-identical (md5 `34c73cab…`): `x64/steam_api64.dll` and
  `x64/FF9_Data/Plugins/steam_api64.dll`. Also a 32-bit `Plugins/steam_api.dll` (unused: Memoria is x64 only).
  This matters: a plain `[DllImport("steam_api64")]` from C# could make Mono load the `Plugins` copy as a *second*
  module instance, whose SteamAPI globals were never initialised (`SteamClient()` would return null). The spike
  avoids the question: it binds with `GetModuleHandle("steam_api64.dll")` + `GetProcAddress` on the module
  `steamwrapper.dll` already loaded and initialised, and logs that module's path.
- Unity is 5.2.3p2 (string in `FF9.exe`), Mono runtime, game code built for .NET 3.5.

## A2. Interface pointers, packets

- Interface version strings in the shipped DLL: `SteamClient017`, `SteamNetworking005`, `SteamMatchMaking009`
  (capital M), `SteamUser018`, `SteamFriends015`, `SteamUtils007`. No `SteamNetworkingSockets`/`Messages`.
- Interfaces come from `SteamClient()` → `SteamAPI_ISteamClient_GetISteam{Networking,Matchmaking,User,Friends}(client,
  hUser, hPipe, version)` and `GetISteamUtils(client, hPipe, version)`, with `SteamAPI_GetHSteamUser/Pipe`
  (`SteamNative.TryLoad`).
- Marshalling (`SteamNative.cs`): every flat function takes the interface pointer first. `CSteamID` and
  `SteamAPICall_t` are `UInt64` (by value in a register on x64). Flat functions returning a `CSteamID`
  (`GetSteamID`, `GetLobbyOwner`, `GetLobbyMemberByIndex`, `GetFriendByIndex`) return `uint64`. C++ `bool` is one
  byte, so every bool return/argument is `MarshalAs(I1)` (the default 4-byte BOOL would read garbage).
  `EP2PSend` is an `Int32`. Strings are passed as null-terminated UTF-8 `Byte[]`, returned `const char*` decoded as
  UTF-8. A startup sanity check verifies the local SteamID has universe 1 / account type 1, which would fail if the
  `uint64` return convention were wrong. *(to verify in game: log line `SteamID check: OK`)*
- **Gotcha found in game (first run crashed):** the flat functions in this `steam_api64.dll` are tiny "jump to
  vtable slot N" stubs, and the linker folded identical ones together, so many exports share one address (e.g.
  `SteamAPI_ISteamClient_GetISteamUtils`, `SteamAPI_ISteamUtils_GetAppID` and 14 others are all at RVA `0x21F0`).
  Unity 5.2's Mono caches the native-call wrapper of `Marshal.GetDelegateForFunctionPointer` **by address only**,
  so calling `GetAppID` reused the `GetISteamUtils` wrapper (with its `byte[]` argument) and crashed in
  `mono_array_to_lparray`. Fix: every binding goes through its own 16-byte `jmp [rip+0]` stub in executable memory
  (`SteamNative.MakeStub`), so every function has a unique address. Any future native binding in this codebase via
  `GetDelegateForFunctionPointer` has the same trap.
- `AllowP2PPacketRelay(true)` is called once (`SteamTransport` constructor).
- Channels: 0 control (reliable), 1 movement (unreliable, ≤1200 bytes), 2 state (reliable). Receiving is pure polling
  (`IsP2PPacketAvailable` / `ReadP2PPacket` per channel), no callbacks needed. *(to verify in game)*

## A3. Callbacks and call results

The approach, in order of what it relies on:

1. **Call results are polled, not registered.** `CreateLobby` and `JoinLobby` return a `SteamAPICall_t`;
   `SteamLobby.PollCallResult` checks `ISteamUtils::IsAPICallCompleted` each frame and reads `LobbyCreated_t` /
   `LobbyEnter_t` with `GetAPICallResult`. No vtable emulation, no dependency on the pump.
2. **Membership is polled** (`GetNumLobbyMembers` / `GetLobbyMemberByIndex` / `GetLobbyOwner` every 0.25 s, and
   immediately when a packet arrives from an unknown sender), so `LobbyChatUpdate_t` isn't needed.
3. **P2P sessions open from both sides:** when a member appears, each side calls `AcceptP2PSessionWithUser` and sends
   a reliable hello, so `P2PSessionRequest_t` isn't needed either.
4. **Only `GameLobbyJoinRequested_t` truly needs a callback** (a friend accepts an invite through the overlay while
   the game is running). For it, `SteamCallback` (`SteamCallbacks.cs`) emulates the C++ `CCallbackBase`, as old
   Steamworks.NET did: a 16-byte unmanaged object `{ vtable*, uint8 flags, int32 iCallback }` registered with
   `SteamAPI_RegisterCallback` (exported, as are `UnregisterCallback`, `RegisterCallResult`,
   `UnregisterCallResult`). The vtable holds three delegate thunks **in MSVC order, which reverses overloads**:
   `[0] Run(param, ioFailure, hCall)`, `[1] Run(param)`, `[2] GetCallbackSizeBytes()`. Delegates are kept alive in a
   static list. If a callback ever arrives through slot 0, it logs a warning (the order would be wrong) and still
   dispatches.

The same mechanism also registers `LobbyChatUpdate_t`, `LobbyEnter_t`, `GameOverlayActivated_t`,
`P2PSessionRequest_t` and `P2PSessionConnectFail_t`, for logging/diagnostics. `GameOverlayActivated_t` fires on
the overlay shortcut, which makes it the cheapest single-PC proof that callbacks work. (confirmed in the single-PC test)

Limits: x64 only (x86 would need real `thiscall` thunks; Memoria dropped x86 anyway). Struct offsets are read by
hand with the Windows pack-8 layout (`LobbyEnter_t.m_EChatRoomEnterResponse` at +16, etc.).

## A4. Join flow

- **Host** (`Ctrl+Shift+H`): `CreateLobby(FriendsOnly, 4)` → on `LobbyCreated_t`: `SetLobbyMemberLimit(4)`,
  `SetLobbyData("mod","ff9sequel")`, `SetLobbyData("proto","1")`. The lobby ID is logged and copied to the clipboard.
- **Invite** (`Ctrl+Shift+I`): `ActivateGameOverlayInviteDialog(lobby)` opens the overlay's invite list directly.
  `ISteamUtils::IsOverlayEnabled` is logged at startup.
- **Join**, three ways:
  1. Overlay invite accepted while the game runs → `GameLobbyJoinRequested_t` → `JoinLobby`.
  2. Invite accepted while the game isn't running → Steam starts it with `+connect_lobby <id>`; the spike checks the
     command line. *(likely broken under Memoria: Steam launches `FF9_Launcher.exe`, which may not pass the argument
     on to `FF9.exe`; the full command line is logged so we can tell)*
  3. **Fallback without the overlay** (`Ctrl+Shift+J`): if the clipboard holds a lobby ID (paste it from the host
     via Discord etc.) it joins that; otherwise it scans Steam friends with `GetFriendGamePlayed` and joins the first
     friend who is in an FF9 lobby. (`RequestLobbyList` was not used: friends-only lobbies don't appear in lobby
     searches.)
- On joining: the lobby owner is the host (`ITransport.HostPeer`); a P2P session is opened with every member.
- Leave: `Ctrl+Shift+L`.

## Prototype

| File | What |
|---|---|
| `ITransport.cs` | `ITransport` (`Send(peer, channel, bytes, length, reliable)`, `Poll(list)`, `PeerJoined`/`PeerLeft`, `Peers`, `HostPeer`) and channel constants. Peers are `UInt64` ids, so a LiteNetLib transport can hand out its own. |
| `SteamNative.cs` | Bindings via `GetProcAddress` on the already-loaded `steam_api64.dll`. |
| `SteamCallbacks.cs` | `CCallbackBase` emulation. |
| `SteamLobby.cs` | Lobby helper: create, join (3 ways), invite overlay, member polling, host = owner. |
| `SteamTransport.cs` | `ITransport` over `SteamNetworking005`. |
| `SteamSpike.cs` | Test driver: hotkeys, ping/RTT, channel tests, on-screen status, log lines. Attached in `SceneDirector.Awake`; dormant until used. |

Traffic once connected: a reliable ping every second on channel 0 (echoed; RTT logged every second per peer), 15 Hz
48-byte unreliable packets on channel 1 (loss measured), a reliable sequenced packet every 2 s on channel 2 (order
checked). A peer silent for 5 s is logged as `STALLED`. A `SUMMARY` line per peer every 30 s, and `FINAL` on quit.

## Dead ends / rejected options

- **Bundled wrapper:** none exists (A1).
- **`SteamNetworkingSockets` / `SteamNetworkingMessages`:** not in this DLL (SDK ~1.35).
- **Plain `DllImport("steam_api64")`:** rejected because of the duplicate DLL in `Plugins` (A1). Not tested whether
  Mono would actually load the second copy; `GetProcAddress` makes it moot.
- **Registering call results with `SteamAPI_RegisterCallResult`:** possible with the same vtable trick, but polling
  `IsAPICallCompleted` is simpler and doesn't depend on the game pumping callbacks.
- **`GetDelegateForFunctionPointer` straight on the exports:** crashed the game (folded exports + Mono's
  per-address wrapper cache, see A2). Fixed with per-function stubs.
- **`RequestLobbyList` as the join fallback:** friends-only lobbies aren't listed; would need a public lobby.

## How to test

### Build and install (first PC)

```powershell
cd ..\Memoria-SteamSpike
.\tools\multiplayer\build.ps1
.\tools\multiplayer\install.ps1
```

`install.ps1` copies `Assembly-CSharp.dll`, `Memoria.Prime.dll` and `XInputDotNetPure.dll` into the game. It reuses
the existing backup in `..\MemoriaMultiplayerBackup`. To go back to the local multiplayer build, run the same two
scripts from the `Memoria-Multiplayer` checkout.

Second PC: it needs the same Memoria build (the upstream Canary matching `main` at `c716e1fa`), then copy
`Output\Assembly-CSharp.dll` and `Output\Memoria.Prime.dll` into its `x64\FF9_Data\Managed\` (back up first).
`Memoria.ini` needs no change.

### Step 1: single PC smoke test (5 minutes)

1. Start the game through Steam. The status box appears top-left once Steam is up.
2. In `Memoria.log`, check: `Using steam_api64.dll already loaded at …`, `Interfaces: …` (no zeros),
   `SteamID check: OK`, `overlay enabled: True`.
3. Wait for the title screen: the log says `The game now pumps SteamAPI_RunCallbacks; callbacks are live`.
4. Press the Steam overlay shortcut (Shift+Tab by default) twice: expect `Callback GameOverlayActivated_t: active=True` / `False`. This proves the
   callback emulation and that the game's pump runs `SteamAPI_RunCallbacks`.
5. Press **Ctrl+Shift+H**: expect `Lobby … created; you are the host` and `Lobby member joined: <you>`.
6. Press **Ctrl+Shift+I**: the Steam overlay's invite dialog should open.
7. Load a save and repeat step 4 in a field.

Send me `Memoria.log` if any of these fail.

### Step 2: two PCs, two Steam accounts (the "done" test)

The two accounts must be Steam friends.

1. Both start the game through Steam and load a save (or stay on the title screen).
2. PC 1: **Ctrl+Shift+H**, then **Ctrl+Shift+I** and invite the other account.
3. PC 2: accept the invite from the Steam overlay or the Steam friends chat. Expect `GameLobbyJoinRequested_t`, then
   `Joined lobby …; host is …`.
   - If that doesn't work: on PC 2 press **Ctrl+Shift+J** (joins a friend's FF9 lobby), or paste PC 1's lobby ID
     (in its clipboard and log) into PC 2's clipboard first, then **Ctrl+Shift+J**.
   - Also try once with PC 2's game *closed* when accepting the invite: does Steam start the game and join?
     (`Command line: …` in the log shows whether `+connect_lobby` got through.)
4. Leave both running **at least 5 minutes**. Both logs should show a `Connected to 1 peer(s) … rtt …` line every
   second, no `STALLED`, and `SUMMARY` lines every 30 s.
5. Quit both games normally and send me both `Memoria.log` files (the `FINAL` lines have the totals).

Pass: steps 2-4 work from inside FF9, no stall in 5 minutes, ping/pong counts match, channel 2 has
0 out of order.

## Verdict (provisional, pending the test)

**Go, provisionally.** Everything the plan needs is exported by the shipped DLL, and the design leans on polling
where it can, so only one feature (joining from an overlay invite while the game runs) depends on the riskiest piece,
the callback emulation. If that one piece fails, the clipboard / friend-lobby fallback still covers joining.

Risks:
- **Callback emulation** (vtable order, struct packing, Mono reverse-P/Invoke): checked in step 1.3. Fallback is the
  `Ctrl+Shift+J` join; no LiteNetLib needed for that.
- **Callback pump**: we depend on the game's pump (step 1.4); pumping ourselves before the title screen crashes
  the game (A1). If the game's pump turns out not to call `SteamAPI_RunCallbacks`, we can pump ourselves *after*
  `Social.IsCreated`.
- **Invite while the game is closed** probably needs `FF9_Launcher.exe` to forward `+connect_lobby`.
- **Legacy P2P is deprecated by Valve** (`ISteamNetworking` is "legacy" since 2020) but still served by the Steam
  client; nothing to do about it while we can't ship a newer `steam_api64.dll`.
- **Unreliable packets ≤ 1200 bytes**; reliable sends are buffered and can stall behind a lost packet (fine for
  Ghosts, worth knowing for shared state).
- Everyone must own FF9 on Steam (P2P runs under App ID 377840); no Steam, no online.
