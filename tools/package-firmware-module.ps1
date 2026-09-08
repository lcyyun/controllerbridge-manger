[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidateSet('esp32s3-ns2-bridge', 'sf32-unified', 'pico-unified-bridge')]
    [string]$ModuleId,
    [string]$OutputDirectory = (Join-Path $PSScriptRoot '..\dist\modules'),
    [string]$SourceDirectory,
    [switch]$Offline
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$source = & (Join-Path $PSScriptRoot 'get-firmware-packages.ps1') `
    -ModuleId $ModuleId -SourceDirectory $SourceDirectory -Offline:$Offline
$output = [IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Path $output -Force | Out-Null
$target = Join-Path $output ([IO.Path]::GetFileName($source.Path))
if (Test-Path -LiteralPath $target) {
    if ((Get-FileHash -LiteralPath $target).Hash -ne (Get-FileHash -LiteralPath $source.Path).Hash) {
        throw "Output contains a different firmware package: $target"
    }
} else {
    Copy-Item -LiteralPath $source.Path -Destination $target
}
Write-Host "Verified locked firmware module: $target"
