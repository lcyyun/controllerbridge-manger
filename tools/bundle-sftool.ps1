[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$OutputDirectory,
    [string]$SftoolPath = $env:BRIDGE_MANAGER_SFTOOL,
    [string]$ArchivePath
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function Assert-PinnedFile {
    param([string]$Path, $Pin)
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw "Missing pinned sftool file: $Path"
    }
    if ((Get-Item -LiteralPath $Path).Length -ne $Pin.size -or
        (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash -ne $Pin.sha256) {
        throw "Pinned sftool size/SHA256 mismatch: $Path"
    }
}

if ($SftoolPath -and $ArchivePath) {
    throw 'Specify either -SftoolPath or -ArchivePath, not both.'
}

$vendorRoot = Join-Path $PSScriptRoot 'vendor\sftool'
$provenancePath = Join-Path $vendorRoot 'PROVENANCE.json'
$pin = Get-Content -LiteralPath $provenancePath -Raw -Encoding UTF8 | ConvertFrom-Json
$licensePath = Join-Path $vendorRoot $pin.license.fileName
Assert-PinnedFile -Path $licensePath -Pin $pin.license

$root = [IO.Path]::GetFullPath($OutputDirectory)
$toolRoot = Join-Path $root 'tools\sftool'
$destination = Join-Path $toolRoot $pin.executable.fileName
$cacheRoot = Join-Path $vendorRoot 'cache'

# Only explicit SDK locations are considered; never select an executable from PATH.
if (-not $SftoolPath -and -not $ArchivePath) {
    $candidates = @(
        (Join-Path $cacheRoot $pin.executable.fileName)
        if ($env:SIFLI_TOOLS_PATH) {
            Join-Path $env:SIFLI_TOOLS_PATH "tools\sftool\$($pin.version)\sftool.exe"
        }
        if ($env:USERPROFILE) {
            Join-Path $env:USERPROFILE ".sifli-tools\tools\sftool\$($pin.version)\sftool.exe"
        }
        "E:\SDKs\.sifli-tools\tools\sftool\$($pin.version)\sftool.exe"
    )
    foreach ($candidate in $candidates) {
        if (Test-Path -LiteralPath $candidate -PathType Leaf) {
            $SftoolPath = $candidate
            break
        }
    }
}

if ($SftoolPath) {
    $source = [IO.Path]::GetFullPath($SftoolPath)
    Assert-PinnedFile -Path $source -Pin $pin.executable
    New-Item -ItemType Directory -Path $toolRoot -Force | Out-Null
    if (-not $source.Equals($destination, [StringComparison]::OrdinalIgnoreCase)) {
        Copy-Item -LiteralPath $source -Destination $destination -Force
    }
} else {
    if (-not $ArchivePath) {
        New-Item -ItemType Directory -Path $cacheRoot -Force | Out-Null
        $ArchivePath = Join-Path $cacheRoot $pin.archive.fileName
        if (-not (Test-Path -LiteralPath $ArchivePath -PathType Leaf)) {
            $downloadPath = Join-Path $cacheRoot "$([Guid]::NewGuid().ToString('N')).download"
            try {
                Invoke-WebRequest -Uri $pin.archive.url -OutFile $downloadPath `
                    -UseBasicParsing
                Assert-PinnedFile -Path $downloadPath -Pin $pin.archive
                Move-Item -LiteralPath $downloadPath -Destination $ArchivePath -Force
            } finally {
                if (Test-Path -LiteralPath $downloadPath -PathType Leaf) {
                    Remove-Item -LiteralPath $downloadPath -Force
                }
            }
        }
    }
    $ArchivePath = [IO.Path]::GetFullPath($ArchivePath)
    Assert-PinnedFile -Path $ArchivePath -Pin $pin.archive
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $archive = [IO.Compression.ZipFile]::OpenRead($ArchivePath)
    try {
        $entries = @($archive.Entries | Where-Object {
            $_.FullName -ceq $pin.executable.archiveEntry
        })
        if ($entries.Count -ne 1) {
            throw "Pinned sftool archive must contain exactly one $($pin.executable.archiveEntry)."
        }
        New-Item -ItemType Directory -Path $toolRoot -Force | Out-Null
        [IO.Compression.ZipFileExtensions]::ExtractToFile($entries[0], $destination, $true)
    } finally {
        $archive.Dispose()
    }
}

# Authenticate the copied/extracted binary before the only read-only invocation.
Assert-PinnedFile -Path $destination -Pin $pin.executable
$versionOutput = (& $destination --version | Out-String).Trim()
if ($LASTEXITCODE -ne 0 -or $versionOutput -cne $pin.executable.versionOutput) {
    throw "Expected '$($pin.executable.versionOutput)', got '$versionOutput'."
}
Copy-Item -LiteralPath $licensePath `
    -Destination (Join-Path $toolRoot $pin.license.fileName) -Force
Copy-Item -LiteralPath $provenancePath `
    -Destination (Join-Path $toolRoot 'PROVENANCE.json') -Force
Assert-PinnedFile -Path (Join-Path $toolRoot $pin.license.fileName) -Pin $pin.license
Write-Host "Bundled $versionOutput ($($pin.target)): $destination"
Write-Host "sftool SHA256: $($pin.executable.sha256)"
