# Focused media performance continuation — High implementation handoff

**Execution update, 28 October:** M0–M5 are now implemented in source. The
planning-pass statement below is historical. Windows host/native and Rust sender
builds passed; the APK builds with updated release ARM64 native audio. Focused
crypto/ownership, actual Kotlin interop, codec-owner/reconnect and source-age/
ciphertext checks passed. Physical performance and candidate-specific hardware
gates remain open. Receive-depth tuning remains a measurement-gated follow-up;
the existing eight-frame SUSPEND policy is retained.

The core-setup continuation is now specified by the 29 October planning resolution,
[Trusted_Enrollment_Bootstrap_High_Handoff.md](Trusted_Enrollment_Bootstrap_High_Handoff.md).
It does not roll back or reopen the implemented media changes.

28 October 2026. **Planning only; none of M0–M5 below was implemented or
qualified by this planning pass.** This refines T06/T07 and the native audio
egress portion of the existing Architecture v0.6 programme. It does not replace
the core security handoff, reorder unrelated work or start another redesign.

Source reference: `experiment/vnext-audio-latency`, HEAD
`2f181cf2da9c4efdde3013b115108ecac650d1ab`, **plus the current uncommitted
implementation** in this checkout. HEAD alone does not identify the reviewed
source. Preserve unrelated changes. Installed binaries and physical performance
are separate evidence; no build, test, installation or hardware operation was
performed for this plan.

## 1. Scope, retained behavior and execution order

Retain the reviewed direct NVENC-to-managed destination, exact retained QUIC
ciphertext slot, fixed PCM packet construction and Android decrypted-record
slice. Source inspection found no introduced ownership/retry defect in them.
Do not restore their removed copies to simplify a later increment.

Preserve these functionality and correctness requirements:

- Adaptive FocusIdd/DPI, exact native viewport, WGC/D3D11 ROI, NVENC and direct
  Android Surface presentation. No quality, resolution, color or cadence
  reduction disguised as an optimization.
- Immutable original video source time, separately identified codec PTS and
  local observations; complete encoded access units and dependency recovery.
  Cached idle-screen IDR recovery must still deliver the final static update.
- Scoped authority, target/generation fences, authenticated records, counter
  ordering, replay rejection and exact length/capacity limits. No per-unit
  signature, registry access or policy IPC is added.
- Existing native audio ownership, Rust/quiche, committed-PCM provenance,
  source-admission contract of at most 60 ms, AAudio and receiver ASRC. A queued
  plaintext offer must pass both original-commit and correlated-presentation
  age checks where the committed-source contract requires them.
- Working control/input, microphone, file fairness, cancellation, reconnect and
  explicit Stop. Faster video must not starve these capabilities.

| Order | Increment | Intended improvement / prerequisite |
|---|---|---|
| M0 | Correct stage measurements and capture a source-bound comparison | Start first; collect only the measurements needed to judge the following changes. |
| M1a | Independent crypto direction locks | Remove video-versus-input contention; unchanged protocol. |
| M2 | One Android codec owner and bounded submission handoff | Remove UI-thread media work and cross-thread codec/state races. |
| M3a | Owned PCM transfer into quiche | Remove its remaining DeskFuse-to-quiche plaintext copy. |
| M4a | NVENC picture-type fast path | Remove repeated full scans of ordinary encoded frames. |
| M1b / M3b / M4b | Small adjacent cost reductions | Separate increments: Android seal output, reactor work, pool fit/retry storage. |
| M5 | Exact-alias Windows video encryption | Remove the second large pooled buffer after M1a and M4b; qualify the deployed crypto backend first. |

After M0, Android, Windows and Rust work can proceed independently. M2 follows
M1a on Android to keep that ownership transition reviewable. M5 is last in this
slice and does not hold up the other improvements. Complete focused verification
per increment; do not turn this into a new framework, benchmark suite or broad
documentation project.

## 2. M0 — measurements with truthful boundaries

