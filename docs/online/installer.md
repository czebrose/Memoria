# One-zip installer

Branch `online-installer` (on `online-merged`), 2026-11-20. Replaces the hand-made PC2 zips (`OnlineStep3-PC2.zip`,
`OnlineSpikeE-PC2.zip` with an `INSTALL-FOR-AGENT.md`): setting up a PC is now unzip, double-click `Install.bat`, done.

## Making the zip

```powershell
.\tools\multiplayer\package.ps1
```

It builds `Assembly-CSharp.dll` (+ `Memoria.Prime.dll`, `XInputDotNetPure.dll`) with `build.ps1` and the launcher with
`build-launcher.ps1` (against the newest .NET 4.x targeting pack if 4.8's isn't installed), then writes
`Output\MemoriaOnline-<branch>-<first 12 hex of the Assembly-CSharp.dll SHA-256>.zip` (about 60 MB). `-SkipBuild`
packages what's already in `Output\`. If source files have uncommitted changes, the commit in the manifest gets `+dirty`.

The base patcher comes from `-PatcherPath` (default: the game folder's `Memoria.Patcher.exe`, which the Memoria
launcher keeps there). The script only accepts the stock upstream patcher our fork is built on: Canary of 2026-11-09,
built from `c716e1fa`, SHA-256 `2C8CA404C9CC...` (the digest upstream publishes for that release). For a different base,
pass `-AllowOtherPatcher -BaseVersion '<name>' -ExpectedPatcherSha256 <hash>` and rebase the fork onto it first.
`package.ps1` reads the patcher's payload (format in `Memoria.Patcher\Program.cs`) to list the files it writes, the
hash of its stock launcher, and its default `Memoria.ini`.

## What the zip holds

```
MemoriaOnline-<branch>-<build>\
  Install.bat, Uninstall.bat, Install.ps1   installer (sources: tools\multiplayer\installer\)
  README.txt                                for the person installing (what, how, how to undo)
  manifest.txt                              build, branch, commit, hashes
  files.sha256                              SHA-256 of every file under game\
  base-files.txt                            the 1416 files the base patcher writes
  base\Memoria.Patcher.exe                  stock upstream patcher, unchanged
  base\Memoria.default.ini                  its default Memoria.ini (source of keys to add)
  game\x64\FF9_Data\Managed\Assembly-CSharp.dll, Memoria.Prime.dll, XInputDotNetPure.dll
  game\FF9_Launcher.exe                     our launcher (blocked battle values, presets)
  LICENSE-Memoria.txt, THIRD-PARTY-NOTICES.md
```

Our build adds nothing else: `StreamingAssets` (scripts, shaders, data) and the other managed DLLs are unchanged from
the base, so the base patcher provides them. **No mod files:** `HadesWorkshopMod\` (with `Data\Online\*.txt`) comes from
the mod side. Both scripts refuse any path under it.

Licence: Memoria is MIT, so the stock patcher can be redistributed as long as the licence and notices go with it.

## What Install.bat does

1. Finds the game: `-GamePath`, else Steam's uninstall entry for app 377840, Steam's own folder and every library in
   `libraryfolders.vdf`, else asks for the folder.
2. Stops if `FF9`, the launcher or a patcher is running. If the folder isn't writable, starts itself again as
   administrator (UAC prompt).
3. Base Memoria check: it's "the same" if `MemoriaOnline.installed.txt` (written by an earlier install) names the same
   patcher, or the launcher is byte-identical to the patcher's. Missing or different: the patcher runs (`-ForcePatcher`
   always runs it).
4. Backup to `Documents\FF9-OnlineCoop-Backups\<yyyyMMdd-HHmmss>\` (outside the game folder: the Memoria launcher
   treats any game subfolder with a `Memoria.ini` as a mod). It saves every file the run may overwrite: our files,
   `Memoria.ini`, `Settings.ini`, and if the patcher runs, all of its files, the `.bak` files it creates and the
   `StreamingAssets\Assets\Resources\CommonAsset\` folder it deletes. Files that don't exist yet go to `created.txt`.
5. Runs `base\Memoria.Patcher.exe <game>` (after checking the patcher's hash), logs to `patcher.log` in the backup, and
   checks that it installed its launcher and `Memoria.Prime.dll`.
6. Puts `Memoria.ini` and `Settings.ini` back byte for byte (the patcher rewrites them while merging), copies our
   files, then **adds only missing keys** to `Memoria.ini` from the base default (in their sections, Windows-1252).
   `-OnlineDebug` (test PCs) also sets `[Debug] Enabled = 1` and `OnlineDebug = 1`. It's the only option that changes
   values.
7. Checks every copied file against `files.sha256` and `Assembly-CSharp.dll` against the manifest, writes
   `MemoriaOnline.installed.txt` in the game folder and prints a summary.

`Uninstall.bat` (`Install.ps1 -Uninstall`) restores the newest backup for that game folder that hasn't been restored
yet: it copies the saved files back, deletes the files in `created.txt` and marks the backup `restored.txt`, so running it
again goes one install further back. `-BackupPath <folder>` picks a backup by hand. Backups are never deleted.

The PC2 install note (`INSTALL-FOR-AGENT.md`) is no longer needed for installing. Test notes (what to check in
`Memoria.log`, hotkeys) can go with a test build as a separate test sheet.

## Tested (2026-11-20, this PC)

Package `MemoriaOnline-online-installer-D1E357058121.zip` (branch `online-merged` code = test build `759B6E7C38E0`,
`a43bf3a3`, rebuilt). Game folder had Memoria with a dev launcher (not the stock one), debug options on.

- Install: base detected as "different", patcher ran (13 s with backup), 1420 files backed up, 51 new files listed;
  hash `D1E357058121` verified. `Memoria.ini`, `Settings.ini` and the whole `HadesWorkshopMod\` folder were
  byte-identical afterwards.
- Second install: base detected as "the same", patcher skipped, 7 files backed up.
- Game start (`x64\FF9.exe`, Steam running): `[Online]` layer up (interfaces, `SteamID check: OK`, callbacks live), mod
  scripts loaded, no exceptions in `Memoria.log`.
- Uninstall twice: first back to the second install's state, then to the original. All 1420 saved files and
  `CommonAsset\` matched an independent copy taken before the test; the game folder's file list matched too.
- `Memoria.ini` merge on a test copy with keys and a whole section removed: only those keys were added, in their own
  sections; `-OnlineDebug` set the two debug values.

Not tested yet: a clean PC (no Memoria) and a game folder that needs administrator rights. Both need the second PC.
