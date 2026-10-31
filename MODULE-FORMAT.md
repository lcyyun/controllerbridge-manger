# Controller Bridge Single-File Firmware Module

`*.cbmodule` is the only file a user needs to add a board or firmware that uses
the current manager Runtime API and an existing flash adapter. It is a
ZIP-compatible archive installed by the manager, but its extension remains
`.cbmodule` so Windows can associate it with Controller Bridge later.

## Runtime boundary

The manager owns only generic behavior:

- real Windows USB HID and serial enumeration;
- module loading, version selection, validation, and rollback;
- GitHub Release discovery and package download;
- common wizard, flashing adapters, settings UI, and diagnostics.

The single module file owns firmware-specific behavior:

- exact HID discovery parameters, VID/PID, serial-number, profile-key, and
  platform matching rules;
- board and firmware names;
- capability flags and USB roles;
- wireless input actions, status commands, settings templates, and rumble;
- firmware artifacts and flash method;
- license, attribution, and full-file SHA-256 manifest.

Static board declarations are catalog choices only. A board is shown as
automatically detected only when a real Windows HID descriptor matches the
module and the match resolves to exactly one board. A generic COM port never
identifies a board.

## Archive layout

```text
new-firmware.cbmodule
|-- module.json
|-- MODULE-SHA256.txt
|-- LICENSE
|-- NOTICE.md                 optional
|-- LICENSES/                 optional third-party notices
`-- artifacts/
    `-- firmware files
```

`module.json` uses schema version 1. A minimal package is:

```json
{
  "schemaVersion": 1,
  "runtimeApiVersion": 1,
  "id": "vendor-board-firmware",
  "moduleVersion": "1.0.0",
  "displayName": "Vendor Board Firmware",
  "boardFamily": "Vendor MCU",
  "description": "Controller bridge firmware for Vendor Board.",
  "priority": 100,
  "capabilities": "DeviceStatus, LiveInput, RawCommands, FirmwareFlashing",
  "matchAny": [
    {
      "usbIdentities": [
        { "vendorId": 4660, "productId": 22136 }
      ],
      "serialNumbers": ["REAL-DEVICE-SERIAL"]
    }
  ],
  "hidDevices": [
    {
      "key": "vendor-manager",
      "displayName": "Vendor HID Manager",
      "vendorId": 4660,
      "productId": 22136,
      "managerUsagePage": 65280,
      "managerUsageId": 1,
      "managerFeatureReportId": 127,
      "usbRole": "Unknown",
      "managerSerialNumber": "REAL-DEVICE-SERIAL",
      "tags": ["vendor"],
      "supportsInputReports": true
    }
  ],
  "boards": [
    {
      "id": "vendor-board",
      "displayName": "Vendor Board",
      "family": "Vendor MCU",
      "description": "Exact supported hardware revision."
    }
  ],
  "firmware": [
    {
      "id": "vendor-firmware",
      "displayName": "Vendor Firmware",
      "version": "1.0.0",
      "description": "Initial release.",
      "boardIds": ["vendor-board"],
      "flashMethod": "None",
      "artifactRelativePath": "artifacts/firmware.bin",
      "flashHint": "Use the vendor bootloader."
    }
  ],
  "statusCommands": ["status"],
  "selfTestCommands": ["status"],
  "primaryStatusCommand": "status",
  "inputStatusCommand": "status"
}
```

Do not add guessed VID/PID or serial values, and do not use a generic serial
port as a board match. Every automatic match must come from a descriptor
observed on real hardware. If an identity is shared by several firmwares, use a
verified serial number or product identity; otherwise the manager must leave
the board unknown.

Runtime API 1 can add HID identities, command templates, boards, settings and
firmware that use `PicoUf2`, `SifliSerial`, or manual `None` flashing. A new
transport, executable protocol, status parser, flash adapter, or UI control
still requires a manager Runtime API update.

## Runtime API 2 dynamic pages

Runtime API 2 keeps every API 1 field and adds `operations` plus `pages`.
Operations isolate device commands from the UI, while pages are rendered from
safe built-in controls rather than module-supplied executable code.

Supported controls are `Toggle`, `Number`, `Slider`, `Select`, `Text`, `Color`,
`ActionButton`, `Status`, `Table`, `MappingEditor`, `Group`, and `Tabs`. The
first practical domain control is `MappingEditor`, which supports direct USB
input capture, reset, apply, and save actions.

```json
{
  "runtimeApiVersion": 2,
  "operations": {
    "mapping.read": {
      "transport": "ManagerCommand",
      "request": "mapping get"
    },
    "mapping.set": {
      "transport": "ManagerCommand",
      "request": "mapping set {target} {source}"
    },
    "mapping.reset": {
      "transport": "ManagerCommand",
      "request": "mapping reset"
    },
    "mapping.save": {
      "transport": "ManagerCommand",
      "request": "mapping save"
    }
  },
  "pages": [
    {
      "id": "button-mapping",
      "label": "Button mapping",
      "icon": "GameController",
      "sections": [
        {
          "id": "default",
          "label": "Default profile",
          "controls": [
            {
              "id": "buttons",
              "type": "MappingEditor",
              "label": "Button sources",
              "binding": "/entries",
              "sourceCatalog": "controller.inputControls",
              "targetCatalog": "bridge.outputControls",
              "readAction": "mapping.read",
              "applyAction": "mapping.set",
              "resetAction": "mapping.reset",
              "saveAction": "mapping.save"
            }
          ]
        }
      ]
    }
  ]
}
```

Bindings and response selectors use absolute JSON Pointer syntax. Operation
parameters are substituted only into declared request templates; control
characters and unresolved placeholders are rejected by the manager. API 1
packages continue to load with empty dynamic page and operation collections.

## Build and publish

