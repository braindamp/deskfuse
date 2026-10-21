# Arena engineering challenge — DeskFuse: preserve near-instant audio while eliminating mouse-triggered underruns, crackle and A/V drift

**Paste everything below into Arena.ai / LMArena as one self-contained technical challenge.** Ask multiple models independently; compare their concrete designs, failure analysis and falsifiable experiments. This is a design/research challenge, **not permission to deploy or modify the live PC**.

---

You are a principal engineer specializing in Windows audio (WASAPI, WaveRT/KS, MMCSS, clock domains), low-latency encrypted network transport, Android AudioTrack/AAudio and real-time A/V synchronization. Design the **best demonstrably achievable** end-to-end Windows-to-Android virtual speaker pipeline for this reproducible failure, with implementation-level precision.

## Non-negotiable product requirements

- Windows 10 19045 PC, authentic Windows audio engine output via a SysVad-derived virtual WaveRT **render** endpoint; Windows software loopback captures the endpoint PCM and sends encrypted LAN audio to a connected Android phone. No synthetic Windows sound effects or fake system dings, no off-device/cloud audio, no replacing genuine PC media.
- One separately named Windows speaker per *connected, authorized* phone; no ghost endpoint while disconnected; robust recoverable reconnect. Audio must work in **speakers-only/accessory mode**, requiring **no virtual monitor, GPU video capture, NVENC, Notepad, or display-role connection**. If display streaming is also on, audio must coexist cleanly. Multiple phones must remain isolated.
- The current experimental driver is `10/21/2026,16.1.56.615`, confirmed live as `DeskFuse Phone Speakers`, `oem128.inf`. It advertises **DEFAULT processing mode only** instead of six separate modes; physical stereo 48 kHz / 32-bit float mix; all **six ordinary IAudioClient2 categories** now produce nonzero loopback audio and the **actual Windows taskbar volume ding is audible**. Previously only Other and GameEffects had valid PCM. Explicit `RawOther` **was not retested**, so compatibility remains an open item.
- The owner reports **"EXCELLENT near zero latency"** and **"PERFECT in terms of speed"** for the new working configuration. That is a *subjective* responsiveness observation, **not a measured end-to-end number**. We must preserve or improve this perceived immediacy. Do **not** just add 100–500 ms of buffering or use a fixed large jitter buffer to conceal glitches. A previously forced ~20 ms Android buffer/startup threshold caused massive sustained underruns; that experiment was reverted.
- The audio is currently unusable in continuity: short intermittent pops/crackles every few seconds across sources, including halfway through the authentic Windows ding; **moving the PC mouse reproducibly causes silence** followed by apparent catch-up audio with pops; repeatedly seeking PC video produces large, variable A/V desynchronization, sometimes recovering after another seek. **Symptoms persist with video/display streaming entirely OFF.** Mouse activity may be a proxy for resource contention; causation is *not* established. Don't simply prescribe GPU offload.
- Preserve correct mix, System Sounds, user volume, encryption/authentication, channel positions, 48 kHz conversion fidelity, video lip-sync, wake/reconnect behavior and finite CPU/battery demand.

## Logs — the strongest new evidence (actual user machine)

Android `adb logcat -v time -s DeskFuseSpeaker:I`; `queued` is estimated AudioTrack written-minus-played duration, `buffer` is AudioTrack capacity, `partial` counts partial writes, `packets` counts completed AudioTrack PCM packet writes:

```text
18:41:48.086 packets=64940 partial=0 queued=10ms buffer=85ms underruns=451
18:42:29.037 packets=66953 partial=0 queued=10ms buffer=85ms underruns=488
18:42:31.052 packets=66985 partial=0 queued=64ms buffer=85ms underruns=492
18:42:33.556 packets=67020 partial=0 queued=10ms buffer=85ms underruns=500
18:42:35.950 packets=67050 partial=0 queued=10ms buffer=85ms underruns=506
18:42:47.818 packets=67376 partial=0 queued=64ms buffer=85ms underruns=519
18:42:55.840 packets=68179 partial=0 queued=65ms buffer=85ms underruns=519
18:43:00.194 packets=68580 partial=0 queued=10ms buffer=85ms underruns=520
18:43:09.104 packets=68973 partial=0 queued=76ms buffer=85ms underruns=540
18:43:16.532 packets=69244 partial=0 queued=10ms buffer=85ms underruns=551
18:44:34.791 packets=69471 partial=0 queued=10ms buffer=85ms underruns=564
18:45:23.038 packets=73100 partial=0 queued=61ms buffer=85ms underruns=595
```

