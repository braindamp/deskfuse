# PC video / phone audio synchronization handover

Updated 2026-10-21 (Europe/Paris), including the subsequent Windows ding investigation. The owner asked to document this feature,
not implement it in this session, to limit token usage. No mode implementation
or browser integration has been added.

## Owner's requested behavior

The owner watches YouTube/video **on the PC screen**, while the enrolled
Android phone is the Windows playback device. The picture was ahead of sound.
This is not synchronization of the phone's streamed desktop.

Provide one saved selection, with these exact choices:

| Selection | Behavior |
| --- | --- |
| Auto | Detect supported video playback on the PC and use Buffered; otherwise use Game. |
| Game | Add no intentional presentation delay. Preserve the audio route's supported minimum buffering. |
| Buffered | Present PC video 100 ms later while its audio continues at normal speed through the phone. |

Default to Auto. Persist explicit Game/Buffered overrides across restarts so
the owner can set a mode permanently. All playback remains at its original
speed: a 100 ms presentation offset is not slower playback. Do not enable a
video offset when the phone audio route is inactive or a different output is
selected. Restore normal presentation on disconnect, pause/seek/source changes,
or integration failure. Clarify per-player versus global persistence only if
needed during implementation; a saved global selection is a reasonable start.

## The implementation boundary that matters

Delaying Windows capture, network delivery, or the Android video decoder does
not delay the picture visible on the PC's physical screen. Increasing the
audio buffer makes sound later and worsens this particular mismatch.

This needs browser/player presentation support. Investigate a player-native
sync control or a narrowly scoped browser integration for YouTube/HTML video.
The owner's browser/player is still unknown: when asked, they requested this
handover instead of continuing implementation. Do not assume a browser was
chosen or that an extension is installed/approved. Avoid global screen capture
overlays, changing monitor timing, browser security changes, and heuristic
"all browser activity is video" detection.

Auto must distinguish actual playing video from paused media, music, games,
and unsupported/DRM playback. Define and display support limits. Keep seeking,
fullscreen, picture-in-picture, live playback, captions, source changes and
multiple tabs in the acceptance plan. Unsupported players must not silently
claim synchronized playback. Start with the requested 100 ms; measurement or
an adjustable offset can refine it later without inventing an A/V measurement.

## Audio work completed before this handover

Working checkout: `D:\Coding\DeskFuse\v0.3`, remote
`https://github.com/braindamp/deskfuse.git`.
Local HEAD is `665a93f`; it is 26 commits ahead of the observed GitHub main
(`a87ee82`). Existing local changes were preserved. Nothing was committed or
pushed during this session.

- `windows/agent/FocusWindow.Core/Session/PhoneSpeakerRoute.cs`: pre-existing
  uncommitted elapsed-time pacing and cancellable per-stream bounded queues.
  These prevent accumulated timer drift and survive stream restart.
- `android/.../media/PcmPacketWriter.kt` and `PcAudioOutput.kt`: pre-existing
  uncommitted complete blocking packet writes on the IO worker, with bounded
  dropping transport flow. A final partial packet no longer depends on another
  packet arriving. Preserve `PcmPacketWriterTest.kt` and host restart tests.
- `PcAudioOutput.kt`: this session removed duplicate attenuation. Android's
  media stream controls volume and the AudioTrack gain stays at unity. Track
  attenuation is a fallback when system volume cannot be changed. Volume
  rounding now preserves slider round trips, and phone changes update the
  cached level used on restart. Diagnostics include effective buffer and
  underrun counts. Regression tests are in `PcAudioOutputTest.kt`.
- `windows/agent/FocusWindow.Agent/WindowsLoopbackAudioCapture.cs`: uses a
  shared-mode loopback subclass with a 20 ms capture buffer instead of NAudio's
  default 100 ms buffer / approximately 50 ms polling. Explicit endpoint
  ownership and channel mapping are preserved.

