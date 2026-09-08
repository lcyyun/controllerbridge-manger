[CmdletBinding()]
param(
    [string]$LockPath = (Join-Path $PSScriptRoot '..\firmware-releases.lock.json'),
    [string]$SourceDirectory,
    [string]$CacheDirectory = (Join-Path $PSScriptRoot '..\.cache\firmware'),
    [ValidateSet('esp32s3-ns2-bridge', 'sf32-unified', 'pico-unified-bridge')]
    [string]$ModuleId,
    [switch]$Offline
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$lock = Get-Content -LiteralPath $LockPath -Raw -Encoding UTF8 | ConvertFrom-Json
$repositories = @{
    'sf32-unified' = 'lcyyun/controllerbridge-SF32LB52'
    'pico-unified-bridge' = 'lcyyun/controllerbridge-pico2w'
    'esp32s3-ns2-bridge' = 'lcyyun/controllerbridge-esp32s3'
}
if ($lock.schemaVersion -ne 1 -or @($lock.modules).Count -ne $repositories.Count) {
    throw 'Expected exactly three locked first-party firmware modules.'
}
$ids = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
foreach ($entry in $lock.modules) {
    if (-not $repositories.ContainsKey([string]$entry.moduleId) -or
        -not $ids.Add([string]$entry.moduleId) -or
        $entry.repository -cne $repositories[$entry.moduleId] -or
        $entry.tag -cnotmatch '^[A-Za-z0-9][A-Za-z0-9._-]{0,80}$' -or
        $entry.asset -cnotmatch '^[A-Za-z0-9][A-Za-z0-9._-]*\.cbmodule$' -or
        -not $entry.asset.StartsWith("$($entry.moduleId)-", [StringComparison]::Ordinal) -or
        $entry.sha256 -cnotmatch '^[0-9a-f]{64}$' -or
        $entry.sourceCommit -cnotmatch '^[0-9a-f]{40}$' -or
        [long]$entry.size -le 0 -or [long]$entry.size -gt 512MB) {
        throw 'Invalid or unapproved firmware release lock entry.'
    }
}
$cacheRoot = [IO.Path]::GetFullPath($CacheDirectory)
foreach ($entry in $lock.modules) {
    if ($ModuleId -and $entry.moduleId -cne $ModuleId) { continue }
    $directory = if ($SourceDirectory) {
        [IO.Path]::GetFullPath($SourceDirectory)
    } else {
        Join-Path $cacheRoot $entry.sha256
    }
    $path = Join-Path $directory $entry.asset
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        if ($Offline -or $SourceDirectory) {
            throw "Locked firmware is unavailable offline: $($entry.asset)"
        }
        $gh = (Get-Command gh -ErrorAction Stop).Source
        $commit = (& $gh api "repos/$($entry.repository)/commits/$($entry.tag)" --jq '.sha')
        if ($LASTEXITCODE -ne 0) {
            throw "Cannot access $($entry.repository). Sign in with gh auth login using an authorized account."
        }
        if (([string]$commit).Trim() -cne $entry.sourceCommit) {
            throw "Release tag moved away from the locked source commit: $($entry.repository)"
        }
        New-Item -ItemType Directory -Path $directory -Force | Out-Null
        & $gh release download $entry.tag --repo $entry.repository --pattern $entry.asset --dir $directory
        if ($LASTEXITCODE -ne 0) { throw "Firmware release download failed: $($entry.asset)" }
    }
    $file = Get-Item -LiteralPath $path
    if ($file.Length -ne [long]$entry.size -or
        (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -ine $entry.sha256) {
        throw "Locked firmware size/SHA256 mismatch: $($entry.asset)"
    }
    [pscustomobject]@{ ModuleId = $entry.moduleId; Path = $file.FullName }
}