Windows elevated speaker broker / host-agent logs from the same experiment:

```text
18:40:03.562 phone audio: 67317 packets sent, 0 dropped | 152712 capture packets, longest hole 219 ms
18:41:45.819 phone audio: 67458 packets sent, 0 dropped | 159229 capture packets, longest hole 234 ms
18:42:29.721 phone audio: 70750 packets sent, 0 dropped | 162015 capture packets, longest hole 234 ms
18:42:31.719 phone audio: 70950 packets sent, 0 dropped | 162143 capture packets, longest hole 234 ms
18:42:33.717 phone audio: 71145 packets sent, 0 dropped | 162264 capture packets, longest hole 234 ms
18:42:35.713 phone audio: 71345 packets sent, 0 dropped | 162389 capture packets, longest hole 234 ms
18:43:09.719 phone audio: 74737 packets sent, 0 dropped | 164569 capture packets, longest hole 234 ms
18:44:27.666 phone said goodbye: user ended the [display] session
18:44:27.910 phone audio: 75031 packets sent, 0 dropped | 169499 capture packets, longest hole 234 ms
18:44:34.484 phone audio: 75378 packets sent, 0 dropped | 169891 capture packets, longest hole 234 ms
18:45:23.038 Android packets=73100 partial=0 queued=61ms buffer=85ms underruns=595
```

At ~18:42:29–31, the Windows **enqueue** counter advances ~200 packets in 2 seconds (consistent with 100 × 10 ms PCM packets/s), while the Android **completed-write** counter advances only 32 in ~2 seconds; Android underruns rise and its queue sometimes falls to 10 ms. At other times Android writes ~200 packets per 2 seconds. These are **different accounting points on different machines**: logs alone cannot prove whether PCM was lost at Windows capture, Windows outbound queue, TCP, Android receive queue or AudioTrack. The PCs' and Android's wall clocks may not be precisely synchronized. The host `"0 dropped"` currently reports *only failed TryWrite/TrySendAudio*, **not actual `DropOldest` evictions or acknowledged network delivery**. The 234 ms capture callback gap is a **cumulative maximum**, not proof that the specific later periods had 234 ms holes. `partial=0` does not establish timely delivery. Do not misuse these statistics.

The user confirmed that removing the video/display session **did not improve audio**. Any explanation requiring video sharing the same socket is contradicted by that controlled result, though Windows GPU/desktop/system contention may remain.

## Actual implementation (critical files and specific logic)

Repository: `https://github.com/braindamp/deskfuse`. Candidate branches:
`experiment/audio-default-mode-20261021` for driver and
`integration/audio-endpoint-lifecycle-20261021` for speaker broker + diagnostic documentation.
Do not assume a repository URL is visible to you: relevant core excerpts follow.

1. `windows/agent/FocusWindow.Agent/WindowsLoopbackAudioCapture.cs`: explicit loopback on phone's render endpoint, `LowLatencyLoopbackCapture : WasapiCapture(device, false, 20)` with loopback flags. WASAPI callback converts float/multichannel into PCM16 in 10 ms (~480 frame at 48 kHz stereo = 1920 bytes) chunks, invoking `onSamples` from the capture callback. Tracks cumulative callbacks and largest `Environment.TickCount64` arrival gap. It currently lacks capture packet **sample-frame index, QPC timestamp, explicit gap/discontinuity diagnostics and full callback timing histograms**.

2. `windows/agent/FocusWindow.Core/Session/PhoneSpeakerRoute.cs`: the **independent speakers-only audio** route owns a 16-element bounded `Channel<byte[]>`, `SingleReader=true`, `FullMode=DropOldest`. Capture callback converts each packet to `samples.ToArray()` and `queue.Writer.TryWrite(packet)`—no actual eviction count. A separate asynchronous `PaceAsync` does:

