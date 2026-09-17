[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$SourceDirectory,
    [string]$OutputPath
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$source = [IO.Path]::GetFullPath($SourceDirectory)
if (-not (Test-Path -LiteralPath $source -PathType Container)) {
    throw "Module source directory does not exist: $source"
}
$manifestPath = Join-Path $source 'module.json'
if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf)) {
    throw "A module source must contain module.json at its root."
}
$manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
if ([int]$manifest.schemaVersion -ne 1) {
    throw "Only module schemaVersion 1 can be packed."
}
if ([int]$manifest.runtimeApiVersion -gt 2) {
    throw "Module requires a newer manager runtime API."
}
if ([int]$manifest.runtimeApiVersion -eq 2) {
    $schemaPath = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot `
        '..\schemas\module-v2.schema.json'))
    if (-not (Test-Path -LiteralPath $schemaPath -PathType Leaf)) {
        throw "Runtime API 2 JSON Schema is missing: $schemaPath"
    }
    $manifestJson = Get-Content -LiteralPath $manifestPath -Raw
    if (-not (Test-Json -Json $manifestJson -SchemaFile $schemaPath `
            -ErrorAction Stop)) {
        throw "module.json does not satisfy the Runtime API 2 JSON Schema."
    }
}
$moduleId = [string]$manifest.id
$moduleVersion = [string]$manifest.moduleVersion
if ($moduleId -notmatch '^[0-9A-Za-z_-]+$') {
    throw "Module id or version is invalid."
}
$semver = '^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)(?:-((?:0|[1-9]\d*|\d*[A-Za-z-][0-9A-Za-z-]*)(?:\.(?:0|[1-9]\d*|\d*[A-Za-z-][0-9A-Za-z-]*))*))?(?:\+([0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*))?$'
if ($moduleVersion -notmatch $semver) {
    throw "Module version must use Semantic Versioning (for example 1.2.3)."
}
$safeVersion = $moduleVersion -replace '[^0-9A-Za-z._-]', '-'

$manifests = Get-ChildItem -LiteralPath $source -Recurse -File | Where-Object {
    $_.Name -ieq 'module.json' -or $_.Name -ilike '*.bridge-module.json'
}
if (@($manifests).Count -ne 1 -or
    $manifests[0].FullName -ine $manifestPath) {
    throw "A module source must contain exactly one root module.json manifest."
}

$boardIds = [Collections.Generic.HashSet[string]]::new(
    [StringComparer]::OrdinalIgnoreCase)
foreach ($board in @($manifest.boards)) {
    $boardId = [string]$board.id
    if ([string]::IsNullOrWhiteSpace($boardId) -or
        -not $boardIds.Add($boardId)) {
        throw "Board ids must be present and unique."
    }
}

