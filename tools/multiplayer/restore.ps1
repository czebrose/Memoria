# [Multiplayer] Undo install.ps1: put back the Managed\ folder and Memoria.ini saved on the first install.
param(
    [string]$GamePath = 'C:\Program Files (x86)\Steam\steamapps\common\FINAL FANTASY IX',
    [string]$BackupPath = (Join-Path $PSScriptRoot '..\..\..\MemoriaMultiplayerBackup')
)
$ErrorActionPreference = 'Stop'
$backup = [IO.Path]::GetFullPath($BackupPath)
if (-not (Test-Path (Join-Path $backup 'Managed'))) { throw "No backup at $backup" }
Copy-Item (Join-Path $backup 'Managed\*') (Join-Path $GamePath 'x64\FF9_Data\Managed') -Recurse -Force
Copy-Item (Join-Path $backup 'Memoria.ini') (Join-Path $GamePath 'Memoria.ini') -Force
Write-Host "Restored Managed\ and Memoria.ini from $backup"
