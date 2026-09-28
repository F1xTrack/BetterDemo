# Troubleshooting

## Video

| Symptom | Check |
|---|---|
| OBS source unavailable | Start OBS Studio's Virtual Camera in the Controls dock. Verify OBS has a live Program scene and retry BetterDemo preview. |
| Physical camera unavailable | Confirm the intended camera is connected and selected in the BetterDemo editor. Stop applications that may hold it, then restart the preview. |
| Discord sees the editor | In Go Live, share the individual `BetterDemo OutputWindow`; the editor is a different top-level window. |
| Discord sees black or a frozen frame | Keep the OutputWindow visible and unminimized, inspect BetterDemo's FPS and capture-drop counters, and verify the selected source has fresh frames. Reselect the window if it was closed and recreated. |
| 1080p window extends past the monitor | The framed window may be taller than a 1080p work area. Its client area is still 1920×1080. Move it to the intended monitor or choose 720p. |

## Audio and phone

| Symptom | Check |
|---|---|
| No cable listed | Install and activate a supported user-owned VB-Audio virtual endpoint, then click **Refresh audio devices**. The app intentionally rejects physical outputs. |
| No audio in Discord | Check the BetterDemo source process and route status, test the tone, and choose the cable's recording side as Discord input. OBS Virtual Camera has no audio payload. |
| Phone shows STALE | Use **RETRY LAST COMMAND** to resend the same ordered command. If the desktop session was restarted, pair again and request state. |
| Phone cannot pair | Check same-LAN private IPv4 address, port, one-time code expiry, and the exact TLS SHA-256 fingerprint displayed in the desktop editor. |

See [obs-discord-setup.md](obs-discord-setup.md), [audio-routing.md](audio-routing.md), and [remote-control.md](remote-control.md) for the full routes. The outstanding live acceptance checks are tracked in [qa-matrix.md](qa-matrix.md).
