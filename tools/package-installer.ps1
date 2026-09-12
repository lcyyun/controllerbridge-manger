[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$PackagePath,
    [Parameter(Mandatory = $true)]
    [string]$IsccPath,
    [ValidatePattern('^\d+\.\d+\.\d+(\.\d+)?$')]
    [string]$Version = '0.2.0',
    [switch]$AllowLocalPreview
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$package = (Resolve-Path -LiteralPath $PackagePath).Path
$compiler = (Resolve-Path -LiteralPath $IsccPath).Path
$output = Join-Path $root 'dist'
& (Join-Path $PSScriptRoot 'verify-modern-package.ps1') `
    -PackagePath $package -AllowLocalPreview:$AllowLocalPreview
& $compiler "/DPackageDir=$package" "/DOutputDir=$output" "/DAppVersion=$Version" `
    (Join-Path $PSScriptRoot 'installer/BridgeManager.iss')
if ($LASTEXITCODE -ne 0) { throw "Installer compiler failed: $LASTEXITCODE" }
$installer = Join-Path $output "ControllerBridge-Setup-$Version-win-x64.exe"
if (-not (Test-Path -LiteralPath $installer)) { throw 'Installer output missing.' }
$hash = (Get-FileHash -LiteralPath $installer -Algorithm SHA256).Hash.ToLowerInvariant()
[IO.File]::WriteAllText("$installer.sha256", "$hash  $([IO.Path]::GetFileName($installer))`n")
Write-Host "Installer: $installer"
