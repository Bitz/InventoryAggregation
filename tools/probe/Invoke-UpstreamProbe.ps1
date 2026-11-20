#Requires -Version 7.2
<#
.SYNOPSIS
    Records how the upstream Inventory API actually behaves.

.DESCRIPTION
    Walks every page several times, adds one pass per extra page size, then sends a few edge
    requests (past the end, out-of-range paging, defaults). Each response's status, headers, body
    and timing is saved under recordings/<timestamp>/ (git-ignored). Run Get-ProbeSummary.ps1 on
    the folder afterwards.

    Settings come from the same places as the service: environment variables
    InventoryApi__BaseUrl / InventoryApi__ApiKey first, then .NET user secrets.

    Requests go through Windows' own curl.exe with no User-Agent header. The API key is passed to
    curl on stdin as a config line, so it never appears in a process command line or in output.

.EXAMPLE
    pwsh tools/probe/Invoke-UpstreamProbe.ps1
    pwsh tools/probe/Invoke-UpstreamProbe.ps1 -Passes 5 -DelayMs 1500
#>
[CmdletBinding()]
param(
    [int]   $Passes = 3,
    [int]   $PageSize = 25,
    [int[]] $ExtraPageSizes = @(20, 7),
    [int]   $DelayMs = 750,
    [int]   $MaxPages = 200,
    [string]$SecretsId = 'inventory-aggregation-7d3f2c1e',
    [string]$OutRoot = (Join-Path $PSScriptRoot '..' '..' 'recordings')
)

$ErrorActionPreference = 'Stop'
$curl = Join-Path $env:SystemRoot 'System32\curl.exe'
if (-not (Test-Path $curl)) { throw "Windows curl not found at $curl" }

function Get-Setting([string]$Name) {
    $fromEnv = [Environment]::GetEnvironmentVariable("InventoryApi__$Name")
    if ($fromEnv) { return $fromEnv }
    $path = Join-Path $env:APPDATA 'Microsoft' 'UserSecrets' $SecretsId 'secrets.json'
    if (Test-Path $path) {
        $secrets = Get-Content $path -Raw | ConvertFrom-Json -AsHashtable
        if ($secrets.ContainsKey("InventoryApi:$Name")) { return $secrets["InventoryApi:$Name"] }
    }
    throw "InventoryApi:$Name is not set. Use: dotnet user-secrets set `"InventoryApi:$Name`" `"<value>`" --id $SecretsId"
}

$baseUrl = (Get-Setting 'BaseUrl').TrimEnd('/')
$apiKey = Get-Setting 'ApiKey'
$escapedKey = $apiKey.Replace('\', '\\').Replace('"', '\"')
# Config lines for curl, fed on stdin: the key header, and an empty User-Agent header (removes it).
$curlConfig = "header = `"X-Api-Key: $escapedKey`"`nheader = `"User-Agent:`""

$runDir = Join-Path $OutRoot (Get-Date -Format 'yyyyMMdd-HHmmss')
New-Item -ItemType Directory -Path $runDir -Force | Out-Null
$indexPath = Join-Path $runDir 'index.jsonl'
$script:sequence = 0

function Invoke-Probe {
    param([string]$Group, [string]$Query, [Nullable[int]]$Page, [Nullable[int]]$Size)

    $script:sequence++
    $label = '{0:D4}-{1}' -f $script:sequence, $Group
    $headersFile = Join-Path $runDir "$label.headers.txt"
    $bodyFile = Join-Path $runDir "$label.body.json"
    $started = [DateTimeOffset]::UtcNow
    $writeOut = '%{http_code} %{time_total} %{size_download}'

    $ErrorActionPreference = 'Continue'   # curl's stderr must not stop the run
    $result = $curlConfig | & $curl --config - --silent --show-error --max-time 30 `
        --dump-header $headersFile --output $bodyFile --write-out $writeOut "$baseUrl/api/records$Query" 2>&1
    $exit = $LASTEXITCODE
    $ErrorActionPreference = 'Stop'

    $parts = "$result".Trim().Split(' ')
    $entry = [ordered]@{
        label     = $label
        group     = $Group
        query     = $Query
        page      = $Page
        pageSize  = $Size
        status    = if ($parts.Count -ge 3) { [int]$parts[-3] } else { 0 }
        seconds   = if ($parts.Count -ge 3) { [double]$parts[-2] } else { $null }
        bytes     = if ($parts.Count -ge 3) { [long]$parts[-1] } else { $null }
        curlExit  = $exit
        error     = if ($exit -ne 0) { "$result" } else { $null }
        startedAt = $started.ToString('o')
    }
    ($entry | ConvertTo-Json -Compress) | Add-Content -Path $indexPath
    Write-Host ('{0}  {1,-22} {2,3}  {3,6:N3}s' -f $label, $Query, $entry.status, $entry.seconds)
    Start-Sleep -Milliseconds $DelayMs
    return $entry, $bodyFile
}

function Get-TotalPages([string]$BodyFile) {
    try {
        $doc = Get-Content $BodyFile -Raw | ConvertFrom-Json -AsHashtable
        if ($doc -isnot [hashtable] -or -not $doc.ContainsKey('totalPages')) { return $null }
        return [int]$doc['totalPages']
    } catch { return $null }
}

function Invoke-Pass([string]$Group, [int]$Size) {
    $first, $firstBody = Invoke-Probe -Group $Group -Query "?page=1&pageSize=$Size" -Page 1 -Size $Size
    if ($first.status -in 401, 403) {
        throw "Page 1 returned $($first.status): the key was refused. Stopping so no more failing requests are sent. See $firstBody."
    }
    if ($first.status -ne 200) { Write-Warning "$Group page 1 returned $($first.status); pass stops."; return $null }
    $total = Get-TotalPages $firstBody
    if ($null -eq $total) { Write-Warning "$Group could not read totalPages from page 1; pass stops."; return $null }
    if ($total -gt $MaxPages) { Write-Warning "$Group reports $total pages, above -MaxPages $MaxPages; pass stops."; return $total }
    for ($page = 2; $page -le $total; $page++) {
        Invoke-Probe -Group $Group -Query "?page=$page&pageSize=$Size" -Page $page -Size $Size | Out-Null
    }
    Invoke-Probe -Group "$Group-past-end" -Query "?page=$($total + 1)&pageSize=$Size" -Page ($total + 1) -Size $Size | Out-Null
    return $total
}

Write-Host "Recording to $runDir"
$lastTotal = $null
for ($pass = 1; $pass -le $Passes; $pass++) {
    $lastTotal = Invoke-Pass -Group "pass$pass-size$PageSize" -Size $PageSize
}
foreach ($size in $ExtraPageSizes) {
    Invoke-Pass -Group "pass1-size$size" -Size $size | Out-Null
}

$edges = [ordered]@{
    'edge-defaults'      = ''
    'edge-page0'         = '?page=0&pageSize=25'
    'edge-page-negative' = '?page=-1&pageSize=25'
    'edge-size0'         = '?page=1&pageSize=0'
    'edge-size26'        = '?page=1&pageSize=26'
    'edge-size1'         = '?page=1&pageSize=1'
    'edge-page-text'     = '?page=abc&pageSize=25'
}
if ($lastTotal) { $edges['edge-far-past-end'] = "?page=$($lastTotal + 5)&pageSize=$PageSize" }
foreach ($name in $edges.Keys) { Invoke-Probe -Group $name -Query $edges[$name] | Out-Null }

Write-Host "Done. Summarize with: pwsh tools/probe/Get-ProbeSummary.ps1 -RunDir `"$runDir`""
