# Mouse-motion-triggered audio interruption and A/V backlog

**Status:** Reproducible on-device user report; root cause **not yet proven**.
**Date:** October 21, 2026. **Priority:** blocking for audio quality, correctness and A/V synchronization.
**System:** Windows 10 19045, Xiaomi phone, experimental DeskFuse WaveRT
DEFAULT-only render driver. Active Windows PnP device **confirmed**
`DeskFuse Phone Speakers / 16.1.56.615 / oem128.inf`.

## Facts we must preserve

- Windows' **genuine volume flyout ding is audible**, and all six
  normal stream categories produce nonzero loopback PCM (details in
  [DEFAULT-mode experiment](experiment-audio-default-mode.md)). The
  `RawOther` variant remains unverified on the new driver.
- **Subjective latency is excellent:** owner calls this build
  **"EXCELLENT near zero latency"** and **"PERFECT in terms of speed"**.
  This is a listening observation, **not** instrumented end-to-end latency.
  Any fix must preserve the observed responsive sound and quantify its
  latency instead of raising buffering without evidence.
- **Audio integrity is poor:** audible irregular pops and very short
  crackles occur every few seconds across different sources; the real
  Windows ding sometimes crackles **halfway through the sound**. It is
  not merely a lab-tone boundary click.
- **Seeking in a video** multiple times can cause large A/V desync
  (including after loading another video). Another seek may make it
  seem to recover, despite continued crackles.
- **Reproducible trigger:** moving the **mouse** interrupts audio, with
  audible silence during movement; after the mouse stops, audio appears
  to catch up with short pops/crackles. This repeated user observation is
  stronger than the anecdotal description of generic glitches.
- No external acoustic loopback timing, QPC-correlated audio packet
  traces, kernel DPC profile or GPU/CPU utilization data yet. Do not
  assert an actual audio sample-rate speedup: a burst of packets, skipped
  stale PCM or video seeking can *sound* like catch-up, while Android
  AudioTrack still renders at its nominal sample rate.

## Code-grounded risk areas (not proven root causes)

1. **Host capture scheduling:** `WindowsLoopbackAudioCapture.cs`
   extends NAudio `WasapiCapture` with a 20 ms capture buffer. It
   tracks callback gaps, but a mouse/GPU/DPC/scheduler-induced callback
   delay could deliver PCM late or in bursts. The Windows capture
   callback also converts source samples on the capture thread.
2. **Host speaker pacing/backlog:** `PhoneSpeakerRoute.cs` uses a
   16-packet `BoundedChannelFullMode.DropOldest` PCM queue, followed
   by an asynchronous `Task.Delay` pacer. The schedule is reset after
   a >100 ms lateness check **only outside the inner draining loop**.
   A stall and accumulated PCM can thus cause rapid sends, silent
   drops and discontinuities. Packet count and time-in-queue are not
   logged. `TryWrite` can report success even when `DropOldest`
   discards an earlier packet.
3. **Encrypted transport audio queue:** `PhoneConnection.cs` has a
   second bounded eight-packet `DropOldest` audio queue. The
   `TrySendAudio` return value alone cannot account for packets
   evicted on overflow. One socket writer chooses audio before queued
   video, but an in-progress large write can block all subsequent
   packets *on that connection*. Track actual socket write durations.
4. **Android audio receive + playout:** `LanTransport.kt` uses an
   eight-element `MutableSharedFlow` with `DROP_OLDEST` for PCM;
   `SpeakerController.kt` receives chunks and `PcAudioOutput.kt`
   performs `AudioTrack.WRITE_BLOCKING` on a coroutine IO worker.
   Blocking writes, long stalls or receiver queue eviction can create
   underruns and discontinuities. `PcAudioOutput.stats()` already
   reports played queue ms, buffer ms, partial writes, underruns;
   `SpeakerController` logs it every ~2 seconds with
   `DeskFuseSpeaker` tag.
5. **Timestamp visibility:** `PhoneConnection.TrySendAudio` sends
   `streamId` + raw PCM but **no audio packet capture QPC,
   sequence ID or sample-position timestamp**. An `AudioClock`
   control message at stream start alone does not establish each
   packet's age after stalls, queue eviction or seeks. Instrument
   capture/send/receive/playout timing before implementing adaptive
   stale-audio discard or synchronized drift correction.
