# Local multiplayer for Memoria (work in progress)

This branch (`multiplayer`) adds **local co-op for up to 4 players** to Memoria: each player uses their own controller and
plays their own characters. Online play works through **Steam Remote Play Together** (everyone's controller shows up as a
local one). Everything is behind one switch: with `[Multiplayer] Enabled = 0` (the default) the game plays exactly like
upstream Memoria.

> Status: a playable prototype, shared for testing and feedback. Expect rough edges (see [Known issues](#known-issues)).
> The detailed working log, with design notes and findings per phase, is [multiplayer-plan.md](multiplayer-plan.md).

## What works

- **Separate controllers.** Up to 4 XInput controllers, one per player; the keyboard belongs to player 1.
  PlayStation/Switch controllers are told apart only with Steam Input enabled for the game.
- **Party slots belong to players.** Player 2 controls the 2nd character in the party, player 3 the 3rd, player 4 the
  4th. Player 1 controls the leader and any slot whose player isn't playing or has no controller connected.
- **Battles.** A character's command menu is driven by its owner's controller. While the main menu is busy with one
  player, any other player whose character is ready gets a **compact command menu** of their own (up to 3 at once), in
  the same style and layout as the main menu.
- **Fields.** Each extra player's character walks next to the leader with their own controller:
  - the screen edge acts as a wall (the camera follows the leader);
  - they can **talk to NPCs**, open chests and pick up items (with their own "!" bubble), and they close the
    messages they open;
  - other characters turn to look at whoever acts, and walk along when the leader leaves an area;
  - they appear next to the leader on every new field.
- **Settings in the launcher.** A **MULTIPLAYER** tab edits the `[Multiplayer]` section of `Memoria.ini`.

## Settings

All in `Memoria.ini`, section `[Multiplayer]` (or the launcher's MULTIPLAYER tab):

| Setting | Default | What it does |
|---|---|---|
| `Enabled` | `0` | Turns local multiplayer on. Off = identical to upstream Memoria. |
| `PlayerCount` | `2` | Number of players (2-4). Player N controls party slot N while their controller is connected; every other slot belongs to player 1. |
| `PlayerPads` | `0, 1, 2, 3` | XInput controller slot (0-3) of players 1, 2, 3, 4. |
| `FieldFollowers` | `1` | Extra players walk on fields. |
| `BlackWaltzModel` | `0` | On fields, an extra player's Beatrix is shown as Black Waltz No. 3: its normal model, walking with the beaten one's animations (`BlackWaltzWalk`), at 25% of the normal movement speed to fit that slow walk. |
| `BlackMageModel` | `0` | On fields, an extra player's Vivi is shown as a Black Mage. |
| `BattleMenus` | `1` | Compact battle menus for players waiting while the main menu is busy. |
| `FieldModels` | empty | Advanced: override any character's field model, `Name=model,headHeight,idle,walk,run,turnLeft,turnRight,size[,walk/run animation speed %]`, separated by `;`. Wins over the two switches above. |
| `DebugOverlay` | `0` | Shows each player's controller, buttons and characters on screen, F10 passes control to the next player, and enables extra diagnostics in `Memoria.log` (lines starting with `[Multiplayer]`). |

Example for three players (players 2 and 3 play the 2nd and 3rd characters in the party):

```ini
[Multiplayer]
Enabled = 1
PlayerCount = 3
```

## Trying it (drop-in build)

The fork's [pre-releases](https://github.com/czebrose/Memoria/releases) contain a zip with only the files this branch
changes. They must go on top of the **exact Memoria build the release names** (the matching upstream Canary):

1. Install that Memoria build with its patcher, as usual.
2. Back up `x64\FF9_Data\Managed\` and `FF9_Launcher.exe` from the game folder.
3. Extract the zip into the game folder (it contains `FF9_Launcher.exe` and `x64\FF9_Data\Managed\*.dll`), overwriting.
4. Start the game, open the launcher's **MULTIPLAYER** tab and choose the number of players.

To undo: put back the files you backed up (or re-run the Memoria patcher).

## Building it yourself

The usual Memoria build works (see the main README). For a lighter dev loop, `tools/multiplayer/` has scripts that build
and install only what this branch changes, without the full toolchain:

| Script | What it does |
|---|---|
| `build.ps1` | Builds `Assembly-CSharp.dll`, `Memoria.Prime.dll`, `XInputDotNetPure.dll` into `Output\`. Needs Visual Studio Build Tools (MSBuild). Skips the native SaXAudio and .NET 10 plugin projects, which aren't needed for it. |
| `build-launcher.ps1` | Builds the launcher into `Output\Launcher\`. Pass `-TargetFramework v4.7.1` if the .NET 4.8 targeting pack isn't installed. |
| `install.ps1` | Copies the built DLLs into the game (`-Launcher` also installs the launcher as `FF9_Launcher.exe`). The first run backs up `Managed\`, `Memoria.ini` and the launcher **outside** the game folder (`..\MemoriaMultiplayerBackup`). Default game path: Steam's; pass `-GamePath` otherwise. |
| `restore.ps1` | Puts the backed-up files back. |

```powershell
powershell -ExecutionPolicy Bypass -File tools\multiplayer\build.ps1
powershell -ExecutionPolicy Bypass -File tools\multiplayer\install.ps1
```

`build.ps1` passes `CI=true`, which stops Memoria's own automatic deployment into the game folder.

## Where the code is

New code lives in the `Memoria.Multiplayer` namespace and in partial classes named `*.Multiplayer.cs`; small edits to
existing files are marked `[Multiplayer]`.

| Area | Files |
|---|---|
| Players, controllers, ownership | `Memoria/Multiplayer/MultiplayerManager.cs`, `XInputPads.cs`, `MultiplayerKeys.cs`, `MultiplayerOverlay.cs` |
| Battle menus | `Memoria/Multiplayer/MultiplayerBattle.cs`, `Global/battle/BattleHUD/BattleHUD.Multiplayer.cs` |
| Fields | `Memoria/Multiplayer/MultiplayerField.cs`, `FieldModels.cs`, `MultiplayerTalk.cs`, `MultiplayerBubbles.cs`, `Global/Field/Map/Actor/FieldMapActorController.Multiplayer.cs`, `Global/Event/Engine/EventEngine.Multiplayer.cs` |
| Settings | `Memoria/Configuration/Structure/MultiplayerSection.cs`, `Access/Multiplayer.cs`, both `Memoria.ini` templates |
| Launcher tab | `Memoria.Launcher/Launcher/Settings.Multiplayer.cs`, `SettingsGrid_Multiplayer.cs` |

## Known issues

- **Cutscenes:** extra players aren't hidden during cutscenes, and they walk along when a script walks the leader.
- **Compact battle menus:** no keyboard support (player 1 needs a controller for it), no battlefield target highlight,
  no help texts.
- **ATB "Wait" mode** freezes everyone's gauge while the main menu is in a sub-menu, so others can't become ready then.
- **Field models:** extra players use each character's normal field model (or the Black Waltz / Black Mage swaps). Mod
  characters have no field models of their own.
- **World map:** leader only.
