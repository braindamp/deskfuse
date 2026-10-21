# Speakers-only DeskFuse: independent audio accessory connection

**Status:** Product architecture requirement; Android speakers-only end-to-end
acceptance is **not yet demonstrated**. This is the intended product path,
not a display-mode workaround.

**Non-negotiable:** An authorized Android phone must be able to act as its
own Windows playback speaker **without** launching or selecting a Windows
application, allocating a phone virtual monitor, initializing FocusIdd,
negotiating display DPI, or starting Windows Graphics Capture / NVENC.
Audio must work if there is no GPU capture source or if video streaming
is unavailable. If the user also chooses display streaming, it may operate
concurrently but is not a prerequisite for audio.

## Observed regression and architectural boundary

On October 21, 2026 the Windows agent authenticated Xiaomi 24117RN76E,
accepted its prior authorization, then rejected its display-role retry:

```text
authenticated (Xiaomi 24117RN76E, client 0.3.0-dev)
'Xiaomi 24117RN76E' has previously approved full-user file scope; connecting
[source] No supported application source was selected.
phone connection closed: Unable to read beyond the end of the stream.
```

The audio broker had been stopped and the old stale speaker was removed.
The user had started the agent with `--enable-pc-audio` but without
`--workspace-app`. The display-only
`windows/agent/FocusWindow.Agent/Media/NativeGpuPipeline.cs`
requires an absolute existing `.exe`; a failed display session
therefore prevented subsequent phone-audio verification. This is **not**
evidence that the audio driver is defective.

Existing server code **already supports** the correct host-side boundary:
- `windows/agent/FocusWindow.Core/Session/SessionHost.cs`
  sends authenticated `ClientHello.Role = "media"` or `"accessory"`
  to `DevicePeerSession`, rather than `PhoneSession`.
- `windows/agent/FocusWindow.Core/Session/DevicePeerSession.cs`
  checks accessory capability requests, authorizes the enrolled device,
  provisions `DeviceCapabilities.Speakers` through `PhoneSpeakerRoute`,
  and maintains a keepalive **without creating a virtual monitor**.
- Display-role `PhoneSession` uses `NativeGpuPipeline` and
  `--workspace-app`, and must remain an **optional separate path**.
- Whether the current Android UI can actually initiate and retain the
  accessory-only role has **not been demonstrated**. Do not claim this
  is a finished feature based on server code alone.

## Required user experience

1. The Android app offers or automatically activates a clear
   **Connect as PC speaker** mode, with no display source or workstation
   app selection. `Try Again` retries the **same mode**; it must
   not silently switch to display role. It must report authorization,
   connectivity, speaker provisioning and stream state separately.
2. The Windows agent runs in a normal user session and may omit
   `--workspace-app` entirely; speakers-only must still connect via
   the accessory-role path. The audio broker retains its privilege
   boundary and manages only registered per-phone endpoints.
3. On authenticated connect + requested speakers capability, Windows
   exposes **exactly one real WaveRT speaker endpoint for that phone**.
   Ordinary Windows apps and System Sounds render genuine audio through it
   to the phone over the authenticated encrypted transport.
4. Before a phone is connected, the UI must not imply a broken service
   with a misleading persistent **"Phone speaker: unavailable"** state.
   Distinguish `Not connected`, `Connecting`, `Ready`,
   `Streaming`, and actionable `Error` states. Experimental audio
   test buttons need not appear in the final product UI.
5. On disconnect or lost keepalive, remove only that phone's PnP
   speaker, never leave a silent but selectable Windows output.
   Reconnect should restore it without re-pairing, starting NVENC,
   replacing another phone's endpoint, or touching the user's documents.
   See [phone-speaker lifecycle](phone-speaker-lifecycle.md).
6. A user may connect speakers-only, switch to / add display mode,
   and stop display mode without losing the speaker stream unnecessarily.
   Define and test ownership/handoff so that closing one role cannot
   tear down another active role's audio device. Preserve normal
   Windows volume mirroring and app/system audio.
7. Neither PC nor Android may generate synthetic replacement dings:
   real Windows System Sounds must travel through the true PCM route.

## Audio performance is a separate acceptance axis

The October 21 DEFAULT-only driver experiment recovered the **real Windows
volume ding** and delivered nonzero WASAPI loopback PCM in all six normal
categories (Other, SoundEffects, Alerts, Media, Movie, GameEffects).
The user also reported intermittent broken/crackling sound. That is not
resolved by the category test; see
[experimental mode result and next diagnostics](experiment-audio-default-mode.md).

Speakers-only should have no unnecessary virtual display / GPU / encoder
startup overhead. Benchmark connection time, end-to-end audible delay,
P50/P95/P99 jitter, sustained glitch rate, audio queue residency,
transport drops, recovery after Wi-Fi interruptions, and resource
consumption. Compete on measured sound quality and stability, not by
assuming a particular mixer or capture architecture is fastest.

## Acceptance matrix

- Cold start: host with **no** `--workspace-app`, phone disconnected:
  no selectable orphan speaker and no display requirement.
- Phone sends accessory role with speakers capability: authenticated
  speaker appears, can be selected as Windows output and emits real
  application + notification audio.
- Reconnect / Try Again from offline: same accessory role, preserved
  enrollment, no display initialization.
- Fail NVENC/display backend intentionally in a controlled test:
  accessory-only audio still plays; no physical monitor changes.
- Turn speakers on/off and connect/disconnect under continuous media
  without duplicate endpoints, stale PnP nodes, or misrouted sound.
- Two independent phones: distinct endpoints and independent teardown.
- Normal volume, no synthetic tones, encrypted audio, minimal reliable
  buffering and crackle-free sustained playback.
- Separate display path still accepts an explicitly chosen app when the
  user actually requests display streaming.

No runtime implementation changes are part of this design document.
