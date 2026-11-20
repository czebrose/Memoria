# Online co-op build of Memoria for FINAL FANTASY IX: installer and uninstaller.
# Runs from the unzipped package (see README.txt). Layout next to this script:
#   manifest.txt      build info and hashes (written by tools\multiplayer\package.ps1)
#   files.sha256      "<sha256>  <path>" for every file under game\
#   base-files.txt    every file the base Memoria patcher writes (paths relative to the game folder)
#   base\Memoria.Patcher.exe, base\Memoria.default.ini
#   game\...          our files, laid out as in the game folder
# It never touches HadesWorkshopMod\ (the mod's own files) and never replaces an existing Memoria.ini:
# it only adds keys that are missing from it.
param(
    [string]$GamePath,
    [switch]$Uninstall,
    # Uninstall from this backup folder instead of the newest one
    [string]$BackupPath,
    # Test PCs: set [Debug] Enabled = 1 and OnlineDebug = 1 in Memoria.ini (the only values this script ever changes)
    [switch]$OnlineDebug,
    # Run the base patcher even if the installed Memoria already matches it
    [switch]$ForcePatcher,
    # Don't wait for Enter at the end (for scripted runs)
    [switch]$NoPause
)
$ErrorActionPreference = 'Stop'
$Here = $PSScriptRoot
$AppId = '377840'
$GameFolderName = 'FINAL FANTASY IX'
$BackupRoot = Join-Path ([Environment]::GetFolderPath('MyDocuments')) 'FF9-OnlineCoop-Backups'
$Enc1252 = [Text.Encoding]::GetEncoding(1252)
$MarkerName = 'MemoriaOnline.installed.txt'

function Write-Step([string]$text) { Write-Host "-> $text" -ForegroundColor Cyan }
function Write-Ok([string]$text) { Write-Host "   OK  $text" -ForegroundColor Green }
function Write-Warn2([string]$text) { Write-Host "   !!  $text" -ForegroundColor Yellow }
function Exit-Script([int]$code) {
    if (-not $NoPause) { Write-Host ''; Read-Host 'Press Enter to close' | Out-Null }
    exit $code
}
function Get-Sha256([string]$path) { (Get-FileHash -Algorithm SHA256 -LiteralPath $path).Hash.ToUpperInvariant() }

function Read-KeyValueFile([string]$path) {
    $map = @{}
    foreach ($line in [IO.File]::ReadAllLines($path)) {
        if ($line -match '^\s*([^#=][^=]*?)\s*=\s*(.*)$') { $map[$Matches[1]] = $Matches[2].Trim() }
    }
    $map
}

function Test-GameFolder([string]$path) {
    $path -and (Test-Path -LiteralPath (Join-Path $path 'x64\FF9_Data\Managed\Assembly-CSharp.dll'))
}

