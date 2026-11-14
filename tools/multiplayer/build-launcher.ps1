# [Multiplayer] Dev build of the launcher (Memoria.Launcher.exe) into .\Output\Launcher, without deploying it.
# The project targets .NET Framework 4.8; if only an older targeting pack is installed, pass e.g. -TargetFramework v4.7.1
# (the launcher uses no 4.8-only API; the exe still runs on the installed .NET 4.8 runtime).
param([string]$Configuration = 'Release', [string]$TargetFramework = '')
$ErrorActionPreference = 'Stop'
$root = Resolve-Path (Join-Path $PSScriptRoot '..\..')
$vswhere = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe"
$msbuild = & $vswhere -latest -products * -requires Microsoft.Component.MSBuild -find 'MSBuild\**\Bin\MSBuild.exe' | Select-Object -First 1
if (-not $msbuild) { throw 'MSBuild not found (install Visual Studio Build Tools).' }
$extra = @()
if ($TargetFramework) { $extra += "/p:TargetFrameworkVersion=$TargetFramework" }
& $msbuild (Join-Path $root 'Memoria.Launcher\Memoria.Launcher.csproj') /restore /t:Build /nologo /verbosity:minimal `
    "/p:Configuration=$Configuration" /p:CI=true "/p:SolutionDir=$root\" `
    "/p:CustomAfterMicrosoftCommonTargets=$PSScriptRoot\SkipNativeRefs.targets" @extra
if ($LASTEXITCODE -ne 0) { throw "Build failed ($LASTEXITCODE)" }
Get-Item (Join-Path $root 'Output\Launcher\Memoria.Launcher.exe') | Select-Object FullName, Length, LastWriteTime
