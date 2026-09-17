[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$PackagePath,
    [switch]$LaunchSmoke
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function Get-ContainedFile {
    param([string]$BasePath, [string]$RelativePath, [switch]$AllowEmpty)
    if ([string]::IsNullOrWhiteSpace($RelativePath) -or
        [IO.Path]::IsPathRooted($RelativePath) -or $RelativePath.Contains(':')) {
        throw "Package file must have a relative path: $RelativePath"
    }
    $prefix = $BasePath.TrimEnd([IO.Path]::DirectorySeparatorChar) +
        [IO.Path]::DirectorySeparatorChar
    $file = [IO.Path]::GetFullPath((Join-Path $BasePath `
        $RelativePath.Replace('/', [IO.Path]::DirectorySeparatorChar)))
    if (-not $file.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) {
        throw "File escapes package directory: $RelativePath"
    }
    if (-not (Test-Path -LiteralPath $file -PathType Leaf)) {
        throw "Required package file is missing: $RelativePath"
    }
    if (-not $AllowEmpty -and (Get-Item -LiteralPath $file).Length -eq 0) {
        throw "Required package file is empty: $RelativePath"
    }
    return $file
}

$root = [IO.Path]::GetFullPath($PackagePath)
if (-not (Test-Path -LiteralPath $root -PathType Container)) {
    throw "Package directory does not exist: $root"
}

$requiredFiles = @(
    'BridgeManager.Modern.App.exe',
    'BridgeManager.Modern.App.dll',
    'BridgeManager.Modern.App.deps.json',
    'BridgeManager.Modern.App.runtimeconfig.json',
    'BridgeManager.Core.dll',
    'Microsoft.WindowsAppRuntime.Bootstrap.dll',
    'PACKAGE-INFO.txt',
    'NOTICE.md',
    'LICENSES\HidSharp-Apache-2.0.txt',
    'LICENSE',
    'firmware-releases.lock.json',
    'modules\esp32s3-ns2-bridge\module.json',
    'modules\bl616-unified\module.json',
    'modules\bl616-unified\artifacts\blflash.json',
    'modules\bl616-unified\artifacts\boot2_bl616_isp_release_v8.1.8.bin',
    'modules\bl616-unified\artifacts\partition.bin',
    'modules\bl616-unified\artifacts\controllerbridge_bl616_bl616.bin',
    'modules\sf32-unified\module.json',
    'modules\sf32-unified\artifacts\sftool_param.json',
    'modules\sf32-unified\artifacts\bootloader\output\bootloader.bin',
    'modules\sf32-unified\artifacts\output\main.bin',
    'modules\sf32-unified\artifacts\ftab.bin',
    'modules\pico-unified-bridge\module.json',
    'modules\pico-unified-bridge\artifacts\pico-controller-bridge-0.1.uf2',
    'tools\sftool\sftool.exe',
    'tools\sftool\LICENSE.txt',
    'tools\sftool\PROVENANCE.json',
    'tools\blflash\BLFlashCommand.exe',
    'tools\blflash\LICENSE.txt',
    'tools\blflash\PROVENANCE.json',
    'SHA256SUMS.txt'
)
foreach ($file in $requiredFiles) {
    [void](Get-ContainedFile -BasePath $root -RelativePath $file)
}
$packageInfo = Get-Content -LiteralPath (Join-Path $root 'PACKAGE-INFO.txt') -Raw
if ($packageInfo.Contains('Classic diagnostics bundled: True')) {
    [void](Get-ContainedFile -BasePath $root -RelativePath 'classic-manager\BridgeManager.App.exe')
}
[void]@(& (Join-Path $PSScriptRoot 'get-firmware-packages.ps1') `
    -LockPath (Join-Path $root 'firmware-releases.lock.json') `
    -SourceDirectory (Join-Path $root 'module-packages') -Offline)

$pinnedProvenance = Join-Path $PSScriptRoot 'vendor\sftool\PROVENANCE.json'
$pin = Get-Content -LiteralPath $pinnedProvenance -Raw -Encoding UTF8 | ConvertFrom-Json
$toolRoot = Join-Path $root 'tools\sftool'
if ((Get-FileHash -LiteralPath (Join-Path $toolRoot 'PROVENANCE.json') `
        -Algorithm SHA256).Hash -ne
    (Get-FileHash -LiteralPath $pinnedProvenance -Algorithm SHA256).Hash) {
    throw 'Bundled sftool provenance does not match the repository pin.'
}
foreach ($filePin in @($pin.executable, $pin.license)) {
    $file = Join-Path $toolRoot $filePin.fileName
    if ((Get-Item -LiteralPath $file).Length -ne $filePin.size -or
        (Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash -ne $filePin.sha256) {
        throw "Pinned sftool size/SHA256 mismatch: $($filePin.fileName)"
    }
}

$expectedMethods = @{
    'esp32s3-ns2-bridge' = 'None'
    'sf32-unified' = 'SifliSerial'
    'pico-unified-bridge' = 'PicoUf2'
}
$blModuleRoot = Join-Path $root 'modules\bl616-unified'
$blManifest = Get-Content -LiteralPath (Join-Path $blModuleRoot 'module.json') `
    -Raw -Encoding UTF8 | ConvertFrom-Json
$blFirmware = @($blManifest.firmware | Where-Object {
    $_.flashMethod -eq 'BouffaloUart'
})
if ($blManifest.id -ne 'bl616-unified' -or $blFirmware.Count -ne 1) {
    throw 'Invalid bundled BL616 firmware manifest.'
}
$blBundlePath = Get-ContainedFile -BasePath $blModuleRoot `
    -RelativePath $blFirmware[0].artifactRelativePath
$blBundle = Get-Content -LiteralPath $blBundlePath -Raw -Encoding UTF8 |
    ConvertFrom-Json
if ($blBundle.schemaVersion -ne 1 -or $blBundle.chip -ne 'bl616' -or
    $blBundle.baudRate -ne 2000000 -or @($blBundle.files).Count -ne 3) {
    throw 'Invalid bundled BL616 flash bundle.'
}
$blArtifactRoot = Split-Path -Parent $blBundlePath
foreach ($filePin in $blBundle.files) {
    $file = Get-ContainedFile -BasePath $blArtifactRoot -RelativePath $filePin.path
    if ((Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash -ne
        $filePin.sha256) {
        throw "BL616 firmware SHA256 mismatch: $($filePin.path)"
    }
}
Add-Type -AssemblyName System.IO.Compression.FileSystem
foreach ($moduleId in @('esp32s3-ns2-bridge', 'sf32-unified', 'pico-unified-bridge')) {
    $moduleRoot = Join-Path $root "modules\$moduleId"
    $manifestPath = Get-ContainedFile -BasePath $moduleRoot -RelativePath 'module.json'
    $manifest = Get-Content -LiteralPath $manifestPath -Raw -Encoding UTF8 | ConvertFrom-Json
    if ($manifest.id -ne $moduleId -or @($manifest.firmware).Count -eq 0) {
        throw "Invalid bundled firmware manifest: $moduleId"
    }
    $artifactFiles = [Collections.Generic.List[string]]::new()
    $artifactFiles.Add($manifestPath)
    foreach ($firmware in $manifest.firmware) {
        if ($firmware.flashMethod -ne $expectedMethods[$moduleId]) {
            throw "Unexpected bundled flash method for ${moduleId}: $($firmware.flashMethod)"
        }
        if ($firmware.flashMethod -eq 'None') {
            if (-not [string]::IsNullOrEmpty([string]$firmware.artifactRelativePath)) {
                throw "Unsupported firmware must not advertise a bundled flash artifact: $moduleId"
            }
            continue
        }
        $artifactPath = Get-ContainedFile -BasePath $moduleRoot `
            -RelativePath $firmware.artifactRelativePath
        $artifactFiles.Add($artifactPath)
        if ($firmware.flashMethod -eq 'SifliSerial') {
            $parameters = Get-Content -LiteralPath $artifactPath -Raw -Encoding UTF8 |
                ConvertFrom-Json
            if (@($parameters.write_flash.files).Count -eq 0) {
                throw "SF32 flash parameters contain no files: $artifactPath"
            }
            $artifactRoot = Split-Path -Parent $artifactPath
            foreach ($flashFile in $parameters.write_flash.files) {
                $artifactFiles.Add((Get-ContainedFile -BasePath $artifactRoot `
                    -RelativePath ([string]$flashFile.path)))
            }
            foreach ($requiredFlashFile in @('bootloader\output\bootloader.bin',
                                             'output\main.bin', 'ftab.bin')) {
                $requiredPath = Get-ContainedFile -BasePath $artifactRoot `
                    -RelativePath $requiredFlashFile
                if (-not ($artifactFiles -contains $requiredPath)) {
                    throw "SF32 Nano parameters must reference $requiredFlashFile"
                }
            }
        } elseif ([IO.Path]::GetExtension($artifactPath) -ne '.uf2') {
            throw "Pico firmware must reference a UF2: $artifactPath"
        }
    }

    $modulePackages = @(Get-ChildItem -LiteralPath (Join-Path $root 'module-packages') `
        -Filter "$moduleId-*.cbmodule" -File)
    if ($modulePackages.Count -ne 1) {
        throw "Expected exactly one independent firmware module package: $moduleId"
    }
    $archive = [IO.Compression.ZipFile]::OpenRead($modulePackages[0].FullName)
    try {
        foreach ($requiredEntry in @('module.json', 'LICENSE', 'NOTICE.md',
                                     'MODULE-SHA256.txt')) {
            if (-not ($archive.Entries.FullName -contains $requiredEntry)) {
                throw "Module package $moduleId is missing $requiredEntry"
            }
        }
        # Validate archive payloads against the expanded defaults, including parameters.
        foreach ($artifactFile in $artifactFiles) {
            $relative = $artifactFile.Substring($moduleRoot.Length + 1).Replace('\', '/')
            $entries = @($archive.Entries | Where-Object { $_.FullName -ceq $relative })
            if ($entries.Count -ne 1 -or $entries[0].Length -eq 0) {
                throw "Module package $moduleId is missing or duplicates firmware file $relative"
            }
            $stream = $entries[0].Open()
            $sha = [Security.Cryptography.SHA256]::Create()
            try {
                $archiveHash = [BitConverter]::ToString($sha.ComputeHash($stream)).Replace('-', '')
            } finally {
                $stream.Dispose()
                $sha.Dispose()
            }
            if ($archiveHash -ne (Get-FileHash -LiteralPath $artifactFile -Algorithm SHA256).Hash) {
                throw "Module package $moduleId differs from bundled firmware file $relative"
            }
        }
    } finally {
        $archive.Dispose()
    }
}

$hashFile = Join-Path $root 'SHA256SUMS.txt'
$hashedFiles = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
foreach ($line in Get-Content -LiteralPath $hashFile) {
    if ($line -notmatch '^([0-9a-f]{64})  (.+)$') {
        throw "Malformed SHA256 entry: $line"
    }
    $expected = $Matches[1]
    $relative = $Matches[2]
    $file = Get-ContainedFile -BasePath $root -RelativePath $relative -AllowEmpty
    if (-not $hashedFiles.Add($file)) {
        throw "Duplicate SHA256 entry: $relative"
    }
    $actual = (Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($actual -ne $expected) {
        throw "SHA256 mismatch: $relative"
    }
}
foreach ($file in Get-ChildItem -LiteralPath $root -Recurse -File -Force) {
    if ($file.FullName -ne $hashFile -and -not $hashedFiles.Contains($file.FullName)) {
        throw "Package file is missing from SHA256SUMS.txt: $($file.FullName)"
    }
}

# The hash-pinned sidecar is only asked for its version; no port is opened.
$sftool = Join-Path $toolRoot $pin.executable.fileName
$versionOutput = (& $sftool --version | Out-String).Trim()
if ($LASTEXITCODE -ne 0 -or $versionOutput -cne $pin.executable.versionOutput) {
    throw "Expected '$($pin.executable.versionOutput)', got '$versionOutput'."
}
Write-Host "Verified bundled $versionOutput ($($pin.target), $($pin.license.spdx))."

$blPinnedProvenance = Join-Path $PSScriptRoot 'vendor\blflash\PROVENANCE.json'
$blPin = Get-Content -LiteralPath $blPinnedProvenance -Raw -Encoding UTF8 |
    ConvertFrom-Json
$blToolRoot = Join-Path $root 'tools\blflash'
if ((Get-FileHash -LiteralPath (Join-Path $blToolRoot 'PROVENANCE.json') `
        -Algorithm SHA256).Hash -ne
    (Get-FileHash -LiteralPath $blPinnedProvenance -Algorithm SHA256).Hash) {
    throw 'Bundled BLFlash provenance does not match the repository pin.'
}
foreach ($filePin in @($blPin.executable, $blPin.license) + @($blPin.supportFiles)) {
    $relative = if ($filePin -eq $blPin.license) { $filePin.fileName }
        elseif ($filePin -eq $blPin.executable) { $filePin.fileName }
        else { $filePin.path }
    $file = Get-ContainedFile -BasePath $blToolRoot -RelativePath $relative
    if ((Get-Item -LiteralPath $file).Length -ne $filePin.size -or
        (Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash -ne $filePin.sha256) {
        throw "Pinned BLFlash size/SHA256 mismatch: $relative"
    }
}
$blHelp = (& (Join-Path $blToolRoot $blPin.executable.fileName) --help 2>&1 |
    Out-String)
if ($LASTEXITCODE -ne 0 -or -not $blHelp.Contains('chipname')) {
    throw 'Bundled BLFlashCommand did not pass its read-only help check.'
}
Write-Host "Verified bundled BLFlashCommand $($blPin.version) (BL616-only, $($blPin.license.spdx))."

if ($LaunchSmoke) {
    $process = Start-Process `
        -FilePath (Join-Path $root 'BridgeManager.Modern.App.exe') `
        -ArgumentList '--launch-smoke' -PassThru -WindowStyle Hidden
    if (-not $process.WaitForExit(30000)) {
        Stop-Process -Id $process.Id -Force
        throw 'Modern manager did not finish its launch smoke within 30 seconds.'
    }
    if ($process.ExitCode -ne 0) {
        throw "Modern manager launch smoke failed with code $($process.ExitCode)."
    }
}

Write-Host "Verified modern package: $root"
