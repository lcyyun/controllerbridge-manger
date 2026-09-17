# ControllerBridge Manager 0.3.0-preview.1

- Separate firmware settings/update page; GitHub Releases is the initial source.
- Only firmware matching the connected BL616/SF32 receiver is listed. Recheck
  package manifest ID and connection before installation; verify download digest.
- BL616 native ROM USB serial flashing at 2,000,000 baud with bundled pinned
  BLFlashCommand. No RP2350 dependency. SF32 flashing retains bundled sftool.
- Setup wizard opens above its Manager owner by default. Welcome checkbox
  remembers "do not show next startup"; the menu can reopen it.
- Responsive pages/wizard, BL616 identity detection, and released mapping,
  input-buffer lifecycle and installer update functionality are preserved.
- Complete Windows x64 portable ZIP and installer include BL616, SF32 Nano,
  Pico and ESP32 metadata/modules. BL616 compatibility package is mirrored here
  because its source repository remains private.

Validated: core automated tests, offline multi-size shell/wizard tests, launch
test, firmware/module/tool hashes. Existing hardware observations are retained;
not every input/output identity combination has completed hardware regression.
Installer is not code-signed. USB ROM flashing is not an in-application A/B OTA
update and does not promise power-loss rollback or signed-firmware authenticity.
