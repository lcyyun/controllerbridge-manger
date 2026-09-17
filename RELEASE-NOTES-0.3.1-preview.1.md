# ControllerBridge Manager 0.3.1-preview.1

Fix for the BL616 flashing failure in 0.3.0-preview.1:

- Redirect standard output and error before starting BLFlash; shared launcher
  enforces both stream settings for every flash tool.
- On a launcher/stream/cancellation exception, terminate and wait for the child
  process before returning to configuration cleanup. Do not delete a configuration
  while the child is still using it.
- Add an end-to-end child-process regression through FlashAsync using a fake
  tool. It checks configuration lifetime, captured output, stderr and exit code
  without opening a serial port or writing a device.

Firmware images, chip/address/hash checks, native ROM serial interface and
2,000,000 baud flashing are unchanged. RP2350 is not required.
Windows x64 portable ZIP and unsigned installer are provided. This is a preview;
the launcher regression is not a new physical-device flashing verification.
