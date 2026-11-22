# [Multiplayer] Builds this branch and packages it as one installer zip in .\Output:
#   Output\MemoriaOnline-<branch>-<Assembly-CSharp hash>.zip
# The zip holds the stock base Memoria patcher, our DLLs and launcher, Install.bat / Uninstall.bat and a README.
# See docs\online\installer.md.
param(
    # The stock upstream Memoria patcher our fork is based on (c716e1fa = Canary of 2026-11-09)
    [string]$PatcherPath = 'C:\Program Files (x86)\Steam\steamapps\common\FINAL FANTASY IX\Memoria.Patcher.exe',
    [string]$ExpectedPatcherSha256 = '2C8CA404C9CCB517E50BB2A8852AA2DEA780755B4399143B3AB9AC61A2704F2B',
    [string]$BaseVersion = 'Canary 2026-11-09 (c716e1fa)',
    # Package a different patcher than the expected one (then also pass -BaseVersion)
    [switch]$AllowOtherPatcher,
    # Package what is already in Output\ instead of building first
    [switch]$SkipBuild
)
$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$out = Join-Path $root 'Output'

function Get-Sha256([string]$path) { (Get-FileHash -Algorithm SHA256 -LiteralPath $path).Hash.ToUpperInvariant() }

# ---- patcher ----
if (-not (Test-Path -LiteralPath $PatcherPath)) { throw "Base patcher not found: $PatcherPath (get Memoria.Patcher.exe from the upstream Memoria release)" }
$patcherSha = Get-Sha256 $PatcherPath
if ($patcherSha -ne $ExpectedPatcherSha256 -and -not $AllowOtherPatcher) {
    throw "$PatcherPath is not the expected base patcher ($($patcherSha.Substring(0, 12)) instead of $($ExpectedPatcherSha256.Substring(0, 12))). Pass -AllowOtherPatcher -BaseVersion '...' to package it anyway."
}

