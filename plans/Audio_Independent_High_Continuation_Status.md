# Independent High continuation while N2 awaits Ultra

**Subsequent checkpoint:** this work and the completed N2 continuation were
committed/pushed as `d57d734`. See the later
[N3 status](Audio_N3_TLS_Control_Implementation_Status.md) and focused
[source-release Ultra stop](Audio_N3_Source_Release_Ultra_Stop.md). The original
uncommitted/awaiting-Ultra chronology below is historical.

29 October 2026. User explicitly authorized independent High work while away.
Checkout: `D:\Coding\DeskFuse\vnext-audio-latency`, branch
`experiment/vnext-audio-latency`, committed base
`09fd7a86b3c674fb1580705b734e64086a5b4e1a`. This continuation and the N2 draft
remain uncommitted/unpushed. Preserve the entire working tree.

## Boundary and current stopping point

**Subsequent Ultra decision selected:**
[Audio_N2_Bounded_Request_Retention_High_Decision.md](Audio_N2_Bounded_Request_Retention_High_Decision.md).
Its bounded renewal/retry protocol is now implemented in source and offline
checked: [current N2 status](Audio_N2_Bounded_Request_Implementation_Status.md).
Continue N3 on High. The original
[N2 stop](Audio_N2_Request_Retention_Ultra_Stop.md) is retained as background.
Neither this independent continuation nor the planning decision completes N2.
Production authority remains blocked by the broker entrypoint.

The independently specified N5 **capture foundation** is implemented and checked
offline. N5's production exit gate is still open: actual service-owned source,
TLS/delivery, admission, freshness/reconnect fence and rundown composition depend
on N2–N4. Installed protected-token loading, COM, audio and physical performance
are not established by these checks. N3/N4/N6 must not be marked complete.

## Implemented independent source work

- `vnext/m1/src/owned_loopback_abi.h` and `owned_loopback.cpp`, included by the
  existing approved source-control DLL: versioned x64 asynchronous open, borrowed
  readiness/data/terminal handles, immutable record borrow/exact-token consume,
  cooperative Stop, bounded join, checked destroy and bounded status.
- Explicit endpoint plus expected native source GUID/build are required. The
  startup cutoff uses the same interrupt-time clock as broker authority, checked
  before Start and Ready. No default endpoint, environment-selected policy,
  child process, PCM pipe or reader thread is introduced. The separate owned
  endpoint query checks corrected driver timing identity without requiring
  committed PCM export ABI 2; the existing direct query remains strict.
- The exact existing eight-slot SPSC queue now supports producer reserve/publish
  and consumer borrow/consume. Full drops the new fragment before conversion.
  There is no overwrite of a borrowed slot, capture-side wait for space, mutex,
  allocation or per-packet logging. One auto-reset event is a persistent hint;
  published cursors determine queue state. Tokens never wrap into old identities.
- Native capture reuses existing format conversion, legal-period/MMCSS helpers
  and fixed drop-position ledger. All COM/WASAPI create/start/service/Stop/release
  work remains on one C++ thread. A scoped packet guard releases each successful
  GetBuffer, including exceptions and cooperative Stop. Original device frame,
  packet QPC/offset, silence, timestamp-error and first-fragment discontinuity
  are preserved. Capture waits include Stop and have no periodic polling timer.
- Native terminal publication follows stream/COM destruction. Join waits for
  actual producer thread exit. Outstanding borrows block destroy; a reported
  Stop failure preserves the object as unconfirmed despite thread exit. The
  caller retains native object and DLL custody for quarantine/exact job exit.
- `native_loopback.rs` loads only the fixed sibling DLL in the retained worker
  closure using restricted DLL search. Its higher-ranked closure consumes the
  borrowed slot before returning to QUIC; unwind also releases the exact borrow.
  Failed consume/cleanup retains custody. Defensive Drop never unloads executable
  code underneath a possibly live C++ producer. Ordinary cleanup explicitly joins.
