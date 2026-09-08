[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',
    [ValidateSet('win-x64')]
    [string]$Runtime = 'win-x64',
    [string]$PackageName = 'BridgeManager-sf32-unified-win-x64',
    [switch]$SkipTests
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$managerRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$repositoryRoot = $managerRoot
$distRoot = [IO.Path]::GetFullPath((Join-Path $managerRoot 'dist'))
$outputDir = [IO.Path]::GetFullPath((Join-Path $distRoot $PackageName))
$zipPath = [IO.Path]::GetFullPath((Join-Path $distRoot "$PackageName.zip"))
$distPrefix = $distRoot.TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
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
    throw "Bridge Manager packaging requires .NET 8 SDK or newer; found $dotnetVersion at $dotnetExe"
}

if (-not $outputDir.StartsWith($distPrefix, [StringComparison]::OrdinalIgnoreCase)) {
    throw "Package output must remain inside $distRoot"
}

if (-not $SkipTests) {
    $testProject = Join-Path $managerRoot 'tests\BridgeManager.Core.Tests\BridgeManager.Core.Tests.csproj'
    & $dotnetExe build $testProject -c $Configuration -p:Platform=x64
    if ($LASTEXITCODE -ne 0) {
        throw 'BridgeManager.Core.Tests build failed.'
    }
    $testExecutable = Join-Path $managerRoot "tests\BridgeManager.Core.Tests\bin\x64\$Configuration\net8.0-windows10.0.19041.0\BridgeManager.Core.Tests.exe"
    & $testExecutable
    if ($LASTEXITCODE -ne 0) {
        throw 'BridgeManager.Core.Tests failed.'
    }
}

if (Test-Path -LiteralPath $outputDir) {
    Remove-Item -LiteralPath $outputDir -Recurse -Force
}
if (Test-Path -LiteralPath $zipPath) {
    Remove-Item -LiteralPath $zipPath -Force
}
New-Item -ItemType Directory -Path $outputDir -Force | Out-Null

$project = Join-Path $managerRoot 'src\BridgeManager.App\BridgeManager.App.csproj'
& $dotnetExe publish $project -c $Configuration -r $Runtime --self-contained true `
    -p:Platform=x64 `
    -p:WindowsPackageType=None `
    -p:WindowsAppSDKSelfContained=true `
    -p:PublishSingleFile=false `
    -o $outputDir
if ($LASTEXITCODE -ne 0) {
    throw 'Bridge Manager publish failed.'
}

Copy-Item -LiteralPath (Join-Path $managerRoot 'README.md') `
    -Destination (Join-Path $outputDir 'README.md') -Force
Copy-Item -LiteralPath (Join-Path $repositoryRoot 'NOTICE.md') `
    -Destination (Join-Path $outputDir 'NOTICE.md') -Force
Copy-Item -LiteralPath (Join-Path $repositoryRoot 'LICENSES') `
    -Destination (Join-Path $outputDir 'LICENSES') -Recurse -Force

$packageInfo = @(
    'SF32LB52 Unified Bridge Manager'
    "Configuration: $Configuration"
    "Runtime: $Runtime"
    ".NET SDK: $dotnetVersion"
    "Built UTC: $([DateTime]::UtcNow.ToString('u'))"
    'Primary transport: USB HID'
    'Diagnostic-only fallback: SF32LB52 serial console at 1000000 baud'
) -join [Environment]::NewLine
Set-Content -LiteralPath (Join-Path $outputDir 'PACKAGE-INFO.txt') `
    -Value $packageInfo -Encoding UTF8

$hashFile = Join-Path $outputDir 'SHA256SUMS.txt'
$hashLines = Get-ChildItem -LiteralPath $outputDir -Recurse -File |
    Where-Object { $_.FullName -ne $hashFile } |
    Sort-Object FullName |
    ForEach-Object {
        $relative = $_.FullName.Substring($outputDir.Length + 1).Replace('\', '/')
        $hash = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
        "$hash  $relative"
    }
Set-Content -LiteralPath $hashFile -Value $hashLines -Encoding ASCII

& (Join-Path $PSScriptRoot 'verify-package.ps1') -PackagePath $outputDir
if ($LASTEXITCODE -ne 0) {
    throw 'Package verification failed.'
}

Compress-Archive -Path (Join-Path $outputDir '*') -DestinationPath $zipPath `
    -CompressionLevel Optimal
Write-Host "Package directory: $outputDir"
Write-Host "Package archive:   $zipPath"
