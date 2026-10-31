# Offline package checks shared by the distributable verifier and its fixtures.
# These functions never enumerate ports or invoke a flashing operation.
function Get-EspressifContainedFile([string]$BasePath, [string]$RelativePath) {
    $parts = $RelativePath.Replace('\', '/').Split('/')
    if ([string]::IsNullOrWhiteSpace($RelativePath) -or [IO.Path]::IsPathRooted($RelativePath) -or
        $RelativePath.Contains(':') -or @($parts | Where-Object {
            $_ -in @('', '.', '..') -or $_.EndsWith(' ') -or $_.EndsWith('.') -or
            $_.IndexOfAny([IO.Path]::GetInvalidFileNameChars()) -ge 0
        }).Count -ne 0) { throw "Unsafe ESP32-S3 asset path: $RelativePath" }
    $current = [IO.Path]::GetFullPath($BasePath)
    foreach ($part in @('') + $parts) {
        if ($part) { $current = Join-Path $current $part }
        if (-not (Test-Path -LiteralPath $current)) { throw "Missing ESP32-S3 asset: $RelativePath" }
        if ((Get-Item -LiteralPath $current -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) {
            throw "ESP32-S3 asset path contains a reparse point: $RelativePath"
        }
    }
    if (-not (Test-Path -LiteralPath $current -PathType Leaf)) { throw "Missing ESP32-S3 asset: $RelativePath" }
    return $current
}

function Assert-EspressifModuleBoards($Manifest) {
    $supported = @('esp32s3-n4', 'esp32s3-n8', 'esp32s3-n8r2', 'esp32s3-n8r8',
        'esp32s3-n16r8', 'esp32s3-esp32supermini')
    $boards = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach ($board in $Manifest.boards) {
        if ($board.id -cnotin $supported -or -not $boards.Add([string]$board.id)) {
            throw 'Unsupported or duplicate ESP32-S3 module board.'
        }
    }
    if (-not $boards.Contains('esp32s3-n16r8') -or $boards.Count -ne @($Manifest.firmware).Count) {
        throw 'ESP32-S3 requires N16R8 and one firmware entry per declared board.'
    }
    $firmwareIds = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    $firmwareBoards = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach ($firmware in $Manifest.firmware) {
        if ([string]::IsNullOrWhiteSpace([string]$firmware.id) -or -not $firmwareIds.Add([string]$firmware.id) -or
            @($firmware.boardIds).Count -ne 1 -or -not $boards.Contains([string]$firmware.boardIds[0]) -or
            -not $firmwareBoards.Add([string]$firmware.boardIds[0]) -or
            $firmware.flashMethod -cne 'EspressifSerial' -or
            [string]::IsNullOrWhiteSpace([string]$firmware.artifactRelativePath)) {
            throw 'ESP32-S3 boards and flash artifacts must have a unique one-to-one correspondence.'
        }
    }
}

function Get-EspressifInteger($Value, [string]$Name, [long]$Minimum, [long]$Maximum) {
    if (($Value -isnot [int] -and $Value -isnot [long]) -or $Value -lt $Minimum -or $Value -gt $Maximum) {
        throw "Invalid ESP32-S3 integer: $Name"
    }
    return [long]$Value
}

function Get-EspressifBundleFiles([string]$ManifestPath, [string[]]$BoardIds) {
    $bundle = Get-Content -LiteralPath $ManifestPath -Raw -Encoding UTF8 | ConvertFrom-Json
    $memory = @{
        n4 = @(4, 0, 'none'); n8 = @(8, 0, 'none'); n8r2 = @(8, 2, 'quad')
        n8r8 = @(8, 8, 'octal'); n16r8 = @(16, 8, 'octal'); esp32supermini = @(4, 2, 'quad')
    }
    $board = [string]$bundle.board
    if (-not $memory.ContainsKey($board) -or $board -cnotin $memory.Keys -or
        $BoardIds.Count -ne 1 -or $BoardIds[0] -cne "esp32s3-$board") {
        throw 'ESP32-S3 module and flash bundle board differ.'
    }
    $flash = Get-EspressifInteger $bundle.flashSizeMb 'flashSizeMb' 4 16
    $psram = Get-EspressifInteger $bundle.psramSizeMb 'psramSizeMb' 0 8
    if ((Get-EspressifInteger $bundle.schemaVersion 'schemaVersion' 1 1) -ne 1 -or
        $bundle.chip -cne 'esp32s3' -or $flash -ne $memory[$board][0] -or
        $psram -ne $memory[$board][1] -or $bundle.psramMode -cne $memory[$board][2] -or
        $bundle.flashMode -cne 'dio' -or
        (Get-EspressifInteger $bundle.flashFrequencyMhz 'flashFrequencyMhz' 80 80) -ne 80 -or
        (Get-EspressifInteger $bundle.baudRate 'baudRate' 460800 460800) -ne 460800 -or
        @($bundle.files).Count -ne 3) { throw 'Invalid ESP32-S3 flash bundle parameters.' }
    $basePath = Split-Path -Parent $ManifestPath
    $images = [Collections.Generic.List[object]]::new()
    $expectedAddresses = @{ bootloader = 0L; partition = 0x8000L; application = 0x10000L }
    $kinds = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach ($entry in $bundle.files) {
        $kind = [string]$entry.kind
        if (-not $expectedAddresses.ContainsKey($kind) -or $kind -cnotin $expectedAddresses.Keys -or
            -not $kinds.Add($kind) -or [string]$entry.address -cnotmatch '^0x[0-9a-fA-F]{1,8}$') {
            throw 'Invalid or duplicate ESP32-S3 image kind/address.'
        }
        $address = [Convert]::ToUInt32($entry.address.Substring(2), 16)
        if ($address -ne $expectedAddresses[$kind]) { throw "Wrong ESP32-S3 $kind address." }
        $file = Get-EspressifContainedFile $basePath ([string]$entry.path)
        $length = (Get-Item -LiteralPath $file).Length
        $size = Get-EspressifInteger $entry.size 'image size' 1 ($flash * 1MB)
        if ($length -ne $size -or [long]$address + $length -gt $flash * 1MB -or
            ($kind -ceq 'bootloader' -and $length -gt 0x8000) -or
            ($kind -ceq 'partition' -and $length -gt 0x1000)) { throw "Invalid ESP32-S3 $kind size/range." }
        foreach ($image in $images) {
            if ($address -lt $image.End -and [long]$address + $length -gt $image.Address) {
                throw 'ESP32-S3 image ranges overlap.'
            }
        }
        if ([string]$entry.sha256 -notmatch '^[0-9a-fA-F]{64}$' -or
            (Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash -ine $entry.sha256) {
            throw "ESP32-S3 firmware size/SHA256 mismatch: $($entry.path)"
        }
        $images.Add([pscustomobject]@{ Kind=$kind; Path=$file; Address=[long]$address; Size=$length; End=([long]$address + $length) })
        if ($kind -cne 'partition') {
            $stream = [IO.File]::OpenRead($file)
            $reader = [IO.BinaryReader]::new($stream)
            try { $header = $reader.ReadBytes(24) } finally { $reader.Dispose(); $stream.Dispose() }
            $sizeCode = @{ 4=2; 8=3; 16=4 }[[int]$flash]
            if ($header.Length -ne 24 -or $header[0] -ne 0xe9 -or $header[1] -lt 1 -or $header[1] -gt 16 -or
                $header[2] -ne 2 -or ($header[3] -shr 4) -ne $sizeCode -or ($header[3] -band 15) -ne 15 -or
                [BitConverter]::ToUInt16($header, 12) -ne 9) { throw "ESP32-S3 $kind image header disagrees with board/DIO/80MHz." }
        }
    }
    $partition = @($images | Where-Object Kind -CEQ 'partition')[0]
    $app = @($images | Where-Object Kind -CEQ 'application')[0]
    $bytes = [IO.File]::ReadAllBytes($partition.Path)
    $ranges = [Collections.Generic.List[object]]::new()
    $factoryCount = 0
    for ($offset = 0; $offset + 32 -le $bytes.Length; $offset += 32) {
        $magic = [BitConverter]::ToUInt16($bytes, $offset)
        if ($magic -in @(0xffff, 0xebeb)) { break }
        if ($magic -ne 0x50aa) { throw 'Invalid ESP32-S3 partition table record.' }
        $start = [long][BitConverter]::ToUInt32($bytes, $offset + 4)
        $length = [long][BitConverter]::ToUInt32($bytes, $offset + 8)
        $end = $start + $length
        if ($start -lt 0x9000 -or $length -eq 0 -or $end -gt $flash * 1MB) { throw 'ESP32-S3 partition exceeds Flash.' }
        foreach ($range in $ranges) {
            if ($start -lt $range.End -and $end -gt $range.Start) { throw 'ESP32-S3 partitions overlap.' }
        }
        $ranges.Add([pscustomobject]@{ Start=$start; End=$end })
        if ($bytes[$offset + 2] -eq 0) {
            if ($bytes[$offset + 3] -ne 0 -or $start -ne $app.Address -or $length -lt $app.Size) {
                throw 'ESP32-S3 application does not fit its factory partition.'
            }
            $factoryCount++
        }
    }
    if ($factoryCount -ne 1) { throw 'ESP32-S3 requires one factory application partition.' }
    return @($images | ForEach-Object Path)
}

function Assert-PinnedEspressifTool([string]$ToolRoot, [string]$PinPath) {
    $pin = Get-Content -LiteralPath $PinPath -Raw -Encoding UTF8 | ConvertFrom-Json
    $provenance = Get-EspressifContainedFile $ToolRoot 'PROVENANCE.json'
    if ((Get-FileHash -LiteralPath $provenance -Algorithm SHA256).Hash -ine
        (Get-FileHash -LiteralPath $PinPath -Algorithm SHA256).Hash) {
        throw 'Bundled esptool provenance does not match the repository pin.'
    }
    foreach ($filePin in @($pin.executable, $pin.license)) {
        $file = Get-EspressifContainedFile $ToolRoot $filePin.fileName
        if ((Get-Item -LiteralPath $file).Length -ne $filePin.size -or
            (Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash -ine $filePin.sha256) {
            throw "Pinned esptool size/SHA256 mismatch: $($filePin.fileName)"
        }
    }
}

function Assert-PackagedArtifactMatchesArchive($Archive, [string]$ModuleRoot,
    [string[]]$ArtifactFiles, [string]$ModuleId) {
    foreach ($artifactFile in $ArtifactFiles) {
        $relative = $artifactFile.Substring($ModuleRoot.Length + 1).Replace('\', '/')
        $entries = @($Archive.Entries | Where-Object { $_.FullName -ceq $relative })
        if ($entries.Count -ne 1 -or $entries[0].Length -eq 0) {
            throw "Module package $ModuleId is missing or duplicates firmware file $relative"
        }
        $stream = $entries[0].Open()
        $sha = [Security.Cryptography.SHA256]::Create()
        try { $archiveHash = [BitConverter]::ToString($sha.ComputeHash($stream)).Replace('-', '') }
        finally { $stream.Dispose(); $sha.Dispose() }
        if ($archiveHash -ne (Get-FileHash -LiteralPath $artifactFile -Algorithm SHA256).Hash) {
            throw "Module package $ModuleId differs from bundled firmware file $relative"
        }
    }
}
