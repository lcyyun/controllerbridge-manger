[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',
    [ValidateSet('win-x64')]
    [string]$Runtime = 'win-x64',
    [string]$PackageName = 'BridgeManager-modern-win-x64',
    [string]$SftoolPath = $env:BRIDGE_MANAGER_SFTOOL,
    [string]$ModulePackageDirectory,
    [string]$ClassicDirectory,
    [switch]$Offline,
    [switch]$SkipTests
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$managerRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$repositoryRoot = $managerRoot
$distRoot = [IO.Path]::GetFullPath((Join-Path $managerRoot 'dist'))
$outputDir = [IO.Path]::GetFullPath((Join-Path $distRoot $PackageName))
$zipPath = [IO.Path]::GetFullPath((Join-Path $distRoot "$PackageName.zip"))
$distPrefix = $distRoot.TrimEnd([IO.Path]::DirectorySeparatorChar) +
    [IO.Path]::DirectorySeparatorChar
$localDotnet = Join-Path $env:USERPROFILE '.dotnet8\dotnet.exe'
$dotnetExe = if ($env:BRIDGE_MANAGER_DOTNET) {
    [IO.Path]::GetFullPath($env:BRIDGE_MANAGER_DOTNET)
} elseif (Test-Path -LiteralPath $localDotnet) {
    $localDotnet
} else {
    (Get-Command dotnet -ErrorAction Stop).Source
}
$dotnetVersion = (& $dotnetExe --version).Trim()
if ([int]($dotnetVersion.Split('.')[0]) -lt 8) {
    throw "Modern manager packaging requires .NET 8 SDK or newer."
}
if (-not $outputDir.StartsWith($distPrefix, [StringComparison]::OrdinalIgnoreCase)) {
    throw "Package output must remain inside $distRoot"
}
$moduleSources = @(& (Join-Path $PSScriptRoot 'get-firmware-packages.ps1') `
    -SourceDirectory $ModulePackageDirectory -Offline:$Offline)
$classicSource = if ($ClassicDirectory) { [IO.Path]::GetFullPath($ClassicDirectory) } else { $null }
if ($classicSource -and
    -not (Test-Path -LiteralPath (Join-Path $classicSource 'BridgeManager.App.exe') -PathType Leaf)) {
    throw "Selected classic manager is missing: $classicSource"
}
if ($classicSource -and
    ($classicSource.Equals($outputDir, [StringComparison]::OrdinalIgnoreCase) -or
     $classicSource.StartsWith($outputDir + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase))) {
    throw 'Classic input must not be inside the package output directory.'
}

if (-not $SkipTests) {
    $testProject = Join-Path $managerRoot `
        'tests\BridgeManager.Core.Tests\BridgeManager.Core.Tests.csproj'
    & $dotnetExe build $testProject -c $Configuration -p:Platform=x64 -p:RestoreLockedMode=true
    if ($LASTEXITCODE -ne 0) { throw 'Core tests build failed.' }
    $testExecutable = Join-Path $managerRoot `
        "tests\BridgeManager.Core.Tests\bin\x64\$Configuration\net8.0-windows10.0.19041.0\BridgeManager.Core.Tests.exe"
    & $testExecutable
    if ($LASTEXITCODE -ne 0) { throw 'Core tests failed.' }
}

if (Test-Path -LiteralPath $outputDir) {
    $resolved = [IO.Path]::GetFullPath($outputDir)
    if (-not $resolved.StartsWith($distPrefix, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to remove output outside dist: $resolved"
    }
    Remove-Item -LiteralPath $resolved -Recurse -Force
}
if (Test-Path -LiteralPath $zipPath) {
    Remove-Item -LiteralPath $zipPath -Force
}
New-Item -ItemType Directory -Path $outputDir -Force | Out-Null

$project = Join-Path $managerRoot `
    'src\BridgeManager.Modern.App\BridgeManager.Modern.App.csproj'
& $dotnetExe publish $project -c $Configuration -r $Runtime --self-contained true `
    -p:Platform=x64 `
    -p:RestoreLockedMode=true `
    -p:WindowsPackageType=None `
    -p:WindowsAppSDKSelfContained=true `
    -p:PublishSingleFile=false `
    -o $outputDir
if ($LASTEXITCODE -ne 0) { throw 'Modern manager publish failed.' }

& (Join-Path $PSScriptRoot 'bundle-sftool.ps1') `
    -OutputDirectory $outputDir -SftoolPath $SftoolPath

$modulePackageRoot = Join-Path $outputDir 'module-packages'
New-Item -ItemType Directory -Path $modulePackageRoot -Force | Out-Null
foreach ($moduleSource in $moduleSources) {
    Copy-Item -LiteralPath $moduleSource.Path -Destination $modulePackageRoot
}

foreach ($modulePackage in Get-ChildItem -LiteralPath $modulePackageRoot `
        -Filter '*.cbmodule' -File) {
    $moduleId = $modulePackage.BaseName -replace '-[0-9].*$', ''
    $moduleRoot = Join-Path $outputDir "modules\$moduleId"
    if (-not (Test-Path -LiteralPath $moduleRoot -PathType Container)) {
        throw "Published module directory is missing: $moduleRoot"
    }
    $extractRoot = Join-Path $modulePackageRoot ".extract-$moduleId"
    if (Test-Path -LiteralPath $extractRoot) {
        Remove-Item -LiteralPath $extractRoot -Recurse -Force
    }
    New-Item -ItemType Directory -Path $extractRoot -Force | Out-Null
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $archive = [IO.Compression.ZipFile]::OpenRead($modulePackage.FullName)
    try {
        $entryNames = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
        [long]$expandedBytes = 0
        foreach ($entry in $archive.Entries) {
            $name = $entry.FullName.Replace('\', '/')
            if ([string]::IsNullOrWhiteSpace($name) -or $name.StartsWith('/') -or
                $name.Contains(':') -or $name.Split('/') -contains '..' -or
                $name.Split('/') -contains '.' -or -not $entryNames.Add($name) -or
                (($entry.ExternalAttributes -shr 16) -band 0xF000) -eq 0xA000) {
                throw "Unsafe or duplicate firmware archive entry: $name"
            }
            $expandedBytes += $entry.Length
            if ($expandedBytes -gt 512MB) { throw 'Expanded firmware archive is too large.' }
        }
    } finally { $archive.Dispose() }
    $temporaryZip = Join-Path $modulePackageRoot "$moduleId.zip"
    Copy-Item -LiteralPath $modulePackage.FullName -Destination $temporaryZip -Force
    Expand-Archive -LiteralPath $temporaryZip -DestinationPath $extractRoot -Force
    Remove-Item -LiteralPath $temporaryZip -Force
    Copy-Item -Path (Join-Path $extractRoot '*') -Destination $moduleRoot `
        -Recurse -Force
    Remove-Item -LiteralPath $extractRoot -Recurse -Force
}

Copy-Item -LiteralPath (Join-Path $managerRoot 'README.md') `
    -Destination (Join-Path $outputDir 'README.md') -Force
Copy-Item -LiteralPath (Join-Path $repositoryRoot 'NOTICE.md') `
    -Destination (Join-Path $outputDir 'NOTICE.md') -Force
Copy-Item -LiteralPath (Join-Path $repositoryRoot 'LICENSES') `
    -Destination (Join-Path $outputDir 'LICENSES') -Recurse -Force
Copy-Item -LiteralPath (Join-Path $repositoryRoot 'LICENSE') `
    -Destination (Join-Path $outputDir 'LICENSE') -Force
Copy-Item -LiteralPath (Join-Path $managerRoot 'firmware-releases.lock.json') `
    -Destination (Join-Path $outputDir 'firmware-releases.lock.json') -Force
if ($classicSource) {
    Copy-Item -LiteralPath $classicSource `
        -Destination (Join-Path $outputDir 'classic-manager') -Recurse -Force
}

$packageInfo = @(
    'Controller Bridge Modern Manager'
    "Configuration: $Configuration"
    "Runtime: $Runtime"
    ".NET SDK: $dotnetVersion"
    "Built UTC: $([DateTime]::UtcNow.ToString('u'))"
    'Primary transport: USB HID'
    'Firmware support: independently updateable .cbmodule packages'
    'Default firmware payloads: SF32 Nano flash parameters and binaries; Pico UF2'
    'SF32 flash tool: tools/sftool/sftool.exe (pinned 0.1.16, Apache-2.0)'
    "Classic diagnostics bundled: $([bool]$classicSource)"
) -join [Environment]::NewLine
Set-Content -LiteralPath (Join-Path $outputDir 'PACKAGE-INFO.txt') `
    -Value $packageInfo -Encoding UTF8

$hashFile = Join-Path $outputDir 'SHA256SUMS.txt'
$hashLines = Get-ChildItem -LiteralPath $outputDir -Recurse -File -Force |
    Where-Object { $_.FullName -ne $hashFile } |
    Sort-Object FullName |
    ForEach-Object {
        $relative = $_.FullName.Substring($outputDir.Length + 1).Replace('\', '/')
        $hash = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
        "$hash  $relative"
    }
Set-Content -LiteralPath $hashFile -Value $hashLines -Encoding ASCII

& (Join-Path $PSScriptRoot 'verify-modern-package.ps1') -PackagePath $outputDir
if ($LASTEXITCODE -ne 0) { throw 'Modern package verification failed.' }

Compress-Archive -Path (Join-Path $outputDir '*') -DestinationPath $zipPath `
    -CompressionLevel Optimal
Write-Host "Modern package directory: $outputDir"
Write-Host "Modern package archive:   $zipPath"
