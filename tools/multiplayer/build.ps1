# [Multiplayer] Dev build of Assembly-CSharp.dll (and Memoria.Prime, XInputDotNetPure, UnityEngine.UI) into .\Output.
# CI=true disables Memoria's automatic deployment into the game folder; use install.ps1 for that.
# After building, checks that the Black Waltz mod's scripts still find what they use of the build (mod-check\):
# what their compiled code calls (Test-ModReferences.ps1) and what they look up by name (Test-ModLookups.ps1).
# -SkipModCheck skips both.
param([string]$Configuration = 'Release', [switch]$SkipModCheck)
$ErrorActionPreference = 'Stop'
$root = Resolve-Path (Join-Path $PSScriptRoot '..\..')
$vswhere = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe"
$msbuild = & $vswhere -latest -products * -requires Microsoft.Component.MSBuild -find 'MSBuild\**\Bin\MSBuild.exe' | Select-Object -First 1
if (-not $msbuild) { throw 'MSBuild not found (install Visual Studio Build Tools).' }
& $msbuild (Join-Path $root 'Assembly-CSharp\Assembly-CSharp.csproj') /restore /t:Build /nologo /verbosity:minimal `
    "/p:Configuration=$Configuration" /p:CI=true `
    "/p:CustomAfterMicrosoftCommonTargets=$PSScriptRoot\SkipNativeRefs.targets"
if ($LASTEXITCODE -ne 0) { throw "Build failed ($LASTEXITCODE)" }
Get-Item (Join-Path $root 'Output\Assembly-CSharp.dll') | Select-Object FullName, Length, LastWriteTime | Format-Table -AutoSize | Out-Host
if (-not $SkipModCheck) {
    # Each in its own process: loading the DLLs to read them keeps them locked until that process ends
    $failed = 0
    foreach ($check in 'Test-ModReferences.ps1', 'Test-ModLookups.ps1') {
        & powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot "mod-check\$check")
        if ($LASTEXITCODE -ne 0) { $failed++ }
    }
    if ($failed -gt 0) { throw 'The build changed or lost things the Black Waltz mod uses (listed above). -SkipModCheck builds anyway.' }
}