**Anchors:** `NativeGpuPipeline.cs`, `gpu_pipeline.cpp`, `nvenc_encoder.cpp`,
`PhoneConnection.cs`, Android `LanTransport.kt` / `VideoDecoder.kt` /
`SessionController.kt` / `SessionState.kt` and their UI/report consumers,
Rust `reactor.rs` / `plaintext_expiry.rs` and their
existing lab reports. Paths are within the existing component directories.

### Local timing contract

| Path | Observations to distinguish | Interpretation |
|---|---|---|
| Windows video | Original capture time/provenance; native read/encode start; encode API return; bitstream lock return; inspection/copy end; managed enqueue/dequeue; crypto-lock wait; seal end; socket-write completion | Attribute source wait, codec work, staging, queues and crypto separately. API return and socket completion do not mean display presentation or peer receipt. |
| Android video | Complete sealed record available; decrypt start/end; validated frame ready; collector/owner admission; input-copy start/end; codec input queued; output callback; output release; optional surface-render timestamp | Include decryption and the formerly removed payload copy in the appropriate measured interval. Distinguish backlog from copy and decoder residence. |
| Audio sender | Original commit/presentation identity and ages at admission and last pre-construction check; packet-build cost; admitted/expired/full counts; plaintext queue count/bytes; reactor wake/maintenance; ciphertext pacing and socket-blocked dwell | Distinguish source age, construction and already-sealed network delay. Preserve the original source clocks rather than restamping at send. |

Use Windows QPC with its explicit frequency for Windows video, the existing
100-ns source-age domain for native audio, and `System.nanoTime()` for Android
local intervals. Never subtract unrelated endpoint clocks. Clock mapping, if
used for cross-device estimates, needs an explicit uncertainty; otherwise report
local stages separately. Codec PTS is an identifier/scheduling value, not proof
of unmodified capture time. Existing `NextPtsUs` enforces monotonicity; do not
replace the raw source timestamp with that adjusted result. Missing source time
or a capture-arrival fallback must be labeled truthfully. Transporting extra
source metadata remains the negotiated T07 wire work, not an incompatible
addition to this patch.

Correct the current Android `decodeLatencyMs` description and every consuming
label/report: it currently starts after decryption and ends at output release.
Call that legacy interval what it measures, keep it separate from the new
complete-record-to-release metric, and version report semantics so old/new values
cannot be compared as though identical. Remove the `SessionState.glassToGlassMs`
claim derived from `RTT / 2 + decodeLatencyMs`, including its UI/report labels:
RTT asymmetry and omitted capture/encode/queue/decrypt/display work make that
formula unsuitable. Show measured RTT and honest local segments separately.
Capture complete-record time immediately
after `readFully`, before `secure.open`; retain that time on the owned frame.
Read-start/idle blocking may be recorded separately, never called network transit.

Register per-frame timing **before** `queueInputBuffer`, remove it if queueing
fails, and clear it on codec retirement. M2 makes this owner-confined. Use a
bounded owner-local timing table with at most 256 sampled records per incarnation;
evict oldest timing metadata on pressure and count the missed samples. This is a
diagnostic memory bound, not a media queue or latency target. Missing render
callbacks may evict timing records, never media or authority. Record
missing/mismatched samples rather than inventing latency.

