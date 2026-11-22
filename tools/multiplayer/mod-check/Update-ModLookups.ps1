# Rebuilds mod-lookups.txt: every type and member of Assembly-CSharp the Black Waltz mod's C# scripts look up by
# name (reflection). Test-ModLookups.ps1 then checks each one still exists in a build. See README.md.
#
#   .\Update-ModLookups.ps1                  # the mod's main branch on GitHub (needs gh, signed in with access)
#   .\Update-ModLookups.ps1 -Ref <sha|tag>   # another commit
#   .\Update-ModLookups.ps1 -ModPath <dir>   # a local checkout of the mod instead
#
# The scan follows where a lookup's type comes from. Where it can't (a method parameter, an expression such as
# UIManager.Battle), mod-lookups-types.txt says. Any lookup it can't place is listed, and nothing is written.
param(
    [string]$Ref = 'main',
    [string]$ModPath,
    [string]$Repo = 'Vuux2/ff9-black-waltz-mod'
)
$ErrorActionPreference = 'Stop'
$sourcesRel = 'src/memoria/StreamingAssets/Scripts/Sources'
$here = $PSScriptRoot
$outFile = Join-Path $here 'mod-lookups.txt'
$typesFile = Join-Path $here 'mod-lookups-types.txt'

# ---- Get the mod's script sources: name -> text ---------------------------------------------------------------
$sources = [ordered]@{}
if ($ModPath) {
    $dir = Join-Path $ModPath $sourcesRel
    if (-not (Test-Path $dir)) { $dir = $ModPath }
    $files = @(Get-ChildItem $dir -Filter *.cs)
    if ($files.Count -eq 0) { throw "No .cs files in $dir" }
    foreach ($f in $files | Sort-Object Name) { $sources[$f.Name] = [IO.File]::ReadAllText($f.FullName) }
    $commit = (& git -C $ModPath rev-parse HEAD 2>$null)
    $dirty = (& git -C $ModPath status --porcelain -- $sourcesRel 2>$null)
    $origin = if ($commit) { "$Repo @ $commit (local checkout$(if ($dirty) { ', with uncommitted changes' }))" } else { 'a local folder (not a git checkout)' }
}
else {
    if (-not (Get-Command gh -ErrorAction SilentlyContinue)) { throw 'gh (GitHub CLI) not found: install it, or use -ModPath' }
    $commit = (& gh api "repos/$Repo/commits/$Ref" -q .sha)
    if ($LASTEXITCODE -ne 0 -or -not $commit) { throw "Can't read $Repo at '$Ref' (gh auth status? access to the repo?)" }
    $list = (& gh api "repos/$Repo/contents/$sourcesRel`?ref=$commit") | ConvertFrom-Json
    foreach ($item in $list | Where-Object { $_.name -like '*.cs' } | Sort-Object name) {
        $b64 = (& gh api "repos/$Repo/git/blobs/$($item.sha)" -q .content)
        $sources[$item.name] = [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String(($b64 -join '')))
    }
    $origin = "$Repo @ $commit"
}
Write-Host "Scanning $($sources.Count) scripts from $origin"

# ---- Types the scan can't see (mod-lookups-types.txt) ---------------------------------------------------------
# <file> <name> <type>: in that mod file, member <name> is looked up on <type> ('-': a type outside Assembly-CSharp,
# not checked). Lookups on objects read through these are then followed automatically.
$map = @{}
if (Test-Path $typesFile) {
    foreach ($row in Get-Content $typesFile) {
        if ($row -match '^\s*(#|$)') { continue }
        $p = $row.Trim() -split '\s+'
        if ($p.Count -ge 3) { $map["$($p[0]) $($p[1])"] = $p[2] }
    }
}
$usedMap = @{}