- `Assembler.push_borrowed` accepts the native PCM slice directly: no extra
  CaptureChunk, Rust PCM queue, serialized local header or input PCM copy. Both
  lab and native input share the existing required 240-frame assembler. Checked
  input ranges and reset preserve gaps, unknown timestamps and delivery fences.
- `WindowsEvents.wait_audio` combines distinct control, terminal and source
  events with existing socket/high-resolution timer readiness. Control/terminal
  have wait priority. The caller must check published state before sleep, bound
  source/network work and recheck authority on every wake; event observation is
  not authority. Runtime cutoff/grace mapping, suspend/resume wake qualification
  and actual owner dispatch remain N4/N6 integration work.
- Setup lifetime review: association enqueue is now serialized with persistence
  shutdown; final Android foreground approval checks one-shot admission again
  after preparation; coordinator remembers authenticated cancellation after its
  task is consumed, so a raced commit rejection cannot replace the completed
  cancellation/rundown result and tear down the result UI prematurely.

Existing M0–M5 media performance source work was checked against its status and
is already implemented. No speculative receive-depth or driver tuning was added.

## Verification actually executed

- Native Release CMake build and **5/5 CTest tests passed**: credits, frame clock,
  exact queue, existing pipe-writer lifecycle and new owned-loopback lifecycle.
  Hardware-free lifecycle covers timeout, pinned PCM through join, wrong/double
  tokens, refusal to destroy a borrow and uncertain-Stop custody. Queue coverage
  includes full/drop, publish visibility, concurrency, idle wake and final close.
- **54/54 Rust library tests passed** with `DESKFUSE_LOOPBACK_TEST_DLL` explicitly
  pointing at the built DLL. This includes actual DLL load/export/calling-convention
  rejection checks without opening a device, ABI layout, borrowed assembly,
  consume-on-return/unwind, failed-consume custody, combined Windows event wake,
  plus existing source-age, recovery, reorder, control and ciphertext ownership.
- **44 focused C# tests passed, zero failed/skipped**, including actual shipping
  Kotlin proof/receipt crypto interoperability and new persistence-owner cases:
  blocked association I/O through shutdown and immediate denial followed by
  serialized durable revocation. Receipt: `out/n5-checks/n5-focused.trx`.
- **34 additional C# media/security/session regression tests passed**, zero
  failed/skipped. Receipt: `out/n5-checks/n5-offline-regression.trx`.
- Broker and real native DLL build passed with zero warnings/errors. Optimized
  explicit-x64 fixed worker/DLL build and staging passed via
  `tools/build-audio-worker.ps1`; both artifacts passed the actual PE x64 checks.
- Android `:protocol3:interopJar :app:compileDebugKotlin --offline` passed, including
  rebuilding the ARM64 native library. Six pre-existing lab-path unused/dead-code
  Rust warnings remain. No app was installed.
- Command-local `core.whitespace=cr-at-eol` diff check passed; baseline CRLF was
  preserved. No global Git settings, source reset or checkout cleanup was used.

These are source/offline results. No installed service, persistent authority key,
driver/PnP, route, live endpoint, phone or device operation occurred. No CPU,
latency, power, protected-token loading or real WASAPI performance claim is made.

## Next order

1. Ultra retention decision is selected; use its focused High decision file above.
2. High implementation of the focused N2 decision is complete in source/offline
   checks. Native media/control formats remain unchanged.
3. High: N3 native TLS/control, N4 actual source/egress/cutoff/rundown composition,
   connect the prepared N5 adapter, then N6 renewal/recovery and service entry.
4. Separately scoped installed/physical qualification under the existing programme.

No further independent High source tranche was identified within this focused
audio/setup continuation without consuming the then-parked decision or its integration
dependencies. Preserve the already implemented media and native audio ownership
contracts; do not restart a broad architecture review on resume.
