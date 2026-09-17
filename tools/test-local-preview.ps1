[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$root = Join-Path $PSScriptRoot ("..\test-results\local-preview-" + [Guid]::NewGuid().ToString('N'))
$verify = Join-Path $PSScriptRoot 'verify-modern-package.ps1'
$files = @(
    'BridgeManager.Modern.App.exe', 'BridgeManager.Modern.App.dll',
    'BridgeManager.Modern.App.deps.json', 'BridgeManager.Modern.App.runtimeconfig.json',
    'BridgeManager.Core.dll', 'Microsoft.WindowsAppRuntime.Bootstrap.dll',
    'PACKAGE-INFO.txt', 'NOTICE.md', 'LICENSES/HidSharp-Apache-2.0.txt', 'LICENSE',
    'firmware-releases.lock.json', 'modules/esp32s3-ns2-bridge/module.json',
    'modules/sf32-unified/module.json', 'modules/sf32-unified/artifacts/sftool_param.json',
    'modules/sf32-unified/artifacts/bootloader/output/bootloader.bin',
    'modules/sf32-unified/artifacts/output/main.bin', 'modules/sf32-unified/artifacts/ftab.bin',
    'modules/pico-unified-bridge/module.json',
    'modules/pico-unified-bridge/artifacts/pico-controller-bridge-0.1.uf2',
    'tools/sftool/sftool.exe', 'tools/sftool/LICENSE.txt', 'tools/sftool/PROVENANCE.json',
    'SHA256SUMS.txt', 'module-packages/sf32-unified-0.6.0-dev.cbmodule'
)
foreach ($relative in $files) {
    $path = Join-Path $root $relative
    New-Item -ItemType Directory -Path (Split-Path -Parent $path) -Force | Out-Null
    [IO.File]::WriteAllText($path, 'Inert negative-test fixture; never executable.')
}
$asset = Join-Path $root 'module-packages/sf32-unified-0.6.0-dev.cbmodule'
$valid = @{
    schemaVersion=1; localPreview=$true; hardwareTested=$false
    moduleId='sf32-unified'; asset='sf32-unified-0.6.0-dev.cbmodule'
    size=(Get-Item -LiteralPath $asset).Length
    sha256=(Get-FileHash -LiteralPath $asset).Hash.ToLowerInvariant()
} | ConvertTo-Json

function Reject([string]$name, [scriptblock]$mutate, [string]$expected, [bool]$allow = $true) {
    $value = $valid | ConvertFrom-Json
    & $mutate $value
    [IO.File]::WriteAllText((Join-Path $root 'LOCAL-PREVIEW.json'), ($value | ConvertTo-Json))
    $failure = $null
    try { & $verify -PackagePath $root -AllowLocalPreview:$allow }
    catch { $failure = $_.Exception.Message }
    if (-not $failure -or -not $failure.Contains($expected)) {
        throw "$name failed at unexpected stage: $failure"
    }
    Write-Host "PASS $name"
}

Reject 'explicit preview opt-in' { param($v) } 'requires explicit' $false
Reject 'wrong module identity' { param($v) $v.moduleId='bl616' } 'Invalid local preview'
Reject 'no false hardware claim' { param($v) $v.hardwareTested=$true } 'Invalid local preview'
Reject 'asset traversal' { param($v) $v.asset='../sf32-unified-0.6.0-dev.cbmodule' } 'Invalid local preview'
Reject 'digest mismatch' { param($v) $v.sha256='a'*64 } 'recorded digest'
Reject 'size mismatch' { param($v) $v.size++ } 'recorded digest'
Reject 'malformed digest' { param($v) $v.sha256='invalid' } 'Invalid local preview'
Write-Host 'PASS 7 local-preview rejection checks; no network or executable fixture launched.'