foreach ($firmware in @($manifest.firmware)) {
    $method = [string]$firmware.flashMethod
    if ($method -notin @('None', 'PicoUf2', 'SifliSerial', 'BouffaloUart')) {
        throw "Unsupported firmware flashMethod: $method"
    }
    foreach ($boardId in @($firmware.boardIds)) {
        if (-not $boardIds.Contains([string]$boardId)) {
            throw "Firmware references unknown board id: $boardId"
        }
    }
    $relative = [string]$firmware.artifactRelativePath
    if ([string]::IsNullOrWhiteSpace($relative)) {
        if ($method -ne 'None') {
            throw "Automatic firmware $($firmware.id) must include an artifact."
        }
        continue
    }
    if ([IO.Path]::IsPathRooted($relative) -or
        $relative.Replace('\', '/').Split('/') -contains '..') {
        throw "Firmware artifact path is unsafe: $relative"
    }
    $artifact = [IO.Path]::GetFullPath((Join-Path $source $relative))
    $prefix = $source.TrimEnd([IO.Path]::DirectorySeparatorChar) +
        [IO.Path]::DirectorySeparatorChar
    if (-not $artifact.StartsWith($prefix,
            [StringComparison]::OrdinalIgnoreCase) -or
        -not (Test-Path -LiteralPath $artifact -PathType Leaf)) {
        throw "Firmware artifact is missing or outside the module: $relative"
    }
    if ($method -eq 'SifliSerial') {
        $parameters = Get-Content -LiteralPath $artifact -Raw | ConvertFrom-Json
        foreach ($file in @($parameters.write_flash.files)) {
            $secondary = [string]$file.path
            if ([string]::IsNullOrWhiteSpace($secondary) -or
                [IO.Path]::IsPathRooted($secondary) -or
                $secondary.Replace('\', '/').Split('/') -contains '..') {
                throw "SiFli parameter contains an unsafe artifact path: $secondary"
            }
            $secondaryPath = [IO.Path]::GetFullPath((Join-Path `
                (Split-Path -Parent $artifact) $secondary))
            if (-not $secondaryPath.StartsWith($prefix,
                    [StringComparison]::OrdinalIgnoreCase) -or
                -not (Test-Path -LiteralPath $secondaryPath -PathType Leaf)) {
                throw "SiFli package is missing a referenced artifact: $secondary"
            }
        }
    } elseif ($method -eq 'BouffaloUart') {
        $bundle = Get-Content -LiteralPath $artifact -Raw | ConvertFrom-Json
        if ([int]$bundle.schemaVersion -ne 1 -or
            [string]$bundle.chip -ne 'bl616' -or
            [int]$bundle.baudRate -ne 2000000 -or
            @($bundle.files).Count -ne 3) {
            throw "Bouffalo UART artifact is not a valid BL616 bundle."
        }
        $expectedAddresses = @{boot2='0x000000'; partition='0x00e000'; firmware='@partition'}
        $bundleKinds = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
        foreach ($file in @($bundle.files)) {
            if (-not $expectedAddresses.ContainsKey([string]$file.kind) -or
                -not $bundleKinds.Add([string]$file.kind) -or
                [string]$file.address -cne $expectedAddresses[[string]$file.kind]) {
                throw 'BL616 bundle has an unexpected or duplicate image kind/address.'
            }
            $secondary = [string]$file.path
            if ([string]::IsNullOrWhiteSpace($secondary) -or
                [IO.Path]::IsPathRooted($secondary) -or
                $secondary.Replace('\', '/').Split('/') -contains '..') {
                throw "BL616 bundle contains an unsafe artifact path: $secondary"
            }
            $secondaryPath = [IO.Path]::GetFullPath((Join-Path `
                (Split-Path -Parent $artifact) $secondary))
            if (-not $secondaryPath.StartsWith($prefix,
                    [StringComparison]::OrdinalIgnoreCase) -or
                -not (Test-Path -LiteralPath $secondaryPath -PathType Leaf) -or
                (Get-FileHash -LiteralPath $secondaryPath -Algorithm SHA256).Hash -ne
                    [string]$file.sha256) {
                throw "BL616 package file is missing or has the wrong SHA-256: $secondary"
            }
        }
    }
}

$hidKeys = [Collections.Generic.HashSet[string]]::new(
    [StringComparer]::OrdinalIgnoreCase)
foreach ($device in @($manifest.hidDevices)) {
    $key = [string]$device.key
    $serial = if ($null -ne $device.PSObject.Properties['managerSerialNumber']) {
        [string]$device.managerSerialNumber
    } else { '' }
    $identity = "${key}:$($device.vendorId):$($device.productId):$serial"
    if ([string]::IsNullOrWhiteSpace($key) -or
        [string]::IsNullOrWhiteSpace([string]$device.displayName) -or
        [int]$device.vendorId -le 0 -or [int]$device.productId -le 0 -or
        -not $hidKeys.Add($identity)) {
        throw "HID discovery entries must have a unique key/identity and real VID/PID."
    }
}

if (-not (Test-Path -LiteralPath (Join-Path $source 'LICENSE') -PathType Leaf)) {
    throw "A redistributable single-file module must include LICENSE."
}

$destination = if ($OutputPath) {
    [IO.Path]::GetFullPath($OutputPath)
} else {
    Join-Path (Split-Path -Parent $source) "$moduleId-$safeVersion.cbmodule"
}
if ([IO.Path]::GetExtension($destination) -ne '.cbmodule') {
    throw "OutputPath must end in .cbmodule"
}
New-Item -ItemType Directory -Path (Split-Path -Parent $destination) `
    -Force | Out-Null

$stage = Join-Path ([IO.Path]::GetTempPath()) `
    "controller-bridge-pack-$([Guid]::NewGuid().ToString('N'))"
New-Item -ItemType Directory -Path $stage -Force | Out-Null
try {
    Copy-Item -Path (Join-Path $source '*') -Destination $stage -Recurse -Force
    $hashFile = Join-Path $stage 'MODULE-SHA256.txt'
    Remove-Item -LiteralPath $hashFile -Force -ErrorAction SilentlyContinue
    $hashLines = Get-ChildItem -LiteralPath $stage -Recurse -File |
        Sort-Object FullName |
        ForEach-Object {
            $relative = $_.FullName.Substring($stage.Length + 1).Replace('\', '/')
            $hash = (Get-FileHash -LiteralPath $_.FullName `
                -Algorithm SHA256).Hash.ToLowerInvariant()
            "$hash  $relative"
        }
    Set-Content -LiteralPath $hashFile -Value $hashLines -Encoding ASCII

    $temporaryZip = "$destination.zip"
    Remove-Item -LiteralPath $temporaryZip, $destination -Force `
        -ErrorAction SilentlyContinue
    Compress-Archive -Path (Join-Path $stage '*') `
        -DestinationPath $temporaryZip -CompressionLevel Optimal
    Move-Item -LiteralPath $temporaryZip -Destination $destination
    Write-Host "Single-file firmware compatibility package: $destination"
}
finally {
    $stagePrefix = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\') + '\'
    if (-not [IO.Path]::GetFullPath($stage).StartsWith($stagePrefix, [StringComparison]::OrdinalIgnoreCase)) { throw 'Unsafe module-stage cleanup path.' }
    if (Test-Path -LiteralPath $stage) {
        Remove-Item -LiteralPath $stage -Recurse -Force
    }
}