6. **Display role must be distinguished from accessory route:**
   `SpeakerController` / `DevicePeerSession` use a separate
   speakers-only accessory connection with **no video**. The
   display-role `PhoneSession.HandlePcAudio` instead captures mixed
   PC output and sends audio over its **display connection**.
   Shared-socket video head-of-line blocking is credible on the latter,
   but must **not** be assumed for independent phone-speaker service.
   Display/GPU activity can still cause resource contention shared
   across both. Record which role/route is active during reproduction.

The DEFAULT-only processing-mode experiment changed the driver
mode advertisement, not Windows capture, host queueing, TCP encryption,
Android buffering or video encoding. Since the exact changed code did
not alter these timing stages, do not attribute every crackle to the
kernel driver without an upstream/downstream A/B trace.

## First non-invasive reproduction

Keep sample rate, Android AudioTrack buffer, driver and agent unchanged.
Use one continuous media source and the **same connected phone speaker**.

1. **10 seconds idle** (no mouse movement).
2. **10 seconds continuous local PC mouse motion**, no video seeking.
3. **10 seconds idle / recovery**. Repeat this sequence once.
4. Repeat while a video is actively playing; separately perform a
   **pause/seek/resume** sequence and note whether sync recovers.
5. If possible, leave only the separate speakers accessory connected
   **without** a display streaming session, and repeat. If mouse
   corruption remains, video packets sharing the connection cannot
   explain it; compare system scheduling and the speaker audio chain.
6. A separate follow-up may compare mouse polling rates or different
   mice to distinguish interrupt/DPC pressure, but **do not change
   global audio services, driver signing, test buffers, OS tuning or
   Windows defaults during baseline capture**.

Collect the Windows agent lines beginning `phone audio:` alongside
the times the mouse moves. On Android, if adb is already available:

```powershell
adb logcat -v time -s DeskFuseSpeaker:I
```

The `audio path:` lines include `queued=...ms`, `buffer=...ms`,
`underruns=...` and `partial=...`. Preserve raw timestamps. A
`0 dropped` host number **does not rule out channel eviction**.

## Minimum instrumentation before any algorithmic change

Use bounded diagnostics only (not per-packet console logging on real-time
capture threads). Correlate events with one monotonic clock on Windows:

- Capture callback arrival gap (P50/P95/P99/max), actual frames/bytes,
  `AUDCLNT_BUFFERFLAGS_DATA_DISCONTINUITY`/silent flags when exposed,
  and source PCM level; record if silence already exists before host
  PCM queueing.
- Host 16-packet pacer: enqueue/dequeue QPC, packet age in ms, depth
  and exact overwritten/drop counts, wait lag, reset events, packets
  per wall-clock second. Count actual eviction even if `TryWrite`
  returns `true`.
- Transport eight-packet queue: equivalent depth, stale age, true
  evictions and write duration; distinguish TCP write blocking and
  video frame activity.
- Android receive gap, incoming audio sequence discontinuities,
  observed queue depth, `AudioTrack` blocking write times,
  playback-head advance, underrun increments and queued-frame
  residence. Record whether audio was delivered but blocked, never
  received, or dropped on purpose.
- Indicate display role versus accessory role, WGC/NVENC activity,
  host CPU scheduling and DPC/ISR latency only if queue timestamps
  implicate those stages. Use ETW/WPR where available.

**Acceptance criterion:** while moving the mouse and while seeking,
no sustained audio stalls, no speed-up illusion caused by bursts or
stale PCM, no repeated pops and no cumulative video/audio divergence.
Sustained media and brief Windows System Sounds must both be clean.
Verify P50/P95/P99 and worst-case physical end-to-end latency after
correctness is restored, preserving current perceived responsiveness.

## GPU/offload decision

Do not move audio mixing or PCM capture to the GPU as a speculative fix.
At 48 kHz stereo PCM16, audio conversion/pacing is small work compared
with WGC/NVENC and can be bottlenecked by scheduling, queues or TCP
regardless of raw GPU arithmetic throughput. GPU offload may introduce
copy/synchronization latency and new contention. Use instrumentation
to prove a material DSP computation bottleneck first; optimize the
specific overloaded stage with real benchmarks. Do not synthesize,
insert or replace Windows dings.
