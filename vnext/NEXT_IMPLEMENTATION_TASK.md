# Next implementation task — Windows source first

Read `vnext/EARLIER_CAPTURE_IMPLEMENTATION_PLAN.md`. That is the detailed design;
this is the execution brief for a High-reasoning implementation pass.

## Target

Make normal Windows applications render to **DeskFuse's own playback endpoint**
and deliver their audio through our existing secure transport and Android
renderer. Physical speakers, including MM-1, are comparisons only.

Implement a qualified short Windows engine period, then compare same-endpoint
software loopback with immediate accepted-WaveRT-packet export. Neither a
virtual endpoint nor a smaller requested buffer proves earlier capture.

## Starting state

- Code baseline: engine commit `73ef3d43008ef3f42679f68c3f2d6583d5cb3dee`,
  plus the planning commit containing this file, branch
  `experiment/vnext-audio-latency`.
- Four matching Release artifacts for73ef3d4 were built and hash-verified
  locally. Preserve them and the older audible references.
- A LOCAL uncommitted/unbuilt `EnginePeriodCompanion` draft may exist in
  `vnext/m1/src/wasapi_capture.cpp`. It is not on GitHub. Inspect/preserve it;
  complete or replace it according to the reviewed design. Do not assume it
  shipped. The required period-owner functionality is explicitly authorized
  implementation scope.
- Existing broker/per-phone endpoint and driver code are useful foundations.
  Installed/staged driver packages do not establish which source is running.

## Implement in this order

1. Reconcile the driver/package/source identity. Build the explicit DeskFuse
   endpoint session using the existing enrolled-peer broker lease. No default
   endpoint lookup, physical-output fallback or hidden routing restoration.
   Establish usable own-endpoint audio with the existing loopback backend first.
2. Complete the supported short-period owner. Handle legal, granted,
   already-active, locked and unsupported states explicitly; service silence
   on audio events; verify teardown and readback. Review the unfinished draft's
   error/lifetime paths. Query actual values rather than forcing128frames.
3. Correct driver fractional frame timing and delayed notifications before
   trusting short periods. Preserve nominal Windows clock semantics; export
   must not wait for virtual consumption or advance presentation early.
4. Add immediate valid-packet export at successful `SetWritePacket`, with the
   plan's private authorized read-only handoff, immutable ownership and
   out-of-band control state. Accepted commit progress includes export drops.
5. Add the direct Rust source adapter and source/socket event wait. Carry
   separate original commit, timeline and generation/fence evidence through
   admission, assembly, recovery and QUIC plaintext expiry. Do not replicate
   the old C++→pipe→reader queues behind the new driver ring.
6. Compare source availability and sender-ready time at matched actual period,
   format and backend settings. If fixed240frame assembly erases the gain,
   implement the plan's negotiated source-aligned framing dependency. Do not
   merely change a packet-size constant.

Use the detailed plan's ownership, clock, timestamp, recovery, gain/mute,
format and failure contracts. In particular, availability QPC is not
presentation QPC; pre-request commits cannot prove recovery freshness.

## Required limits and verification

- Existing48kHz/stereo/PCM16 and Android DSP remain fixed initially.
- Export: at most8descriptors and960outstandingframes, no overwrite/wait.
- Separate control snapshot remains observable when media credits are full.
- Keep60ms stale protection, original commit age, source frontiers and
  authenticated recovery. Never restamp old data, increase global future
  tolerance or expand storage to pass a test.
- Extend existing source/host/Rust/driver checks. No new general test service,
  dashboard, profiler or architecture-document cycle.
- Source/build verification is necessary; user listening is operated through
  the existing Audio Lab. Do not launch playback without user coordination.
- Prepare concrete driver/endpoint/routing actions for review. This handoff
  does not authorize driver installation/replacement, trust changes or default
  device/physical routing changes.
- Report separately: implemented, built, device-qualified and actually faster.

## Later independent workstreams

Android exclusive/GAME requests,0/2/3burst hardware controls and fixed native
reserve choices are already implemented. Qualify them instead of rebuilding
them. Then add explicit Android route enumeration/selection and bounded
reserve adaptation as specified in the detailed plan.

Keep Windows source, Android hardware path and native playback reserve as
independent comparisons. Android GAME usage is not DeskFuse Game Mode. Do not
change resampler quality, hardware buffer, reserve and capture source together.

When a package is complete, provide its actual code/build status and remaining
gate. Do not call capability queries or endpoint provisioning the completion
of earlier capture. Return for deeper review only when a concrete plan gate is
defeated, not for routine implementation choices.
