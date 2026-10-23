# DeskFuse M2a — preregistered transport acceptance gates (initial)

**Recorded before Windows↔Android QUIC measurements.** Keep this baseline and date any revisions; never silently relax a gate after seeing results. Model tests, Windows tests and physical Xiaomi tests are different evidence classes.

## Invariants and correctness

- End-to-end QUIC TLS 1.3 authentication; both peers' identities verified, DATAGRAM negotiated, no 0-RTT media, no unauthenticated fallback.
- Exactly specified 40-byte big-endian header; authenticated config binds media epoch, source, format, layout, and generation. Reject invalid lengths, overflow, unknown mandatory flags, stale epochs, duplicates and post-commit late data.
- Every source frame either arrives correctly or is explicitly missing; no lost-generation replay; no retroactive rewrite of committed intervals; no unbounded payload, descriptor or ciphertext queues.
- Producer alone publishes SPSC slots; consumer alone releases slots. Feedback uses generation, monotonic sequence and publication-time-bounded lease; delayed feedback cannot renew an expired lease.
- Known-expired plaintext never newly enters encryption at a sender service opportunity; protocol-only ACKs, stream frames, path handling, key updates and QUIC timers must not starve.

## Fixed load envelope and observations

- 48 kHz, two channels, PCM16, initially 240-frame/5ms DATAGRAM: 960 PCM bytes + 40 header bytes = 1000 app bytes. Actual path datagram limit is discovered and respected; no IP fragmentation assumption.
- Three independent 30-minute runs per profile, including 15+ Brave-window restoration at minutes 5 and 20; CPU/memory/disk pressure and a separately capped 20 Mbit/s video-like competing load. Run matched audio-only controls. Record Wi-Fi/driver/firmware, power, RSS, CPU, route and Android build.
- Include CUBIC versus BBR2Gcongestion (where pinned implementation supports it), optional burst guard on/off, congestion/pacing release timing, datagram send `Done` with pending audio, RTT increases of 100 and 200 ms, packet loss and out-of-order delivery, WOULD_BLOCK, long producer/egress stalls, handshake/key/path events, and state/clock generation changes. Do not treat transport timing as another audio presentation clock.
- Observe sample-age at construction, QUIC seal, last freshness check, UDP acceptance and Android receive; cwnd, pacing deadlines, ACK/control backlog, sender ready-to-run delays, queue highs and network statistics.

## Post-sealing expiration policy: measured A/B/C comparison

A. Transmit unchanged already-sealed ciphertext even if audio is stale; document the explicit **sender freshness exception**; authenticated receiver enforces late rejection and never rewrites committed playback.

B. Discard the entire sealed packet locally and let QUIC normal loss/recovery account for it; measure congestion and retransmission effects including coalesced reliable frames and protocol obligations. Never fake rollback of quiche packet numbers or bytes in flight.

C. Prototype-only connection retirement as a diagnostic comparator, **never as a routine production recovery strategy**.

No ordinary-load audio-driven QUIC connection retirement is acceptable. If every supported post-sealing policy fails continuity and bound requirements, reject/change the adapter or transport; do not increase queues until symptoms disappear.

## Initial gates

- 100% correctness assertions for identity, ordering, ownership, authenticated peer rejection, bounded resources and generation/feedback freshness.
- For a forced 100ms sender stall, useful sending resumes within 500ms **after both endpoints are ready**; every gap accounted. This is an engineering threshold, not an acoustic promise.
- Zero routine audio-triggered connection retirement across the declared ordinary-load suite. Investigate >5MiB sustained RSS growth after first 5min stabilization.
- A test-only wire parser passing CTest **does not** satisfy encryption, continuity, scheduler, Wi-Fi or Android qualification.

## Dependencies to verify explicitly

- Pin exact quiche tag/commit, Rust toolchain, TLS backend, Android NDK/API/ABI, certificate chain/peer role and credential-storage strategy. M2a file-based preprovisioned *test* keys are acceptable only with proper protection and live verification; production keys need a separate keystore/CNG review.
- Pinned quiche C API Windows send time can be unusable; relative waits must be calculated from Rust `Instant` with checked/saturating expiry; service timer/read readiness even with no audio.
- The sealed send packet and protocol-only traffic have independent bounded lifetimes. quiche's `send()` returns one packet per call but may require repeated calls to service protocol state.

**M2a exit decision:** Accept this precise transport implementation only after actual Windows↔Android authenticated, stressed and instrumented hardware measurements meet the gates. Otherwise produce a failure report and a directly comparable alternate transport prototype.