# ---- Scan -----------------------------------------------------------------------------------------------------
# Kinds: type, field, property, method, member (field or property: the mod's GetMember/Get helpers try both).
# A type is written as its full name, Outer+Nested, Type::member (the type of that field or property, or a method's
# return type) or Type[] (a collection's item type); Test-ModLookups.ps1 works these out from the build.
$found = New-Object System.Collections.Generic.List[object]
$unresolved = New-Object System.Collections.Generic.List[object]
$rhs = '\s*=(?!=)\s*(?:[^;=]|==|!=)*?'
$recv = '(typeof\(\s*[\w.]+\s*\)|\w+\s*\.\s*GetType\(\s*\)|\w+)(?:\s*\.\s*(FieldType|PropertyType|ReturnType))?'
$reAssignType = [regex]('\b(\w+)' + $rhs + '\bGetType\(\s*"([^"]+)"\s*\)')
$reAssignTypeof = [regex]'\b(\w+)\s*=\s*typeof\(\s*([\w.]+)\s*\)\s*[;)]'
$reAssignNested = [regex]('\b(\w+)' + $rhs + $recv + '\s*\.\s*GetNestedType\(\s*"([^"]+)"')
$reAssignMemberType = [regex]'\b(\w+)\s*=\s*\(?\s*(\w+)\s*\.\s*(FieldType|PropertyType|ReturnType)\b'
$reAssignRuntimeType = [regex]'\b(\w+)\s*=\s*(\w+)\s*\.\s*GetType\(\s*\)\s*;'
$reAssignMember = [regex]('\b(\w+)' + $rhs + $recv + '\s*\.\s*Get(Field|Property|Method)\(\s*"([^"]+)"')
$reAssignValue = [regex]('\b(\w+)' + $rhs + '\b(\w+)\s*\.\s*GetValue\(')
# The mod's helpers that look a member up by name on an object: GetMember(obj, "x"), Get(obj, "x"), Prop<T>(obj, "x")
$helper = '\b(?:GetMember|Get|Prop)(?:<[\w.]+>)?\('
$reAssignHelper = [regex]('\b(\w+)' + $rhs + $helper + '\s*([\w.]+)\s*,\s*"([^"]+)"\s*\)')
$reAssignItem = [regex]'\b(\w+)\s*=\s*(\w+)\s*\[[^\]]+\]\s*;'
$reForeach = [regex]'\bforeach\s*\(\s*[\w.]+\s+(\w+)\s+in\s+(\w+)(?:\s*\.\s*Values)?\s*\)'
$reType = [regex]'\bGetType\(\s*"([^"]+)"\s*\)'
$reChained = [regex]'\bGetType\(\s*"([^"]+)"\s*\)\s*\.\s*Get(Field|Property|Method)\(\s*"([^"]+)"'
$reOnRecv = [regex]($recv + '\s*\.\s*Get(Field|Property|Method)\(\s*"([^"]+)"')
$reHelper = [regex]($helper + '\s*([^,()"]+?)\s*,\s*"([^"]+)"\s*\)')
$reDynamic = [regex]'\.\s*Get(Field|Property|Method)\(\s*(?!")(\w+)\s*[,)]'

