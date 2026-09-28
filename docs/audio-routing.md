# Process audio through a virtual cable

BetterDemo's audio path is separate from OBS Virtual Camera and the video OutputWindow. It captures the selected application's process tree with Windows application loopback, excludes Discord, and writes to an active VB-Audio virtual cable render endpoint. No virtual cable was installed during the current live checks, so delivery to Discord has not yet been verified.

## Configure

1. Install and configure a user-owned virtual audio cable from its vendor. BetterDemo does not ship a driver. In VB-CABLE, playback goes to **CABLE Input** and another application records from **CABLE Output**; see the vendor's [reference manual](https://vb-audio.com/Cable/VBCABLE_ReferenceManual.pdf).
2. In BetterDemo, click **Refresh audio devices**. Select the application process whose audio should be sent and select the active virtual cable render endpoint. The app rejects physical speakers/headphones and Discord as the source process tree.
3. Click **Start audio route**. Use **Test tone** to produce the bounded 440 Hz check signal, then play a known sound from the selected application. Check that the cable's recording-side meter moves.
4. In Discord, select the cable's recording endpoint, such as **CABLE Output**, as the microphone/input device. Keep the ordinary microphone separate unless an external mixer combines it intentionally. Confirm the second participant hears the selected application and that Discord voice/notifications do not loop back.
5. Try BetterDemo's **Mute application audio** toggle and verify it silences only this route. Stop the route before removing or changing the cable device.

## Acceptance evidence still needed

Record the cable endpoint names, selected source PID, 440 Hz tone result, second participant result, underrun count during 30 FPS video, Discord voice exclusion, physical speaker/headphone monitoring status, and measured A/V offset. Repeat after unplug/replug or endpoint disable/re-enable. Automated fake-route tests and a live process-loopback start/stop do not establish this end-to-end result.
