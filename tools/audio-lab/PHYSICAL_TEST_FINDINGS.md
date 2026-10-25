# DeskFuse Audio Lab — field findings incorporated into v0.4

**Evidence status:** Reported from latest local listening sessions and user-supplied technical continuation notes. GitHub public source checked: branch `fix/vnext-audio-playout`, `ca747d8b5d288c503ce2f74f8c9918a3ad9f164f` (draft PR #30). The local evidence files, matching binaries and the Xiaomi were not accessible for independent verification here.

| Condition | Reported observation | Lab interpretation |
| --- | --- | --- |
| 30s, AAudio buffer policy 0 (platform default), MM-1, 128-tap sinc | User heard uninterrupted audio; zero active reprimes/recovery anchors | **Preferred experimental listening reference**; not a full continuity or latency qualification |
| 30s, AAudio requested 3 bursts (2880 frames at reported 960 frames/burst) | Android nominal pending estimate median reportedly changed from 128.0 to 104.6 ms; user heard **more** interruptions; 12 reprimes | **Rejected as new default**. Queue estimate is not end-to-end/acoustic latency. Do not proceed to 2 bursts solely from this observation. |
| 120s control, AAudio policy 0 | Failed early with authenticated recovery-anchor timeout; incomplete sender diagnostics after forced teardown | Long-duration stability remains unqualified. Do not call this an uninterrupted 120s pass. |
| Android negotiated output route | Requested low-latency but actual reported performance `NONE` / sharing `SHARED`; frames per burst 960; reported size/capacity 4104 frames | Hardware path is not established as low-latency. 4104/48000 = 85.5 ms of capacity, **not** necessarily queued or acoustic delay. |
| Native renderer | 128-tap finite-support sinc, source-identity tracking, 1440-frame post-callback reserve, ±400ppm drift, 100ppm/s maximum slew, 2ms fades, up to 10ms concealment | Preserve these unless one-factor testing justifies change. The bridge supports only existing startup mode ON/OFF and AAudio buffer 0/2/3. |
| Earlier interruptions | 17 in 20s, 25 in 30s despite reorder zero capacity/budget drops, later priming correction improved results | Focused root-cause path is receiver starvation, callback-demand-aware priming and recovery, not enlarging all queues. |
| Windows scheduling | An earlier ~110ms Windows stall reportedly exceeded 80ms native receiver ring budget, causing pre-QUIC loss in a run | Distinguish source gaps from receiver-starvation; do not equate recovery-retired sequences with network packet loss. |

**What is not proved:** universal defaults; low-latency Android HAL route; acoustic/e2e latency; 2-burst viability; ability to change startup controls live; sustained continuity or superiority versus other platforms.

**Safeguard:** This lab executes one of six controlled fixed variants. All other sliders are explicitly models/future options, not concealed runtime changes.