**Important device result:** forcing Android's effective AudioTrack buffer to
20 ms and its startup threshold to one frame caused severe sustained underruns
and audible distortion, reported by the owner. Both changes were reverted.
The installed final build uses Android's supported buffer (85 ms on this
phone), its normal startup threshold, and the existing low-latency request.
Do not put the failed 20 ms setting back under the name "Game mode."

## Validation and installed state

- Final Android build: **25 unit tests passed**, including packet completion
  and volume regression tests. APK assembled successfully.
- Host session/protocol subset: **7 passed, 5 existing optional skips**.
  Windows host published self-contained x64 with warnings as errors.
- Real endpoint probe used the production capture implementation: 2, 20,
  150 and 3000 ms quiet tones all reached capture. Observed onset was about
  2–12 ms in that run. This measures renderer-to-loopback onset, not total
  phone latency or A/V offset.
- Final sustained 15-second probe: 1500 signal packets, first captured signal
  after 19.2 ms, probe's longest callback gap 32 ms. Phone diagnostics stayed
  at **0 underruns / 0 partial writes**, with an 85 ms buffer and roughly
  67–81 ms queued during the steady part. Intentional silence can itself
  produce an underrun; compare counts during continuous playback.
- These checks do not prove the precise Windows volume-slider chime is
  audible or that A/V synchronization is implemented. No measured end-to-end
  A/V offset is available.

Running host: `out/acceptance/speakers/windows-final/FocusWindow.Agent.exe`,
unelevated, PID 58240 at deployment. Staged matching build:
`out/acceptance/speakers/windows-audio-latency/`. Existing broker/driver were
left running. Logs: `host-final.log`, `host-audio-latency.stdout.log` in the
same speakers directory. The phone speaker service and workspace were
restored after installation; one transient WGC startup failure cleared on Retry.

Installed APK: `out/acceptance/speakers/app-audio-latency.apk`, package
`com.focuswindow.client.debug`. Enrollment/data were preserved. Windows
default debug signing differs from the installed app. Use the existing
documented development signer at `C:\Users\codexlab\.android\debug.keystore`
for updates; do not uninstall or commit/copy signing material into the repo.
Installed signer SHA-256:
`e8a1b33378ef147244065f61b11e596810517f8c8deaa00ab864f07bb6a0210e`.

## Existing timing scaffold is not a working sync mechanism

`docs/av-sync-design.md` assumes the opposite lead direction in places and
discusses phone-side display. Its assumptions are unmeasured and do not
describe the owner's PC-screen scenario. `media/SyncMeasurement.kt` is not
wired to presentation, and `noteSound` does not use its host timestamp to pair
matching samples/frames. The one `audio_clock` message is emitted at stream
startup, not tied to the captured first sample's actual device timestamp.
Do not present this scaffold or queue depth as an A/V measurement.

## Acceptance for the future feature

1. Auto switches on supported PC video playback only when phone audio is
   active; interactive/game use receives no additional video delay.
2. Buffered delays actual PC video presentation by 100 ms without changing
   audio content, audio playback rate, or video playback rate.
3. Game and Buffered selections survive restart; returning to Auto restores
   automatic decisions. Show selected versus effective mode clearly.
4. Verify lip-sync on the PC screen, plus pause/seek/fullscreen/tab changes,
   phone reconnect, output-device changes, and supported-player failures.
5. Verify sustained audio under representative network/GPU load, with no
   growing queue, truncated final sounds, or repeated continuous underruns.
6. Preserve the speaker endpoint, enrollment, unsaved documents, physical
   display settings, and existing stream behavior. The owner permits host
   restarts/session interruptions, but not data loss.

## Latest checkpoint: taskbar ding unresolved; broader Volume Mixer works

After the audio fixes, the owner reported that clicking the Windows volume
slider still did not produce the usual ding. After an initial handover request,
they authorized continuing the system-sounds investigation. An initially
successful manual test was interpreted too broadly: the owner later clarified
that the sound played in the **broader desktop Volume Mixer**, while the
**taskbar speaker popup's slider still remains silent**. Do not claim the
taskbar symptom is fixed. No additional DeskFuse production changes were made.

The following are established on the current machine:

