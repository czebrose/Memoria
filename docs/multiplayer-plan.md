# Handoff: local multiplayer (up to 4 players) for FF9 via a Memoria fork

This is a self-contained brief. Read it fully, then `CLAUDE.md` (project conventions) before starting.

## Goal

Up to **4 players on one PC**, each controlling their own party members:
- **Battles:** each player runs the command menus of their own characters.
- **Fields:** each player walks their own character on a shared screen.
- **Online play:** comes for free through **Steam Remote Play Together**, because remote guests' controllers show up as local controllers. No networking code is needed.

This is a large feature. Build it in the phases below, and stop after each one for the user to test in game.

## Why a Memoria fork

- **Hades Workshop and Memoria's mod files can't do it:** the game merges all input into one controller and expects one controllable character.
- **The steam port has no usable 2-player mode:** the original PlayStation game had a 2-player battle mode, but the Steam port doesn't offer it.
- **Memoria builds the game's main code from source:** current Memoria builds the game's `Assembly-CSharp.dll` from editable sources, so the work is ordinary C# edits in a fork of https://github.com/Albeoris/Memoria.

## Environment

- **Machine:** Windows 11. Use PowerShell 5.1: there's no bash, and `&&` doesn't work.
- **Installed tools:** Git and the GitHub CLI are installed but not on PATH. Use their full paths: `C:\Program Files\Git\cmd\git.exe` and `C:\Program Files\GitHub CLI\gh.exe`. The CLI is signed in as user `Vuux2`.
- **Game folder:** `C:\Program Files (x86)\Steam\steamapps\common\FINAL FANTASY IX\`
  - Installed Memoria build: **2026-11-09** (see the first lines of `Memoria.log`).
  - Game code: `x64\FF9_Data\Managed\Assembly-CSharp.dll`, built by Memoria.
- **Current mod:** this repository, private on GitHub at https://github.com/Vuux2/ff9-black-waltz-mod.
  - It builds into `<game>\HadesWorkshopMod` with `powershell -ExecutionPolicy Bypass -File tools/build.ps1`.
  - It's turned on in `Memoria.ini` under `[Mod] FolderNames`.
- **The mod's content:** a new game starts on the Cargo Ship (fields 507 deck, 502 bridge, 504 engine room, 505 rear deck).
  - The party is Clove (Garnet's slot) and B.Waltz (Beatrix's slot, `CharacterId.Beatrix`).
  - It has NPCs, two shops, a save moogle, and repeatable battles from the moogle Mogwin.
  - This is the test area for multiplayer.

## Rules

- **Ask the user first** before installing a Memoria build into the game folder. Back up the files it replaces first, at least `x64\FF9_Data\Managed\` and `Memoria.ini`, and give the user a way back.
- **The project's other rule still applies:** never edit the game's own files by hand.
- **Put all multiplayer behaviour behind a config switch,** e.g. a `[Multiplayer]` section in `Memoria.ini` with `Enabled = 0` as the default. With it off, the game must behave exactly like upstream Memoria.
- **Keep changes small and in clearly marked places.** New code goes in its own namespace (e.g. `Memoria.Multiplayer`), so the fork can be rebased onto new Memoria releases.
- **Only the user can test in game.** After each phase, say exactly what to try and where, and what to look for in `Memoria.log`.
- **Saves must not change.** Player assignments live in the config, not in save files.

## What is already known from this mod's work (verified)

- **Inspecting the game code:** `Assembly-CSharp.dll` can be inspected with .NET reflection from PowerShell.
  - Load it with an `AssemblyResolve` handler pointing at `x64\FF9_Data\Managed`.
  - Resolve call and field tokens in method IL to read the logic.
  - A Memoria source checkout is better once you have one.
- **Battle command flow:**
  - `BattleHUD` (with `CurrentPlayerIndex`) opens the command menu for the character whose turn bar is full.
  - `btl_cmd.RunCommandFromQueue` runs queued commands.
- **`IOverloadOnCommandRunScript.OnCommandRun`** runs just before a queued command executes.
  - When this hook exists, Memoria **skips its built-in Heat check**, so the hook must repeat it.
  - Example: `WaltzAscendCommand` in `src/memoria/StreamingAssets/Scripts/Sources/BlackWaltzMorph.cs`.
- **B.Waltz's transformation:**
  - `BattleUnit.ChangeToMonster` gives him Black Waltz No. 3's moves.
  - All his moves are in `monster_transform.spell`, run as `spell[sub_no]` under `new_command`.
  - None of this depends on which controller chose the command.
- **Field scripts:**
  - Object **250** is "the player character". Talk, region and exit triggers all check against it.
  - `DefinePlayerCharacter()` in an entry's init makes that entry the player.
  - Scripts take control away with `DisableMove()` and give it back with `EnableMove()`; every cutscene does this.
- **Opcode arguments:** `SetCharacterData( char, updateLevel, equipSet, category, commandSet )` (EventEngine.DoEventCode calls `ff9play.FF9Play_Change`).
- **Field backgrounds are pre-drawn images** with fixed cameras, so split-screen is not practical. Everyone shares one screen.

## Unknowns to answer first (Phase 0)

1. **Where the game merges all input into one:**
   - Find the classes: probably `HonoInputManager`, `UIManager.Input`, `UIKeyTrigger`, or Memoria's own input code.
   - Find what reads the controllers (Unity's `Input` or XInput).
   - Check how Steam Input affects controllers being told apart.
2. **Where the field leader's movement code lives,** and whether it can drive more than one character. Look for the field actor or controller classes that apply walkmesh collision.
3. **How the event engine stores "the player character" (object 250),** and whether it can safely be pointed at another object for the length of one interaction.
4. **How Memoria builds and installs:**
   - Its build steps and output.
   - Which files in the game folder an install replaces.

Write the answers into this file under "Findings" before moving on.

## Phases

| Phase | Work | Done when |
|---|---|---|
| **0. Fork and setup** | Fork Memoria to the user's GitHub. Build it, and install it after backing up and asking. Add the config switch, and answer the unknowns above. | With the switch off, the game plays normally on the fork, and the mod still works. |
| **1. Separate controllers** | Read up to 4 controllers individually (XInput; the keyboard counts as P1). Add a central `MultiplayerManager`: which controller is which player, which player owns which character (from config), and which player has control right now. Route the game's input through it. Add a debug overlay showing each player's buttons and who has control, plus log lines. | The overlay shows P1–P4 separately. With multiplayer on, P1 still plays normally. |
| **2. Battles: each player runs their own characters' menus** | When `BattleHUD` opens a character's menu, give control to that character's owner. Only their controller can navigate and pick targets, and cycling to the next ready character stays within their characters. Show a "P1/P2" tag on the menu and name. | Two controllers: P1 can only command Clove and P2 only B.Waltz, in Mogwin's fights, including B.Waltz's Ascend and Waltz Mag. **First playable milestone.** |
| **3. Fields** | Sub-steps, each tested on the Cargo Ship. **3a** Extra players' characters appear next to the leader (their normal field models) and walk with the leader's movement and collision code. **3b** Camera stays on the leader; anyone off-screen or too far away is pulled back next to the leader. **3c** Talking: any player can press talk next to an NPC; their character briefly becomes object 250, the script runs, then it's restored. Whoever started a dialogue, shop or save controls it; everyone else is frozen until it ends. **3d** Only the leader triggers exits; others appear next to the leader in the new area. **3e** Cutscenes (control taken away): extra characters hide, then reappear next to the leader. World map: leader only. | Two players walk the Cargo Ship together, both can talk to NPCs and use shops and Cordell's save, area changes and Mogwin's battles work, and nothing gets stuck. |
| **4. Battles: menus at the same time (optional)** | Split `BattleHUD`'s single menu state (open menu, highlighted ability, target cursor) into one session per player, and draw up to 4 smaller panels. | Two players choose commands at the same time without interfering. |
| **5. Release** | Publish a release of the fork (and how to install it). Update this repo's README to require it, and document the `[Multiplayer]` settings. | A new user can install it from the README alone. |

Phases 0–2 are a contained first project. Phase 3 is the big unknown: prototype 3a on one field (507, the deck) before committing to the rest.

## Findings

Answered in Phase 0, against fork https://github.com/czebrose/Memoria at upstream `c716e1fa` (2026-11-09, the build that is installed). Paths are relative to the fork root.

### Fork setup

- **Repo:** `C:\Users\corde\Documents\Projects\FF9\Memoria-Multiplayer`. `origin` is the czebrose fork and `upstream` is Albeoris/Memoria.
- **Code markers:** fork changes to upstream files carry a `// [Multiplayer]` comment. New code lives in `Assembly-CSharp/Memoria/Multiplayer/` (namespace `Memoria.Multiplayer`).
- **Config switch:**
  - `[Multiplayer] Enabled = 0` is in `Assembly-CSharp/Memoria/Configuration/Memoria.ini` and `Memoria.Launcher/Ini/Memoria.ini`. Both are Windows-1252, so edit them byte-for-byte to keep the `« »` characters.
  - Accessed as `Configuration.Multiplayer.Enabled`. A missing section reads as the default (off), so existing `Memoria.ini` files need no change.
  - At startup `MultiplayerManager.Initialize()` logs `[Multiplayer] Enabled: False/True`, right after `[Initialization] Mods: ...`.

