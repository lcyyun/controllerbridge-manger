[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$PackagePath,
    [switch]$LaunchSmoke
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$root = [IO.Path]::GetFullPath($PackagePath)
if (-not (Test-Path -LiteralPath $root -PathType Container)) {
    throw "Package directory does not exist: $root"
}

$requiredFiles = @(
    'BridgeManager.App.exe',
    'BridgeManager.App.dll',
    'BridgeManager.App.deps.json',
    'BridgeManager.App.runtimeconfig.json',
    'BridgeManager.Core.dll',
    'Microsoft.WindowsAppRuntime.Bootstrap.dll',
    'PACKAGE-INFO.txt',
    'NOTICE.md',
    'LICENSES\HidSharp-Apache-2.0.txt',
    'SHA256SUMS.txt'
)
foreach ($file in $requiredFiles) {
    $path = Join-Path $root $file
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        throw "Required package file is missing: $file"
    }
}

$hashFile = Join-Path $root 'SHA256SUMS.txt'
foreach ($line in Get-Content -LiteralPath $hashFile) {
    if ($line -notmatch '^([0-9a-f]{64})  (.+)$') {
        throw "Malformed SHA256 entry: $line"
    }
    $expected = $Matches[1]
    $relative = $Matches[2].Replace('/', [IO.Path]::DirectorySeparatorChar)
    $file = [IO.Path]::GetFullPath((Join-Path $root $relative))
    $rootPrefix = $root.TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
    if (-not $file.StartsWith($rootPrefix, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Hash entry escapes package directory: $relative"
    }
    $actual = (Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($actual -ne $expected) {
        throw "SHA256 mismatch: $relative"
    }
}

if ($LaunchSmoke) {
    $process = Start-Process -FilePath (Join-Path $root 'BridgeManager.App.exe') `
        -ArgumentList '--launch-smoke' -PassThru -WindowStyle Hidden
    if (-not $process.WaitForExit(30000)) {
        Stop-Process -Id $process.Id -Force
        throw 'BridgeManager.App did not finish its clean launch smoke test within 30 seconds.'
    }
    if ($process.ExitCode -ne 0) {
        throw "BridgeManager.App launch smoke test failed with code $($process.ExitCode)."
    }
}

Write-Host "Verified package: $root"