```csharp
var bytesPerSecond = sampleRate * channels * 2;
var nextPacket = Stopwatch.GetTimestamp();
while (await reader.WaitToReadAsync(ct)) {
    var now = Stopwatch.GetTimestamp();
    if (now - nextPacket > Stopwatch.Frequency / 10) nextPacket = now;
    while (reader.TryRead(out var packet)) {
        var remaining = nextPacket - Stopwatch.GetTimestamp();
        if (remaining > 0)
            await Task.Delay(TimeSpan.FromSeconds(
                remaining / (double)Stopwatch.Frequency), ct);
        if (conn.TrySendAudio(packet, streamId)) _sentPackets++;
        else _droppedPackets++;
        nextPacket += (long)(packet.Length *
            (double)Stopwatch.Frequency / bytesPerSecond);
    }
}
```

Notice that its >100 ms reset occurs **only before** the inner queue-drain loop, and that even apparently successful sends can be silently evicted downstream. `ReportAudioHealth` prints `sent/dropped/capture largest gap` about every 2s, but no *packet age, true queue depth, resync counter or transport transmit confirmation*. Assess whether software timer pacing is counterproductive when WASAPI already gives device-clock-paced packets; don't assume removal is safe until capture cadence is measured.

3. `windows/agent/FocusWindow.Core/Transport/PhoneConnection.cs`: `TrySendAudio` creates `[streamId:int32 BE][PCM16]` (no packet sequence/sample index/capture timestamp). It enqueues to another **8-element `BoundedChannelFullMode.DropOldest`**. There is a single ordered authenticated AES-GCM socket writer for control, audio, video/files, with audio priority before queued video. `TrySendAudio` returning true means **accepted by channel** only, not transmitted to the phone; `TryWrite` is not a reliable eviction indicator in `DropOldest`.

4. `android/app/src/main/java/com/focuswindow/client/transport/LanTransport.kt`: encrypted TCP read loop performs `readFully`, authenticates, creates PCM chunk, then publishes into `MutableSharedFlow<PcmAudioChunk>(extraBufferCapacity=8, onBufferOverflow=DROP_OLDEST)`. No packet sequence/age visibility, and there may be hidden eviction during `emit`.

5. `android/app/src/main/java/com/focuswindow/client/session/SpeakerController.kt`: separate foreground speaker service, its **own accessory/media connection**, no virtual display; `transport.audio.collect { chunk -> output.write(chunk) }`. `SpeakerService` owns a `Dispatchers.Default` coroutine scope. Periodic Logcat `DeskFuseSpeaker` reports output stats.

6. `android/app/src/main/java/com/focuswindow/client/media/PcAudioOutput.kt`: `AudioTrack.Builder().setPerformanceMode(LOW_LATENCY)`, `MODE_STREAM`; Android chooses ~85 ms effective buffer on this device. `suspend fun write` runs on `Dispatchers.IO` and loops over `current.write(..., AudioTrack.WRITE_BLOCKING)` until chunk complete. `stats()` reports written-minus-playback-head queue length, buffer capacity and `AudioTrack.underrunCount`. Output route targets built-in phone speaker; start/write/clock state is not currently designed as a packet-timestamped playout controller.

7. `windows/agent/FocusWindow.Core/Session/PhoneSession.cs` has a different, **display-role mixed-PC-audio** route. Do not conflate it with `PhoneSpeakerRoute`; physical video streaming has already been disabled during a failed audio test. Audio device is actual Windows output, not a synthetic/test tone. The 6/6 probe checks category PCM integrity but does **not** measure sustained glitch rate or end-to-end latency.

## Deliverables — be exact and falsifiable

**A. Triage.** Rank the most credible root causes of **(i) mouse-motion silence, (ii) downstream underruns, (iii) apparent catch-up, (iv) seek-induced sync drift**, with an evidence ledger: what is *proven*, *plausible*, and *contradicted* by no-display reproduction. Challenge the tempting but invalid inferences: `0 dropped`, `0.0 ms GetStreamLatency`, subjective near-zero ≠ physical timing, 234 ms cumulative max ≠ every stall. Distinguish actual sped-up AudioTrack playback from burst arrival, eviction and skipped samples. Propose the **smallest decisive experiment** to discriminate the top 3 mechanisms.

