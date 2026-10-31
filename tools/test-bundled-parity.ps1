[CmdletBinding()]
param([Parameter(Mandatory = $true)][string]$PackagePath)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$root = (Resolve-Path -LiteralPath $PackagePath).Path
Add-Type -Path (Join-Path $root 'BridgeManager.Core.dll')
$lock = Get-Content -LiteralPath (Join-Path $root 'firmware-releases.lock.json') -Raw | ConvertFrom-Json
if (@($lock.modules).Count -ne 4) { throw 'All four actual firmware modules must be locked.' }
$expectedIds = @('bl616-unified', 'sf32-unified', 'pico-unified-bridge', 'esp32s3-ns2-bridge')
foreach ($id in $expectedIds) {
    $entry = @($lock.modules | Where-Object moduleId -CEQ $id)
    if ($entry.Count -ne 1) { throw "Missing unique firmware lock: $id" }
    $module = [BridgeManager.Core.FirmwareModules.BridgeModulePackageLoader]::LoadFile(
        (Join-Path $root "modules/$id/module.json"))
    if ($module.ModuleVersion -cne $entry[0].moduleVersion) { throw "Wrong bundled version: $id" }
    $roles = @($module.UsbRoles | ForEach-Object { $_.Role.ToString() } | Sort-Object -Unique)
    if (($roles -join ',') -cne 'DualSense,DualSenseEdge,NintendoNs2Pro,Xbox') {
        throw "USB output roles are incomplete: $id"
    }
    $wireless = @($module.WirelessControllers | ForEach-Object Id)
    if ($id -eq 'esp32s3-ns2-bridge') {
        if ($wireless.Count -ne 1 -or $wireless[0] -notin @('ns2','ns2pro') -or
            @($module.InputSources | Where-Object { $_.Preference.ToString() -eq 'DualSense' }).Count -ne 0) {
            throw 'ESP32-S3 must expose NS2Pro BLE physical input and retain all USB output roles.'
        }
    } elseif ($wireless -notcontains 'ds5' -or
              (@($wireless | Where-Object { $_ -in @('ns2','ns2pro') }).Count -ne 1)) {
        throw "DS5/Edge Classic and NS2Pro BLE controls are required: $id"
    }
    if ($module.InputStatusCommand -notin @('bridge input','input status')) { throw "Missing physical input snapshot contract: $id" }
    $mapping = @($module.Pages | ForEach-Object Sections | ForEach-Object Controls |
        Where-Object { $_.Type.ToString() -eq 'MappingEditor' })
    if ($mapping.Count -eq 0) { throw "Missing actual mapping editor: $id" }
    foreach ($firmware in $module.Firmware) {
        if ($firmware.FlashMethod.ToString() -eq 'None') { throw "Metadata-only firmware is forbidden: $id" }
        $details = [BridgeManager.Core.FirmwareModules.FirmwareFlashService]::DescribeArtifact($module, $firmware)
        if (-not $details.Available -or $details.TotalBytes -lt 1024) {
            throw "Firmware image validation failed: $id / $($details.Message)"
        }
        if ($id -eq 'sf32-unified' -and
            (@($firmware.BoardIds).Count -ne 1 -or $firmware.BoardIds[0] -ne 'sf32lb52-devkit-nano')) {
            throw 'SF32 Nano firmware must not be offered for another board.'
        }
    }
    Write-Host "PASS $id $($module.ModuleVersion): four USB identities, physical input controls, mapping and real firmware"
}
Write-Host 'PASS four-platform package contract; no USB/serial device is opened.'
