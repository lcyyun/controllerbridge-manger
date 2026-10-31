[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$OutputDirectory,
    [string]$ArchivePath
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$vendor = Join-Path $PSScriptRoot 'vendor/esptool'
$pinPath = Join-Path $vendor 'PROVENANCE.json'
$pin = Get-Content -LiteralPath $pinPath -Raw -Encoding UTF8 | ConvertFrom-Json
function Assert-Pin([string]$Path, $Expected) {
    $item = Get-Item -LiteralPath $Path
    if ($item.PSIsContainer -or ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -or
        $item.Length -ne $Expected.size -or
        (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash -ne $Expected.sha256) {
        throw "Pinned esptool size/SHA256 mismatch: $Path"
    }
}
$license = Join-Path $vendor $pin.license.fileName
Assert-Pin $license $pin.license
if (-not $ArchivePath) {
    $cache = Join-Path $vendor 'cache'
    New-Item -ItemType Directory -Force -Path $cache | Out-Null
    $ArchivePath = Join-Path $cache $pin.archive.fileName
    if (-not (Test-Path -LiteralPath $ArchivePath)) {
        $download = Join-Path $cache "$([Guid]::NewGuid().ToString('N')).download"
        try {
            Invoke-WebRequest -Uri $pin.archive.url -UseBasicParsing -OutFile $download
            Assert-Pin $download $pin.archive
            Move-Item -LiteralPath $download -Destination $ArchivePath
        } finally {
            if (Test-Path -LiteralPath $download) { Remove-Item -LiteralPath $download -Force }
        }
    }
}
Assert-Pin $ArchivePath $pin.archive
$target = Join-Path ([IO.Path]::GetFullPath($OutputDirectory)) 'tools/esptool'
New-Item -ItemType Directory -Force -Path $target | Out-Null
Add-Type -AssemblyName System.IO.Compression.FileSystem
$archive = [IO.Compression.ZipFile]::OpenRead([IO.Path]::GetFullPath($ArchivePath))
try {
    $entries = @($archive.Entries | Where-Object { $_.FullName -ceq $pin.executable.archiveEntry })
    if ($entries.Count -ne 1) { throw 'Official archive must contain exactly one pinned esptool.exe.' }
    $destination = Join-Path $target $pin.executable.fileName
    [IO.Compression.ZipFileExtensions]::ExtractToFile($entries[0], $destination, $true)
} finally { $archive.Dispose() }
Assert-Pin $destination $pin.executable
$version = (& $destination version | Out-String).Trim()
if ($LASTEXITCODE -ne 0 -or $version -notmatch '(?m)^4\.8\.1\s*$') { throw 'Pinned esptool version check failed.' }
Copy-Item -LiteralPath $license -Destination (Join-Path $target 'LICENSE.txt') -Force
Copy-Item -LiteralPath $pinPath -Destination (Join-Path $target 'PROVENANCE.json') -Force
Write-Host "Bundled official esptool 4.8.1: $destination"
Write-Host "SHA-256: $($pin.executable.sha256)"
