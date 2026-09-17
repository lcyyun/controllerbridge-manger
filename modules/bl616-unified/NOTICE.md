# Notices and Attribution

controllerbridge-BL616 contains the BL616 firmware exported from the
controller bridge working tree. The following source and protocol attributions
are retained from that tree.

## Main Code Base

The original controller bridge repository was created from `DS5Dongle`:

- Project: DS5Dongle
- Upstream: https://github.com/awalol/DS5Dongle
- Local reference revision: `8760ee3 fix: ci artifact path`
- License: MIT License
- License copy: `LICENSES/DS5Dongle-MIT.txt`

The BL616 firmware follows its DualSense report and HIDP protocol behavior.
This standalone repository does not contain the Pico SDK/TinyUSB/BTstack
runtime or the Pico UF2 build.

## NS2Pro / Switch 2 Pro Protocol Reference

The NS2Pro / Switch 2 Pro controller protocol work in this repository uses
`y700-switch2-pro-bridge` as an important reference:

- Project: y700-switch2-pro-bridge
- Upstream: https://github.com/LeonChrome/y700-switch2-pro-bridge
- Local reference revision: `3697227 Make README bilingual`
- License: Apache License 2.0
- License copy: `LICENSES/y700-switch2-pro-bridge-Apache-2.0.txt`

The bridge uses protocol knowledge and design references from that project,
especially:

- BLE service/characteristic UUIDs and controller discovery heuristics.
- NS2Pro initialization command sequence.
- FD2 input report layout, including stick packing and motion offset notes.
- Nintendo-style USB HID report identity and report ID usage.
- HID OUT to BLE rumble forwarding strategy.
- Report-rate/status concepts used by the local WebHID tuner.

Source files with protocol-derived implementation notes include:

- `protocol/bl616_ns2_protocol.c`
- `src/ble_gatt_bl616.c`
- `src/ns2_profile.c`

The isolated BL616 target also follows DS5Dongle's documented DualSense HID
report and Bluetooth HIDP behavior while using Bouffalo SDK/CherryUSB APIs rather
than copying the Pico SDK, TinyUSB, or BTstack runtime architecture.  Relevant
files are under `src/ds5_classic_bl616.c`,
`protocol/bl616_bridge_protocol.c`, and
`usb/bl616_usb_device.c`.

## Additional Design References

### Current BL616 Audio-Haptics and Role-Mapping References

The current BL616 audio-haptics conversion and cross-role mapping were
independently implemented after comparing several actively maintained public
projects at pinned revisions:

- Switch2Connect, revision `688f8149ff5441efad713997def484c3cc5e90cc`
  (2026-08-23): DualSense 4-channel UAC capture, channel 3/4 spectral analysis,
  independent ordinary/audio rumble state, and NS2Pro HD-rumble scheduling.
  License file at this revision: GNU GPL v3.
  https://github.com/TommyWabg/Switch2Connect/tree/688f8149ff5441efad713997def484c3cc5e90cc
- S2P-XInput-Lite, revision `1fd759bdcabdfb265bf00c84b3b83e6f205e9694`
  (2026-08-20): audio-haptics activity gating and saturating soft mixing with
  ordinary rumble. License file at this revision: GNU GPL v3.
  https://github.com/duoduo-88/S2P-XInput-Lite/tree/1fd759bdcabdfb265bf00c84b3b83e6f205e9694
- VIIPER, revision `88f66f1ed0c3716c78f810d92b1924112093f896`:
  current DualSense and NS2Pro report packing and cross-role axis conventions.
  License file at this revision: GNU GPL v3.
  https://github.com/Alia5/VIIPER/tree/88f66f1ed0c3716c78f810d92b1924112093f896

No source file from these projects is vendored or copied into this repository.
The SF32 implementation is a fixed-point, allocation-free implementation for
the board's RT-Thread/CherryUSB data path. The older Y700 project remains a
historical NS2 protocol reference for the Pico-era implementation; it is not
the implementation basis for the SF32 audio-haptics converter.

The BL616 bridge architecture and compatibility checklist were also
compared against these public projects supplied as design references:

- https://github.com/AizawaHikaru233/DS5_NS2Pro_Dongle
- https://github.com/lcyyun/ns2pro-bridge
- https://github.com/lcyyun/pico-controller-bridge

They informed interoperability checks such as single-active controller
selection, parsed/repacked USB reports, role management, rumble translation,
and motion preservation. No source file from those three repositories is
vendored into the isolated BL616 target; their own licenses and notices
remain authoritative for their code.

## SDK Components

The build uses Bouffalo Lab `bouffalo_sdk`, not SiFli SDK and not
`sqlCRT/bouffalo_sdk master`. The SDK is maintained separately from this source.

- CherryUSB, FreeRTOS, EasyFlash, the BL616 BSP, Bluetooth host/controller and
  related SDK components retain their upstream notices and license terms.
- The fixed-point Opus sources are vendored under `third_party/opus` and built
  through `third_party/opus.cmake`; their authoritative license is
  `third_party/opus/COPYING`. The restricted-CELT configuration and transport
  behavior were compared against `sqlCRT/ds5dongle-bl618-opensource` and
  `Ganmoko/pro2dongle-bl616-opensource`; those projects are references, not SDK
  dependencies. Preserve all notices embedded in the vendored codec sources.
- The RT-Thread compatibility layer maps the application API to FreeRTOS; it
  does not link a SiFli runtime.

Firmware release packaging must retain the notices for linked SDK components,
the Opus COPYING file and these source attributions. The SDK's flashing utility
is bundled separately by ControllerBridge Manager with its license/provenance.

## Trademarks

This project is not affiliated with, endorsed by, or sponsored by Nintendo,
Sony, Valve, Raspberry Pi, or any other hardware/software vendor. Nintendo
Switch, Switch Pro Controller, DualSense, Steam, Raspberry Pi, and related
names are trademarks of their respective owners.