- The owner explicitly confirmed **"Yes, the test ding played"** after a real
  Windows `SystemDefault` sound was played through the current default output.
  Thus the sound itself can travel from Windows through DeskFuse to the phone.
- Current Windows Console and Multimedia default outputs both resolve to the
  phone endpoint. Communications points at MM-1; it was not changed.
- The Windows System Sounds session on the phone endpoint is present,
  unmuted, with session gain 1.0. Endpoint level was 0.65 during the checks.
  The mixer's displayed System Sounds slider is also 65 because it reflects
  the endpoint level; do not interpret that as conflicting session gain.
- The user's active default sound event is configured, not blank:
  `HKCU\AppEvents\Schemes\Apps\.Default\.Default\.Current` points to
  `C:\Windows\media\Windows Balloon.wav`; the scheme is `.Modified`.
  Read HKCU as the interactive user: the sandbox's registry view initially
  made this existing key appear absent.
- The production capture received **71 signal packets** when playing
  `SystemDefault` via `PlaySoundW` with `SND_ALIAS | SND_NODEFAULT`.
  A repeat with `SND_SYSTEM` also received 71 signal packets. The latter
  exercises Windows' System Sounds session rather than an ordinary test
  program session. SDK flag values were checked against local
  `playsoundapi.h`, not guessed.
- Host logs show those records being sent with **0 transport drops**. Phone
  logs show the packet count advancing; output remains the supported 85 ms
  buffer. An underrun after an intentional silent gap is expected; this is
  different from the repeated underruns in the rejected 20 ms experiment.
- Those explicit sound plays alone did not establish the actual slider's
  behavior. A subsequent manual test of the broader mixer confirmed its ding
  was audible. Capture went from
  **0 to 244 signal packets between 12:29:27 and 12:29:31 local time**,
  then remained at 244 through the end of the observation. No synthetic test
  sound was injected during that monitoring period. Endpoint volume stayed
  at 0.65 and unmuted in the one-second snapshots.

If the symptom recurs, reproduce the actual slider release while
watching the System Sounds session meter / production capture. Determine
whether Windows emits any sound, then follow that specific burst. Do not
generate an extra ding on every volume notification: the PC and phone mirror
volume both ways, so that would introduce unwanted/duplicate sounds and loops.
Do not reopen the failed Android buffer experiment to fix an unproven cause.

Temporary diagnostic source is in ignored
`out/acceptance/speakers/audio-probe/`. It reflects the production
`WindowsLoopbackAudioCapture` from `windows-audio-latency` and never changes
default outputs. To run the current ding probe as the interactive user:

```powershell
dotnet run --project out/acceptance/speakers/audio-probe/AudioProbe.csproj `
  -c Release --no-build -- `
  '{0.0.0.00000000}.{7e97b7dd-d4a7-4130-9bd6-9107e95dae2a}' --ding
```

It prints default roles, session gain/mute, and actual captured signal packets;
the current source includes `SND_SYSTEM`. The endpoint GUID is an observed
current value, not a permanent identity: resolve it again after re-enumeration.

For an eventual actual-slider UI check, the computer-use skill was initialized
through `@oai/sky` in `node_repl`. Read that skill and its required guidance
before using it in a new session. A temporary `SndVol.exe` mixer was opened,
inspected, then closed without changing volume. A second mixer was opened
for the manual test; the screenshot helper timed out twice, so no automated
slider action was attempted. The owner performed the successful interaction.
The mixer's accessibility tree showed
the correct phone endpoint title, System Sounds and master sliders at 65, and
an unexpected toolbar label **"No audio device is installed"**. That label
might be a legacy mixer artifact or relevant to the virtual endpoint; its
cause is unverified. Do not treat that label as a diagnosed driver failure.

All temporary logcat recording sessions were stopped. The host and phone
speaker session remain running; enrollment and output selection are intact.
The separate Auto/Game/Buffered feature remains a handover only.

## Final stop checkpoint: taskbar feedback still silent

The owner explicitly requested: **"stop now, write into handover."** Stop
investigation here. The taskbar slider problem is **not fixed**.

