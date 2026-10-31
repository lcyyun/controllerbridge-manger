# Four-platform preview acceptance

Host tests and successful firmware builds do not establish hardware compatibility.
No receiver is flashed by the automated tests. In particular, an attached RP2350
UART helper used for BL616 debugging is not a Pico 2 W receiver target.

## Physical input and USB output

| Platform | Physical controller connections | USB output identities |
| --- | --- | --- |
| BL616 | DualSense/Edge Bluetooth Classic; NS2Pro BLE | DualSense, Edge, XInput, NS2Pro |
| Pico 2 W | DualSense/Edge Bluetooth Classic; NS2Pro BLE | DualSense, Edge, XInput, NS2Pro |
| SF32 Nano n16r16 | DualSense/Edge Bluetooth Classic; NS2Pro BLE | DualSense, Edge, XInput, NS2Pro |
| ESP32-S3 N4/N8/N8R2/N8R8/N16R8/ESP32SUPERMINI | NS2Pro BLE | DualSense, Edge, XInput, NS2Pro |

USB emulation does not add a physical Bluetooth transport. XInput is the wired
Xbox 360 protocol; it does not implement Xbox console authentication. Feedback
translation between controller families must be judged on hardware, including
the existing audio/haptic path and any unsupported controller-specific effects.

## Hardware checks for each newly installed image

1. Select the actual board and receiver download interface. Verify the module
   version and image hashes in Manager, flash it, then reconnect its native USB
   data interface. Confirm Manager identifies the same receiver family.
2. Pair each supported physical controller, save settings, and power-cycle the
   receiver. Confirm the saved controller reconnects. Test disconnect, connect,
   forget, pairing cancellation, failed connection attempts, and switching the
   preferred input. Two available controllers must still yield one active input.
3. For NS2Pro, use its connection button to turn it off. Wait until Manager clears
   input and telemetry. Press an ordinary controller button to wake it and verify
   automatic reconnection without pairing again. Repeat after receiver restart
   and after a failed connection attempt; check that scanning resumes.
4. Switch through all four USB identities. Verify OS re-enumeration, Manager
   reconnection, buttons, both sticks, triggers, motion, and supported extended
   buttons. The input drawing must clear on disconnect or expiry. In XInput,
   verify that the physical-input snapshot also animates the drawing.
5. Keep the controller still: fresh source frames must still produce a rate.
   Disconnect or stop reports: the rate must become unavailable, without a burst
   on reconnection. Compare against captured physical reports when possible.
   Unknown source battery must remain unknown, including NS2Pro behind a
   DualSense USB identity.
6. Remap ordinary and extended controls, save, reboot, and read back. Confirm
   the correct input/output mapping table is changed. SF32 has six route tables;
   its Edge USB output shares the DualSense output table. Other platforms use
   the configuration dimensions exposed by their firmware module.
7. Exercise left/right rumble, stop feedback, and supported audio/haptic output.
   Check that disconnect and source changes stop old feedback. Assess motion
   direction and feedback quality; host tests cannot validate either.
8. Check module updates from the public feed, install the matching module, and
   verify recovery using the documented BOOTSEL/ROM/serial path for that board.
   Reject packages for another board and confirm an interrupted update leaves a
   usable recovery path. Do not test interruption on a device without that path.

Record the exact module SHA-256, board, physical controller revision, host OS,
USB output role, and outcome. A result for one board/memory configuration does
not validate a different board. The preview's release notes list which offline
checks ran and any remaining implementation or hardware limitations.