foreach ($file in $sources.Keys) {
    $lines = $sources[$file] -split "`r?`n"
    # Pass 1: variables holding a Type, a FieldInfo/PropertyInfo/MethodInfo, or an object read through one
    $assigns = @{}
    function Add-Assign($v, $line, $how) {
        if (-not $assigns.ContainsKey($v)) { $assigns[$v] = New-Object System.Collections.Generic.List[object] }
        $assigns[$v].Add(@{ Line = $line; How = $how })
    }
    for ($i = 0; $i -lt $lines.Count; $i++) {
        $l = $lines[$i]
        foreach ($m in $reAssignType.Matches($l)) { Add-Assign $m.Groups[1].Value $i @{ What = 'Type'; Type = $m.Groups[2].Value } }
        foreach ($m in $reAssignTypeof.Matches($l)) { Add-Assign $m.Groups[1].Value $i @{ What = 'Type'; Type = $m.Groups[2].Value } }
        foreach ($m in $reAssignNested.Matches($l)) { Add-Assign $m.Groups[1].Value $i @{ What = 'Type'; Recv = $m.Groups[2].Value; Via = $m.Groups[3].Value; Nested = $m.Groups[4].Value } }
        foreach ($m in $reAssignMemberType.Matches($l)) { Add-Assign $m.Groups[1].Value $i @{ What = 'Type'; OfMember = $m.Groups[2].Value } }
        foreach ($m in $reAssignRuntimeType.Matches($l)) { Add-Assign $m.Groups[1].Value $i @{ What = 'Type'; RuntimeOf = $m.Groups[2].Value } }
        foreach ($m in $reAssignMember.Matches($l)) { Add-Assign $m.Groups[1].Value $i @{ What = 'Member'; Recv = $m.Groups[2].Value; Via = $m.Groups[3].Value; Member = $m.Groups[5].Value } }
        foreach ($m in $reAssignValue.Matches($l)) { Add-Assign $m.Groups[1].Value $i @{ What = 'Value'; ValueOf = $m.Groups[2].Value } }
        foreach ($m in $reAssignHelper.Matches($l)) { Add-Assign $m.Groups[1].Value $i @{ What = 'Value'; HelperRecv = $m.Groups[2].Value; Member = $m.Groups[3].Value } }
        foreach ($m in $reAssignItem.Matches($l)) { Add-Assign $m.Groups[1].Value $i @{ What = 'Value'; ItemOf = $m.Groups[2].Value } }
        foreach ($m in $reForeach.Matches($l)) { Add-Assign $m.Groups[1].Value $i @{ What = 'Value'; ItemOf = $m.Groups[2].Value } }
    }
    function Get-Assign([string]$v, [int]$line, [string]$what) {
        if (-not $assigns.ContainsKey($v)) { return $null }
        $all = @($assigns[$v] | Where-Object { $_.How.What -eq $what })
        if ($all.Count -eq 0) { return $null }
        $before = @($all | Where-Object { $_.Line -le $line } | Sort-Object { $_.Line })
        if ($before.Count -gt 0) { return $before[-1] }
        return $all[0]
    }
    function Get-Mapped([string]$name) {
        $key = "$file $name"
        if ($map.ContainsKey($key) -and $map[$key] -ne '-') { $usedMap[$key] = $true; return $map[$key] }
        return $null
    }
    # The type a receiver stands for: typeof(X), a Type variable, obj.GetType(), or a member's type (fi.FieldType)
    function Resolve-Recv([string]$r, [string]$via, [int]$line, [int]$depth) {
        if ($depth -gt 12) { return $null }
        if ($via) {
            $a = Get-Assign $r $line 'Member'
            if (-not $a) { return $null }
            $t = Resolve-Recv $a.How.Recv $a.How.Via $a.Line ($depth + 1)
            if (-not $t) { $t = Get-Mapped $a.How.Member }
            if ($t) { return "${t}::$($a.How.Member)" } else { return $null }
        }
        if ($r -match '^typeof\(\s*([\w.]+)\s*\)$') { return $Matches[1] }
        if ($r -match '^(\w+)\s*\.\s*GetType\(\s*\)$') { return Resolve-Value $Matches[1] $line ($depth + 1) }
        $a = Get-Assign $r $line 'Type'
        if (-not $a) { return $null }
        $h = $a.How
        if ($h.ContainsKey('Type')) { return $h.Type }
        if ($h.ContainsKey('Nested')) {
            $t = Resolve-Recv $h.Recv $h.Via $a.Line ($depth + 1)
            if ($t) { return "$t+$($h.Nested)" } else { return $null }
        }
        if ($h.ContainsKey('OfMember')) { return Resolve-Recv $h.OfMember 'FieldType' $a.Line ($depth + 1) }
        if ($h.ContainsKey('RuntimeOf')) { return Resolve-Value $h.RuntimeOf $a.Line ($depth + 1) }
        return $null
    }
    # The declared type of an object read through a lookup (fi.GetValue, Get(obj, "x"), dict[key], foreach)
    function Resolve-Value([string]$v, [int]$line, [int]$depth) {
        if ($depth -gt 12) { return $null }
        $a = Get-Assign $v $line 'Value'
        if (-not $a) { return $null }
        $h = $a.How
        if ($h.ContainsKey('ValueOf')) { return Resolve-Recv $h.ValueOf 'FieldType' $a.Line ($depth + 1) }
        if ($h.ContainsKey('ItemOf')) {
            $t = Resolve-Value $h.ItemOf $a.Line ($depth + 1)
            if ($t) { return "$t[]" } else { return $null }
        }
        if ($h.ContainsKey('HelperRecv')) {
            $t = Resolve-Value $h.HelperRecv $a.Line ($depth + 1)
            if (-not $t) { $t = Get-Mapped $h.Member }
            if ($t) { return "${t}::$($h.Member)" } else { return $null }
        }
        return $null
    }
    # Pass 2: the lookups
    for ($i = 0; $i -lt $lines.Count; $i++) {
        $l = $lines[$i]
        if ($l -match '^\s*//') { continue }
        $where = "${file}:$($i + 1)"
        foreach ($m in $reType.Matches($l)) { $found.Add(@{ Type = $m.Groups[1].Value; Kind = 'type'; Name = ''; Where = $where }) }
        foreach ($m in $reAssignNested.Matches($l)) {
            $t = Resolve-Recv $m.Groups[2].Value $m.Groups[3].Value $i 0
            if ($t) { $found.Add(@{ Type = "$t+$($m.Groups[4].Value)"; Kind = 'type'; Name = ''; Where = $where }) }
        }
        $done = @{}
        foreach ($m in $reChained.Matches($l)) {
            $found.Add(@{ Type = $m.Groups[1].Value; Kind = $m.Groups[2].Value.ToLower(); Name = $m.Groups[3].Value; Where = $where })
            $done[$m.Groups[3].Index] = $true
        }
        foreach ($m in $reOnRecv.Matches($l)) {
            if ($done.ContainsKey($m.Groups[4].Index) -or $m.Groups[1].Value -eq 'GetType') { continue }
            $kind = $m.Groups[3].Value.ToLower(); $name = $m.Groups[4].Value
            $t = Resolve-Recv $m.Groups[1].Value $m.Groups[2].Value $i 0
            if (-not $t) { $t = Get-Mapped $name }
            if ($t) { $found.Add(@{ Type = $t; Kind = $kind; Name = $name; Where = $where }) }
            elseif (-not $map.ContainsKey("$file $name")) { $unresolved.Add(@{ File = $file; Kind = $kind; Name = $name; Where = $where }) }
        }
        foreach ($m in $reHelper.Matches($l)) {
            $name = $m.Groups[2].Value
            $t = Resolve-Value $m.Groups[1].Value $i 0
            if (-not $t) { $t = Get-Mapped $name }
            if ($t) { $found.Add(@{ Type = $t; Kind = 'member'; Name = $name; Where = $where }) }
            elseif (-not $map.ContainsKey("$file $name")) { $unresolved.Add(@{ File = $file; Kind = 'member'; Name = $name; Where = $where }) }
        }
        foreach ($m in $reDynamic.Matches($l)) {
            # A name that isn't written in the source: fine inside the helpers above (their callers are scanned)
            $from = [Math]::Max(0, $i - 12)
            if (-not (($lines[$from..$i] -join "`n") -match '\b(GetMember|Get|Prop)(<\w+>)?\(\s*(System\.)?Object \w+,\s*String \w+\)')) {
                Write-Warning "${where}: a lookup by a name that isn't written in the source ($($m.Groups[2].Value)): check it by hand"
            }
        }
    }
}

