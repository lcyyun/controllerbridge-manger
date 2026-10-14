# ControllerBridge Manager 0.3.3-preview.1

Development prerelease, not a stable hardware/USB-ID certification.

- Original teal gamepad/bridge icon: executable, taskbar/window, setup wizard,
  sidebar, installer and shortcuts. Transparent multi-resolution Windows ICO.
- Recognizes the BL616 native XInput + independent management-HID preview
  without environment variables. Exact serial/usage checks remain mandatory.
- The BL616 package includes signed native USB OTA and verified ROM-download
  files. Firmware selection still follows the connected receiver family.
- Retains SF32 flash support and other previously bundled firmware modules.
- Fixes Manager stick-Y visualization and preserves source-specific decoding.

## Important preview limitations

BL616 Xbox mode uses **FFFE:CB16**, a temporary, unallocated experimental USB
identity. It is not an official VID allocation and may conflict with other
devices. No borrowed open-source project's ID or custom Windows driver is used.
The standalone management HID does not duplicate gamepad input: native XInput
provides game input while HID provides configuration/OTA.

Firmware signatures/hardware markers, SHA-256 and anti-downgrade checks are
retained. Interrupted-transfer/failed-trial recovery has offline regression
coverage; arbitrary physical power-cut testing and other Windows configurations
are not certified. The Windows installer is not Authenticode-signed.

BL616 firmware repository visibility is unchanged. Its `.cbmodule` is also
attached to this public Manager prerelease, so the existing GitHub module update
feed can download it without accessing a private repository.
