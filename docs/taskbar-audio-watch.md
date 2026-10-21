# Taskbar flyout audio routing watch

This is a **read-only** diagnostic for the unresolved difference between Windows 10's
**taskbar speaker flyout** and the **broader Volume Mixer** when a DeskFuse phone is
the selected Windows playback device. The handover is
[`handover-pc-media-sync.md`](handover-pc-media-sync.md).

The handover already proved that a real Windows System Sounds playback reaches the
phone and the broader mixer plays its sound; during the taskbar-only interaction,
the phone endpoint's production loopback captured no above-threshold packets.
The Android 20 ms buffer experiment distorted audio and must **not** be repeated.

## Run (interactive Windows user, no elevation)

From the repository root, after building the latest agent:

```powershell
dotnet run --project windows/agent/FocusWindow.Agent/FocusWindow.Agent.csproj -- speakers taskbar-watch --seconds 30
```

Or run the corresponding **newly built** `FocusWindow.Agent.exe` with
`speakers taskbar-watch --seconds 30`. An older deployed binary will not have
this command. Running the command does **not** restart the current host, change
volume/default devices or invoke a Windows ding.

Keep music and videos paused. During the watch, first move and **release** the
volume knob in the small taskbar speaker popup, then independently open the broad
Volume Mixer and perform its slider test. Note the approximate UTC timestamps of
each action. The diagnostic reads all *currently active* Windows render endpoints:
it lists default roles, logs volume/mute changes and loopback samples above
80/32768, and samples each endpoint's audio sessions including System Sounds.
No PCM is saved to disk. There is no synthetic sound, no network transmission,
and no privileged broker call.

The output can distinguish:

- **Phone loopback signal coincides with taskbar action:** follow that packet
  through host transport and phone playback; this contradicts the earlier
  taskbar-only watch and should be reproduced.
- **Another endpoint shows the taskbar signal, phone does not:** investigate
  per-app/system-session audio routing and endpoint selection on Windows.
- **No endpoint above threshold, yet broad mixer is captured:** investigate
  taskbar flyout sound generation, activation and shell event tracing; Windows
  might not have emitted a sound at all. A zero count cannot prove absolute
  digital silence, because the signal detector has a threshold.

The session meter may miss a transient shorter than its one-second polling
period. Loopback callback counters are independent of that polling. The watch
cannot establish a causal relationship without the user's action timestamps,
cannot discover endpoints added *after* startup, and is not an end-to-end
lip-sync/phone-latency measurement.

## Safety boundaries

Do not play probe tones through the enrolled phone. Do not change system
sound schemes, restart Windows audio services, install/reinstall an audio
driver, or synthesize a ding from DeskFuse volume events. The volume is
mirrored PC↔phone and adding sound in that handler would generate loops or
extra notifications. Do not confuse this taskbar issue with Auto/Buffered
PC-screen video synchronization: that needs a supported player presentation
integration, not bigger Android audio buffers.