if ($unresolved.Count -gt 0) {
    Write-Host ''
    Write-Host "These lookups don't say which type they're on. Add a line for each to mod-lookups-types.txt" -ForegroundColor Yellow
    Write-Host "(<file> <name> <full type name>, or '-' if the type isn't in Assembly-CSharp), then run this again:" -ForegroundColor Yellow
    $unresolved | ForEach-Object { "  $($_.File) $($_.Name)   ($($_.Kind), $($_.Where))" } | Sort-Object -Unique | ForEach-Object { Write-Host $_ }
    exit 1
}
foreach ($key in $map.Keys) {
    if ($map[$key] -ne '-' -and -not $usedMap.ContainsKey($key)) { Write-Warning "mod-lookups-types.txt: '$key' isn't used any more (it can go)" }
}

# A property and a field of the same name looked up within a few lines are alternatives, either will do:
# GetProperty("x") ?? GetField("x"), or instField = GetField("x"); instProp = GetProperty("x"); if (instField != null) ...
$near = 3
$groups = $found | Where-Object { $_.Kind -in 'field', 'property' } | Group-Object { "$(($_.Where -split ':')[0])|$($_.Type)|$($_.Name)" }
foreach ($g in $groups) {
    $fields = @($g.Group | Where-Object { $_.Kind -eq 'field' })
    $props = @($g.Group | Where-Object { $_.Kind -eq 'property' })
    foreach ($f in $fields) {
        foreach ($p in $props) {
            if ([Math]::Abs([int]($f.Where -split ':')[1] - [int]($p.Where -split ':')[1]) -le $near) { $f.Kind = 'member'; $p.Kind = 'member' }
        }
    }
}

# ---- Write mod-lookups.txt ------------------------------------------------------------------------------------
$rows = $found | Group-Object { "$($_.Type)`t$($_.Kind)`t$($_.Name)" } | Sort-Object Name | ForEach-Object {
    $files = ($_.Group | ForEach-Object { ($_.Where -split ':')[0] } | Sort-Object -Unique) -join ','
    "$($_.Name)`t$files"
}
$header = @(
    '# Generated by Update-ModLookups.ps1: do not edit by hand (see README.md).',
    "# Source: $origin",
    '# <type> <kind> <member> <mod files>. Kind: type, field, property, method, or member (field or property).',
    '# Type: full name, Outer+Nested, Type::member (that member''s type), Type[] (a collection''s item type).'
)
[IO.File]::WriteAllLines($outFile, [string[]]($header + $rows), (New-Object Text.UTF8Encoding $false))
Write-Host "Wrote $($rows.Count) lookups to $outFile"
