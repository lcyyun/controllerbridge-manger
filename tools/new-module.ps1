[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidatePattern('^[0-9A-Za-z_-]+$')]
    [string]$Id,
    [Parameter(Mandatory = $true)]
    [string]$DisplayName,
    [Parameter(Mandatory = $true)]
    [string]$BoardFamily,
    [Parameter(Mandatory = $true)]
    [string]$LicensePath,
    [string]$OutputDirectory = (Join-Path (Get-Location) $Id)
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$destination = [IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path -LiteralPath $destination) {
    throw "Destination already exists: $destination"
}
if (-not (Test-Path -LiteralPath $LicensePath -PathType Leaf)) {
    throw "License file does not exist: $LicensePath"
}

New-Item -ItemType Directory -Path $destination -Force | Out-Null
try {
    $manifest = [ordered]@{
        schemaVersion = 1
        runtimeApiVersion = 2
        id = $Id
        moduleVersion = '0.1.0'
        displayName = $DisplayName
        boardFamily = $BoardFamily
        description = "$DisplayName controller firmware module."
        priority = 100
        capabilities = 'DeviceStatus, RawCommands'
        matchAny = @()
        hidDevices = @()
        boards = @(
            [ordered]@{
                id = 'replace-with-real-board-id'
                displayName = 'Replace with real board name'
                family = $BoardFamily
                description = 'Use an identity observed on real hardware.'
            }
        )
        firmware = @(
            [ordered]@{
                id = 'replace-with-firmware-id'
                displayName = $DisplayName
                version = '0.1.0'
                description = 'Initial firmware integration.'
                boardIds = @('replace-with-real-board-id')
                flashMethod = 'None'
                artifactRelativePath = $null
                flashHint = 'Follow the firmware vendor instructions.'
            }
        )
        usbRoles = @()
        inputSources = @()
        wirelessControllers = @()
        statusCommands = @('status')
        selfTestCommands = @('status')
        primaryStatusCommand = 'status'
        inputStatusCommand = 'status'
        saveSettingsCommand = $null
        settingsCommandTemplates = @()
        rumbleCommandTemplate = $null
        operations = [ordered]@{
            'status.read' = [ordered]@{
                transport = 'ManagerCommand'
                request = 'status'
            }
        }
        pages = @(
            [ordered]@{
                id = 'module-status'
                label = 'Module status'
                description = 'A page supplied entirely by the module.'
                icon = 'Settings'
                sections = @(
                    [ordered]@{
                        id = 'status'
                        label = 'Device status'
                        controls = @(
                            [ordered]@{
                                id = 'status-json'
                                type = 'Status'
                                label = 'Status reply'
                                readAction = 'status.read'
                            }
                        )
                    }
                )
            }
        )
    }

    $manifest | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath `
        (Join-Path $destination 'module.json') -Encoding UTF8
    Copy-Item -LiteralPath $LicensePath -Destination `
        (Join-Path $destination 'LICENSE')
    New-Item -ItemType Directory -Path (Join-Path $destination 'artifacts') `
        -Force | Out-Null
    Write-Host "Runtime API 2 module template: $destination"
    Write-Host "Edit real HID identities and board data, then run:"
    Write-Host "  .\tools\pack-module-directory.ps1 -SourceDirectory '$destination'"
}
catch {
    if (Test-Path -LiteralPath $destination) {
        Remove-Item -LiteralPath $destination -Recurse -Force
    }
    throw
}
