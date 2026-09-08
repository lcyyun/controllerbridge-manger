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
