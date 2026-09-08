[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$root = Join-Path $PSScriptRoot ("..\test-results\firmware-lock-" + [Guid]::NewGuid().ToString('N'))
$source = Join-Path $root 'source'
New-Item -ItemType Directory -Path $source -Force | Out-Null
$lockPath = Join-Path $root 'lock.json'
$reader = Join-Path $PSScriptRoot 'get-firmware-packages.ps1'
$repoMap = [ordered]@{
    'sf32-unified' = 'lcyyun/controllerbridge-SF32LB52'
    'pico-unified-bridge' = 'lcyyun/controllerbridge-pico2w'
    'esp32s3-ns2-bridge' = 'lcyyun/controllerbridge-esp32s3'
}
$entries = @(
    foreach ($id in $repoMap.Keys) {
        $name = "$id-1.0.0.cbmodule"
        $path = Join-Path $source $name
        [IO.File]::WriteAllText($path, "Offline lock resolver fixture: $id")
        [ordered]@{
            moduleId = $id; repository = $repoMap[$id]; tag = 'fixture-1.0.0'
            asset = $name; sourceCommit = ('a' * 40)
            size = (Get-Item -LiteralPath $path).Length
            sha256 = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant()
        }
    }
)
$valid = @{ schemaVersion = 1; modules = $entries } | ConvertTo-Json -Depth 8
function Save-Lock($value) {
    [IO.File]::WriteAllText($lockPath, ($value | ConvertTo-Json -Depth 8))
}
function Require([bool]$condition, [string]$message) {
    if (-not $condition) { throw $message }
}
$passed = 0
function Reject-Lock([string]$name, [scriptblock]$mutate) {
    $copy = $valid | ConvertFrom-Json
    & $mutate $copy
    Save-Lock $copy
    $rejected = $false
    try { [void]@(& $reader -LockPath $lockPath -SourceDirectory $source -Offline) }
    catch { $rejected = $true }
    Require $rejected "Accepted invalid lock: $name"
    $script:passed++
    Write-Host "PASS $name"
}
Save-Lock ($valid | ConvertFrom-Json)
$resolved = @(& $reader -LockPath $lockPath -SourceDirectory $source -Offline)
Require ($resolved.Count -eq 3) 'Did not resolve all locked modules.'
$selected = @(& $reader -LockPath $lockPath -SourceDirectory $source -ModuleId sf32-unified -Offline)
Require ($selected.Count -eq 1 -and $selected[0].ModuleId -eq 'sf32-unified') 'Module selection failed.'
$passed += 2
Reject-Lock 'schema' { param($v) $v.schemaVersion = 2 }
Reject-Lock 'missing module' { param($v) $v.modules = @($v.modules[0]) }
Reject-Lock 'duplicate module' { param($v) $v.modules[1] = $v.modules[0] }
Reject-Lock 'unapproved repository' { param($v) $v.modules[0].repository = 'other/firmware' }
Reject-Lock 'module identity' { param($v) $v.modules[0].moduleId = 'bl616' }
Reject-Lock 'asset path traversal' { param($v) $v.modules[0].asset = '../payload.cbmodule' }
Reject-Lock 'asset wildcard' { param($v) $v.modules[0].asset = 'sf32-unified-*.cbmodule' }
Reject-Lock 'wrong asset family' { param($v) $v.modules[0].asset = 'pico-unified-bridge-1.0.0.cbmodule' }
Reject-Lock 'tag path traversal' { param($v) $v.modules[0].tag = '../../other' }
Reject-Lock 'invalid commit' { param($v) $v.modules[0].sourceCommit = 'main' }
Reject-Lock 'invalid digest' { param($v) $v.modules[0].sha256 = 'no' }
Reject-Lock 'oversized asset' { param($v) $v.modules[0].size = 513MB }
Reject-Lock 'empty asset' { param($v) $v.modules[0].size = 0 }
Reject-Lock 'mismatched digest' { param($v) $v.modules[0].sha256 = 'b' * 64 }
Reject-Lock 'mismatched size' { param($v) $v.modules[0].size++ }
Save-Lock ($valid | ConvertFrom-Json)
$cache = Join-Path $root 'cache'
foreach ($entry in $entries) {
    $directory = Join-Path $cache $entry.sha256
    New-Item -ItemType Directory -Path $directory -Force | Out-Null
    Copy-Item -LiteralPath (Join-Path $source $entry.asset) -Destination $directory
}
Require (@(& $reader -LockPath $lockPath -CacheDirectory $cache -Offline).Count -eq 3) 'Offline cache failed.'
$passed++
$missingRejected = $false
try { [void]@(& $reader -LockPath $lockPath -CacheDirectory (Join-Path $root 'missing') -Offline) }
catch { $missingRejected = $true }
Require $missingRejected 'Missing offline files triggered no failure.'
$passed++
Write-Host "PASS $passed offline firmware lock checks; no network or device operations."
