# Building And Releases

## Source And Runtime

The root is standalone: there is no parent firmware tree dependency.
`SOURCE-SNAPSHOT.json` records original imported working-file hashes, before
standalone layout changes. It does not describe later commits.

The SDK is pinned in `global.json`; NuGet graphs are checked in as
`packages.lock.json`. Use `dotnet restore --locked-mode` for CI. The application
is WinUI 3, unpackaged, self-contained Windows x64. MSIX signing is not configured.
`Directory.Build.props` keeps the `win-x64` restore graph present in both normal
builds and publish builds; packaging also enforces locked restore mode.

## Firmware

`firmware-releases.lock.json` names exactly three reviewed firmware assets, with
repository, source commit, release tag, size and SHA-256.
SF32 and Pico assets contain firmware. ESP32 supplies management metadata only.
No BL616 binary is substituted.

```powershell
# Downloads missing pinned assets with an already authenticated GitHub CLI.
pwsh -File tools\get-firmware-packages.ps1
pwsh -File tools\package-modern-windows.ps1

# No network, using previously downloaded release assets.
pwsh -File tools\package-modern-windows.ps1 -Offline

# This directory must contain the exact locked .cbmodule bytes.
pwsh -File tools\package-modern-windows.ps1 -ModulePackageDirectory C:\releases\modules -Offline
```

Private repositories require an account with access (`gh auth login`). The
builder delegates authentication to GitHub CLI; no token belongs in source,
release assets, the lock file or the application. The initial Actions workflow
builds and tests source only, without cross-repository private downloads.

The application release feed is `lcyyun/controllerbridge-manger`. Firmware
repositories own their canonical builds; manager releases can carry
byte-identical copies of reviewed firmware assets. App-side anonymous updates
cannot read private releases. Until user authentication is implemented in the
app, download those releases through an authenticated external client.

Firmware builds and new module packages are produced in the respective
firmware repositories using `module/package.ps1`. Do not rename an old image
to match a new release. Update the lock after validating the new build.

For an unvalidated local SF32 build, keep the baseline lock and published
package intact. The archive's manifest must match the checked-in manager
manifest. Use a separate output name:

```powershell
pwsh -File tools\package-modern-windows.ps1 -Offline `
  -PackageName BridgeManager-mapping-preview-win-x64 `
  -LocalSf32Module C:\builds\sf32-unified-0.6.0-dev.cbmodule
pwsh -File tools\verify-modern-package.ps1 `
  -PackagePath dist\BridgeManager-mapping-preview-win-x64 -AllowLocalPreview
```

`LOCAL-PREVIEW.json` records the local override's size and digest, explicitly
marks hardware testing as incomplete, and does not claim a published source
commit. Verification rejects local previews without `-AllowLocalPreview`.
The unchanged ESP32 and Pico archives must still match the release lock.

## Windows Installer

Build the verified portable package first, then use Inno Setup 6.5.4 or newer:

```powershell
pwsh -File tools\package-installer.ps1 `
  -PackagePath dist\BridgeManager-input-mapping-win-x64 `
  -IsccPath 'C:\Tools\Inno Setup 6\ISCC.exe' `
  -Version 0.2.0 -AllowLocalPreview
```

The resulting `dist\ControllerBridge-Setup-0.2.0-win-x64.exe` is a single
download containing the app, runtime dependencies, firmware and flash tool.
Installation is per-user and creates a Start menu shortcut; a desktop shortcut
is optional. App settings under `%LOCALAPPDATA%\ControllerBridge` are not removed
on uninstall. Releases are unsigned unless a separate signing step is used.
Use `-AllowLocalPreview` only for prereleases with unvalidated firmware.

Run `tools\test-installer.ps1 -InstallerPath <setup.exe>` in a clean Windows
account to verify installation, all payload hashes, offline UI smoke tests
and uninstall. It refuses to overwrite an existing installed manager.

## Classic Diagnostics

Classic output is optional; the default package does not require old local
`dist` folders. To include a specific preserved classic build:

```powershell
pwsh -File tools\package-modern-windows.ps1 -ClassicDirectory C:\approved-classic-build
```

The complete package hash manifest covers every included classic file. Retain
the original binary's provenance separately; a new source build is not the same
frozen binary.

## Verification

```powershell
pwsh -File tools\test-firmware-packages.ps1
pwsh -File tools\verify-modern-package.ps1 -PackagePath dist\BridgeManager-modern-win-x64
```

`BridgeManager.Modern.App.exe --shell-smoke <absolute-output-directory>` and
`--mapping-smoke <absolute-output-directory>` are isolated in-app UI tests.
They use labeled offline fixtures and must not flash or open a real device.
Adding `--keep-open` to `--shell-smoke` keeps a successful offline test window
open for mouse-wheel and window-resize checks; close it after inspection.
The separate `--launch-smoke` mode performs discovery and is not used by CI.
