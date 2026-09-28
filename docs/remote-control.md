# Android remote control

The Android app controls the Windows editor and shows its live Program output. The desktop sends a downscaled 640×360 JPEG preview at up to five frames per second over the authenticated, certificate-pinned private-LAN connection. Full-resolution composition and audio routing stay on the desktop.

## Connect

1. Build and install the Android debug APK with the commands in [build.md](build.md).
2. Connect the phone and PC to the same private Wi‑Fi network. On Windows, select that network's private IPv4 address in **Connect your phone**.
3. Click **Start preview** on the desktop when you want a live Program feed; select OBS, CAMERA, CORNER, or BLACK as needed.
4. Click **Start LAN remote**. The editor displays a pairing QR and a manual fallback with the address, one-use six-digit code, and TLS certificate SHA-256 fingerprint.
5. Scan the QR with the phone camera and open **BetterDemo Remote**. The address, port, code, and TLS pin fill in automatically; review the connection and tap **PAIR DESKTOP**.
6. Confirm the phone shows **CONNECTED**, the Program panel shows the live desktop composition, and the selected scene matches the desktop. The code expires and cannot pair another session after use.

Both devices must be on the same private LAN. The service binds only to the selected private IPv4 interface and uses HTTPS with the exact desktop certificate pinned by the phone. It does not expose a cloud or public Internet endpoint. The app does not request cleartext network traffic.

## Controls

- **Scenes** select OBS Virtual Camera, physical camera, screen with camera corner, or black. Desktop output eases between scene changes with a short crossfade.
- **Sources** lists the video inputs in the selected scene. Tap a row to select it; tap its eye control to hide or show that layer in the desktop composition. The Program schematic updates the camera-corner inset with the layer visibility state.
- **Program** shows the actual desktop composition with a live/stale indicator. It gestures pinch to zoom and use a two-finger drag to pan. Updates are coalesced and the final transform is sent when the gesture ends.
- **Camera Corner** adjusts size, margin, and rotation.
- **Filters** applies blur across the full output frame; the radius slider controls its strength.
- **Controls** resets the transform or requests a fresh desktop snapshot. The Program switch shows or hides the desktop `OutputWindow`.
- **Connection** can refresh state, inspect the certificate pin, or revoke the paired token with **UNPAIR**.

If a command loses its response, the phone retains and offers to retry the same sequence, idempotency key, and payload. Retry the pending command before sending another one so both devices preserve command order. If the phone reports a stale sequence, stop and restart LAN remote on the desktop, then pair again.

## Limits

- QR pairing uses the phone camera to open the app's pairing link. Manual entry remains available when scanning is unavailable; there is no automatic host discovery.
- The phone preview is a bandwidth-limited 640×360 JPEG feed at up to five frames per second; Discord should use the desktop `OutputWindow` for the full-rate output. Audio remains on the desktop.
- The OBS-inspired Android UI and TLS protocol have build, unit/integration, and on-device LAN coverage. A phone command changed the actual WinUI OutputWindow to the physical-camera scene. Connection-loss retry and two-finger gesture acceptance still need an on-device run.
