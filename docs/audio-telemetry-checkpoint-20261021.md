# DeskFuse Windows audio diagnostic checkpoint (host-only)

**Date:** October 21, 2026
**Branch:** `integration/audio-pipeline-telemetry-20261021`
**Base:** `experiment/audio-default-mode-20261021` (the locally installed DEFAULT-only WaveRT driver)
**Tracked defect:** [issue #14](https://github.com/braindamp/deskfuse/issues/14)
**Stage:** Diagnostics / Windows build verification; not yet measured on the user's machine.

## Safety and experiment invariant

**Do not reinstall or alter the signed audio driver.** The currently active
`DeskFuse Phone Speakers / 16.1.56.615 / oem128.inf` is delivering
real Windows taskbar dings and passing all six normal audio categories.
Preserve the owner's excellent perceived responsiveness, even though audio
currently crackles and desynchronizes.

This branch changes **only Windows host diagnostic counters**, while
preserving:

- Exact captured PCM samples, floating-point downmix and PCM16 layout
- 20 ms NAudio loopback capture configuration and 10 ms PCM packetization
- 16-packet route `DropOldest`, 8-packet connection `DropOldest`
- Existing `PaceAsync` `Task.Delay` timing and schedule reset
- Existing AES-GCM / wire protocol: `[streamId:int32 BE][PCM]`
- Existing Android application, AudioTrack buffer, startup threshold
- Installed broker, endpoint ownership and driver's processing modes

**No per-packet I/O or new wire fields.** The capture thread increments fixed
counters and stores its arrival QPC; the route queue wraps existing
`byte[]` with a no-heap-allocated value-type enqueue timestamp.
Health diagnostics are aggregated in the same approximately two-second
host reporter that already existed; no background logger is added.

## Additional Windows `phone audio:` fields

| Field | What it means |
| --- | --- |
| `packets queued` | Former `sent` counter: **accepted into the connection channel**, not delivered or played |
| `rejected` | `TrySendAudio` returned false |
| `routeEvict` | **Cumulative actual `DropOldest` loss** in the 16-packet capture-to-pacer queue |
| `routeReject` | Capture callback rejected because its route writer closed |
| `routeDeq` | Cumulative packets taken by the pacer |
| `ageMaxMs`, `age>50ms`, `age>100ms` | Two-second window: age from *route enqueue* to connection enqueue (includes pacer wait); **not** sample-to-phone latency |
| `paceReset` | Cumulative number of >100 ms pacer schedule resets |
| `delayCalls`, `oversleep>5ms`, `oversleep>10ms`, `oversleepMaxMs` | Two-second window: delay calls and time beyond the requested delay, **not** total delay duration |
| `txAccept` | Cumulative packets accepted into the outbound transport channel |
| `txEvict` | **Cumulative actual `DropOldest` loss** in the eight-packet encrypted writer queue |
| `txWritten` | Cumulative encrypted audio records whose local `WriteAsync + FlushAsync` completed; **not TCP ACK / Android reception** |
| `txAgeMaxMs`, `txAge>50ms` | Two-second window: packet age when the writer dequeues audio |
| `txWriteMaxMs`, `txWrite>20ms`, `txWrite>50ms` | Two-second window: local socket write + flush duration |
| `cap2s callbacks`, `maxGapMs`, `gap>20ms`, `gap>50ms`, `gap>100ms` | Two-second window: timing between **WASAPI callback arrivals**, not yet verified PCM sample discontinuities |
| `longest hole` | **Cumulative largest** callback-arrival gap for this capture instance |

The `itemDropped` callbacks are supported in .NET 8 and count losses the
original `TryWrite` return values cannot see.
For this first build we do **not** add QPC/sequence fields to the wire,
rework queues or claim exact end-to-end packet age. A second diagnostic
revision may need coordinated Android and host wire-version updates,
**only if** these results cannot localize the primary stall.

## First real-device test

1. Keep the installed driver and the existing **elevated broker**
   unchanged. Stop only the **unelevated Windows agent** via `Ctrl+C`.
2. Build a new Windows agent from this branch without overwriting your
   known-good acceptance executable; preferably obtain the full
   `deskfuse-windows-x64` artifact from this branch's green
   **Unified DeskFuse software checks** Windows workflow.
   Extract the **entire** bundle to its own directory. Do not launch
   just the EXE while leaving its native DLLs behind.
3. Run the new agent as your normal Windows user, with the **same**
   previously working arguments. For example, if the UI still makes a
   display-role connection:
   ```powershell
   & 'D:\Coding\DeskFuse\out\audio-telemetry\FocusWindow.Agent.exe' `
      --enable-pc-audio --workspace-app "$env:WINDIR\System32\notepad.exe"
   ```
   The above output path is **an example**, not evidence of where the
   artifact was extracted. Point at the folder you actually extracted.
   Display-only `--workspace-app` is not a requirement for speakers-only
   mode; this repeats the current working setup to avoid confounding tests.
4. Connect the Xiaomi's independent **Phone speaker** capability and
   select its real DeskFuse output in Windows. Optionally end only the
   display session; the speaker must remain connected.
5. Play continuous music/video. Reproduce:
   - 10 seconds mouse still
   - 10 seconds moving the PC mouse continuously
   - 10 seconds mouse still
   - repeat once (without seeking)
6. Copy the matching Windows `phone audio:` lines. On Android with ADB:
   ```powershell
   adb logcat -v time -s DeskFuseSpeaker:I
   ```
   Capture the `queued=`, `underruns=`, `packets=`, `partial=`
   lines. PC and Android wall times **must not** be assumed synchronized.
7. Finally, with mouse still, perform a separate short **pause/seek/resume**
   test and record whether audio desynchronizes.

## How to interpret the results

- **Large `cap2s maxGapMs` and `gap>50ms` at the moment of mouse motion**:
  prioritize Windows WASAPI event/capture timing, system scheduling, and
  only then kernel/driver evidence.
- **Capture callback cadence healthy, but `oversleepMaxMs`,
  `ageMaxMs` and/or `routeEvict` spike**: prioritize the host
  pacer / thread-pool and route queue.
- **Route metrics healthy but `txEvict` or `txWriteMaxMs` spikes**:
  prioritize the host encrypted transport writer / TCP backpressure.
- **All Windows stages smooth, but Android underruns grow**:
  investigate Android receive `DROP_OLDEST`, collector scheduling,
  `AudioTrack.WRITE_BLOCKING` and playback-clock behavior next.
- **Windowed callback gaps and queue eviction both rise**: record both
  as possible *effects of the same stall*, not automatically separate
  root causes.

Avoid drawing conclusions from cumulative counters without looking at
**deltas over each test window**. Diagnostic overhead exists and should
be A/B compared to the previous agent once the first trace is collected.

## Validation and rollback

The GitHub Windows workflow must compile the .NET 8 host and pass
`FocusWindow.Tests`. The new regression test verifies that an overflowing
`DropOldest` channel reports actual lost items **even if `TryWrite`
returns true**.

These diagnostics do not prove driver/WASAPI kernel timing behavior or
measure acoustic latency. For any unexpected regression, stop the
diagnostic agent and restart the preserved previously working agent.
The signed driver, installed broker and Android APK are unchanged.

The goal is **not** to eliminate all jitter by enlarging a playback
buffer. It is to identify the first stage that stalls during mouse
movement, then change only that stage and verify that perceived
near-instant playback is preserved.

## First actual user run: unexpectedly excellent sound (October 21, 20:04–20:06)

**User's direct assessment:** "MUCH MUCH BETTER! This was practically
PERFECT." Occasional small pops/crackles remain, but this exact
diagnostic-agent bundle is now a **valuable subjectively acceptable
baseline** and must be preserved. Previous agent/run was reported
massively broken, especially on mouse movement and repeated seeks.

**Interpretation caution:** Changes in this branch were intended to be
diagnostic-only: same sample conversion, pacing, channel capacity and
Android application. A different compiled agent from an earlier local
acceptance bundle was deployed. Improved sound is **empirical**, not
yet causally attributable to any instrumentation code. Different
binary vintage, timing perturbation, changed process launch or other
environmental factors cannot be ruled out. Do **not** call the mouse
or A/V sync defects completely fixed, and do not remove the pacer yet.
Do not overwrite or replace the known-good diagnostic executable
while testing the remaining crackles.

**Windows agent log, 31 provided health lines:**

- Cumulative `routeEvict=0`, `txEvict=0`,
  `routeReject=0`, and `0 rejected` throughout. No loss was
  reported in either measured Windows channel during this good run.
  This does **not** establish zero Android `SharedFlow` loss, and
  does not prove a channel could never evict during a worse run.
- By 20:06:36, `packets queued=6297`, `txAccept=6297`,
  `txWritten=6296`: roughly one audio packet in flight at
  the host's two distinct accounting points; **not** an Android ACK.
  Across the steady intervals roughly 200 packets/2 s are accepted.
- `txWriteMaxMs` within supplied windows was at most 18 ms;
  `txAgeMaxMs` at most 18 ms; no `txWrite>20ms` or
  `txAge>50ms` was reported.
- The 2-second capture arrival-window largest gap peaked at
  **68 ms** and the cumulative `longest hole` reached 68 ms.
  Several otherwise stable intervals had roughly 129–131 callback
  arrivals every ~2 s while ~200 PCM chunks were sent. A
  **callback is not equivalent to a 10 ms audio chunk**; capture
  can deliver multiple chunks per callback.
- `Task.Delay` oversleep is demonstrably nonzero:
  `oversleepMaxMs` reached **69 ms**, and recurring intervals
  saw >5 ms and >10 ms late delays. Nevertheless overall sound
  was judged acceptable; oversleep alone does not establish
  that a particular audible pop was caused by the pacer.
- At **20:06:06**, `ageMaxMs=126`, `age>50ms=18`,
  `age>100ms=2`, `paceReset=6`, `oversleepMaxMs=69`;
  the same reporting window has `cap2s maxGapMs=68`,
  `gap>20ms=21`, `gap>50ms=2`.
  At **20:06:10**, `ageMaxMs=102`, `age>50ms=159`
  and `cap2s maxGapMs=41`. Correlation between these
  timestamps and audible clicks is **not available**.
- A large `cap2s callbacks=2938` row at 20:05:38
  follows a long interval between supplied log lines; it should
  not be interpreted as 2938 callbacks in two seconds.
  Health reporting happens when the sender can call
  `ReportAudioHealth`, not on its own strict scheduler.
- The follow-up `adb logcat -d -v time -s DeskFuseSpeaker:I`
  was reported to produce an empty saved file. Therefore
  **no current Android underrun delta can be asserted**.
  The earlier 451–595 rising underrun sequence was from
  the previous severely broken run, **not this improved run**.

**CI validation caveat:** GitHub Actions run
[39644243550](https://github.com/braindamp/deskfuse/actions/runs/39644243550)
built the Windows agent, completed unit/native tests and published
`deskfuse-windows-x64`; the Windows job was ultimately marked
**cancelled during the final PowerShell wrapper smoke step**.
The Android job finished successfully. Do not mark the whole
workflow green; the new artifact exists and was exercised on
the user's Windows/Android device.

**Next step:** Preserve this compiled bundle, the current
`16.1.56.615` driver, elevated broker and existing Android APK.
Obtain a fresh **live** Android `DeskFuseSpeaker` trace while
this good host agent is playing, so we can quantify whether
underruns persist during the occasional residual clicks.
If available, note the approximate local time of a click
against a Windows `phone audio:` log interval. Only then run a
separately versioned A/B experiment (same workload, explicit
user rollback) to see whether reducing `Task.Delay` oversleep
can improve quality without degrading perceived speed.

## Second on-device session: live Android underruns persist despite nearly clean sound (20:26–20:27)

The owner re-launched the **same improved Windows diagnostic agent**,
elevated broker and installed speaker driver. The Android phone had an
authenticated **separate accessory speaker** connection. Display video
was also running in this particular session. Subjective report:
**"there IS some breaking but no extreme desync."** This supersedes
any temptation to mark the stream glitch-free.

### Evidence: live Android `DeskFuseSpeaker` (20:26:02–20:26:59)

- `packets=146 → 5170`; during continuous media,
  typically ~199–202 ten-ms PCM packets per two-second log interval:
  the **average long-run rate is nearly the nominal 100 packets/s**.
  This alone is *not* proof that every packet was delivered or
  that playout intervals were sufficiently regular.
- Android `AudioTrack.underrunCount` **1 → 123** in
  approximately **56.8 seconds**: **+122 underrun events**,
  roughly 2.15/s averaged over this interval. This is
  **not** an acceptable zero-glitch performance result,
  even though the owner reports vastly better subjective
  quality and no major video sync drift.
- `queued` repeatedly **10–56 ms** in reported snapshots,
  `buffer=85ms` hardware/AudioTrack capacity, `partial=0`
  throughout. `partial=0` does not show that incoming
  PCM was on time.
- At `20:26:18 → 20:26:20`, the completed-write packet count
  advances `1748 → 1946` (~99/s) **while underruns advance
  `18 → 32` (+14)**. Thus average packet throughput is
  compatible with *serious subsecond delivery/consumption jitter*.
  Other sharp rises include `79 → 96` at
  `20:26:46 → 20:26:48` (+17).
- `20:26:28 → 20:26:34` has a conspicuous reduction in
  packet count; the host log also shows a period with very
  few PCM packets. This may be the Windows render endpoint
  temporarily becoming silent/idle, **not** necessarily
  a transport fault. Correlate media activity, not only
  log rate.

### Host diagnostic Windows agent (user attached 92 lines)

- **No observed Windows queue loss:** `routeEvict=0`,
  `txEvict=0`, `routeReject=0`, `0 rejected`
  across the supplied audio health lines.
- Host transport: `txWritten` tracks `txAccept` closely
  (typically one packet behind). `txWriteMaxMs=0` in the
  measured windows; no `txWrite>20ms` reported.
  These are only **local WriteAsync completions**, not remote
  delivery or AudioTrack deadline guarantees.
- During steady media the sender accepts roughly 200 ten-ms
  PCM packets every two seconds, while NAudio loopback produces
  about 130 callbacks (roughly **15–16 ms callback interval**).
  Callback and packet count are different units: the capture
  callback frequently contributes more than one PCM chunk.
  That cadence is compatible with a timer-quantized polling
  capture path, but no direct proof of Windows timer resolution
  or its role in every pop.
- Isolated Windows capture callback arrival gaps
  reach **66 ms** cumulative max; `paceReset` reaches
  **16**, and `oversleepMaxMs` reached **43 ms**
  in this trace. Most periods are much healthier, but
  significant jitter exists.
- Windows host agent timestamps and Android Logcat
  timestamps **must not be subtracted** or aligned packet
  for packet without establishing clock offset. No packet
  sequence/timestamp header currently crosses the wire.

### Next diagnostic gate (no speculative buffering)

1. Preserve this improved binary and the signed `16.1.56.615`
   driver as the known-good baseline. Do not treat the
   telemetry build as a deliberate algorithmic improvement.
2. Add **Android-side local instrumentation**, preferably
   without changing PCM delivery policy: inbound chunk count
   and arrival-gap histogram **before** `SharedFlow.emit`,
   `SharedFlow` consumer gap/eviction accounting, receiver
   queue age, `AudioTrack.write(WRITE_BLOCKING)` duration,
   and playback-head/underrun snapshots.
3. Only if host-plus-Android local counters cannot localize the
   interruption, add a **versioned host/Android audio packet
   sequence/timestamp extension**, not a unilateral wire
   format change.
4. Compare instrumented app and original app in a reversible
   A/B test while listening for real taskbar dings, continuous
   music, mouse movement and seek behavior. Do **not** assume
   that increasing Android occupancy or stripping the
   Windows pacer will improve this now-acceptable configuration.
