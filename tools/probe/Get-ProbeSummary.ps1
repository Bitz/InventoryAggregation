#Requires -Version 7.2
<#
.SYNOPSIS
    Summarizes a probe run: statuses, timing, paging consistency, completeness, record quality.

.DESCRIPTION
    Reads recordings/<run>/index.jsonl and the saved bodies, and writes summary.md next to them.
    Bodies are parsed with System.Text.Json so prices stay exact and timestamps stay as sent.
    Two runs can be compared by summarizing both and diffing the summary files.

.EXAMPLE
    pwsh tools/probe/Get-ProbeSummary.ps1 -RunDir recordings/20261120-070000
#>
[CmdletBinding()]
param([Parameter(Mandatory)][string]$RunDir)

$ErrorActionPreference = 'Stop'
$entries = Get-Content (Join-Path $RunDir 'index.jsonl') | ForEach-Object { $_ | ConvertFrom-Json }
$out = [System.Collections.Generic.List[string]]::new()
function Say([string]$Line = '') { $out.Add($Line) }

function Read-Body($Entry) {
    $path = Join-Path $RunDir "$($Entry.label).body.json"
    if (-not (Test-Path $path)) { return @{ ok = $false; error = 'no body file' } }
    $text = Get-Content $path -Raw
    if ([string]::IsNullOrWhiteSpace($text)) { return @{ ok = $false; error = 'empty body'; text = '' } }
    try { return @{ ok = $true; doc = [System.Text.Json.JsonDocument]::Parse($text); text = $text } }
    catch { return @{ ok = $false; error = "not JSON: $($_.Exception.Message)"; text = $text } }
}

function Get-Header($Entry, [string]$Name) {
    $path = Join-Path $RunDir "$($Entry.label).headers.txt"
    if (-not (Test-Path $path)) { return $null }
    $line = Get-Content $path | Where-Object { $_ -match "^${Name}:" } | Select-Object -Last 1
    if ($line) { return ($line -split ':', 2)[1].Trim() }
}

function Get-Prop($Element, [string]$Name) {
    $value = [System.Text.Json.JsonElement]::new()
    if ($Element.ValueKind -eq 'Object' -and $Element.TryGetProperty($Name, [ref]$value)) { return $value }
    return $null
}

function Get-Int($Element, [string]$Name) {
    $p = Get-Prop $Element $Name
    $n = 0L
    if ($null -ne $p -and $p.ValueKind -eq 'Number' -and $p.TryGetInt64([ref]$n)) { return $n }
    return $null
}

# ---------------------------------------------------------------- statuses and timing
Say "# Probe summary: $(Split-Path $RunDir -Leaf)"
Say
Say "Requests: $($entries.Count). First $($entries[0].startedAt), last $($entries[-1].startedAt)."
Say
Say '## Status codes'
Say
Say '| Status | Count |'
Say '|---|---|'
$entries | Group-Object status | Sort-Object Name | ForEach-Object { Say "| $($_.Name) | $($_.Count) |" }
Say
$times = @($entries | Where-Object { $null -ne $_.seconds } | ForEach-Object { [double]$_.seconds } | Sort-Object)
if ($times.Count) {
    $p50 = $times[[int][math]::Floor(($times.Count - 1) * 0.5)]
    $p95 = $times[[int][math]::Floor(($times.Count - 1) * 0.95)]
    Say ('Latency: min {0:N3}s, p50 {1:N3}s, p95 {2:N3}s, max {3:N3}s.' -f $times[0], $p50, $p95, $times[-1])
    Say
}

$odd = @($entries | Where-Object { $_.status -ne 200 -or $_.curlExit -ne 0 })
if ($odd.Count) {
    Say '## Non-200 and transport failures'
    Say
    Say '| Request | Query | Status | curl exit | Retry-After | Body (start) |'
    Say '|---|---|---|---|---|---|'
    foreach ($e in $odd) {
        $b = Read-Body $e
        $snippet = if ($b.text) { ($b.text -replace '\s+', ' ').Substring(0, [math]::Min(140, $b.text.Length)) -replace '\|', '\|' } else { $b.error }
        Say "| $($e.label) | ``$($e.query)`` | $($e.status) | $($e.curlExit) | $(Get-Header $e 'Retry-After') | $snippet |"
    }
    Say
}

# ---------------------------------------------------------------- passes
$records = @{}            # id -> set of raw record texts seen
$recordSamples = @{}      # id -> one JsonElement clone
$passGroups = $entries | Where-Object { $_.group -match '^pass\d+-size\d+$' } | Group-Object group
$pageFingerprints = @{}   # "size:page" -> list of id lists across passes

