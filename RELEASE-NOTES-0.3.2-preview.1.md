# ControllerBridge Manager 0.3.2-preview.1

- Add “通过 USB 更新 BL616” to the independent firmware page. After the first
  OTA-capable firmware installation, no ROM mode, download serial port or
  RP2350 is required for USB OTA.
- Check the connected receiver's real hardware/build, signed target, RAW
  header, embedded image identity, SHA-256 and P-256 release signature.
  Reject mismatched, damaged, unsigned or non-newer OTA images.
- Coordinate packet retries and radio quiescence without changing saved pairing
  or automatic-connect settings. Protect activation from ambiguous/lost ACKs.
- Reconnect after activation and report success only after the expected build
  and the firmware's startup-health confirmation are both observed.
- Bundle BL616 module `0.2.0-preview.1`, build `1790776918`, plus the existing
  SF32 programming support. Firmware listings remain scoped to the connected
  receiver. The 0.3.1 BLFlash stdout/stderr fix is retained.

Windows x64 portable ZIP and unsigned installer are provided. Manager Core
tests and WinUI build/package verification passed. Physical transfers used the
same Core updater, including controlled failed-trial rollback and successful
confirmed reboot retention. A physical GUI-button test and exhaustive sudden
power-loss qualification are still pending. Only BL616 has this new USB OTA
flow; SF32 keeps its existing flashing flow.