# Reads the payload the patcher appends to itself (format: Memoria.Patcher\Program.cs, Run/ExtractFiles):
# returns the list of files it writes, the hash of its launcher and its default Memoria.ini.
function Read-PatcherPayload([string]$path) {
    $magic = 0x004149524F4D454D
    $fs = [IO.File]::OpenRead($path)
    try {
        $br = New-Object IO.BinaryReader($fs)
        $pos = -1
        $fs.Seek(-0x18, 'End') | Out-Null
        if ($br.ReadInt64() -eq $magic) { $pos = $fs.Length - 0x18 }
        else {
            for ($off = 0x2920; $off -gt 0x2700; $off--) {   # signed patcher: the payload header sits before the signature
                $fs.Seek(-$off, 'End') | Out-Null
                if ($br.ReadInt64() -eq $magic) { $pos = $fs.Length - $off; break }
            }
        }
        if ($pos -lt 0) { throw "No Memoria payload in $path" }
        $fs.Seek($pos + 8, 'Begin') | Out-Null
        $left = $br.ReadInt64()
        $fs.Position = $br.ReadInt64()
        $gz = New-Object IO.Compression.GZipStream($fs, [IO.Compression.CompressionMode]::Decompress)
        $r = New-Object IO.BinaryReader($gz)
        $names = @{}
        $files = New-Object System.Collections.Generic.List[string]
        $launcherSha = $null; $iniBytes = $null
        $sha = [Security.Cryptography.SHA256]::Create()
        while ($left -gt 0) {
            $size = $r.ReadUInt32()
            [void]$r.ReadInt64()
            $count = $r.ReadByte()
            $parts = @()
            for ($i = 0; $i -lt $count; $i++) {
                $id = $r.ReadUInt16()
                if ($id -band 0x8000) {
                    $len = $r.ReadByte()
                    $name = [Text.Encoding]::UTF8.GetString($r.ReadBytes($len))
                    $names[[int]($id -band 0x7FFF)] = $name
                } else { $name = $names[[int]$id] }
                $parts += $name
            }
            $rel = ($parts -join '\') -replace '\{PLATFORM\}', 'x64'
            $data = $r.ReadBytes([int]$size)
            if ($data.Length -ne $size) { throw 'Truncated patcher payload' }
            $files.Add($rel)
            if ($rel -eq 'FF9_Launcher.exe') { $launcherSha = ([BitConverter]::ToString($sha.ComputeHash($data)) -replace '-', '') }
            if ($rel -eq 'Memoria.ini') { $iniBytes = $data }
            $left -= $size
        }
        @{ Files = $files; LauncherSha256 = $launcherSha; DefaultIni = $iniBytes }
    } finally { $fs.Dispose() }
}

Write-Host 'Reading the base patcher payload...'
$payload = Read-PatcherPayload $PatcherPath
if (-not $payload.LauncherSha256 -or -not $payload.DefaultIni) { throw 'The patcher payload has no FF9_Launcher.exe or Memoria.ini' }
if ($payload.Files | Where-Object { $_ -match '^(?i)HadesWorkshopMod\\' }) { throw 'The patcher payload contains mod files: not packaging it' }
Write-Host "  $($payload.Files.Count) files"

# ---- build ----
if (-not $SkipBuild) {
    & (Join-Path $PSScriptRoot 'build.ps1')
    $tf = ''
    $refs = "${env:ProgramFiles(x86)}\Reference Assemblies\Microsoft\Framework\.NETFramework"
    if (-not (Test-Path "$refs\v4.8")) {
        $tf = Get-ChildItem $refs -Directory -Name | Where-Object { $_ -match '^v4\.\d' } | Sort-Object | Select-Object -Last 1
        Write-Host "No .NET 4.8 targeting pack: building the launcher against $tf"
    }
    if ($tf) { & (Join-Path $PSScriptRoot 'build-launcher.ps1') -TargetFramework $tf } else { & (Join-Path $PSScriptRoot 'build-launcher.ps1') }
}

# Files we put over the base install: source in Output\ -> path in the game folder
$ours = [ordered]@{
    'Assembly-CSharp.dll'            = 'x64\FF9_Data\Managed\Assembly-CSharp.dll'
    'Memoria.Prime.dll'              = 'x64\FF9_Data\Managed\Memoria.Prime.dll'
    'XInputDotNetPure.dll'           = 'x64\FF9_Data\Managed\XInputDotNetPure.dll'
    'Launcher\Memoria.Launcher.exe'  = 'FF9_Launcher.exe'
}

$branch = (git -C $root rev-parse --abbrev-ref HEAD).Trim()
$commit = (git -C $root rev-parse --short=8 HEAD).Trim()
$dirty = [bool](git -C $root status --porcelain --untracked-files=no -- Assembly-CSharp Memoria.Launcher Memoria.Prime Memoria.XInputDotNetPure)
if ($dirty) { Write-Warning 'Uncommitted source changes: the package is marked +dirty'; $commit += '+dirty' }
$acsSha = Get-Sha256 (Join-Path $out 'Assembly-CSharp.dll')
$build = $acsSha.Substring(0, 12)
$name = "MemoriaOnline-$($branch -replace '[^A-Za-z0-9._-]', '-')-$build"

$stage = Join-Path $out "package\$name"
if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
New-Item -ItemType Directory -Force (Join-Path $stage 'base') | Out-Null

$hashLines = New-Object System.Collections.Generic.List[string]
foreach ($src in $ours.Keys) {
    $dest = Join-Path (Join-Path $stage 'game') $ours[$src]
    New-Item -ItemType Directory -Force (Split-Path $dest) | Out-Null
    Copy-Item (Join-Path $out $src) $dest
    $hashLines.Add("$(Get-Sha256 $dest)  $($ours[$src] -replace '\\', '/')")
}
[IO.File]::WriteAllLines((Join-Path $stage 'files.sha256'), $hashLines)
Copy-Item -LiteralPath $PatcherPath (Join-Path $stage 'base\Memoria.Patcher.exe')
[IO.File]::WriteAllBytes((Join-Path $stage 'base\Memoria.default.ini'), $payload.DefaultIni)
[IO.File]::WriteAllLines((Join-Path $stage 'base-files.txt'), $payload.Files)

$builtUtc = (Get-Date).ToUniversalTime().ToString('yyyy-MM-dd HH:mm')
[IO.File]::WriteAllLines((Join-Path $stage 'manifest.txt'), @(
    '# Written by tools\multiplayer\package.ps1; read by Install.ps1',
    "Build = $build", "Branch = $branch", "Commit = $commit", "BuiltUtc = $builtUtc",
    "AssemblyCSharpSha256 = $acsSha",
    "BaseVersion = $BaseVersion", "BasePatcherSha256 = $patcherSha", "BaseLauncherSha256 = $($payload.LauncherSha256)"))

$installer = Join-Path $PSScriptRoot 'installer'
Copy-Item (Join-Path $installer 'Install.ps1') $stage
foreach ($bat in 'Install.bat', 'Uninstall.bat') {   # cmd wants CRLF
    $text = [IO.File]::ReadAllText((Join-Path $installer $bat)) -replace "`r?`n", "`r`n"
    [IO.File]::WriteAllText((Join-Path $stage $bat), $text)
}
foreach ($sh in 'Install.sh', 'Uninstall.sh', 'install_linux.py') {   # Linux: LF only, no BOM
    $text = [IO.File]::ReadAllText((Join-Path $installer $sh)) -replace "`r`n", "`n"
    [IO.File]::WriteAllText((Join-Path $stage $sh), $text, (New-Object Text.UTF8Encoding($false)))
}
$readme = [IO.File]::ReadAllText((Join-Path $installer 'README.txt'))
$readme = $readme.Replace('{{BUILD}}', $build).Replace('{{BRANCH}}', $branch).Replace('{{COMMIT}}', $commit).Replace('{{BUILT}}', $builtUtc).Replace('{{ACS}}', $acsSha).Replace('{{BASEVERSION}}', $BaseVersion)
[IO.File]::WriteAllText((Join-Path $stage 'README.txt'), ($readme -replace "`r?`n", "`r`n"))
Copy-Item (Join-Path $root 'LICENSE') (Join-Path $stage 'LICENSE-Memoria.txt')
Copy-Item (Join-Path $root 'THIRD-PARTY-NOTICES.md') $stage

$zip = Join-Path $out "$name.zip"
if (Test-Path $zip) { Remove-Item $zip -Force }
Add-Type -AssemblyName System.IO.Compression.FileSystem
Add-Type -AssemblyName System.IO.Compression
# Entry by entry, so the Linux scripts keep their executable bit (Unix mode in the high 16 bits of the external
# attributes, which unzip and Linux file managers apply)
$archive = [IO.Compression.ZipFile]::Open($zip, [IO.Compression.ZipArchiveMode]::Create)
try {
    $parent = Split-Path $stage
    foreach ($file in Get-ChildItem -LiteralPath $stage -Recurse -File) {
        $entryName = $file.FullName.Substring($parent.Length + 1) -replace '\\', '/'
        $entry = [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive, $file.FullName, $entryName, [IO.Compression.CompressionLevel]::Optimal)
        $mode = if ($file.Extension -in '.sh', '.py') { 0x81ED } else { 0x81A4 }   # 0100755 / 0100644
        $entry.ExternalAttributes = $mode -shl 16
    }
} finally { $archive.Dispose() }
Get-Item $zip | Select-Object FullName, @{ n = 'MB'; e = { [math]::Round($_.Length / 1MB, 1) } }
Write-Host "Build $build ($branch $commit); Assembly-CSharp.dll $acsSha"