The owner's clarification is decisive: the sound played when the broader
Windows Volume Mixer was shown, but did not play from the normal taskbar
speaker popup. Earlier statements that the actual taskbar ding was confirmed
were incorrect; they conflated these two controls.

### Controlled taskbar-only reproduction

With other audio paused and no synthetic sound being injected, the owner
moved/released the taskbar popup slider and reported **"Done — still silent."**
The production loopback probe observed endpoint volume changing from 0.65 to
0.47, 0.54 and 0.72, while detecting **zero signal packets throughout**.
That run is recorded at:

`out/acceptance/speakers/taskbar-ding-watch.log`

The relevant changes occurred around 12:32:40–12:32:45 local time on
2026-10-21. The detector counted PCM packets containing samples with absolute
PCM16 magnitude over 80; "zero" means no signal above that threshold, not an
instrumented proof of absolute digital silence. The same detector registered
71 packets for the explicit Windows default ding and 244 during the broader
mixer test. No new phone playback records appeared in the taskbar-only log.

This points upstream of phone playback: the taskbar control changes the
correct endpoint level, but its feedback is not reaching that endpoint's
loopback. Do not claim the Android buffer causes this symptom, or that all
possible Windows routing causes have been excluded.

### Narrow Windows setting/cache trial, unsuccessful and reverted

1. Saved the existing default-beep value to
   `out/acceptance/speakers/taskbar-beep-setting-before.json`.
2. Temporarily changed only
   `HKCU\AppEvents\Schemes\Apps\.Default\.Default\.Current` from
   `C:\Windows\media\Windows Balloon.wav` to the installed scheme's default,
   `C:\WINDOWS\media\Windows Background.wav`.
3. Stopped only `ShellExperienceHost.exe` (PID 17132) to refresh the popup's
   cached state. Windows subsequently relaunched it as PID 56256. Explorer,
   the DeskFuse host, audio service, broker, and driver were not restarted.
4. A subsequent probe recorded one 36-packet burst around 12:34:47, then no
   further sound. The owner said: **"i heard one ping played buit it was not
   a windows sound, then when clicking the taskbar, nothing happens."** The
   burst must not be treated as a successful taskbar-feedback test.
5. Restored the exact saved original beep value:
   `C:\Windows\media\Windows Balloon.wav`. Verified the popup process was
   running; no manual relaunch was needed. Whether "nothing happens" meant
   no sound or no popup was not clarified before the stop request.

The post-trial recording is
`out/acceptance/speakers/taskbar-ding-after-default.log`. Both WAV files have
read/execute access for ALL APPLICATION PACKAGES and ALL RESTRICTED APP
PACKAGES. File permissions were inspected, not changed. The sound-scheme
trial did not leave a persistent change. No DeskFuse production code or audio
buffer was changed during this taskbar-specific follow-up.

The temporary phone log recorder (adb PID 29456) was stopped. The two
45-second taskbar capture probes exited on their own. No diagnostic monitor
was intentionally left running. Host and phone playback remain installed as
documented above; the last observed endpoint volume was 0.72, chosen through
the owner's slider interaction, and was left unchanged.

### Next investigation, only when the owner asks to resume

Investigate the Windows popup's actual sound generation/routing rather than
adding a ding to DeskFuse's volume-change handler. Check all render endpoints
for a misrouted feedback stream, shell/media activation errors, and any
per-application routing state. A source review already confirmed the driver's
`SpeakerHostPinSupportedDeviceModes` advertises NOTIFICATION as well as RAW,
DEFAULT, MEDIA, MOVIE and COMMUNICATIONS: "notification mode absent" is not
an established explanation. No driver change or reinstall was attempted.

Community reports on Microsoft Q&A describe a similar distinction between
the taskbar flyout and the broader mixer, but they are anecdotes, not a
diagnosis of this machine. The suggested media-folder/read-permission cause
does not fit the file paths and ACLs inspected here. Avoid speculative driver,
registry, security, or global sound-scheme changes without a discriminating
test.
