# Checks that a build of Assembly-CSharp / Memoria.Prime still has every type, method and field the Black Waltz
# mod's compiled scripts use (mod-references.txt), with the same signature. Exit code 0: all there; 1: something
# changed or is missing (each one is listed). See README.md.
#
#   .\Test-ModReferences.ps1                    # the build in ..\..\..\Output
#   .\Test-ModReferences.ps1 -BuildDir <dir>    # a folder holding Assembly-CSharp.dll and Memoria.Prime.dll
#
# It loads the DLLs to read them (nothing in them runs), resolving Unity's assemblies from References\.
param([string]$BuildDir)
$ErrorActionPreference = 'Stop'
$root = Resolve-Path (Join-Path $PSScriptRoot '..\..\..')
if (-not $BuildDir) { $BuildDir = Join-Path $root 'Output' }
$BuildDir = (Resolve-Path $BuildDir).Path
$listFile = Join-Path $PSScriptRoot 'mod-references.txt'

Add-Type -Path (Join-Path $PSScriptRoot 'ModReferences.cs')
[ModReferences]::Setup([string[]]@($BuildDir, (Join-Path $root 'References')))

$lines = @(Get-Content $listFile | Where-Object { $_ -and -not $_.StartsWith('#') })
$source = (Get-Content $listFile | Where-Object { $_ -like '# Source:*' } | Select-Object -First 1) -replace '^# Source:\s*', ''
$fails = New-Object System.Collections.Generic.List[string]
foreach ($line in $lines) {
    $parts = $line -split "`t# ", 2
    $why = [ModReferences]::CheckLine($parts[0])
    if ($why) { $fails.Add("$why   (used by $($parts[1] -replace 'HadesWorkshopMod\.', ''))") }
}

Write-Host "Mod references: $($lines.Count) checked against $BuildDir (list from $source)"
if ($fails.Count -gt 0) {
    foreach ($f in $fails) { Write-Host "  FAIL  $f" -ForegroundColor Red }
    Write-Host "$($fails.Count) of what the mod's compiled scripts use changed or is missing in this build: those scripts fail" -ForegroundColor Red
    Write-Host 'when the game loads or runs them. Change it back, or agree it with the mod (see tools\multiplayer\mod-check\README.md).' -ForegroundColor Red
    exit 1
}
Write-Host 'Mod references: all found' -ForegroundColor Green
exit 0
