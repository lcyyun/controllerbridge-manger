# ControllerBridge Manager

Windows desktop manager for ControllerBridge receivers.

- Separate setup wizard for board selection, firmware, flashing and settings.
- Receiver connection, input source selection, rumble and diagnostics.
- Separate PlayStation and Nintendo button-mapping editors.
- Live input from USB reports, never from serial/status snapshots.
- Independent Windows Gamepad and native DualSense input testing.
- Light/dark interface and simplified controller illustrations.

The maintained receiver targets are BL616, ESP32-S3, SF32LB52 and Pico 2 W.
Available controls depend on the connected firmware. BL616 is identified
independently in Xbox 360, DualSense, DualSense Edge and Nintendo NS2Pro modes;
its native ROM download port is supported by the Manager firmware page.

BL616 and SF32LB52-DevKit-Nano firmware are included in complete application
packages. BL616 flashing uses the chip's native ROM download port and the bundled
BLFlashCommand; SF32 flashing uses the bundled sftool. Pico uses BOOTSEL/UF2 and
ESP32-S3 still uses its platform tools. Flashing requires explicit confirmation.

Firmware updates have their own page, separate from the setup wizard. The first
update source is GitHub Releases; downloaded module packages are validated before
installation. The page only offers Manager-integrated BL616 and SF32 targets.
Only packages matching the currently connected receiver are listed. Installation
also verifies the manifest ID and rejects disconnects/device changes.
The setup wizard opens above its Manager owner by default; its welcome page has
a persistent option to disable automatic startup display. It remains available
from the navigation menu.

Native NS2Pro initialization and direct-PC rumble are not implemented by the
independent input tester. Receiver-based input and rumble use their existing
firmware paths.

## Build

Use Windows x64 and the .NET SDK pinned in `global.json`:

```powershell
dotnet build src\BridgeManager.Modern.App\BridgeManager.Modern.App.csproj -p:Platform=x64 -c Release -r win-x64
dotnet run --project tests\BridgeManager.Core.Tests\BridgeManager.Core.Tests.csproj -p:Platform=x64 -c Release
pwsh -File tools\package-modern-windows.ps1
```

The complete application is written to `dist/BridgeManager-modern-win-x64`
and the adjacent ZIP. See [BUILDING.md](BUILDING.md) for dependency locks,
offline packaging and release preparation.

`src/BridgeManager.App` retains the classic diagnostics source. Previously
distributed classic binaries remain separate from newly built application
packages unless explicitly supplied when packaging.

Hardware connection, flashing, motion direction and feedback quality still need
physical acceptance testing. Offline tests do not establish those results.