Prepare one source directory with the layout above, then run:

```powershell
tools\pack-module-directory.ps1 `
  -SourceDirectory path\to\module
```

To scaffold a Runtime API 2 module with a dynamic status page:

```powershell
tools\new-module.ps1 `
  -Id vendor-board-firmware `
  -DisplayName "Vendor Board Firmware" `
  -BoardFamily "Vendor MCU" `
  -LicensePath path\to\LICENSE
```

The packer validates Runtime API 2 manifests against
`schemas\module-v2.schema.json` before creating the archive. Editors can use
the same schema for completion and immediate field validation.

Upload the resulting `.cbmodule` as an asset on a release in
`lcyyun/ns2pro-bridge`. The setup wizard reads the GitHub Releases API and lists
real `.cbmodule` assets from published releases. Installing the asset adds
the new compatibility module without replacing or rebuilding the manager.

The current format provides archive integrity through complete SHA-256
coverage. A future public auto-update channel should additionally require a
publisher signature before unattended installation.

## ESP32-S3 native USB management and flashing

ESP32-S3 modules use `managerWireProtocol: "Nsm1"` in each HID profile. The
profile selects the existing vendor collection (usage page `0xff00`,
usage `1`, feature report ID `0x7e` / decimal `126`, 63-byte payload), with
`supportsInputReports: false` and no command output report. Discovery requires
the role-specific ControllerBridge product string; the serial is a stable
per-chip identifier. The four USB output identities are independent of the
single supported physical input, NS2Pro over BLE.

NSM1 keeps its 22-byte little-endian header. Opcode `0x0010` transports an
un-terminated UTF-8 Manager command (up to 512 bytes) and a UTF-8 JSON response
(up to 4096 bytes), in contiguous 41-byte fragments. The client matches the
transaction and opcode, rejects malformed/misordered fragments, and retries
status `8` (Busy) without reissuing mutations. Legacy text Manager profiles
continue using their existing protocol.

NSM1 shares the existing HID interface, vendor collection and interrupt IN
endpoint. Legacy feature report `0x7f` remains available; no new USB interface,
endpoint or transmit FIFO is required. The existing interrupt IN endpoint
satisfies [HID 1.11 section 4.4](https://www.usb.org/sites/default/files/documents/hid1_11.pdf).
`supportsInputReports: false` means the Manager does not subscribe to that
stream; commands and telemetry use feature report `0x7e` exclusively.

Physical input telemetry is polled through feature commands and drawn with the existing
vector controller. Only `unique_input_reports` and `input_rate_time_ms` from
fresh physical source frames determine effective report rate. `source_epoch`,
source changes, counter/time wrap, invalid input and stale input reset the
measurement. USB output retransmits and Manager polling are never counted.
Battery comes from `input_battery_percent` (or explicit null for unknown), not
the emulated gamepad. The NS2Pro mapping page uses the firmware's single
25-control source table across all output roles.

`flashMethod: "EspressifSerial"` points to a board-specific manifest:

```json
{
  "schemaVersion": 1,
  "chip": "esp32s3",
  "board": "n16r8",
  "flashSizeMb": 16,
  "psramSizeMb": 8,
  "psramMode": "octal",
  "flashMode": "dio",
  "flashFrequencyMhz": 80,
  "baudRate": 460800,
  "files": [
    { "kind": "bootloader", "path": "bootloader.bin", "address": "0x0", "size": 0, "sha256": "actual SHA-256" },
    { "kind": "partition", "path": "partition-table.bin", "address": "0x8000", "size": 0, "sha256": "actual SHA-256" },
    { "kind": "application", "path": "application.bin", "address": "0x10000", "size": 0, "sha256": "actual SHA-256" }
  ]
}
```

The illustrative sizes and hashes above must be replaced with actual values.
Each firmware entry names exactly one `esp32s3-<board>` board and includes only
that board's built images. Recognized memory profiles are `n4` (4/0), `n8`
(8/0), `n8r2` (8/2 Quad), `n8r8` (8/8 Octal), `n16r8` (16/8 Octal) and
`esp32supermini` (4/2 Quad), in MB Flash/PSRAM. Image headers must target
ESP32-S3, DIO, 80 MHz and the selected flash capacity. The application must fit
the sole factory application partition. Paths, lengths, hashes and all write
ranges are checked before writing.

The app runs only its pinned official esptool 4.8.1 executable. It probes the
chip and exact physical flash capacity, writes the three images, and runs
`verify_flash` before requesting the final reset. It never runs a module
command/script, `--force`, `erase_flash`, or an executable from PATH. NVS is
outside these writes. External PSRAM cannot be identified reliably through
this ROM probe; selecting the actual board memory profile remains required.
ROM recovery uses BOOT + RESET and the ESP32-S3 download serial interface;
normal game/controller management uses the native USB data interface.

Distributors must run `tools/bundle-esptool.ps1 -OutputDirectory <app-root>`
before final package hashing and archiving. This copies the pinned executable,
license and provenance to `tools/esptool`; no Python installation is required.
The existing package extraction step must copy the complete ESP `.cbmodule`,
including `firmware/<board>/`, into the app's module directory. The flasher
cannot run if its binary hash differs from the compiled pin.

Offline validation: Core tests cover NSM1 transactions/fragments, physical
telemetry, unsafe/corrupted/wrong-board bundles and a fake executable that
verifies chip-probe/write/readback ordering without opening serial ports.
`BridgeManager.Modern.App.exe --shell-smoke <output>` exercises the shared
shell and NSM1 client-to-UI behavior with in-memory transports, including
physical input snapshots, vector controls, stale reset, role transitions,
ESP reset completion, feedback configuration and firmware update pages.
Hardware enumeration, host drivers, actual flashing/recovery and controllers
still require hardware validation.
