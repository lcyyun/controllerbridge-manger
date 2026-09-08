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
The separate `--launch-smoke` mode performs discovery and is not used by CI.
