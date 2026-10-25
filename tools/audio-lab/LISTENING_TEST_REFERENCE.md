# DeskFuse Audio Lab — Listening-test reference REF 01

**Date recorded:** 2026-10-24  
**Source:** User-provided summary of an intensive fine-adjustment listening session, relaying the testing LLM's recommendation.  
**Status:** Candidate standard/reference. **Do not represent as verified production defaults, independently measured acoustic latency, or GitHub implementation status.**

| Parameter | Reported reference |
|---|---|
| Windows source | MM-1 |
| PCM | 48 kHz, stereo, 16-bit |
| Packetization | 240 frames / 5 ms |
| Android output request | Shared, low latency requested |
| AAudio buffer request | Platform default (`0`) |
| Reported actual AAudio buffer size | 4,104 frames (85.5 ms *capacity* at 48 kHz; not measured latency) |
| Native reserve after callback | 1,440 frames / 30 ms |
| Resampler | 128-tap streaming sinc |
| Drift correction limit | ±400 ppm |
| Drift maximum slew | 100 ppm/s |
| Fades | Enabled; 96 frames / 2 ms |
| Short-gap concealment | Enabled; max. 480 frames / 10 ms |

**Already reportedly available as developer controls:** AAudio buffer policy and DSP toggles.  
**Proposed developer controls later:** Sharing mode, native reserve, fade and concealment lengths, detailed drift tuning.  
**Next focused task:** Review receiver starvation, priming, and recovery against existing implementation, counters, and listening tests.

**Not reported, therefore not confirmed as reference:** Exact startup prebuffer threshold, steady-state target queue depth, actual frames per AAudio burst, granted AAudio performance mode, actual hardware presentation latency, Android audio usage category, specific recovery thresholds, network jitter/loss, acoustic synchronization measurements, and whether all reported parameter values are present in the current GitHub branch. Mockup starting values for unreported fields are conceptual only.

**Laboratory methodology:** Compare against REF 01, alter one parameter at a time whenever practical, log hearing/continuity/actual measured delay separately, then establish stable and potential auto defaults across many sessions, routes and devices.