**B. Observability design.** Specify data types, APIs and units for capture-time `Stopwatch/QPC` timestamp, cumulative sample-frame index, sequence ID, sender enqueue/dequeue/transmission, Android receive and AudioTrack playback-head/written-frame index, underruns and clocks. Explain clock synchronization/offset and drift across PC/phone: how to compare monotonic clocks correctly, with limits. Instrument true evictions at *every* bounded queue; record packet/bytes/samples and packet-age P50/P95/P99/max (not just periodic log counts). No per-packet console logging or real-time thread blocking; use bounded counters/histograms/events. Include sample telemetry strings and where the instrumentation should be inserted in the exact files.

**C. Ideal production pipeline.** Draw a concrete stage-by-stage architecture with thread/lifecycle ownership and queueing invariants. Determine whether to eliminate or replace `Task.Delay` PCM pacing, where to implement deadline-based *late-data expiry*, how to prevent cascading silent eviction or old-audio replays, and how to restart after a real discontinuity **without popping or manufacturing substitute Windows tones**. Handle short System Sounds faithfully and avoid dropping the real ding just because it is brief. Preserve a strict connection and identity boundary. Explain how PCM sample timeline and network timing map to Android's actual playback clock, including sample-rate mismatch/drift, underflow recovery, low-latency start and how to remain synchronized with the *PC's local on-screen video* during pause/seek or player reload. Should streams use TCP, QUIC datagrams, UDP with AEAD, or remain on a separate encrypted TCP accessory connection? Evaluate reliability/latency/HOL concretely; don't propose a protocol rewrite without evidence. Consider `IAudioClient3` low-latency shared periods on Win10 only if actually supported and measured.

**D. Tradeoffs and exact code plan.** Propose first a safe **instrumentation-only** patch, then a minimal production fix once traces isolate the stage, then the ambitious best-in-class architecture. Give Kotlin/C#-level pseudocode or concrete function-level diffs for the high-confidence stage(s); identify tests that detect silent `DropOldest` loss, timer oversleep, 10ms packet burst, Android WRITE_BLOCKING starvation, 32-bit playback-head wrapping, intermittent silent WASAPI capture, buffer discontinuity and reconnect/seek. Preserve existing PCM channel mix, volume mirroring, authenticated encryption, per-phone device lifecycle and 6/6 category correctness.

**E. Performance budget and acceptance.** Define an end-to-end measurement rig with physical audio onset and audio/video event alignment (e.g., common external mic/camera/timestamp markers), not round-trip ping or `GetStreamLatency`. Set a defensible minimal reliable buffer budget based on actual callback jitter and phone hardware minimum, not a guessed 20 ms; measure P50/P95/P99/worst-case latency, CPU/GPU/DPC impact, battery, actual underruns, glitches/hour and A/V sync after repeated seeks. Success requires no audible mouse-motion interruptions, zero persistent A/V drift, and no degradation from the currently excellent perceived responsiveness (until hardware evidence supports a necessary tradeoff). Test display active and absent, other CPU/GPU loads, fast repeated seeks, audio start/stop, 2+ phones, unplug/ reconnect, multiple Windows mixer categories and brief taskbar dings.

**F. GPU suggestion.** Explicitly evaluate whether GPU fusion/offload could help *this* problem. State the evidence threshold at which it beats straightforward CPU event-driven audio operations after accounting for PCIe/memory transfers, scheduling, pipeline synchronization and competition with display encode. Be willing to conclude **no GPU** if the bottleneck is starvation, timebase or queueing. Likewise, do not propose Windows 11 solely to evade Windows 10 engineering work without a specific measured advantage.

**Response quality:** Lead with the top causal theory and confidence; then a clear proposed pipeline, diagnostic plan, code-level changes and numerical verification criteria. Address the strongest contradictory observation rather than handwaving it away. State assumptions explicitly. Avoid slogans ("use a jitter buffer", "use GPU", "use real-time priorities", "use UDP") without showing precisely why, where, and how it avoids silent data loss *without adding user-perceived latency*. Be intellectually adversarial: if the facts do not support one culprit, say which data would decide it.

---

**For the challenge organizer:** This prompt is intentionally self-contained, because a model in Arena might not browse a private repository or have access to GitHub/ADB. Compare candidates on evidence handling, queue-loss accounting, clock-domain correctness, realistic Win10 + Android support, preservation of the real taskbar ding, and a staged patch with regression tests. Paste model responses back into DeskFuse's issue/discussion for critique before modifying the currently working kernel driver.
