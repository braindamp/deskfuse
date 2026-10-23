# DeskFuse vNext — first audible Windows → Xiaomi reference

**Date:** 2026-10-23  
**Evidence class:** physical Windows 10 PC → Xiaomi Android ARM64 Wi-Fi,
developer-observed audible output; *not* M2a/M2b final qualification.  
**Reference branch:** `reference/vnext-audio-first-audible-20261023`  
**Exact reference commit:** `913a2f6620d906d4539dd3f321cdbf06ece9d112`  
**Development branch:** `feature/vnext-wp001-native-lab`

This is the deliberately preserved **first known-audible source baseline**.
The reference branch points to that exact source commit. Do not move it during
normal feature development. Build outputs are reproducible from source; the
original locally built binaries, machine environment, executable permissions,
and private credentials are *not* part of this Git reference.

## Verified end-to-end behavior

1. Existing Windows endpoint `Speakers (MM-1)` was captured through new
   event-driven native C++20 WASAPI adapter without endpoint rerouting.
2. Captured PCM16 stereo 48-kHz frames were sent in authenticated QUIC
   DATAGRAMs over real Wi-Fi to the Xiaomi phone.
3. Android native AAudio output rendered real Windows audio on the handset.
4. Developer explicitly confirmed **sound heard on the phone**, otherwise
   clean. A small crackle/pop was noticed while adjusting volume.

### Physical 10-second run — source and sink observations

| Item | Observation |
| --- | ---: |
| WASAPI source frames observed | 480,480 |
| WASAPI writer queue drops | 0 |
| Sender PCM DATAGRAMs queued | 1,958 |
| Sender QUIC queue-full rejections | 44 |
| Sender expired source chunks | 0 |
| Sender capture discontinuities | 1 |
| Sender QUIC DATAGRAMs sent | 1,958 |
| Android received PCM DATAGRAMs | 1,957 |
| Android missing packet-sequence positions | 45 |
| Android source-frame gaps | 10,800 frames |
| Android AAudio accepted frames | 469,680 |
| Android AAudio rendered frames | 469,680 |
| Android AAudio silence frames | 89,040 |
| Android ring overflow count | 0 |
| Android output stream errors | 0 |
| Observed output stream format | PCM16 stereo 48,000 Hz |

**Interpretation:** The 44 reported sender-side QUIC queue-full events account
for most missing packet positions. One more sent application DATAGRAM was not
received in this run. The 89,040 rendered zeros include session priming and
shutdown and cannot be taken as continuous-run underrun duration. Audible
volume-change clicks have no confirmed root cause. This is **first audible
functionality**, not a 0-dropout/latency/stress acceptance result.

## Baseline versus later experiments

After this reference, the development branch added:

- `655a2fa` — drain QUIC egress between bursty WASAPI blocks instead of
  filling its small DATAGRAM send queue.
- `a4ca1ef` — soften first playback and genuine underrun discontinuities.
- Documentation updates recording developer hardware observations.

These changes are **not part of the hardware-proven reference** until the
physical phone-speaker session is repeated successfully. To reproduce exact
baseline sources, checkout the dedicated reference branch in a **separate
worktree**, never reset an actively used DeskFuse working copy or modify its
installed driver/APK. Do not commit phone certificates, private keys, pairing
secrets, Wi-Fi identifiers, captured PCM or local machine configuration.

## Subsequent product milestones

1. Make the 48-kHz Windows → phone audio path more continuous: sender deadline
   servicing, bounded media freshness, source gap treatment, underrun edges.
2. Implement Android output-demand-based drift adaptation with a single clock
   correction authority; no periodic network send clock or retroactive edits.
3. Move the native Android output into a properly managed app lifecycle:
   audio focus, route changes, background policy, pause/resume and reconnect.
4. Integrate the intended DeskFuse virtual audio endpoint in the native WASAPI
   sender without changing the existing working installed driver.
5. Qualify measured audio latency and continuity under the original
   preregistered physical load and recovery gates. Only then proceed to video.

The eventual source-of-truth for *acceptance*, not this milestone, remains
`vnext/m2a/PREREGISTERED_GATES.md`.
