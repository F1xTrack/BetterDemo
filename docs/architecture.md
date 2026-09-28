# BetterDemo Architecture

## Current implementation

The Windows application is an unpackaged WinUI 3 process. It enumerates camera devices through Media Foundation, starts independent OBS Virtual Camera and physical-camera capture sources, and transfers the latest owned frame into a D3D11 presentation surface. The CPU reference compositor converts NV12 to BGRA, applies source selection, camera-corner layout, transforms, blur, and unavailable-source placeholders. The ordinary top-level `OutputWindow` has a stable ID and HWND; it is separate from the WinUI editor.

The live renderer builds the selected built-in scene as a `SceneDocument`, appends the editor's custom layers, resolves active camera/image frames, and sends the CPU-composited BGRA pixels to D3D11. Scene-mode changes use a short eased crossfade. Imported still images are copied into a bounded local asset store. The Sources panel edits visibility, relative order, position, size, and rotation with undo/redo; versioned JSON scene files persist those layers and the active remote-control settings. General composition, blur, and transitions use the CPU reference path, with an optimized untransformed opaque BGRA path. GPU shader composition, canvas drag handles, text rasterization, video-file playback, DPI/monitor-transition testing, and tray behavior remain outstanding. DXGI device removed/reset/hung errors recreate the D3D11 device and surface and retry the current frame once; this recovery is covered with an injected integration failure, not a physical GPU removal.

`BetterDemo.Remote` contains authenticated, ordered command processing and a TLS-only HTTP server. The server binds to one selected RFC1918 IPv4 address, issues a one-time pairing code, supports command acknowledgements and state snapshots, and permits a paired client to revoke its own token. The server creates a self-signed certificate and the desktop UI displays its SHA-256 fingerprint for manual pinning. The phone app paired with the actual WinUI host over the private LAN and changed the live OutputWindow from OBS to the physical camera. The editor's scene picker follows the remote scene state.

The audio project routes selected-process WASAPI loopback samples to any active render endpoint, with per-route volume, mute, and tone test controls. Disconnected endpoints are excluded from the picker. The Android app has a Kotlin/Compose Material 3 remote dashboard with an OBS Studio-inspired panel layout, HTTPS certificate pinning, secure token storage, and protocol controls. It is control-only; it does not receive video or audio.

## Dependency direction

```text
BetterDemo.App     -> BetterDemo.Core, BetterDemo.Audio, BetterDemo.Capture, BetterDemo.Interop, BetterDemo.Remote
BetterDemo.Capture -> BetterDemo.Core, BetterDemo.Interop
BetterDemo.Audio   -> BetterDemo.Core, BetterDemo.Interop
BetterDemo.Remote  -> BetterDemo.Core
BetterDemo.Interop -> BetterDemo.Core
BetterDemo.Core   -> no product project
```

Production projects do not reference tests. `BetterDemo.Core` remains platform-neutral and has no dependency on Interop.

## Core contracts

`src/BetterDemo.Core/Contracts` defines:

- Four scene modes and their case-sensitive wire names: `black`, `screen`, `physicalCamera`, and `screenPlusPhysicalCameraCorner`.
- Stable camera IDs, NV12/BGRA32 frame metadata, owned frame payloads, QPC timestamps, and a frame-freshness check.
- Source lifecycle, audio routing, window lifecycle, diagnostics, and versioned remote-command contracts.

`src/BetterDemo.Core/Scene` contains the scene document model, versioned JSON serialization, undo/redo commands, built-in scene factory, and CPU video compositor. `BetterDemo.App` uses those documents in its live D3D11 presentation path and wraps scene data with the camera/zoom/pan/blur settings used by the phone remote.

## Native boundaries

`BetterDemo.Interop` owns native calls, D3D11/Win32 adapters, process-loopback WASAPI activation, and virtual-cable output. `BetterDemo.Capture` implements Media Foundation camera enumeration and capture. `BetterDemo.Audio` coordinates audio capture and cable writing. `BetterDemo.App` coordinates source lifecycles and presentation.

Native handles are opaque and invalid by default. The OutputWindow adapter creates a normal top-level HWND, dispatches its lifecycle operations on the host UI thread, and resolves it by stable `OutputWindowId`.
The adapter sizes the client area to the selected video resolution after accounting for non-client chrome; its max-track handling permits a full 1920×1080 client area even when the framed window exceeds the current display's work area.
Interactive edge and corner resizing keeps the client area at the selected 16:9 aspect ratio so Discord receives an undistorted composition.
The editor saves the OutputWindow's normal top-left position under the user's LocalAppData on preview stop and restores it before showing the next preview. A stored position is ignored if fewer than 64×64 pixels would remain in a connected monitor's work area.

## Remote protocol and network limits

Pairing uses a six-digit, one-time code with a short expiry. Each paired device receives a random token. Every command carries a protocol version, sequence number, idempotency key, and payload; the response includes an acknowledgement and the latest scene snapshot. The command set includes mode, camera corner, zoom, pan, reset, blur, layer visibility, output visibility, state, and ping.

The LAN server accepts only RFC1918 IPv4 or loopback bind addresses, uses TLS 1.2 or later, limits request sizes, and rejects unauthenticated commands. Clients must pin the displayed certificate fingerprint. The service does not bind to a wildcard or a public address. Pairing codes and tokens must not be written to diagnostics.

## Forbidden fallbacks

The product video path uses OBS Virtual Camera as a Media Foundation device. It must not add Windows Graphics Capture, DXGI Desktop Duplication, `BitBlt`, `PrintWindow`, or another desktop/window capture fallback.

Audio does not silently capture the default system mix. The implemented path uses Windows application loopback for the chosen process tree and rejects a tree containing Discord. Render-endpoint capture fallback remains unsupported. Routed output can target any active Windows render endpoint, including a VB-Audio cable or physical speakers/headphones; endpoint loss requires a refresh and route restart.

## Verification

`ContractInventoryTests` prints the scene wire names and production project edges, checks for dependency cycles, and scans source for forbidden native capture/audio tokens. Core tests cover scene serialization/history, frame freshness, composition, layer rotation, pairing, command sequencing, and idempotency. Integration tests cover image import/decode/composition, end-to-end image-bearing scene-file round-trip, input limits, Media Foundation capture where hardware exists, Win32 HWND lifecycle, D3D11 upload/presentation, and the TLS pairing/command/revoke flow. Debug and Release currently pass 65 Core and 67 Integration tests each. Android unit tests cover private IPv4 and certificate-fingerprint validation.

Passing automated tests do not replace physical-camera unplug, Discord Go Live, virtual-cable audio, connection-loss testing, or multiple-monitor manual acceptance. A phone-to-WinUI LAN scene change was verified on the connected handset.