Say '## Passes'
Say
Say '| Pass | Pages | totalRecords (page 1) | Records seen | Distinct ids | Duplicates | Page anomalies | Past-end |'
Say '|---|---|---|---|---|---|---|---|'
$anomalies = [System.Collections.Generic.List[string]]::new()
foreach ($g in $passGroups) {
    $size = [int]($g.Name -replace '.*-size', '')
    $refTotalPages = $null; $refTotalRecords = $null
    $seen = 0; $ids = [System.Collections.Generic.List[long]]::new(); $passAnomalies = 0
    foreach ($e in ($g.Group | Sort-Object page)) {
        if ($e.status -ne 200) { continue }
        $b = Read-Body $e
        if (-not $b.ok) { $anomalies.Add("$($e.label): page $($e.page) body $($b.error)"); $passAnomalies++; continue }
        $root = $b.doc.RootElement
        $tp = Get-Int $root 'totalPages'; $tr = Get-Int $root 'totalRecords'
        $pg = Get-Int $root 'page'; $ps = Get-Int $root 'pageSize'
        $recs = Get-Prop $root 'records'
        if ($null -eq $refTotalPages) { $refTotalPages = $tp; $refTotalRecords = $tr }
        $problems = @()
        if ($pg -ne $e.page) { $problems += "page echo $pg" }
        if ($ps -ne $size) { $problems += "pageSize echo $ps" }
        if ($tp -ne $refTotalPages) { $problems += "totalPages $tp vs $refTotalPages" }
        if ($tr -ne $refTotalRecords) { $problems += "totalRecords $tr vs $refTotalRecords" }
        if ($null -ne $tr -and $tp -ne [math]::Ceiling($tr / $size)) { $problems += "totalPages $tp != ceil($tr/$size)" }
        $count = if ($recs -and $recs.ValueKind -eq 'Array') { $recs.GetArrayLength() } else { -1 }
        if ($count -lt 0) { $problems += 'records is not an array' }
        else {
            $expected = if ($refTotalRecords -eq 0) { 0 } elseif ($e.page -lt $refTotalPages) { $size } else { $refTotalRecords - ($refTotalPages - 1) * $size }
            if ($count -ne $expected) { $problems += "record count $count, expected $expected" }
            $pageIds = @()
            foreach ($r in $recs.EnumerateArray()) {
                $seen++
                $id = Get-Int $r 'id'
                $pageIds += $id
                if ($null -eq $id) { continue }
                $ids.Add($id)
                $canon = $r.GetRawText()   # exact text: padding and case differences count
                if (-not $records.ContainsKey($id)) { $records[$id] = [System.Collections.Generic.HashSet[string]]::new(); $recordSamples[$id] = $r.Clone() }
                [void]$records[$id].Add($canon)
            }
            $key = "${size}:$($e.page)"
            if (-not $pageFingerprints.ContainsKey($key)) { $pageFingerprints[$key] = [System.Collections.Generic.List[string]]::new() }
            $pageFingerprints[$key].Add(($pageIds -join ','))
        }
        if ($problems) { $passAnomalies++; $anomalies.Add("$($e.label): page $($e.page) — $($problems -join '; ')") }
    }
    $dups = @($ids | Group-Object | Where-Object Count -gt 1 | ForEach-Object Name)
    $distinct = @($ids | Select-Object -Unique).Count
    $past = $entries | Where-Object group -eq "$($g.Name)-past-end" | Select-Object -First 1
    $pastText = if ($past) {
        $pb = Read-Body $past
        $n = if ($pb.ok) { $r = Get-Prop $pb.doc.RootElement 'records'; if ($r -and $r.ValueKind -eq 'Array') { $r.GetArrayLength() } else { '?' } } else { '-' }
        "$($past.status), $n records"
    } else { 'not requested' }
    $dupText = if ($dups.Count) { "$($dups.Count) ($($dups -join ', '))" } else { '0' }
    Say "| $($g.Name) | $refTotalPages | $refTotalRecords | $seen | $distinct | $dupText | $passAnomalies | $pastText |"
}
Say
if ($anomalies.Count) {
    Say '### Page anomalies'
    Say
    $anomalies | ForEach-Object { Say "- $_" }
    Say
}

$unstable = @($pageFingerprints.GetEnumerator() | Where-Object { @($_.Value | Select-Object -Unique).Count -gt 1 })
Say "### Same page, different answers across passes: $($unstable.Count)"
Say
foreach ($u in $unstable | Sort-Object Key) { Say "- size:page $($u.Key) — $(@($u.Value | Select-Object -Unique).Count) different id lists" }
if ($unstable.Count) { Say }

$conflicts = @($records.GetEnumerator() | Where-Object { $_.Value.Count -gt 1 } | ForEach-Object Key | Sort-Object)
Say "### Ids seen with different content: $($conflicts.Count)"
Say
if ($conflicts.Count) { Say ($conflicts -join ', '); Say }

