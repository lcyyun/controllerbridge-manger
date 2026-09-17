[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$OutputDirectory,
    [string]$FlashCubeRoot = $env:BRIDGE_MANAGER_BLFLASH_ROOT
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function Assert-PinnedFile {
    param([string]$Path, $Pin)
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw "Missing pinned BLFlash file: $Path"
    }
    if ((Get-Item -LiteralPath $Path).Length -ne $Pin.size -or
        (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash -ne $Pin.sha256) {
        throw "Pinned BLFlash size/SHA256 mismatch: $Path"
    }
}

$vendorRoot = Join-Path $PSScriptRoot 'vendor\blflash'
$provenancePath = Join-Path $vendorRoot 'PROVENANCE.json'
$pin = Get-Content -LiteralPath $provenancePath -Raw -Encoding UTF8 |
    ConvertFrom-Json
if (-not $FlashCubeRoot -and $env:USERPROFILE) {
    $candidate = Join-Path $env:USERPROFILE `
        'Documents\controllerbridge\bouffalo_sdk\tools\bflb_tools\bouffalo_flash_cube'
    if (Test-Path -LiteralPath $candidate -PathType Container) {
        $FlashCubeRoot = $candidate
    }
}
if (-not $FlashCubeRoot) {
    throw 'BLFlash source was not found. Set BRIDGE_MANAGER_BLFLASH_ROOT to bouffalo_flash_cube.'
}
$sourceRoot = [IO.Path]::GetFullPath($FlashCubeRoot)
$destinationRoot = Join-Path ([IO.Path]::GetFullPath($OutputDirectory)) 'tools\blflash'
New-Item -ItemType Directory -Path $destinationRoot -Force | Out-Null

$sourceExe = Join-Path $sourceRoot $pin.executable.fileName
Assert-PinnedFile -Path $sourceExe -Pin $pin.executable
Copy-Item -LiteralPath $sourceExe -Destination $destinationRoot -Force
foreach ($filePin in $pin.supportFiles) {
    $relative = ([string]$filePin.path).Replace('/', '\')
    $source = Join-Path $sourceRoot $relative
    Assert-PinnedFile -Path $source -Pin $filePin
    $destination = Join-Path $destinationRoot $relative
    New-Item -ItemType Directory -Path (Split-Path -Parent $destination) -Force |
        Out-Null
    Copy-Item -LiteralPath $source -Destination $destination -Force
}
$sdkRoot = [IO.Path]::GetFullPath((Join-Path $sourceRoot '..\..\..'))
$sourceLicense = Join-Path $sdkRoot $pin.license.sourceFileName
Assert-PinnedFile -Path $sourceLicense -Pin $pin.license
Copy-Item -LiteralPath $sourceLicense `
    -Destination (Join-Path $destinationRoot $pin.license.fileName) -Force
Copy-Item -LiteralPath $provenancePath `
    -Destination (Join-Path $destinationRoot 'PROVENANCE.json') -Force

$helpOutput = (& (Join-Path $destinationRoot $pin.executable.fileName) --help 2>&1 |
    Out-String)
if ($LASTEXITCODE -ne 0 -or -not $helpOutput.Contains('chipname')) {
    throw 'Bundled BLFlashCommand did not pass its read-only help check.'
}
Write-Host "Bundled BLFlashCommand $($pin.version), BL616 support only: $destinationRoot"