An optional `OnFrameRenderedListener` supplies surface-render time: use its
reported `nanoTime`, not the time its callback executes. Callbacks can be delayed,
batched or absent on older Android versions. Surface render is still not physical
input-to-photon latency. See the [Android callback contract](https://developer.android.com/reference/android/media/MediaCodec.OnFrameRenderedListener).

Native `qpcCopyDone` currently follows `CopySubresourceRegion`/`Flush` submission;
it does not establish completed GPU-copy duration. Keep GPU duration unavailable
unless a nonblocking GPU-timestamp measurement actually establishes it. No query
wait, GPU readback or synchronization barrier is added to measure the hot path.

### Comparison discipline

Use existing instrumentation where available. Add fixed counters/bounded timing
storage and sampled tracing only where needed; no per-frame log strings,
unbounded maps or new contended statistics lock. Keep diagnostics comparable and
measure their own overhead with detailed tracing disabled/enabled.

Compare one increment against the same current-source baseline and the same
geometry, codec, bitrate/quality, refresh, content, connection and device power/
thermal conditions. Use ordinary desktop activity, scrolling/text, static final
updates and IDR bursts. Include concurrent input/control, mic/audio and file work,
plus blocked network and lifecycle cases. Report distributions/tails, allocation
rate, actual rented capacity/working set, CPU, queue age/depth, drops, IDR requests
and visual correctness. Use existing lab facilities; do not create an unrelated
competitor campaign for this slice.

No arbitrary latency ceiling certifies success. Adopt a change for demonstrated
cost reduction or responsiveness with preserved functionality/fidelity and no
material regression elsewhere. Hardware/power and physical latency claims wait
for separately authorized matched runs. Absence of hardware does not block
independent source work; it leaves that qualification explicitly open.

## 3. M1 — crypto contention and Android output assembly

### M1a: independent directions

**Windows:** `windows/agent/FocusWindow.Core/Transport/SecureChannel.cs`.

- `_sendGate` owns send counter/cipher/key reference; `_recvGate` owns receive
  counter/cipher and first authenticated credential selection.
- `_locked` transitions only from null to one immutable winning candidate.
  Install the receive cipher before publishing the winner with `Volatile.Write`;
  send and identity/verification-code getters use `Volatile.Read`. There is no
  live rekey protocol to invent here. Preserve candidate-zero first-Hello
  compatibility and authenticated Hello before the host writer starts.
- `Dispose` alone acquires **send then receive**, marks disposed, destroys both
  ciphers and zeroes keys under both locks. Neither steady-direction operation
  acquires the other lock, and no crypto lock spans socket I/O. The existing
  disposed checks remain inside the relevant lock.

**Android:** `SecureChannel.kt` uses separate private send/receive monitors for
the distinct cipher/key/counter states. Replace the shared `@Synchronized`
monitor, not all synchronization. Keep replay-counter advancement after successful
authentication. There is currently no channel-disposal/key-zeroing API; adding
one is not a prerequisite or authorization to race new zeroing against operations.

**Both:** retain the single outbound ordered writer and Android
`SecureRecordWriter`'s complete seal/write ordering lock. Lane selection precedes
sealing; counter order remains wire order. No parallel outbound encryption or
input-message reordering. Counter exhaustion, authentication/crypto failure and
partial/failed writes retire that connection; never retry different plaintext
under a consumed nonce or replay a partially written record.

Necessary verification: simultaneous large video and outbound input/mic;
multi-credential first Hello; forged high receive counter; replay; send-counter
exhaustion; Windows disposal during operations; mixed-lane ordering. Compare lock
wait and input/control tails. Rollback is the former shared crypto lock with the
same keys/protocol, not removal of authentication.

### M1b: assemble Android ciphertext directly in its final record

This additional optimization is in the same reviewed `SecureChannel.kt` path.
Current `seal` allocates the ciphertext body, constructs an eight-byte counter
array, then concatenates them into another array. After M1a, allocate the final
record once, write the counter at offset 0 and use the JCA output-buffer
`doFinal` overload at offset 8. Plaintext and output stay separate.

After cipher initialization, validate required output capacity and actual bytes
written against the existing AES-GCM wire format, including its 16-byte tag.
Expose/transmit only the exact record length; no unwritten tail. Keep nonce
consumption and error termination unchanged. The owned result remains valid
through `SecureRecordWriter` completion; no new pool or JNI is needed. Verify
cross-language wire equivalence and actual Android-provider behavior. The
[Cipher API](https://developer.android.com/reference/javax/crypto/Cipher)
supports caller-provided output and reports its written length. This removes
DeskFuse's temporary body/concatenation; provider-internal allocations are not
claimed eliminated.

## 4. M2 — Android codec ownership without another frame queue

**Anchors:** `VideoDecoder.kt`, `VideoSyncGate.kt`, `SessionController.kt`,
`LanTransport.kt`. Preserve the direct decrypted-array slice and direct Surface.

First close the existing local reconnect-envelope gap: video frames currently
carry a target version without the local connection generation. Tag received
frame, control/target and lifecycle envelopes with their originating local
connection generation; no wire change is needed. Recheck current generation
after decrypt and before publication, then check again when the consumer applies
an envelope. A publication check alone cannot fence an already queued envelope.
Ready/Closed/Failed handling must reject retired generations as well. A target
and frame must belong to the same active connection before configuring/submitting,
even when a reconnect reuses the same target-version number. New generation
admission fences the old decoder epoch; preserve the last displayed image while
reconnecting. Implement this narrow metadata change before owner migration.

Use one decoder-owner HandlerThread for configure/start, input availability,
submission, gate transitions, callbacks, statistics, recover/release and stop.
Keep it for the `VideoDecoder` object's lifetime; retire codec incarnations
without repeatedly creating threads. Final close retires the codec, then exits
the thread; never join or synchronously wait on that thread from itself.

Move frame collection off Main. Post **at most one submission command at a
time**, awaiting that command's processing before collecting another frame.
That completion is not a wait for an unavailable codec input buffer. Keep the
existing `SharedFlow` capacity eight / SUSPEND and current no-input-index
`VideoSyncGate` loss/IDR behavior for this increment. Do not stack another
mailbox, launch a coroutine per frame, post an unbounded Handler FIFO or silently
drop dependent frames. Do not replace this with a rendezvous that blocks the
shared TCP reader waiting for codec capacity; later control must remain operable.

Start/reset/stop advance a local codec incarnation and close old admission
immediately. Owner commands capture expected incarnation and target version;
stale commands/callbacks cannot mutate the new codec or clear its keyframe need.
Retirement fails/completes outstanding submissions and releases their references
exactly once. A canceled submission in a still-active epoch is a lost AU and
requires the existing IDR gate; cancellation of a retired epoch needs no new
request for that epoch. Only a successful codec queue operation may clear the
IDR requirement. Stop/configure/recover operations execute in owner order; do not
hold a UI/session lock while awaiting them. Post UI callbacks back to Main and
fence late callbacks against the current session. A target change is ready only
after that incarnation is configured; it still waits for a successfully queued
keyframe. Keep the existing 500-ms IDR-request throttle. Main still performs
`view.configureFor(...)` before passing the immutable start specification to the
owner. The local generation metadata does not claim completion of T07's
authenticated wire-generation/input fencing.

Timing records are installed before queueing. With one owner, replace the
concurrent input-index queue and concurrent timing map with bounded owner-local
storage where appropriate; that is a consequence of real ownership, not removal
of necessary cross-thread publication.

Keep codec candidate/basic-mode fallback and native-size/color behavior. Save the
start specification and recover through the same retirement/configuration path,
using a fresh codec object, clearing obsolete input indices/timing and requiring
fresh IDR. Report failure if all existing candidates fail. This fixes the existing
`mc.stop(); mc.start()` recoverable-error path, which omits reconfiguration and
suppresses failure; it is not a copy-removal regression. The
[MediaCodec state contract](https://developer.android.com/reference/android/media/MediaCodec#states)
requires configuration before restarting after stop. Do not treat a failed
recovery as a harmless frame drop or leave `configured` true for a stopped codec.

Necessary verification: immediate output callback, absent input index, queue
exception, oversized AU, simultaneous submit/target reset/stop, stale callbacks,
surface loss, recoverable codec error and reconnect. No deadlocks, old-target
rendering, incomplete admissions or retained arrays after retirement. Measure
UI responsiveness and callback/submit residence as well as frame latency.

**Receive-depth follow-up, not a hidden M2 behavior change:** measure the existing
eight-frame backlog after owner migration. If it contributes meaningful queue
age/memory, compare a smaller bounded SUSPEND capacity (start with one) under the
same overload/control cases. Adopt only if input/control tails and recovery also
hold. This is no encoded-drop policy. If improving queue age requires changing
TCP multiplexing or H.264 discontinuity semantics, stop that experiment and use
T07's existing recovery/transport work; do not invent a new protocol on High.

## 5. M3 — one owned audio plaintext and lean reactor service

**Anchors:** `vnext/m2a/quiche-lab/src/plaintext_expiry.rs`, `reactor.rs`,
`direct_sender.rs`, `bin/continuous-quic-lab.rs`; pinned quiche 0.30.0.

### M3a: transfer, do not copy into quiche

The current fixed 1,000-byte array removed DeskFuse staging allocations, but
`dgram_send(&[u8])` still copies into quiche's owned buffer. Use its existing
`dgram_send_buf` ownership-transfer API. Build directly into one `Vec` with
capacity 1,000: original 40-byte header then the borrowed 960 PCM bytes. Do not
build a complete stack packet and call `to_vec` afterward. No quiche fork,
custom buffer factory or cross-thread PCM queue is required.

Avoid allocating for offers already expired or rejected by pressure. A narrow
PCM admission helper receives header, borrowed PCM and original timing. On the
single connection owner it purges, checks age, duplicate identity, metadata
capacity and quiche queue capacity before building. Re-sample the source clock
after building, revalidate authoritative freshness, reserve the exact metadata,
then transfer immediately. No intervening `conn.send`, unrelated reactor work
or source callback is permitted between reservation and enqueue. Both original
committed ages remain admissible at exactly 600,000 100-ns units and expire
above that boundary; preserve the existing future-timestamp semantics. If the
source expires during construction, drop it; never rewrite its timing to admit it.

Preserve return semantics and error custody:

- `Ok`: the queue owns the buffer and the ledger tracks its exact identity.
- Queue-full `Done`: the moved input is dropped by quiche; remove only this
  reservation, report Full, preserve previously queued entries/byte counts.
- Other errors are terminal for the connection. In pinned quiche, enqueue can
  occur **before** a later active-path error. Thus Err does not universally mean
  nothing was retained. Close admission and retire/drop the connection before
  any further packet construction; never retry the offered PCM or reuse that
  connection with its reservation removed. Recovery-in-place is outside this
  increment and would require exact-identity purge and a separate contract.

Quiche's four configured datagram slots also carry control/anchor messages;
metadata capacity alone does not establish room. One accepted PCM packet adds
exactly 1,000 queued plaintext bytes (length, not Vec capacity). Zero plaintext
queue bytes does not imply drained transport while ciphertext remains pending.

Keep pre-construction plaintext expiry before every `conn.send`, including when
an older ciphertext slot is paced/blocked. Already-sealed bytes remain immutable
and are retried exactly; do not reconstruct QUIC packets to apply a later audio
age check. Preserve source eligibility, original fragment offsets and the
existing no-fragment-exception rule for committed PCM.

Necessary verification: exact wire bytes, 60-ms boundary and expiry during
construction, both committed ages, duplicate/ledger-full/queue-full, unchanged
quiche queue bytes on Done, fatal post-enqueue error, and pending ciphertext
WouldBlock/pacing. Compare accepted/rejected-offer allocation and packet-build
cost. An owned Vec removes a copy, not all allocation; reject an apparent win
that merely moves allocation into dropped offers.

### M3b: reactor bookkeeping

- On service entry, perform maintenance if a ciphertext slot is already pending,
  including blocked/paced returns. When empty, perform the single maintenance
  immediately before construction. Retain maintenance before every subsequent
  construction in the burst; remove only the adjacent duplicate empty-entry pass.
- Retain the bound local address with the actual UDP socket owner instead of
  calling `local_addr()` on each service opportunity. Refresh it on socket
  replacement/rebind; keep `SendInfo.from` equality validation. No global cache
  across reconnects or new assumption about wildcard/multi-path sockets.
- Preserve the one ciphertext slot, current burst/fairness limits, OS writable
  readiness and pacing deadlines. No busy polling, media timer or unsafe
  uninitialized buffer is introduced.

Verify maintenance counts/order under empty, blocked, paced, flush-then-next-send
and fatal paths. Confirm readiness remains live and expiry is not delayed.

## 6. M4 — shorten native inspection and fit owned buffers

**Anchors:** `windows/native/FocusWindow.Gpu/src/nvenc_encoder.cpp`,
`encoded_access_unit.h`, `gpu_pipeline.cpp`, managed `NativeGpuPipeline.cs` and
`windows/agent/FocusWindow.Core/Media/IVideoPipeline.cs` (`GpuVideoPacket`).

### M4a: use trusted encoder metadata for ordinary frames

After size/source-timestamp validation, inspect `NV_ENC_LOCK_BITSTREAM.pictureType`:

- Known P or non-IDR I, with no forced recovery: classify non-IDR without scanning
  every compressed byte. Non-IDR I must not become a decoder recovery keyframe.
- Metadata IDR, initial/forced recovery: inspect actual Annex-B bytes and require
  SPS/PPS/IDR exactly as the existing recovery contract requires.
- UNKNOWN or unexpected type: retain the current byte inspection/fail behavior;
  do not guess. Metadata/byte contradiction on an inspected recovery frame is
  an encoder/profile error, not a reason to send an undecodable frame.

Keep the O(1) output-versus-input timestamp check, no-reorder encoder profile and
8-MiB native AU cap. Copy only while the bitstream remains locked and preserve
the ABI's complete-AU insufficient-capacity retry. This shortens locks without
restoring the intermediate vector. NVIDIA documents output picture metadata and
bitstream lock lifetime in its [NVENC programming guide](https://docs.nvidia.com/video-technologies/video-codec-sdk/13.0/nvenc-video-encoder-api-prog-guide/index.html).

Verify known/unknown classification and recovery headers in the helper checks;
later authorized GPU qualification covers initial IDR, ordinary P, forced cached
idle-screen IDR, timestamp identity and repeated undersized/query retries. Actual
NVENC behavior is not established by a CPU helper test.

### M4b: pool capacity and exceptional retry storage

Choose the initial **total** pool budget, then subtract framing overhead from its
payload capacity. The present 1-MiB payload plus 20-byte prefix crosses a shared
pool bucket; start with a 1-MiB total allocation, not a larger effective FIFO.
Respect the returned array's actual length and existing maximums; no dependence
on an arbitrary custom pool returning a particular bucket size. Where safe, use
the existing rented payload capacity instead of discarding usable tail space.
Expose only the completed payload/record length, never that capacity on the wire.

Before M5 the reserved overhead is 20 bytes; M5 changes it to 49 total bytes.
Keep growth for a real large IDR and the native retained retry; do not cap away
valid frames for a smaller allocation. Wire.MaxFrameBytes is 16 MiB of sealed
body; managed AU maximum is that minus 44 bytes, while native remains stricter
at 8 MiB. The outer five bytes are not part of the sealed-body maximum.

After successful exceptional retry, release the native vector allocation rather
than retaining its high-water capacity indefinitely; managed destination has
already grown. Do not shrink every frame, discard a still-pending AU, or change
its source timestamp/keyframe flags. Verify exact-size/one-byte-short/custom-pool
and disposal paths. Record actual rented bytes and copy counts, not just logical
payload sizes. The old vector reused capacity, so do not claim a malloc was
removed on every native frame.

## 7. M5 — exact-alias Windows video encryption

**Depends on M1a/M4b.** Change only owned video records initially; controls,
files and other audio writers keep their existing storage contracts.

| Offset in the one final rented array | Content |
|---|---|
| 0 | Kind, 1 byte |
| 1 | Sealed-body length, 4 bytes |
| 5 | Counter, 8 bytes |
| 13 | Encrypted frame header, 20 bytes |
| 33 | Encoded AU, N bytes |
| 33 + N | GCM tag, 16 bytes |

Total transmitted length is **N + 49**. Reserve 33 bytes before native payload
and 16 after it. Plaintext begins at 13; encrypt onto **that identical span**.
The sealed record begins at 5. Writing the counter must not overwrite plaintext.
Windows CNG supports equal input/output addresses, but forbids other overlap:
[BCryptEncrypt buffer contract](https://learn.microsoft.com/en-us/windows/win32/api/bcrypt/nf-bcrypt-bcryptencrypt).

First qualify exact-alias `AesGcm` on the actual supported Windows/.NET8 backend
against separate-buffer vectors and Android decryption. The native CNG contract
alone is not a promise for every .NET backend. Do not replace the crypto library,
introduce unsafe aliasing or handle this as shifted overlap if the deployed API
does not support it.

Keep native ABI payload-pointer semantics; changing its managed offset requires
no native media bus. `TrySendFrame` still transfers ownership only on acceptance.
Admission writes the frame header at offset 13 with its PTS/target version.
The single writer selects the lane, writes the outer record header, seals once
and awaits the exact write. Packet states are Writable → Complete → Accepted → Sealed →
Released; after acceptance, producer access ends. The packet may track these
states within its existing owner, without a new public buffer framework.

Return the single array after actual write completion/failure in writer finally,
or after queued cancellation/drain; never while in-flight I/O can still read it.
No resealing of a mutated packet, post-accept AnnexB inspection, pooled-tail
disclosure or double return. Keep counter exhaustion/crypto/write failure terminal.

The benefit is removing the second large pool rent and simultaneous plaintext/
ciphertext residency. The current writer already seals directly into its output,
so this does **not** remove another standalone plaintext memcpy or AES-GCM's
mandatory read/write work. Adopt on measured memory/cache/throughput benefit with
all ownership and latency behavior preserved. On failed API qualification, retain
the current separate destination and continue other increments; do not redesign
encryption on High.

## 8. Bounded follow-ups, rollback and High stopping rule

Do not reopen adaptive DPI, driver publication, broker trust, speaker categories,
audio-process migration, specialized providers or transport replacement for this
slice. Their existing workstreams/gates remain in force and can proceed
independently. Do not remove WGC's GPU ROI copy, per-frame D3D Flush or resource
synchronization based solely on a CPU timestamp. A supported interop/lifetime
proof and a discriminating profile are prerequisites for such a later experiment.
Likewise, no speculative direct decryption into codec buffers, codec dropping
scheme, quiche fork or new pool infrastructure is needed for these gains.

Each increment is independently revertible in source. Retire the affected
connection/codec and drain its owned buffers before changing implementation;
restart on fresh connection/codec state, never mixed nonce or lifetime state.
Retain authentication, source-age and recovery fixes on rollback. No live
installation, driver operation, route/configuration change or hardware run is
authorized by this handoff.

High should implement the settled contracts, run only the affected build and
meaningful ownership/protocol checks, then record which physical claims remain
open. Compare against a stable source-bound baseline; do not clean/reset the
dirty checkout or reuse unrelated prior receipts as proof of a new change.

**Stop and report for Ultra** if source contradicts these contracts, exact
ownership/order cannot be preserved, an optimization requires new wire/security/
codec-loss semantics, or a measured regression cannot be isolated and reverted.
Do not reason around those findings by weakening fidelity, authority, timing or
recovery. Expected missing hardware and a failed optional optimization gate mean
retain the qualified/current path and continue independent work; they do not
justify another broad redesign.
