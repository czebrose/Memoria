# [Multiplayer] Dev build of Assembly-CSharp.dll (and Memoria.Prime, XInputDotNetPure, UnityEngine.UI) into .\Output.
# CI=true disables Memoria's automatic deployment into the game folder; use install.ps1 for that.
param([string]$Configuration = 'Release')
$ErrorActionPreference = 'Stop'
$root = Resolve-Path (Join-Path $PSScriptRoot '..\..')
$vswhere = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe"
$msbuild = & $vswhere -latest -products * -requires Microsoft.Component.MSBuild -find 'MSBuild\**\Bin\MSBuild.exe' | Select-Object -First 1
if (-not $msbuild) { throw 'MSBuild not found (install Visual Studio Build Tools).' }
& $msbuild (Join-Path $root 'Assembly-CSharp\Assembly-CSharp.csproj') /restore /t:Build /nologo /verbosity:minimal `
    "/p:Configuration=$Configuration" /p:CI=true `
    "/p:CustomAfterMicrosoftCommonTargets=$PSScriptRoot\SkipNativeRefs.targets"
if ($LASTEXITCODE -ne 0) { throw "Build failed ($LASTEXITCODE)" }
Get-Item (Join-Path $root 'Output\Assembly-CSharp.dll') | Select-Object FullName, Length, LastWriteTime
