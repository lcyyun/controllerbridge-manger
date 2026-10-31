[CmdletBinding()]
param([Parameter(Mandatory = $true)][string]$EsptoolArchive)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
. (Join-Path $PSScriptRoot 'verify-espressif-assets.ps1')
Add-Type -AssemblyName System.IO.Compression.FileSystem
$results = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../test-results'))
$root = Join-Path $results ('esp-package-' + [Guid]::NewGuid().ToString('N'))
$module = Join-Path $root 'module'
$assets = Join-Path $module 'firmware/n16r8'
New-Item -ItemType Directory -Path $assets -Force | Out-Null
$script:passed = 0
function Reject([string]$Name, [scriptblock]$Action, [string]$Expected) {
    $failure = $null
    try { & $Action | Out-Null } catch { $failure = $_.Exception.Message }
    if (-not $failure -or -not $failure.Contains($Expected)) { throw "$Name failed at unexpected stage: $failure" }
    $script:passed++
    Write-Host "PASS $Name"
}
try {
    $moduleDefinition = @{
        boards=@(@{id='esp32s3-n16r8'})
        firmware=@(@{id='esp32s3-n16r8';boardIds=@('esp32s3-n16r8');flashMethod='EspressifSerial';
            artifactRelativePath='firmware/n16r8/flash-manifest.json'})
    } | ConvertTo-Json -Depth 8
    Assert-EspressifModuleBoards ($moduleDefinition | ConvertFrom-Json)
    $script:passed++
    Write-Host 'PASS N16R8-only module is a complete supported release'
    foreach ($case in @(
        @{Name='missing required N16R8'; Change={param($v) $v.boards[0].id='esp32s3-n8';$v.firmware[0].boardIds=@('esp32s3-n8')}; Error='requires N16R8'},
        @{Name='uncompiled board advertised'; Change={param($v) $v.boards+=@{id='esp32s3-n8'}}; Error='one firmware entry'},
        @{Name='phantom firmware board'; Change={param($v) $v.firmware[0].boardIds=@('esp32s3-n8')}; Error='one-to-one'},
        @{Name='unsupported board'; Change={param($v) $v.boards[0].id='esp32c3-n4'}; Error='Unsupported'},
        @{Name='duplicate board declaration'; Change={param($v) $v.boards+=@{id='esp32s3-n16r8'}}; Error='duplicate'},
        @{Name='no artifact for declared board'; Change={param($v) $v.firmware[0].artifactRelativePath=''}; Error='one-to-one'}
    )) {
        $value = $moduleDefinition | ConvertFrom-Json
        & $case.Change $value
        Reject $case.Name { Assert-EspressifModuleBoards $value } $case.Error
    }
    $paired = $moduleDefinition | ConvertFrom-Json
    $paired.boards += @{id='esp32s3-n8'}
    $paired.firmware += @{id='esp32s3-n8';boardIds=@('esp32s3-n8');flashMethod='EspressifSerial';
        artifactRelativePath='firmware/n8/flash-manifest.json'}
    Assert-EspressifModuleBoards $paired
    $script:passed++
    $paired.firmware[1].id = 'esp32s3-n16r8'
    Reject 'duplicate firmware identity' { Assert-EspressifModuleBoards $paired } 'one-to-one'
    $paired.firmware[1].id = 'esp32s3-n8'
    $paired.firmware[1].boardIds = @('esp32s3-n16r8')
    Reject 'two images claim one board' { Assert-EspressifModuleBoards $paired } 'one-to-one'
    $image = [byte[]]::new(512)
    $image[0] = 0xe9; $image[1] = 3; $image[2] = 2; $image[3] = 0x4f; $image[12] = 9
    $partition = [byte[]]::new(3072)
    for ($i = 0; $i -lt $partition.Length; $i++) { $partition[$i] = 0xff }
    [BitConverter]::GetBytes([uint16]0x50aa).CopyTo($partition, 0)
    $partition[2] = 0; $partition[3] = 0
    [BitConverter]::GetBytes([uint32]0x10000).CopyTo($partition, 4)
    [BitConverter]::GetBytes([uint32]0x100000).CopyTo($partition, 8)
    $entries = @()
    foreach ($kind in @('bootloader', 'partition', 'application')) {
        $bytes = if ($kind -eq 'partition') { $partition } else { $image }
        $path = Join-Path $assets "$kind.bin"
        [IO.File]::WriteAllBytes($path, $bytes)
        $entries += @{ kind=$kind; path="$kind.bin"; address=@{bootloader='0x0';partition='0x8000';application='0x10000'}[$kind]
            size=$bytes.Length; sha256=(Get-FileHash -LiteralPath $path).Hash.ToLowerInvariant() }
    }
    $definition = @{
        schemaVersion=1; chip='esp32s3'; board='n16r8'; flashSizeMb=16; psramSizeMb=8; psramMode='octal'
        flashMode='dio'; flashFrequencyMhz=80; baudRate=460800; files=$entries
    } | ConvertTo-Json -Depth 8
    $manifest = Join-Path $assets 'flash-manifest.json'
    [IO.File]::WriteAllText($manifest, $definition)
    $files = @(Get-EspressifBundleFiles $manifest @('esp32s3-n16r8'))
    if ($files.Count -ne 3) { throw 'Valid bundle did not expose exactly its three images.' }
    $script:passed++
    $missingImagePath = Join-Path $assets 'application.bin'
    $missingImageBytes = [IO.File]::ReadAllBytes($missingImagePath)
    Remove-Item -LiteralPath $missingImagePath
    Reject 'declared image missing on disk' { Get-EspressifBundleFiles $manifest @('esp32s3-n16r8') } 'Missing ESP32-S3 asset'
    [IO.File]::WriteAllBytes($missingImagePath, $missingImageBytes)
    foreach ($case in @(
        @{Name='wrong physical board'; Change={param($v) $v.board='n8r8'}; Error='module and flash bundle'},
        @{Name='wrong chip'; Change={param($v) $v.chip='esp32'}; Error='parameters'},
        @{Name='wrong PSRAM'; Change={param($v) $v.psramSizeMb=2}; Error='parameters'},
        @{Name='wrong flash mode'; Change={param($v) $v.flashMode='qio'}; Error='parameters'},
        @{Name='numeric string'; Change={param($v) $v.flashSizeMb='16'}; Error='integer'},
        @{Name='wrong address'; Change={param($v) $v.files[2].address='0x8000'}; Error='address'},
        @{Name='duplicate image kind'; Change={param($v) $v.files[2].kind='bootloader'}; Error='duplicate'},
        @{Name='size mismatch'; Change={param($v) $v.files[2].size=99}; Error='size/range'},
        @{Name='hash mismatch'; Change={param($v) $v.files[2].sha256='0'*64}; Error='SHA256'},
        @{Name='parent traversal'; Change={param($v) $v.files[2].path='../application.bin'}; Error='Unsafe'},
        @{Name='alternate data stream'; Change={param($v) $v.files[2].path='application.bin:ads'}; Error='Unsafe'},
        @{Name='trailing dot alias'; Change={param($v) $v.files[2].path='application.bin.'}; Error='Unsafe'}
    )) {
        $value = $definition | ConvertFrom-Json
        & $case.Change $value
        [IO.File]::WriteAllText($manifest, ($value | ConvertTo-Json -Depth 8))
        Reject $case.Name { Get-EspressifBundleFiles $manifest @('esp32s3-n16r8') } $case.Error
    }
    $appPath = Join-Path $assets 'application.bin'
    $image[12] = 0
    [IO.File]::WriteAllBytes($appPath, $image)
    $value = $definition | ConvertFrom-Json
    $value.files[2].sha256 = (Get-FileHash -LiteralPath $appPath).Hash
    [IO.File]::WriteAllText($manifest, ($value | ConvertTo-Json -Depth 8))
    Reject 'hash-correct non-S3 image' { Get-EspressifBundleFiles $manifest @('esp32s3-n16r8') } 'image header'
    $image[12] = 9
    [IO.File]::WriteAllBytes($appPath, $image)
    [BitConverter]::GetBytes([uint32]256).CopyTo($partition, 8)
    $partitionPath = Join-Path $assets 'partition.bin'
    [IO.File]::WriteAllBytes($partitionPath, $partition)
    $value = $definition | ConvertFrom-Json
    $value.files[1].sha256 = (Get-FileHash -LiteralPath $partitionPath).Hash
    [IO.File]::WriteAllText($manifest, ($value | ConvertTo-Json -Depth 8))
    Reject 'application exceeds partition' { Get-EspressifBundleFiles $manifest @('esp32s3-n16r8') } 'factory partition'
    [BitConverter]::GetBytes([uint32]0x100000).CopyTo($partition, 8)
    [IO.File]::WriteAllBytes($partitionPath, $partition)
    [IO.File]::WriteAllText($manifest, $definition)

    $archivePath = Join-Path $root 'esp32s3-fixture.cbmodule'
    [IO.Compression.ZipFile]::CreateFromDirectory($module, $archivePath)
    $archive = [IO.Compression.ZipFile]::OpenRead($archivePath)
    try {
        $artifacts = @($manifest) + $files
        Assert-PackagedArtifactMatchesArchive $archive $module $artifacts 'esp32s3-fixture'
        $script:passed++
        $image[100] = 1
        [IO.File]::WriteAllBytes($appPath, $image)
        Reject 'expanded image differs from cbmodule' {
            Assert-PackagedArtifactMatchesArchive $archive $module $artifacts 'esp32s3-fixture'
        } 'differs from bundled'
        $image[100] = 0
        [IO.File]::WriteAllBytes($appPath, $image)
        [IO.File]::AppendAllText($manifest, ' ')
        Reject 'expanded manifest differs from cbmodule' {
            Assert-PackagedArtifactMatchesArchive $archive $module $artifacts 'esp32s3-fixture'
        } 'differs from bundled'
    } finally { $archive.Dispose() }

    # Only the hash-pinned official tool's version command runs. Fixtures are data.
    & (Join-Path $PSScriptRoot 'bundle-esptool.ps1') -OutputDirectory $root -ArchivePath $EsptoolArchive
    $toolRoot = Join-Path $root 'tools/esptool'
    $pinPath = Join-Path $PSScriptRoot 'vendor/esptool/PROVENANCE.json'
    Assert-PinnedEspressifTool $toolRoot $pinPath
    $script:passed++
    foreach ($name in @('PROVENANCE.json', 'LICENSE.txt', 'esptool.exe')) {
        $path = Join-Path $toolRoot $name
        $original = [IO.File]::ReadAllBytes($path)
        [IO.File]::AppendAllText($path, 'tampered')
        $expectedFailure = if ($name -eq 'PROVENANCE.json') { 'provenance' } else { 'SHA256' }
        Reject "pinned $name tamper" { Assert-PinnedEspressifTool $toolRoot $pinPath } $expectedFailure
        [IO.File]::WriteAllBytes($path, $original)
    }
    Write-Host "PASS $script:passed ESP32-S3 package checks; offline, no port opened or firmware written."
} finally {
    $prefix = $results.TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
    if (-not [IO.Path]::GetFullPath($root).StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Unsafe test fixture cleanup path.'
    }
    if (Test-Path -LiteralPath $root) { Remove-Item -LiteralPath $root -Recurse -Force }
}