### Q4. How Memoria builds and installs

- **Full build:** `msbuild Memoria.sln` (see `.github/workflows/build.yml`).
  - Output goes to `Output\`.
  - `Memoria.Patcher` packs everything into one self-extracting `Memoria.Patcher.exe`, the release artifact.
- **Local builds deploy automatically:** when `CI` isn't `true`, `Memoria.Launcher` and `Memoria.Patcher` run the `Deploy` task (`Memoria.MSBuild/Deploy.cs`). It copies `Output\` into the game folder, located via the Steam registry key.
  - The files it writes are listed in `Memoria.MSBuild/DeploymentFileSet.cs`.
  - Main ones: `x64\FF9_Data\Managed\{Assembly-CSharp,Memoria.Prime,UnityEngine.UI,XInputDotNetPure,Newtonsoft.Json}.dll`, `FF9_Launcher.exe` (the Memoria launcher), the `StreamingAssets\` and `FF9_Data\` folders, plugin DLLs in `x64\FF9_Data\Plugins\`, and `Memoria.ini`/`Settings.ini` (only if missing).
  - **So always build with `/p:CI=true`.**
- **Reference DLLs:** `Directory.Build.targets` copies `mscorlib`, `UnityEngine`, `Assembly-CSharp-firstpass` and others *from* the game into `References\`. It only reads the game folder.
- **This machine's toolchain** (Build Tools 2026 + VS 2022 Community, partially installed) can't build the whole solution:
  - The .NET Framework 4.8 targeting pack is missing, which blocks the launcher, `Memoria.MSBuild` and the compiler.
  - The C++ v143 toolset is missing, which blocks `SaXAudio` and `Memoria.Injection`.
  - The .NET 10 SDK is missing, which blocks `FF9SpecialEffectPlugin`.
  - `References\Memoria.MSBuild.dll` ships prebuilt, so none of these are needed for game code.
- **Dev loop used instead:**
  - `tools/multiplayer/build.ps1` builds `Assembly-CSharp` (net35) plus `Memoria.Prime`, `XInputDotNetPure` and `UnityEngine.UI` into `Output\`, with `CI=true`.
  - It strips the two build-order-only project references (SaXAudio, effect plugin) through `SkipNativeRefs.targets`.
  - `tools/multiplayer/install.ps1` backs up `x64\FF9_Data\Managed\` and `Memoria.ini` to `..\MemoriaMultiplayerBackup\`, next to the repo and **outside the game folder**, on first run. It then copies `Assembly-CSharp.dll` and `Memoria.Prime.dll`. (The launcher treats any game subfolder that contains a `Memoria.ini` as a mod and adds it to `FolderNames`.)
  - `tools/multiplayer/restore.ps1` undoes it.
- **For a Phase 5 release,** either install the `.vsconfig` components, or let GitHub Actions on the fork build the patcher. That needs the `DEPENDENCIES_PASSWORD` secret for `References/Dependencies.7z`, which only upstream has, so local builds are the realistic route.

### Q1. Where input is merged into one

There are three layers. Everything ends up in a single set of `HonoInputManager` arrays.

1. **Device layer:**
   - **XInput:** `Memoria.XInputDotNetPure/GamePad.cs` `GamePad.GetState(PlayerIndex)` calls native XInput per slot 0–3, so the device layer *can* read four pads separately.
   - **PlayStation/Switch pads via JoyShockLibrary:** these are **OR-ed into whatever slot is asked for** (the loop at `GamePad.cs:359`). Every DS4/DualSense/Pro controller is merged.
   - **Unity's legacy `UnityEngine.Input`:** the `"Horizontal"`, `"Vertical"` and `"JoystickButtonN"` axes from the InputManager asset merge all DirectInput joysticks.
2. **Pad selection:**
   - `Assembly-CSharp/UnityXInput/XInputManager.cs` (`PersistenSingleton`) keeps **one** `playerIndex`. `CheckConnection()` latches onto the first connected slot 0–3 and stores `CurrentState`/`PreviousState` for that slot only.
   - `UnityXInput/Input.cs` (`GetXButton*`, `GetXAxis`) maps `"JoystickButton0..7"` and the triggers onto that single state, falling back to the keyboard and `UnityEngine.Input`.
   - Some places bypass it with `GamePad.GetState(PlayerIndex.One)` directly: `HonoInputManager.CheckPersistentInput` and `IsJoystickConnect`.
3. **Logical controls:**
   - `Global/Hono/HonoInputManager.cs` `Update()` → `ScanKey()`, `ScanDirectionKeys()`, `CheckPersistentInput()` and `ScanSources()` fill `isInput[]`/`isInputDown[]`/`isInputUp[]`, indexed by `Control` (Confirm, Cancel, Menu, Special, bumpers, triggers, Pause, Select, Up/Down/Left/Right). `GetAxis()` returns the stick.
   - **Every consumer reads these:**
     - `UIKeyTrigger.Update` → `OnKeyConfirm`/`OnKeyCancel`/... on the current `UIScene`, which covers BattleHUD and every menu.
     - `UIManager.Input.GetKey(Control.X)`.
     - `EventInput` (field talk/confirm bitmask).
     - `FieldMapActorController.MovePC` (`GetAxis()` + `UIManager.Input.GetKey(Up/Down/...)`; it also peeks at `XInputManager.CurrentState.DPad`).
- **Steam Input:** with Steam Input on, Steam presents each physical pad, and each Remote Play guest, as its own virtual Xbox pad in its own XInput slot (see the comment at `HonoInputManager.cs:919`). That makes plain XInput slots 0–3 the right per-player source.
  - XInput caps this at 4 pads, which matches the 4-player goal.
  - **Recommendation for Phase 1:**
    - With multiplayer on, read XInput slots 0–3 directly per player; the keyboard is P1.
    - Ignore the merged JoyShock and `UnityEngine.Input` joystick paths, and tell users to turn Steam Input on for PlayStation/Switch pads.
    - Route by giving `HonoInputManager` a per-player scan plus an "active player" filter. Its public `IsInput*`/`GetAxis` then report only the player who has control, so the consumers above need no changes.

### Q2. The field leader's movement code

- **Where it lives:** `Global/Field/Map/Actor/FieldMapActorController.cs`, one `MonoBehaviour` per field actor.
  - `HonoUpdate()` → `UpdateMovement()`. If `isPlayer`: `MovePC()` (reads input, builds the move vector, walkmesh collision via `walkMesh.Collision(this, ...)`, slope factor, `UpdateActiveTri`). Otherwise: `MoveNPC()`.
  - Running is decided in `HonoUpdate` from `Control.Cancel` and the analog magnitude.
- **It can drive more than one character:**
  - Movement, collision and walkmesh state are all per instance: `curPos`, `activeTri`, `activeFloor`, `radius`, `walkMesh`.
  - The only blockers are global ones: input read straight from the singletons, `isPlayer` gating, the static `FieldMapActorController.ccSMoveKey` (the leader's "is moving" flag for encounters/idle), and `GetUserControl()`.
  - **Phase 3a plan:** give extra players' controllers an "owner player" field. Have `MovePC` take input from that player's state instead of the globals, and keep `isPlayer`/`ccSMoveKey`/encounters on the leader only.
- **Where `isPlayer` is set:** `FieldMap.AddFieldChar(..., isPlayer, ...)` (`FieldMap.cs:555`) when models are created, then `FieldMap.updatePlayer()` and `EventEngine.updateModelsToBeAdded` (`playerUID = GetControlUID()`). `DefinePlayerCharacter` clears it on the previous controller. `FieldMap.player`/`playerController` hold the leader, which the camera follows.
- **Spawning extra characters (3a):**
  - `EventEngine` already tracks party member objects: `_context.partyUID[0..3]` (local UIDs 251–254, filled at `EventEngine.cs:724–745`).
  - Most fields spawn only the leader's model, so 3a has to create models for the other party members itself. Use the same path as `AddFieldChar` with each character's field model.

### Q3. How "the player character" (object 250) is stored

- **Storage:**
  - It's a single byte, `EventContext.controlUID` (`Global/Event/EventContext.cs:134`). It's copied along with the rest of `EventContext` (`EventContext.cs:76`). I haven't checked whether saves serialize it, so treat it as possibly saved.
  - `EventEngine.GetUIDFromLocalUID(250)` returns it (`EventEngine.cs:836`). `GetControlChar()`/`GetControlUID()` read it.
  - `DefinePlayerCharacter` (opcode `0x2C`, `EventEngine.DoEventCode.cs:980`) sets it to the running entry's UID and clears `isPlayer` on the old actor.
- **Readers:**
  - `ProcessEvents` (`EventEngine.ProcessEvents.cs:52–252`): movement key → encounters, the per-frame `CollisionRequest` (talk/push detection and the "!" icon via `EIcon.ProcessHereIcon`), and collision with NPCs.
  - `MoveToward`, a few opcodes (`DoEventCode.cs:842`, `2203`), `UCOFF` (`DisableMove`, which clears the 250 actor's move target) and `EventEngine.cs:1076/1326`.
- **Talk detection is already parameterised:** `EventCollision.CollisionRequest(PosObj po)` takes the actor, and the talk/push request is raised with `instance.Request(npc, 1, 2/3, ...)`.
- **Swapping it for one interaction (3c) looks safe:**
  - **Swap in:** set `controlUID` to the talker's UID before the NPC's talk tag is requested.
  - **Swap out:** restore it when the script hands control back. That's `UCON`/`EnableMove`, or the talk script's level returning.
  - **Things to handle:**
    1. Skip `DefinePlayerCharacter` during the swap, since a script could re-define 250.
    2. Never let the swapped value survive a field change or a save. Restore it before `FieldMap` changes and before any save.
    3. `isPlayer`/`FieldMap.playerController` (camera) must stay on the leader. Only the event-side UID moves.
  - **To prototype first on field 507.**

### Battle notes (for Phase 2)

- **`BattleHUD` state:** `ReadyQueue` (battle IDs whose ATB is full) and `CurrentPlayerIndex` (whose menu is open, -1 = none). Both are public, in `BattleHUD.Public.cs`.
  - `SwitchPlayer(int)` (`BattleHUD.cs:1282`) opens a character's menu.
  - Automatic selection is at `BattleHUD.Unity.cs:296–311`.
  - The "switch to the next ready character" key handling is at `BattleHUD.Scene.cs:417–453`.
- **Input path:** menu input reaches BattleHUD only through `UIKeyTrigger` → `OnKey*`. So Phase 2 is:
  1. When `CurrentPlayerIndex` changes, set the active player to that character's owner.
  2. Make the next-ready cycling skip characters owned by other players.

## Phase 1 notes (separate controllers)

- **Config** (`[Multiplayer]` in `Memoria.ini`):
  - `Enabled`.
  - `PlayerPads = 0, 1, 2, 3`: the XInput slot of P1..P4. The keyboard is always P1's.
  - `Player2Characters` / `Player3Characters` / `Player4Characters`: `CharacterId` names or numbers. Unlisted characters belong to P1.
  - `DebugOverlay`.
  - The file is re-read live when it changes.
- **`Memoria/Multiplayer/XInputPads.cs`:** reads slots 0–3 straight from `xinput1_4`, falling back to `1_3` and then `9_1_0`. JoyShock isn't merged in, and empty slots are polled once a second.
- **`MultiplayerManager`:**
  - Tracks the active player and resolves slots and owners.
  - In `XInputManager.Update`, it polls the pads and logs connect/disconnect.
  - With `DebugOverlay = 1`, it creates the overlay, handles **F10** (pass control to the next player) and logs button presses.
- **Routing:** only the active player's input reaches the game.
  1. `XInputManager.Refresh` reads `MultiplayerManager.ActivePadSlot` instead of the first connected pad. On a switch it seeds the new slot's state, so the switch doesn't register as a press or a disconnection.
  2. `UnityXInput.Input.GetKey*` blocks the keyboard (except F1–F15) when P1 isn't in control. So does `HonoInputManager`'s Win32 key polling (`CheckPersistentInput`, `CheckPersistentDirectionInput`).
  3. `UnityXInput.Input.GetAxis*` stops reading Unity's merged `*Trigger` axes and uses the active pad's triggers.
  4. `XInputDotNetPure.GamePad.MergeJoyShockDevices = false` while multiplayer is on, so DS4/DualSense/Pro controllers aren't OR-ed into every slot. Those controllers need Steam Input on to be seen as separate XInput slots.
- **Install:** `install.ps1` now also copies `XInputDotNetPure.dll`.
- **Not yet routed:** Unity's merged joystick reads that only classify the input source (`ScanSources`), and the PSX pause combo in `ScanPauseCombinationKey`. Neither drives gameplay. Phase 2 sets the active player from `BattleHUD.CurrentPlayerIndex`.

## Phase 2 notes (battle menus per player)

- **`Memoria/Multiplayer/MultiplayerBattle.cs`** holds the battle rules. `BattleHUD` calls it from marked one-liners:
  - **`SwitchPlayer`:** the only place a command menu opens. It calls `OnCommandMenuOpened(playerId)`, which hands control to the owner of that character's slot (`CharacterId`). Control stays with them through the sub-menus and targeting.
  - **`InitialBattle`:** calls `OnBattleStart()`, which gives control to P1. This also runs when the battle menu is re-enabled with no command menu open.
  - **`OnKeyMenu`:** the next-ready-character key (Triangle/Y). It only switches to characters for which `CanSelect` is true, meaning the active player owns them. Party-panel mouse clicks follow the same rule.
  - **`DisplayCommand`:** the caption becomes `P2 Command` / `P2 Trance`.
  - **`DisplayCharacterParameter`:** party names get a `P2 ` prefix.
- **Battle end:** `MultiplayerBattle.Update` (called from `MultiplayerManager.Update`) returns control to P1 once the scene is no longer a battle scene.
- **Index note:** BattleHUD's "player id" (`CurrentPlayerIndex`, `ReadyQueue`) is the index into `btl_data`. `BattleUnit.Id` is the `btl_id` **bitmask**, so don't mix them.
- **One menu at a time:** while one player's menu is open, the other player waits. Simultaneous menus are Phase 4.
