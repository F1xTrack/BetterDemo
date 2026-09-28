# BetterDemo

BetterDemo is a Windows 11 x64 prototype for composing an OBS Virtual Camera feed with an independently selectable physical camera and presenting it in a separate `OutputWindow` for Discord Go Live.

## Implemented

- Unpackaged WinUI 3 editor with independent OBS and physical-camera selection.
- Media Foundation camera enumeration and capture with bounded latest-frame handoff and stale-frame placeholders. The first supported physical camera is selected by default and remains manually selectable.
- D3D11 device, BGRA surface upload, flip-model swap chain, and stable top-level OutputWindow.
- Exact 720p/1080p client sizing, a locked 16:9 user resize, and saved monitor position with offscreen recovery.
- Black, OBS, physical-camera, and OBS-with-camera-corner modes; crop/zoom/pan, mirror/flip, blur, and unavailable-source placeholders in the CPU compositor.
- A document-based live compositor with imported still-image sources. The editor can hide/show, reorder, position, size, rotate, undo/redo, and remove image layers; scene JSON stores the active mode, remote transforms, blur, layer state, and source names.
- Optional manual LAN pairing over TLS with one-time code, displayed certificate fingerprint, ordered commands, acknowledgements, and token revoke.
- Android Kotlin/Compose Material 3 controller with an OBS Studio-inspired Program/Scenes/Sources layout, a scene-specific source list, source selection and visibility toggles, desktop-state sync, camera/scene controls, zoom gestures, and encrypted token storage.
- Windows process-loopback audio capture for the system mix while excluding the Discord process tree. The editor lets you choose an active output device, set the route level, mute it, and play a test tone. If Discord restarts or its process tree changes, forwarding stops until the route is restarted.
- Scene document serialization/history, QR-assisted phone pairing with a manual fallback, and protocol test coverage. Debug and Release each pass 67 Core tests and 72 Integration tests.

## Not complete

The editor still needs canvas drag/resize handles, text rasterization, video-file playback, tray behavior, and a full properties editor. Saved scene JSON references image files in BetterDemo's local asset store; copying just the JSON to another computer leaves missing-asset placeholders. D3D11 presents CPU-composited BGRA frames; GPU shader composition remains. DXGI device-removed/reset failures recreate the device and surface and retry the current frame once, with simulated recovery coverage. Android pairing supports QR prefill and a manual fallback. The phone now receives an authenticated, certificate-pinned 640×360 JPEG preview of the desktop composition at up to five frames per second; Discord should use the full-rate desktop OutputWindow. The Android Sources dock lists the selected scene's inputs and can hide or show built-in camera/OBS layers. The OBS-inspired phone dashboard paired over the private LAN with the actual WinUI editor: selecting CAMERA changed the editor scene picker and live OutputWindow to HD WebCam 2MP, and unpair revoked the device token. A selected-process WASAPI capture start/stop passed locally, but no VB-Audio cable is installed, so live cable audio, second-viewer Discord acceptance, camera unplug/replug, and virtual-monitor acceptance remain open. The latest Sources and scene-file controls pass automated round-trip/compositor tests; a fresh WinUI click-through for save/load and Undo/Redo remains pending.

## Build and run

Use [docs/build.md](docs/build.md) for the pinned .NET/Visual Studio build command, Release tests, unpackaged Windows launch, and Android Gradle check. Follow [docs/obs-discord-setup.md](docs/obs-discord-setup.md) for the video route and [docs/audio-routing.md](docs/audio-routing.md) for the separate cable path. See [docs/architecture.md](docs/architecture.md) for module boundaries, [docs/remote-control.md](docs/remote-control.md) for phone pairing, [docs/troubleshooting.md](docs/troubleshooting.md) for common failures, and [docs/qa-matrix.md](docs/qa-matrix.md) for verified and outstanding checks.

To try the desktop prototype, start the app, choose a scene mode and 720p or 1080p output resolution, then click **Start preview**. The editor shows rendered FPS, render time, latest-frame skips, and capture drops. The separate `OutputWindow` is the window to select in Discord Go Live. LAN remote stays disabled until **Start LAN remote** is clicked; connect using the address, one-time code, and TLS SHA-256 fingerprint shown in the editor. See [docs/remote-control.md](docs/remote-control.md) for Android pairing and control details.
Move the OutputWindow to the desired monitor while preview runs; its position is saved when preview stops and restored on the next start if that monitor is still connected.
The **Sources** panel adds and edits image layers. Use **Save scene** and **Load scene** in the same panel to persist the mode, source order and properties, camera-corner settings, zoom/pan, and blur.
