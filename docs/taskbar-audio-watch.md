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

## October 21 field observations (Windows 10, physical phone)

Owner-supplied 30-second standalone watch recordings:

- At 11:29:48–11:30:17 UTC, when changing from the DeskFuse phone output
  to MM-1 toward the end, the watcher recorded 25 signal packets from the phone,
  24 from MM-1, and none from the monitor output. This was a **mixed UI and
  output-selection run**; it does not isolate the taskbar sound generation.
- In the subsequent **taskbar-flyout-only** run at 11:32:20–11:32:50 UTC,
  the phone was still Console and Multimedia default, MM-1 Communications
  default. The user opened the small flyout, clicked its slider, waited, moved
  it left, and clicked again. Phone volume changed from 0.72 to 0.42 to 0.23.
  The phone System Sounds session meter briefly read 0.0926 and 0.0205,
  yet the phone, MM-1 and monitor loopback each reported **0 above-threshold
  packets**, with 485/485/484 total callbacks respectively. This reproduces
  the missing flyout ding without any synthetic stimulus.
- The owner confirmed that **the broader Volume Mixer still produces audible
  sound on the phone**. When opening it, they hear faint background hiss
  beginning about half a second later with their ear near the phone speaker.
  One hypothesis is Android's output/amp activating in response to mixer
  playback; hiss alone **cannot prove** a particular stream was opened.
- A historic Microsoft Q&A report describes essentially the same Windows 10
  distinction without DeskFuse: modern taskbar slider silent while classic
  Volume Mixer plays the default beep. It is an **anecdote, not a root-cause
  diagnosis or confirmation of a general Windows 10 issue**:
  https://learn.microsoft.com/en-us/answers/questions/3802693/default-beep-not-working-in-windows-10-volume-slid

Next discriminating comparison: **select MM-1 as the default Console/
Multimedia output before starting a separate watch, then operate only the
small taskbar flyout**. Determine whether that slider produces an audible ding
on physical speakers, with no output switching during the recording. If this
also fails, the symptom is not isolated to DeskFuse's virtual endpoint; if it
works, investigate capability/pin format and sound-session routing differences.
The earlier observed System Sounds peaks must not be equated with audible
rendered PCM. New diagnostic summaries provide raw nonzero-sample counts and
captured frame counts to distinguish silent captures from subthreshold samples.

## Follow-up: physical MM-1 taskbar feedback is working

The owner has now explicitly confirmed that **MM-1 operates normally with
the Windows taskbar flyout**. By contrast, the same flyout fails to produce
an audible ding on the DeskFuse phone endpoint, despite normal media and
classic Volume Mixer playback. This strongly localizes the difference to
endpoint-specific Windows handling, but does not yet prove which driver or
endpoint property is responsible.

Static review of DeskFuse's current WaveRT miniport:

- `speakerwavtable.h` **does advertise**
  `STATIC_AUDIO_SIGNALPROCESSINGMODE_NOTIFICATION` with a 48 kHz stereo
  default. Do **not** claim notification mode is absent.
- The speaker filter is a two-pin shared-mode render endpoint, without a
  hardware loopback or offload engine, in `minipairs.h`. Its other
  applications' audio and the classic mixer can reach software loopback.
- `DeskFuseAudio.inf` does **not** register
  `PKEY_AudioEndpoint_Supports_EventDriven_Mode` on the wave filter
  interface. Microsoft's SysVad sample does register that endpoint
  property, but its documentation primarily describes HDAudio endpoints;
  the omission is a **candidate difference only**, not demonstrated to
  suppress taskbar feedback. Do not apply or reinstall an INF based on
  this inference alone.
- The driver's speaker topology advertises `KSNODETYPE_SPEAKER`, a
  connected integrated jack, and `eConnTypeUnknown`; other endpoint
  metadata may differ from MM-1.

Next investigate the endpoint's actual property store/capabilities and
Windows audio event trace while operating each flyout, without generating
a duplicate/custom phone sound or modifying the installed driver. Only
then consider an isolated signed driver candidate with a rollback plan.