# ---------------------------------------------------------------- record quality
Say '## Record quality (all distinct ids across all passes)'
Say
Say "Distinct ids: $($records.Count)."
Say
$issues = [System.Collections.Generic.List[string]]::new()
$zeroQty = 0
$categories = [hashtable]::new([StringComparer]::Ordinal)   # case-sensitive keys
foreach ($id in ($recordSamples.Keys | Sort-Object)) {
    $r = $recordSamples[$id]
    $why = @()
    foreach ($f in 'id', 'name', 'category', 'price', 'quantity', 'lastUpdated') {
        $p = Get-Prop $r $f
        if ($null -eq $p) { $why += "$f missing" } elseif ($p.ValueKind -eq 'Null') { $why += "$f null" }
    }
    $name = Get-Prop $r 'name'
    if ($name -and $name.ValueKind -eq 'String' -and [string]::IsNullOrWhiteSpace($name.GetString())) { $why += 'name blank' }
    $cat = Get-Prop $r 'category'
    if ($cat -and $cat.ValueKind -eq 'String') {
        $c = $cat.GetString()
        if ([string]::IsNullOrWhiteSpace($c)) { $why += 'category blank' }
        else {
            if ($c -ne $c.Trim()) { $why += "category has padding '$c'" }
            $k = $c.Trim().ToUpperInvariant()
            if (-not $categories.ContainsKey($k)) { $categories[$k] = [hashtable]::new([StringComparer]::Ordinal) }
            $categories[$k][$c] = 1 + [int]$categories[$k][$c]
        }
    } elseif ($cat -and $cat.ValueKind -notin 'Null') { $why += "category is $($cat.ValueKind)" }
    $price = Get-Prop $r 'price'
    if ($price -and $price.ValueKind -eq 'Number') {
        $d = [decimal]0
        if (-not $price.TryGetDecimal([ref]$d)) { $why += "price $($price.GetRawText()) not decimal" }
        elseif ($d -lt 0) { $why += "price $d negative" }
        elseif ([decimal]::Round($d, 2) -ne $d) { $why += "price $d has more than 2 places" }
    } elseif ($price -and $price.ValueKind -ne 'Null') { $why += "price is $($price.ValueKind) $($price.GetRawText())" }
    $qty = Get-Prop $r 'quantity'
    if ($qty -and $qty.ValueKind -eq 'Number') {
        $q = 0L
        if (-not $qty.TryGetInt64([ref]$q)) { $why += "quantity $($qty.GetRawText()) not integer" }
        elseif ($q -lt 0) { $why += "quantity $q negative" }
        elseif ($q -eq 0) { $zeroQty++ }
    } elseif ($qty -and $qty.ValueKind -ne 'Null') { $why += "quantity is $($qty.ValueKind) $($qty.GetRawText())" }
    $lu = Get-Prop $r 'lastUpdated'
    if ($lu -and $lu.ValueKind -eq 'String') {
        $s = $lu.GetString()
        $dto = [DateTimeOffset]::MinValue
        if ($s -notmatch '(Z|[+-]\d{2}:\d{2})$') { $why += "lastUpdated '$s' has no offset" }
        elseif (-not [DateTimeOffset]::TryParse($s, [Globalization.CultureInfo]::InvariantCulture, [Globalization.DateTimeStyles]::RoundtripKind, [ref]$dto)) { $why += "lastUpdated '$s' unparsable" }
        elseif ($dto.Offset -ne [TimeSpan]::Zero) { $why += "lastUpdated '$s' not UTC" }
    } elseif ($lu -and $lu.ValueKind -ne 'Null') { $why += "lastUpdated is $($lu.ValueKind)" }
    $extra = @($r.EnumerateObject() | ForEach-Object Name | Where-Object { $_ -notin 'id', 'name', 'category', 'price', 'quantity', 'lastUpdated' })
    if ($extra.Count) { $why += "extra fields: $($extra -join ', ')" }
    if ($why) { $issues.Add("- $id — $($why -join '; ')") }
}
Say "Zero-quantity records: $zeroQty."
Say
Say "Records with issues: $($issues.Count)"
Say
$issues | ForEach-Object { Say $_ }
if ($issues.Count) { Say }

Say '### Categories'
Say
Say '| Category (normalized) | Spellings seen (count) |'
Say '|---|---|'
foreach ($k in ($categories.Keys | Sort-Object)) {
    $spellings = ($categories[$k].GetEnumerator() | Sort-Object Name | ForEach-Object { "'$($_.Name)' ($($_.Value))" }) -join ', '
    Say "| $k | $spellings |"
}
Say

# ---------------------------------------------------------------- edges
Say '## Edge requests'
Say
Say '| Request | Query | Status | Result |'
Say '|---|---|---|---|'
foreach ($e in ($entries | Where-Object group -like 'edge-*')) {
    $b = Read-Body $e
    $result = if ($b.ok) {
        $root = $b.doc.RootElement
        $recs = Get-Prop $root 'records'
        if ($recs -and $recs.ValueKind -eq 'Array') { "$($recs.GetArrayLength()) records, page $(Get-Int $root 'page'), pageSize $(Get-Int $root 'pageSize'), totalPages $(Get-Int $root 'totalPages')" }
        else { $d = Get-Prop $root 'detail'; if ($d) { $d.ToString() } else { ($b.text -replace '\s+', ' ').Substring(0, [math]::Min(120, $b.text.Length)) } }
    } else { $b.error }
    Say "| $($e.label) | ``$($e.query)`` | $($e.status) | $($result -replace '\|', '\|') |"
}

$summaryPath = Join-Path $RunDir 'summary.md'
$out | Set-Content -Path $summaryPath -Encoding utf8
$out | ForEach-Object { Write-Output $_ }
Write-Host "`nWritten to $summaryPath"