function Find-GameFolder {
    $candidates = New-Object System.Collections.Generic.List[string]
    foreach ($key in "HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\Steam App $AppId",
                     "HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\Steam App $AppId") {
        try { $loc = (Get-ItemProperty -LiteralPath $key -ErrorAction Stop).InstallLocation; if ($loc) { $candidates.Add($loc) } } catch {}
    }
    $steamRoots = New-Object System.Collections.Generic.List[string]
    foreach ($pair in @(@('HKCU:\Software\Valve\Steam', 'SteamPath'), @('HKLM:\SOFTWARE\WOW6432Node\Valve\Steam', 'InstallPath'), @('HKLM:\SOFTWARE\Valve\Steam', 'InstallPath'))) {
        try { $v = (Get-ItemProperty -LiteralPath $pair[0] -ErrorAction Stop).($pair[1]); if ($v) { $steamRoots.Add(($v -replace '/', '\')) } } catch {}
    }
    $steamRoots.Add("${env:ProgramFiles(x86)}\Steam")
    foreach ($root in $steamRoots) {
        $candidates.Add((Join-Path $root "steamapps\common\$GameFolderName"))
        $vdf = Join-Path $root 'steamapps\libraryfolders.vdf'
        if (Test-Path -LiteralPath $vdf) {
            foreach ($m in [regex]::Matches([IO.File]::ReadAllText($vdf), '"path"\s+"([^"]+)"')) {
                $lib = $m.Groups[1].Value -replace '\\\\', '\'
                $candidates.Add((Join-Path $lib "steamapps\common\$GameFolderName"))
            }
        }
    }
    foreach ($c in $candidates) { if (Test-GameFolder $c) { return (Resolve-Path -LiteralPath $c).Path } }
    Write-Warn2 'Could not find FINAL FANTASY IX automatically.'
    while ($true) {
        $answer = Read-Host "Paste the game folder (the one with FF9_Launcher.exe), or leave empty to cancel"
        if (-not $answer) { return $null }
        $answer = $answer.Trim('"', ' ')
        if (Test-GameFolder $answer) { return (Resolve-Path -LiteralPath $answer).Path }
        Write-Warn2 "No x64\FF9_Data\Managed\Assembly-CSharp.dll under '$answer'."
    }
}

function Assert-GameNotRunning {
    $running = Get-Process -Name 'FF9', 'FF9_Launcher', 'Memoria.Launcher', 'Memoria.Patcher' -ErrorAction SilentlyContinue
    if ($running) {
        throw "Close the game and its launcher first (running: $(($running | ForEach-Object { $_.ProcessName } | Sort-Object -Unique) -join ', '))."
    }
}

function Test-CanWrite([string]$dir) {
    $probe = Join-Path $dir ('.memoria-online-write-test-' + [Guid]::NewGuid().ToString('N'))
    try { [IO.File]::WriteAllText($probe, ''); Remove-Item -LiteralPath $probe -Force; $true } catch { $false }
}

function Assert-NotModPath([string]$relative) {
    if ($relative -match '^(?i)HadesWorkshopMod([\\/]|$)') { throw "Refusing to touch mod file '$relative' (the mod's files are installed separately)." }
}

# ---- Memoria.ini: keep the user's file, add only the keys it lacks ----
function Read-IniSections([string[]]$lines) {
    # Returns ordered list of @{ Name; Keys (hashset, lowercase); LastLine (index of the last non-empty line) }
    $sections = New-Object System.Collections.Generic.List[object]
    $current = @{ Name = ''; Keys = New-Object 'System.Collections.Generic.HashSet[string]'; Start = -1; LastLine = -1 }
    $sections.Add($current)
    for ($i = 0; $i -lt $lines.Length; $i++) {
        $line = $lines[$i]
        if ($line -match '^\s*\[([^\]]+)\]') {
            $current = @{ Name = $Matches[1].Trim(); Keys = New-Object 'System.Collections.Generic.HashSet[string]'; Start = $i; LastLine = $i }
            $sections.Add($current)
            continue
        }
        if ($line -match '^\s*([A-Za-z0-9_.]+)\s*=') { [void]$current.Keys.Add($Matches[1].ToLowerInvariant()) }
        if ($line.Trim() -ne '' -and -not ($line -match '^\s*;')) { $current.LastLine = $i }
    }
    $sections
}

function Merge-MissingIniKeys([string]$iniPath, [string]$defaultIniPath, [bool]$setOnlineDebug) {
    $user = [IO.File]::ReadAllLines($iniPath, $Enc1252)
    $default = [IO.File]::ReadAllLines($defaultIniPath, $Enc1252)
    $userSections = Read-IniSections $user
    $defSections = Read-IniSections $default
    $inserts = @{}          # line index -> list of lines to insert after it
    $appendAtEnd = New-Object System.Collections.Generic.List[string]
    $added = New-Object System.Collections.Generic.List[string]
    $wanted = New-Object System.Collections.Generic.List[object]
    foreach ($ds in $defSections) {
        if (-not $ds.Name) { continue }
        $keyLines = New-Object System.Collections.Generic.List[string]
        $end = $default.Length
        $next = $defSections | Where-Object { $_.Start -gt $ds.Start } | Select-Object -First 1
        if ($next) { $end = $next.Start }
        for ($i = $ds.Start + 1; $i -lt $end; $i++) { if ($default[$i] -match '^\s*([A-Za-z0-9_.]+)\s*=') { $keyLines.Add($default[$i].Trim()) } }
        $wanted.Add(@{ Name = $ds.Name; Lines = $keyLines })
    }
    if ($setOnlineDebug) {
        $dbg = $wanted | Where-Object { $_.Name -eq 'Debug' } | Select-Object -First 1
        if (-not $dbg) { $dbg = @{ Name = 'Debug'; Lines = New-Object System.Collections.Generic.List[string] }; $wanted.Add($dbg) }
        if (-not ($dbg.Lines | Where-Object { $_ -match '^OnlineDebug\s*=' })) { $dbg.Lines.Add('OnlineDebug = 1') }
    }
    foreach ($w in $wanted) {
        $us = $userSections | Where-Object { $_.Name -eq $w.Name } | Select-Object -First 1
        $missing = New-Object System.Collections.Generic.List[string]
        foreach ($kl in $w.Lines) {
            $k = ($kl -split '=', 2)[0].Trim()
            if (-not $us -or -not $us.Keys.Contains($k.ToLowerInvariant())) { $missing.Add($kl); $added.Add("[$($w.Name)] $k") }
        }
        if ($missing.Count -eq 0) { continue }
        if ($us) {
            if (-not $inserts.ContainsKey($us.LastLine)) { $inserts[$us.LastLine] = New-Object System.Collections.Generic.List[string] }
            $inserts[$us.LastLine].AddRange($missing)
        } else {
            $appendAtEnd.Add(''); $appendAtEnd.Add("[$($w.Name)]"); $appendAtEnd.AddRange($missing)
        }
    }
    $out = New-Object System.Collections.Generic.List[string]
    for ($i = 0; $i -lt $user.Length; $i++) {
        $line = $user[$i]
        $out.Add($line)
        if ($inserts.ContainsKey($i)) { $out.AddRange($inserts[$i]) }
    }
    $out.AddRange($appendAtEnd)
    # Test PCs only: switch the two debug values on (explicit opt-in)
    $changed = New-Object System.Collections.Generic.List[string]
    if ($setOnlineDebug) {
        $inDebug = $false
        for ($i = 0; $i -lt $out.Count; $i++) {
            if ($out[$i] -match '^\s*\[') { $inDebug = $out[$i] -match '^\s*\[Debug\]' }
            elseif ($inDebug -and $out[$i] -match '^\s*(Enabled|OnlineDebug)\s*=\s*(.*)$' -and $Matches[2].Trim() -ne '1') {
                $changed.Add("[Debug] $($Matches[1]) = 1"); $out[$i] = "$($Matches[1]) = 1"
            }
        }
    }
    if ($added.Count -gt 0 -or $changed.Count -gt 0) { [IO.File]::WriteAllLines($iniPath, $out, $Enc1252) }
    @{ Added = $added; Changed = $changed }
}

# ---- base Memoria version ----
function Get-BaseStatus([string]$game, $manifest) {
    $prime = Join-Path $game 'x64\FF9_Data\Managed\Memoria.Prime.dll'
    $ini = Join-Path $game 'Memoria.ini'
    if (-not (Test-Path -LiteralPath $prime) -or -not (Test-Path -LiteralPath $ini)) { return 'missing' }
    $marker = Join-Path $game $MarkerName
    if (Test-Path -LiteralPath $marker) {
        $m = Read-KeyValueFile $marker
        if ($m['BasePatcherSha256'] -eq $manifest['BasePatcherSha256']) { return 'same' }
    }
    foreach ($name in 'FF9_Launcher.exe', 'FF9_Launcher.fix') {
        $p = Join-Path $game $name
        if ((Test-Path -LiteralPath $p) -and (Get-Sha256 $p) -eq $manifest['BaseLauncherSha256']) { return 'same' }
    }
    'other'
}

function Get-LauncherTarget([string]$game) {
    # With the Steam overlay fix the real launcher is FF9_Launcher.fix (as the base patcher does it)
    try {
        $k = Get-ItemProperty -LiteralPath 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Image File Execution Options\FF9_Launcher.exe' -ErrorAction Stop
        if ($k.Debugger) { return 'FF9_Launcher.fix' }
    } catch {}
    'FF9_Launcher.exe'
}

function Get-OurFiles {
    $list = New-Object System.Collections.Generic.List[object]
    foreach ($line in [IO.File]::ReadAllLines((Join-Path $Here 'files.sha256'))) {
        if ($line -match '^([0-9A-Fa-f]{64})\s+(.+)$') {
            $rel = $Matches[2].Trim() -replace '/', '\'
            Assert-NotModPath $rel
            $list.Add(@{ Hash = $Matches[1].ToUpperInvariant(); Source = $rel; Target = $rel })
        }
    }
    $list
}

function Get-BaseFiles {
    $list = New-Object System.Collections.Generic.List[string]
    foreach ($line in [IO.File]::ReadAllLines((Join-Path $Here 'base-files.txt'))) {
        $rel = $line.Trim() -replace '/', '\'
        if ($rel) { Assert-NotModPath $rel; $list.Add($rel) }
    }
    $list
}

# ---- uninstall ----
function Invoke-Restore([string]$game) {
    $backup = $BackupPath
    if (-not $backup) {
        $backup = Get-ChildItem -LiteralPath $BackupRoot -Directory -ErrorAction SilentlyContinue |
            Where-Object { (Test-Path -LiteralPath (Join-Path $_.FullName 'backup.txt')) -and -not (Test-Path -LiteralPath (Join-Path $_.FullName 'restored.txt')) } |
            Sort-Object Name -Descending |
            Where-Object { (Read-KeyValueFile (Join-Path $_.FullName 'backup.txt'))['GamePath'] -eq $game } |
            Select-Object -First 1 -ExpandProperty FullName
    }
    if (-not $backup -or -not (Test-Path -LiteralPath (Join-Path $backup 'backup.txt'))) { throw "No backup found for $game in $BackupRoot." }
    Write-Step "Restoring from $backup"
    $files = Join-Path $backup 'files'
    $restored = 0
    if (Test-Path -LiteralPath $files) {
        foreach ($f in Get-ChildItem -LiteralPath $files -Recurse -File) {
            $rel = $f.FullName.Substring($files.Length + 1)
            Assert-NotModPath $rel
            $dest = Join-Path $game $rel
            New-Item -ItemType Directory -Force -Path (Split-Path $dest) | Out-Null
            Copy-Item -LiteralPath $f.FullName -Destination $dest -Force
            $restored++
        }
    }
    $removed = 0
    $createdList = Join-Path $backup 'created.txt'
    if (Test-Path -LiteralPath $createdList) {
        foreach ($rel in [IO.File]::ReadAllLines($createdList)) {
            if (-not $rel) { continue }
            Assert-NotModPath $rel
            $p = Join-Path $game $rel
            if (Test-Path -LiteralPath $p -PathType Leaf) { Remove-Item -LiteralPath $p -Force; $removed++ }
        }
    }
    $marker = Join-Path $game $MarkerName
    if ((Test-Path -LiteralPath $marker) -and -not (Test-Path -LiteralPath (Join-Path $files $MarkerName))) { Remove-Item -LiteralPath $marker -Force }
    Write-Ok "Put back $restored file(s), removed $removed file(s) the install had added."
    # A second uninstall then goes one install further back
    [IO.File]::WriteAllText((Join-Path $backup 'restored.txt'), "Restored = $(Get-Date -Format s)")
    $acs = Join-Path $game 'x64\FF9_Data\Managed\Assembly-CSharp.dll'
    Write-Host ''
    Write-Host 'Restore finished.' -ForegroundColor Green
    Write-Host "  Game folder:          $game"
    Write-Host "  Assembly-CSharp.dll:  $((Get-Sha256 $acs).Substring(0, 12))"
    Write-Host "  The backup folder is kept: $backup"
}

# ---- main ----
try {
    $manifestPath = Join-Path $Here 'manifest.txt'
    if (-not (Test-Path -LiteralPath $manifestPath)) { throw "manifest.txt not found next to the script: run Install.bat from the unzipped package." }
    $manifest = Read-KeyValueFile $manifestPath
    Write-Host "Memoria online co-op build $($manifest['Build'])" -ForegroundColor White
    Write-Host "  branch $($manifest['Branch']), commit $($manifest['Commit']), built $($manifest['BuiltUtc']) UTC"
    Write-Host ''

    if ($GamePath) {
        if (-not (Test-GameFolder $GamePath)) { throw "Not a FINAL FANTASY IX folder: $GamePath" }
        $game = (Resolve-Path -LiteralPath $GamePath).Path
    } else {
        Write-Step 'Looking for FINAL FANTASY IX'
        $game = Find-GameFolder
        if (-not $game) { throw 'No game folder given.' }
    }
    Write-Ok $game
    Assert-GameNotRunning

    if (-not (Test-CanWrite $game)) {
        $isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
        if ($isAdmin) { throw "Can't write to $game." }
        Write-Step 'The game folder needs administrator rights: asking Windows to run this again as administrator'
        $argList = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', "`"$PSCommandPath`"", '-GamePath', "`"$game`"")
        if ($Uninstall) { $argList += '-Uninstall' }
        if ($BackupPath) { $argList += @('-BackupPath', "`"$BackupPath`"") }
        if ($OnlineDebug) { $argList += '-OnlineDebug' }
        if ($ForcePatcher) { $argList += '-ForcePatcher' }
        Start-Process -FilePath 'powershell.exe' -ArgumentList $argList -Verb RunAs -Wait
        exit 0
    }

    if ($Uninstall) { Invoke-Restore $game; Exit-Script 0 }

    $ourFiles = Get-OurFiles
    $baseFiles = Get-BaseFiles
    $launcherTarget = Get-LauncherTarget $game
    foreach ($f in $ourFiles) { if ($f.Target -eq 'FF9_Launcher.exe') { $f.Target = $launcherTarget } }

    Write-Step 'Checking the installed Memoria'
    $baseStatus = Get-BaseStatus $game $manifest
    $runPatcher = $ForcePatcher -or $baseStatus -ne 'same'
    switch ($baseStatus) {
        'missing' { Write-Warn2 'Memoria is not installed: the base patcher will install it.' }
        'other'   { Write-Warn2 "A different (or unknown) Memoria version is installed: the base patcher ($($manifest['BaseVersion'])) will update it." }
        'same'    { if ($ForcePatcher) { Write-Warn2 'Base Memoria matches; running the patcher anyway (-ForcePatcher).' } else { Write-Ok "Base Memoria is $($manifest['BaseVersion'])." } }
    }

    # Back up everything this run may overwrite
    $stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
    $backup = Join-Path $BackupRoot $stamp
    $backupFiles = Join-Path $backup 'files'
    Write-Step "Backing up to $backup"
    $toBackup = New-Object 'System.Collections.Generic.HashSet[string]' ([StringComparer]::OrdinalIgnoreCase)
    foreach ($f in $ourFiles) { [void]$toBackup.Add($f.Target) }
    foreach ($n in 'Memoria.ini', 'Settings.ini', $MarkerName) { [void]$toBackup.Add($n) }
    if ($runPatcher) {
        foreach ($rel in $baseFiles) {
            [void]$toBackup.Add($rel)
            # The patcher keeps the first original of each .exe/.dll it replaces as <name>.bak
            if ($rel -match '\.(exe|dll)$') { [void]$toBackup.Add([IO.Path]::ChangeExtension($rel, '.bak')) }
        }
        foreach ($n in 'FF9_Launcher.bak', 'FF9_Launcher.fix', 'Memoria.ini.bak', 'Settings.ini.bak') { [void]$toBackup.Add($n) }
        # The patcher deletes this folder before extracting
        $commonAsset = Join-Path $game 'StreamingAssets\Assets\Resources\CommonAsset'
        if (Test-Path -LiteralPath $commonAsset) {
            foreach ($f in Get-ChildItem -LiteralPath $commonAsset -Recurse -File) { [void]$toBackup.Add($f.FullName.Substring($game.Length + 1)) }
        }
    }
    $created = New-Object System.Collections.Generic.List[string]
    $savedCount = 0
    foreach ($rel in $toBackup) {
        Assert-NotModPath $rel
        $src = Join-Path $game $rel
        if (Test-Path -LiteralPath $src -PathType Leaf) {
            $dest = Join-Path $backupFiles $rel
            New-Item -ItemType Directory -Force -Path (Split-Path $dest) | Out-Null
            Copy-Item -LiteralPath $src -Destination $dest -Force
            $savedCount++
        } elseif ($rel -notmatch '^(?i)(Memoria|Settings)\.ini\.bak$') {
            # Not there yet: uninstall removes it again if this run (or the patcher) adds it
            $created.Add($rel)
        }
    }
    [IO.File]::WriteAllLines((Join-Path $backup 'created.txt'), $created)
    [IO.File]::WriteAllLines((Join-Path $backup 'backup.txt'), @(
        "GamePath = $game", "Made = $(Get-Date -Format s)", "Build = $($manifest['Build'])",
        "PatcherRun = $runPatcher", "FilesSaved = $savedCount", "FilesAdded = $($created.Count)"))
    Write-Ok "Saved $savedCount file(s); $($created.Count) file(s) will be new."

    $iniPath = Join-Path $game 'Memoria.ini'
    $iniBefore = $null
    if (Test-Path -LiteralPath $iniPath) { $iniBefore = [IO.File]::ReadAllBytes($iniPath) }
    $settingsPath = Join-Path $game 'Settings.ini'
    $settingsBefore = $null
    if (Test-Path -LiteralPath $settingsPath) { $settingsBefore = [IO.File]::ReadAllBytes($settingsPath) }

    if ($runPatcher) {
        $patcher = Join-Path $Here 'base\Memoria.Patcher.exe'
        if ((Get-Sha256 $patcher) -ne $manifest['BasePatcherSha256']) { throw 'base\Memoria.Patcher.exe does not match manifest.txt: the package is damaged.' }
        Write-Step "Running the base Memoria patcher ($($manifest['BaseVersion'])); this takes a minute"
        $log = Join-Path $backup 'patcher.log'
        $out = $null | & $patcher $game 2>&1
        $out | Out-File -LiteralPath $log -Encoding utf8
        $text = ($out | Out-String)
        if ($text -match 'could only complete|error has occurred') { throw "The base patcher reported a problem; see $log" }
        foreach ($rel in 'x64\FF9_Data\Managed\Memoria.Prime.dll', 'Memoria.ini') {
            if (-not (Test-Path -LiteralPath (Join-Path $game $rel))) { throw "The base patcher didn't install $rel; see $log" }
        }
        $launcherNow = Join-Path $game $launcherTarget
        if ((Get-Sha256 $launcherNow) -ne $manifest['BaseLauncherSha256']) { throw "The base patcher didn't install its launcher; see $log" }
        Write-Ok 'Base Memoria installed.'
    }

    # Keep the user's ini files byte for byte (the patcher rewrites them when it merges)
    if ($iniBefore) { [IO.File]::WriteAllBytes($iniPath, $iniBefore) }
    if ($settingsBefore) { [IO.File]::WriteAllBytes($settingsPath, $settingsBefore) }
    foreach ($n in 'Memoria.ini.bak', 'Settings.ini.bak') {
        $p = Join-Path $game $n
        if ((Test-Path -LiteralPath $p) -and -not (Test-Path -LiteralPath (Join-Path $backupFiles $n))) { Remove-Item -LiteralPath $p -Force }
    }

    Write-Step 'Copying the online co-op files'
    foreach ($f in $ourFiles) {
        $src = Join-Path (Join-Path $Here 'game') $f.Source
        $dest = Join-Path $game $f.Target
        New-Item -ItemType Directory -Force -Path (Split-Path $dest) | Out-Null
        Copy-Item -LiteralPath $src -Destination $dest -Force
        Write-Ok $f.Target
    }

    Write-Step 'Memoria.ini'
    $iniResult = Merge-MissingIniKeys $iniPath (Join-Path $Here 'base\Memoria.default.ini') ([bool]$OnlineDebug)
    if ($iniResult.Added.Count -eq 0 -and $iniResult.Changed.Count -eq 0) { Write-Ok 'Kept as it was (no keys missing).' }
    foreach ($a in $iniResult.Added) { Write-Ok "added $a" }
    foreach ($c in $iniResult.Changed) { Write-Ok "set $c (-OnlineDebug)" }

    Write-Step 'Verifying'
    $bad = 0
    foreach ($f in $ourFiles) {
        $h = Get-Sha256 (Join-Path $game $f.Target)
        if ($h -ne $f.Hash) { $bad++; Write-Warn2 "$($f.Target): hash $($h.Substring(0, 12)) but expected $($f.Hash.Substring(0, 12))" }
    }
    $acsHash = Get-Sha256 (Join-Path $game 'x64\FF9_Data\Managed\Assembly-CSharp.dll')
    if ($acsHash -ne $manifest['AssemblyCSharpSha256']) { $bad++; Write-Warn2 'Assembly-CSharp.dll does not match manifest.txt.' }
    if ($bad -gt 0) { throw "$bad file(s) failed verification. Run Uninstall.bat to undo." }
    Write-Ok "Assembly-CSharp.dll $($acsHash.Substring(0, 12)) matches."

    [IO.File]::WriteAllLines((Join-Path $game $MarkerName), @(
        "Build = $($manifest['Build'])", "Branch = $($manifest['Branch'])", "Commit = $($manifest['Commit'])",
        "BaseVersion = $($manifest['BaseVersion'])", "BasePatcherSha256 = $($manifest['BasePatcherSha256'])",
        "AssemblyCSharpSha256 = $acsHash", "Installed = $(Get-Date -Format s)", "Backup = $backup"))

    Write-Host ''
    Write-Host 'Installed.' -ForegroundColor Green
    Write-Host "  Game folder:          $game"
    Write-Host "  Build:                $($manifest['Build']) ($($manifest['Branch']) $($manifest['Commit']))"
    Write-Host "  Base Memoria:         $($manifest['BaseVersion'])$(if ($runPatcher) { ' (patcher run)' } else { ' (already installed)' })"
    Write-Host "  Assembly-CSharp.dll:  $($acsHash.Substring(0, 12)) (verified)"
    Write-Host "  Backup:               $backup"
    if (-not (Test-Path -LiteralPath (Join-Path $game 'HadesWorkshopMod'))) {
        Write-Warn2 'No HadesWorkshopMod folder: install the mod too (it comes separately) before playing online.'
    }
    Write-Host '  Undo with Uninstall.bat (restores the newest backup).'
    Exit-Script 0
} catch {
    Write-Host ''
    Write-Host "FAILED: $($_.Exception.Message)" -ForegroundColor Red
    Exit-Script 1
}
