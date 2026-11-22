Memoria online co-op build for FINAL FANTASY IX (Black Waltz)
==============================================================

Build {{BUILD}}: branch {{BRANCH}}, commit {{COMMIT}}, built {{BUILT}} UTC.
Assembly-CSharp.dll SHA-256: {{ACS}}

WHAT IT INSTALLS
  1. Base Memoria {{BASEVERSION}} (base\Memoria.Patcher.exe, the unmodified upstream patcher),
     only if this PC doesn't already have that exact version.
  2. The online co-op changes on top of it:
       x64\FF9_Data\Managed\Assembly-CSharp.dll   (the game code with online co-op)
       x64\FF9_Data\Managed\Memoria.Prime.dll
       x64\FF9_Data\Managed\XInputDotNetPure.dll
       FF9_Launcher.exe                            (Memoria launcher: battle speeds online can't
                                                    use are blocked; presets adjusted)
  It does NOT install or change the Black Waltz mod (the HadesWorkshopMod folder). Install the
  mod separately, as usual.
  Your Memoria.ini is kept as it is; only settings it lacks are added.

HOW TO INSTALL ON WINDOWS
  1. Close the game and its launcher.
  2. Unzip this whole folder anywhere (not inside the game folder).
  3. Double-click Install.bat. It finds the game through Steam (or asks for the folder),
     backs up every file it will replace, installs, and checks the result.
     If the game is under Program Files, Windows asks for administrator rights.
  4. Start the game through Steam as usual.

  Options (run from a PowerShell window in this folder, or add them after Install.bat):
     -GamePath "D:\SteamLibrary\steamapps\common\FINAL FANTASY IX"   use this game folder
     -OnlineDebug     test PCs: turn on [Debug] Enabled and OnlineDebug in Memoria.ini
     -ForcePatcher    run the base Memoria patcher even if it's already installed

HOW TO INSTALL ON LINUX (Steam with Proton; Fedora, Bazzite, Nobara and other distros)
  1. Close the game and its launcher.
  2. Unzip this whole folder anywhere (not inside the game folder).
  3. Run Install.sh: double-click it (choose "Run" or "Run in terminal" if asked), or in a
     terminal in this folder:   bash Install.sh
     It finds the game in Steam's libraries (native and Flatpak Steam), backs up every file it
     will replace, installs base Memoria itself (no Wine needed) and checks the result.
     It needs python3, which Fedora and its spins already have.
  4. Start the game through Steam as usual (Proton).

  Options (add them after "bash Install.sh"):
     --game-path "/path/to/steamapps/common/FINAL FANTASY IX"   use this game folder
     --online-debug    test PCs: turn on [Debug] Enabled and OnlineDebug in Memoria.ini
     --force-patcher   install base Memoria even if it's already installed

HOW TO UNDO
  Windows: double-click Uninstall.bat. Linux: run Uninstall.sh (bash Uninstall.sh).
  It puts back the files saved by the newest install and removes the files that install
  added. Running it again undoes the install before that.
  Backups are in Documents\FF9-OnlineCoop-Backups\<date-time>\ (on Linux: ~/Documents/...;
  kept after uninstalling; delete them yourself when you no longer need them).
  If a backup is lost: Steam -> FINAL FANTASY IX -> Properties -> Installed Files ->
  "Verify integrity of game files" restores the unmodded game.

LICENCE
  Memoria is MIT-licensed (LICENSE-Memoria.txt; third-party notices in THIRD-PARTY-NOTICES.md).
