# Checks that every type and member the Black Waltz mod looks up by name (mod-lookups.txt) exists in a build of
# Assembly-CSharp.dll. Exit code 0: all found; 1: something is missing (each one is listed). See README.md.
#
#   .\Test-ModLookups.ps1                       # ..\..\..\Output\Assembly-CSharp.dll
#   .\Test-ModLookups.ps1 -Dll <path>
#
# It loads the DLL for reflection only (nothing in it runs), resolving Unity's assemblies from References\.
#
# Lookups for the local (same-PC) multiplayer build, listed in mod-lookups-other-build.txt, are skipped.
param([string]$Dll)
$ErrorActionPreference = 'Stop'
$root = Resolve-Path (Join-Path $PSScriptRoot '..\..\..')
if (-not $Dll) { $Dll = Join-Path $root 'Output\Assembly-CSharp.dll' }
$Dll = (Resolve-Path $Dll).Path
$listFile = Join-Path $PSScriptRoot 'mod-lookups.txt'
$otherBuild = @(Get-Content (Join-Path $PSScriptRoot 'mod-lookups-other-build.txt') | Where-Object { $_ -and $_ -notmatch '^\s*#' } | ForEach-Object { $_.Trim() })

# Dependencies come from next to the DLL first, then References\ (UnityEngine and the game's own assemblies)
$searchDirs = @((Split-Path $Dll), (Join-Path $root 'Output'), (Join-Path $root 'References')) | Select-Object -Unique
$resolver = [ResolveEventHandler] {
    param($sender, $e)
    $name = (New-Object Reflection.AssemblyName $e.Name).Name
    foreach ($d in $searchDirs) {
        $p = Join-Path $d "$name.dll"
        if (Test-Path $p) { return [Reflection.Assembly]::LoadFrom($p) }
    }
    return $null
}
[AppDomain]::CurrentDomain.add_AssemblyResolve($resolver)
$asm = [Reflection.Assembly]::LoadFrom($Dll)

$all = [Reflection.BindingFlags]'Public, NonPublic, Instance, Static, DeclaredOnly'
$typesByName = $null
function Find-Type([string]$name) {
    $t = $asm.GetType($name, $false)
    if ($t) { return $t }
    # A short name (typeof(Configuration) in the mod's source): the one type with that name
    if (-not $script:typesByName) {
        try { $types = $asm.GetTypes() } catch [Reflection.ReflectionTypeLoadException] { $types = $_.Exception.Types | Where-Object { $_ } }
        $script:typesByName = $types | Group-Object Name -AsHashTable -AsString
    }
    $last = ($name -split '[.+]')[-1]
    $c = @($script:typesByName[$last] | Where-Object { $_ -and $_.FullName } | Where-Object { $_.FullName -eq $name -or $_.FullName.EndsWith(".$name") -or $_.FullName.EndsWith("+$name") -or $_.Name -eq $name })
    if ($c.Count -eq 1) { return $c[0] }
    return $null
}
# Members named $name of kind $kind on $type or its base types
function Find-Members($type, [string]$kind, [string]$name) {
    for ($t = $type; $t; $t = $t.BaseType) {
        $m = @()
        if ($kind -in 'field', 'member') { $m += @($t.GetFields($all) | Where-Object { $_.Name -eq $name }) }
        if ($kind -in 'property', 'member') { $m += @($t.GetProperties($all) | Where-Object { $_.Name -eq $name }) }
        if ($kind -eq 'method') { $m += @($t.GetMethods($all) | Where-Object { $_.Name -eq $name }) }
        if ($m.Count -gt 0) { return $m }
    }
    return @()
}
function Get-ItemType($t) {
    if ($t.IsArray) { return $t.GetElementType() }
    if ($t.IsGenericType) { return $t.GetGenericArguments()[-1] }   # List<T>: T; Dictionary<K, V>: V
    foreach ($i in $t.GetInterfaces()) { if ($i.IsGenericType -and $i.GetGenericTypeDefinition() -eq [Collections.Generic.IEnumerable`1]) { return $i.GetGenericArguments()[0] } }
    return $null
}
# A type expression from the list: Name, Outer+Nested, Type::member, Type[]
function Resolve-TypeExpr([string]$expr) {
    if ($expr -notmatch '^([^:\[]+)((?:::\w+|\[\])*)$') { return @{ Error = "can't read type '$expr'" } }
    $t = Find-Type $Matches[1]
    if (-not $t) { return @{ Error = "type $($Matches[1]) not found" } }
    foreach ($step in [regex]::Matches($Matches[2], '::\w+|\[\]')) {
        if ($step.Value -eq '[]') {
            $item = Get-ItemType $t
            if (-not $item) { return @{ Error = "$($t.FullName) isn't a collection" } }
            $t = $item; continue
        }
        $n = $step.Value.Substring(2)
        $m = @(Find-Members $t 'member' $n) + @(Find-Members $t 'method' $n) | Select-Object -First 1
        if (-not $m) { return @{ Error = "$($t.FullName) has no member $n" } }
        $t = switch ($m.MemberType) { 'Field' { $m.FieldType } 'Property' { $m.PropertyType } default { $m.ReturnType } }
    }
    return @{ Type = $t }
}

$rows = @(Get-Content $listFile | Where-Object { $_ -and -not $_.StartsWith('#') })
$source = (Get-Content $listFile | Where-Object { $_ -like '# Source:*' } | Select-Object -First 1) -replace '^# Source:\s*', ''
$fails = New-Object System.Collections.Generic.List[string]
$skipped = 0
foreach ($row in $rows) {
    $p = $row -split "`t"
    $typeExpr, $kind, $name, $files = $p[0], $p[1], $p[2], $p[3]
    $text = "$typeExpr $kind $name".TrimEnd()
    if (@($otherBuild | Where-Object { $text.StartsWith($_) }).Count -gt 0) { $skipped++; continue }
    $r = Resolve-TypeExpr $typeExpr
    $what = if ($kind -eq 'type') { "type $typeExpr" } else { "$typeExpr $kind $name" }
    if ($r.Error) { $fails.Add("$what`: $($r.Error)   (used by $files)"); continue }
    if ($kind -eq 'type') { continue }
    if (@(Find-Members $r.Type $kind $name).Count -eq 0) {
        $kindText = if ($kind -eq 'member') { 'field or property' } else { $kind }
        $fails.Add("$what`: $($r.Type.FullName) has no $kindText $name   (used by $files)")
    }
}

Write-Host "Mod lookups: $($rows.Count - $skipped) checked against $Dll (list from $source)"
if ($skipped -gt 0) { Write-Host "  $skipped skipped: for the local multiplayer build (mod-lookups-other-build.txt)" }
if ($fails.Count -gt 0) {
    foreach ($f in $fails) { Write-Host "  FAIL  $f" -ForegroundColor Red }
    Write-Host "$($fails.Count) of the mod's lookups are missing from this build: the mod's code that uses them stops working." -ForegroundColor Red
    Write-Host 'Rename it back, or tell the mod (see tools\multiplayer\mod-check\README.md).' -ForegroundColor Red
    exit 1
}
Write-Host 'Mod lookups: all found' -ForegroundColor Green
exit 0
