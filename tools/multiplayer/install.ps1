# [Multiplayer] Copy the dev-built managed DLLs into the game. The first run backs up the originals
# (x64\FF9_Data\Managed\ and Memoria.ini) to $BackupPath; restore.ps1 puts them back.
# The backup must live OUTSIDE the game folder: the Memoria launcher treats any game subfolder
# containing a Memoria.ini as a mod and adds it to [Mod] FolderNames.
param(
    [string]$GamePath = 'C:\Program Files (x86)\Steam\steamapps\common\FINAL FANTASY IX',
    [string]$BackupPath = (Join-Path $PSScriptRoot '..\..\..\MemoriaMultiplayerBackup')
)
$ErrorActionPreference = 'Stop'
$root = Resolve-Path (Join-Path $PSScriptRoot '..\..')
$managed = Join-Path $GamePath 'x64\FF9_Data\Managed'
$backup = [IO.Path]::GetFullPath($BackupPath)
if ($backup.StartsWith([IO.Path]::GetFullPath($GamePath), [StringComparison]::OrdinalIgnoreCase)) { throw 'BackupPath must be outside the game folder' }
if (-not (Test-Path $managed)) { throw "Game not found at $GamePath" }
if (-not (Test-Path $backup)) {
    New-Item -ItemType Directory $backup | Out-Null
    Copy-Item $managed (Join-Path $backup 'Managed') -Recurse
    Copy-Item (Join-Path $GamePath 'Memoria.ini') $backup
    Write-Host "Backed up Managed\ and Memoria.ini to $backup"
} else {
    Write-Host "Backup already exists at $backup (kept as-is)"
}
foreach ($dll in 'Assembly-CSharp.dll', 'Memoria.Prime.dll', 'XInputDotNetPure.dll') {
    Copy-Item (Join-Path $root "Output\$dll") (Join-Path $managed $dll) -Force
    Write-Host "Installed $dll"
}
