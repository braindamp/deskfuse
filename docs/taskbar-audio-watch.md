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

From the repository root, use the independent read-only runner:

```powershell
dotnet run --project windows/audio/DeskFuse.TaskbarWatch/DeskFuse.TaskbarWatch.csproj -c Release -- 30
```

This lightweight project references the existing DeskFuse audio library but does
**not** build the GPU host, require CMake, install a driver, or touch the running
host process. Run it *after* selecting the phone output in the Windows speaker picker,
so the phone endpoint exists when enumeration begins. It accepts an optional
duration in seconds (5–120, default 30).

The full host also offers `FocusWindow.Agent.exe speakers taskbar-watch --seconds
30`, but building that host requires native CMake/Visual Studio. An older deployed
binary will not have the command. Running either watch does **not** restart the
host, change volume/default devices, or invoke a Windows ding.

Keep music and videos paused. During the watch, first move and **release** the
volume knob in the small taskbar speaker popup, then independently open the broad
Volume Mixer and perform its slider test. Note the approximate UTC timestamps of
each action. The diagnostic reads all *currently active* Windows render endpoints:
it lists default roles, logs volume/mute changes and loopback samples above
80/32768, and samples each endpoint's audio sessions including System Sounds.
No PCM is saved to disk. There is no synthetic sound, no network transmission,
and no privileged broker call.

The final summary now prints `signal / nonzero / total loopback packets`,
captured frame count, and maximum raw PCM peak. If a taskbar-only run shows
`0 signal / 0 nonzero` **with a positive frame count**, every captured float
sample was exactly zero; Windows' nonzero System Sounds *session meter* did
not become audible endpoint-loopback PCM. If `nonzero` is positive but
`signal` is zero, captured samples existed but never crossed the
80/32768 threshold. These observations do not identify which Windows audio
component suppressed the sound.

To compare the taskbar flyout independently, collect separate **taskbar-only**
runs with (1) DeskFuse phone selected as default output and (2) physical MM-1
selected as default output. Keep the watch active after changing the default
output, and record whether the ding was audible on MM-1. Do not confuse a
burst caused by **switching** endpoints with a burst caused by the subsequent
slider click. The watcher is enumerating all endpoints at launch; no output
selection is changed by the watcher.

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
