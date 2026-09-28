# OBS and Discord setup

This guide describes the intended Windows 11 video route: OBS Studio Virtual Camera → BetterDemo → the separate `BetterDemo OutputWindow` → Discord Go Live application-window share. The editor and Android app are controls; neither is the window to share. The Discord second-viewer acceptance check is still outstanding; record its result in [qa-matrix.md](qa-matrix.md).

## Prepare OBS and BetterDemo

1. Open OBS Studio, select the scene to show, and confirm it moves in OBS's Program view. In OBS's Controls dock, click **Start Virtual Camera**. In its settings, select **Program** if the stream should follow the active OBS output. OBS documents the [Virtual Camera controls](https://obsproject.com/kb/virtual-camera-guide) and [installation troubleshooting](https://obsproject.com/kb/virtual-camera-troubleshooting).
2. Connect the physical webcam if the CAMERA or CORNER scene will be used. BetterDemo enumerates it separately from OBS Virtual Camera.
3. Build and open BetterDemo using [build.md](build.md). Select the physical camera and a 720p or 1080p output resolution, then click **Start preview**. Confirm the separate `BetterDemo OutputWindow` is visible. The editor's diagnostics should not report an unavailable source.
4. Select OBS, CAMERA, CORNER, and BLACK in turn. Check the actual pixels in `BetterDemo OutputWindow`, not only the mode selection in the editor or phone. The output window stays open across scene changes. Keep it visible and unminimized; it may be moved to a user-created virtual monitor.

## Share and verify in Discord

1. In the Discord desktop app, join a voice channel or start a call and open **Screen Share / Go Live**.
2. Choose the individual application window named `BetterDemo OutputWindow`, then start sharing. Do not choose `BetterDemo Editor`, the entire display, or OBS Virtual Camera as the primary video path. Discord's [Go Live guide](https://support.discord.com/hc/en-us/articles/360040816151-Go-Live-and-Screen-Share) documents application-window selection.
3. Have a second participant open the stream. For at least 60 seconds, switch through OBS, CAMERA, BLACK, and CORNER while the participant confirms the visible result. Check that the editor, controls, and cursor are absent, and that the stream continues without replacing the shared window.
4. Record the selected resolution, rendered FPS, capture-drop counters, the output HWND before/after scene changes, viewer observations, and any audio delay in [qa-matrix.md](qa-matrix.md). A local preview alone does not prove the second-viewer result.

Discord's browser client and desktop client can behave differently for application-window audio. The video acceptance route is the desktop client. Audio is configured separately in [audio-routing.md](audio-routing.md); OBS Virtual Camera itself carries video only.

## If the window is absent or blank

- Verify **Start preview** is active and `BetterDemo OutputWindow` remains visible. If hidden, use **Show OutputWindow**. If closed, restart the preview and reselect the newly created window in Discord.
- Verify OBS shows **Stop Virtual Camera** in its Controls dock, which indicates the virtual camera was started. Then check BetterDemo's source diagnostics.
- For CAMERA or CORNER, select the intended physical camera in BetterDemo and check that no other application has exclusive use of it.
- In Discord, reselect the individual `BetterDemo OutputWindow` if its share source points to the editor or an older closed window. Discord describes its [Windows application capture methods](https://support.discord.com/hc/en-us/articles/9410427556375--Windows-Capturing-Application-Window-for-Screen-Share-and-Go-Live).
- Keep the window visible on a monitor. A hidden or minimized HWND is outside the supported sharing path.
