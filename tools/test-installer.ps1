[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$InstallerPath
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$testRoot = Join-Path $root ('test-results/installer-' + [Guid]::NewGuid().ToString('N'))
$installDir = Join-Path $testRoot 'app'
$registryKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\{7C73BD4C-BFC6-482E-94E2-757650B823A2}_is1'
if (Test-Path -LiteralPath $registryKey) {
    throw 'An installed manager exists; run installer tests in a clean Windows account.'
}
New-Item -ItemType Directory -Path $testRoot | Out-Null
$installer = (Resolve-Path -LiteralPath $InstallerPath).Path
function Invoke-TestProcess {
    param([string]$File, [string[]]$Arguments)
    $process = Start-Process -FilePath $File -ArgumentList $Arguments -PassThru -WindowStyle Hidden
    $process.WaitForExit()
    if ($process.ExitCode -ne 0) { throw "$File failed: $($process.ExitCode)" }
}
try {
    Invoke-TestProcess $installer @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART',
        '/NOICONS', '/TASKS=', '/NOCLOSEAPPLICATIONS',
        "/DIR=`"$installDir`"", "/LOG=`"$(Join-Path $testRoot 'install.log')`"")
    & (Join-Path $PSScriptRoot 'verify-modern-package.ps1') `
        -PackagePath $installDir -AllowLocalPreview -Installed
    foreach ($mode in @('mapping', 'shell')) {
        $resultDir = Join-Path $testRoot $mode
        Invoke-TestProcess (Join-Path $installDir 'BridgeManager.Modern.App.exe') `
            @("--$mode-smoke", "`"$resultDir`"")
        $results = if ($mode -eq 'mapping') { @('result.txt') } else { @('shell-result.txt', 'wizard-result.txt') }
        foreach ($name in $results) {
            $text = Get-Content -LiteralPath (Join-Path $resultDir $name) -Raw
            if (-not $text.StartsWith('PASS')) { throw "$mode smoke failed: $text" }
            Write-Host $text
        }
    }
} finally {
    # Only remove this test's installation, never an existing user installation.
    $uninstaller = Join-Path $installDir 'unins000.exe'
    $prefix = [IO.Path]::GetFullPath((Join-Path $root 'test-results')) + [IO.Path]::DirectorySeparatorChar
    if (-not [IO.Path]::GetFullPath($installDir).StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Unsafe test uninstall path.'
    }
    if (Test-Path -LiteralPath $uninstaller) {
        Invoke-TestProcess $uninstaller @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART',
            "/LOG=`"$(Join-Path $testRoot 'uninstall.log')`"")
    }
}
if ((Test-Path -LiteralPath $registryKey) -or
    (Test-Path -LiteralPath (Join-Path $installDir 'BridgeManager.Modern.App.exe'))) {
    throw 'Test installation was not uninstalled cleanly.'
}
Write-Host "PASS installer, installed mapping/shell/wizard UI, uninstall. Logs: $testRoot"
