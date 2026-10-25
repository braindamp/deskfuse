# DeskFuse — earlier Windows capture and independent latency workstreams

25 October 2026. Implementation plan only. No implementation, builds, tests,
device activation, driver installation or routing changes were performed while
preparing this document.

## Read this first

**Production applications must render to DeskFuse's own Windows playback
endpoint. Physical speakers, including MM-1, are comparison or optional local
monitoring outputs. They are not the production capture source.**

The selected Windows architecture is:

```text
Application(s) explicitly render to DeskFuse
    → Windows shared audio engine, at a qualified short period
    → DeskFuse virtual render endpoint
        → preferred candidate: immediate export of accepted WaveRT PCM
          into a bounded driver-owned handoff read by the Rust sender
        → comparison/fallback candidate: same-endpoint software WASAPI loopback
    → existing deadline-aware QUIC owner
    → existing Android renderer
```

Two gains must be established separately:

1. **Earlier production of mixed PCM:** a shorter audio-engine period on our
   endpoint. A smaller capture allocation does not accomplish this.
2. **Earlier availability to the sender:** publish valid committed PCM without
   waiting for virtual playback completion or an extra capture delivery cycle.

The direct export is the preferred engineering candidate, not a proven winner.
Software loopback can already receive a copy of the engine output. We will
compare the two on the **same DeskFuse endpoint, actual period and format**.
If virtual software loopback is equally fast or faster with better reliability,
it remains a legitimate production source. Neither choice returns production
capture to a physical speaker.

Workstream order is Windows source first, then Android hardware output, then
native playback reserve/recovery. Their controls and comparisons remain
independent. Preserve the working transport, source identity, sinc renderer and
audio references throughout.

**The first implementation deliverable is usable application audio from a real
DeskFuse endpoint reaching the existing phone path. A query, endpoint icon,
smaller capacity request or configuration website alone does not fulfill it.**

## 1. Verified starting point and unfinished work

Repository checkout:
`C:\Users\Arne\Documents\Codex\2026-10-22\read-the-attached-document-i-want\work\deskfuse-peer-fixes`.
Branch `experiment/vnext-audio-latency`; last committed engine revision
`73ef3d43008ef3f42679f68c3f2d6583d5cb3dee`.

All four Release artifacts at that revision were built and their hashes were
checked against `vnext/m1/build/audio-pair-build.json`. This establishes build
identity, not physical improvement. The original reference artifact sets stay
protected. The standalone Lab is a separate local application; it is not another
audio engine.

There is now an **uncommitted, unbuilt** `EnginePeriodCompanion` draft in
`vnext/m1/src/wasapi_capture.cpp`, written before this planning request. Leave it
unbuilt during this task. Review it against section 4 before reuse. In
particular, its present rejection of an already-minimum engine period is too
restrictive for a real source backend: already-running-short is a valid state,
though it is not a new measured improvement. Do not include this draft in claims
about the built revision.

The user has explicitly requested completion of this draft if it contributes
to the result. Its required functionality is therefore included in W-E1/W-E2:
complete and verify a period owner using the reviewed design, rather than
publishing the current unfinished experiment. This planning pass does not
complete or build it.

| Workstream | Already implemented/built | Still missing or unproven |
|---|---|---|
| Windows capture | Explicit endpoint loopback; event-driven acquisition; bounded SPSC/pipe; original WASAPI provenance; capability query; capacity-request control | Dedicated-source session integration; qualified low-period driver timing; immediate PCM export; source-specific timing/recovery contract; proof of earlier sender availability |
| Android hardware | Shared/exclusive request; optional GAME usage; LOW_LATENCY request; default/2/3-burst policies; actual grant/device/buffer observations; callback-size buckets; optional wall-time observation | Qualification of those options; route enumeration/selection; per-route policy; proof of hardware-path and acoustic latency |
| Native playback | Fixed raw reserves 1440/1200/960/720/480; callback-demand-aware priming; capacity guard; existing fades, concealment, recovery and drift control | Qualified latency profiles; bounded adaptive reserve; independently justified recovery changes |

Relevant Windows facts from earlier read-only inspection:

- This PC runs Windows 10 build 19045.6466. That does not constrain the
  architecture to a physical endpoint, but excludes supported process loopback
  on this host.
- Four DeskFuse packages exist in DriverStore and the DeskFuseAudio service is
  registered, Manual/Stopped. No active DeskFuse render endpoint or running
  broker was found. Package presence does not prove the desired source is usable.
- The repository driver is an experimental render-only, DEFAULT-mode candidate.
  Its comments distinguish it from an older installed/multimode candidate.
  Installed package, current source and active device must be reconciled.
- Current source advertises stereo PCM16, a preferred 48 kHz format, a 2 ms
  minimum processing interval and a 128-frame processing constraint. These are
  declarations, not measured operating periods.

## 2. Architecture decision and alternatives

| Candidate | Decision | Reason and limit |
|---|---|---|
| Own endpoint + qualified short engine period | Required production foundation | Removes dependency on whichever physical output happens to be connected; controls the supported endpoint processing contract. Earlier PCM still requires runtime proof. |
| Immediate accepted-WaveRT-packet export | Preferred direct source to implement and compare | Explicit valid-data boundary; can avoid a recapture wake/handoff. It follows engine processing and does not inherently precede software loopback. |
| Own endpoint + event-driven software loopback | Required comparator and potential production backend | Reuses a functioning supported source. It may already expose the engine mix promptly enough to beat a more complex export. |
| Smaller capture capacity on a physical endpoint | Retain as an isolated diagnostic only | Changes capacity, not necessarily when PCM becomes available. Does not deliver the production objective. |
| Endpoint EFX APO tap | Conditional alternative if measured driver-boundary delay matters | Could publish inside the engine callback, but adds audio-process deployment, clock-correlation and realtime-lifecycle obligations. A render APO on this host must not be assumed to receive a presentation timestamp. |
| Process loopback | Later source-selection option on supported Windows | Endpoint-independent selection, not a latency guarantee; Microsoft's sample requires build 20348+. |
| Cooperative application PCM submission | Future fastest-input option for integrating applications | Can bypass application-to-endpoint stages only with application cooperation; does not capture arbitrary desktop applications transparently. |
| Exclusive/ASIO interception | Not the general desktop solution | Cooperating applications can bypass shared mixing; this does not provide a universal interceptor or mixed desktop source. |
| Polling live WaveRT memory, injected app hooks | Reject | No reliable valid-data ownership boundary or supported general contract. |

Microsoft describes software loopback as a possible copy of engine output,
which is why direct export must earn its place through comparison.
[Loopback implementation](https://learn.microsoft.com/en-us/windows/win32/coreaudio/loopback-recording).
Driver-supported periods and client requests provide the actual scheduling
mechanism; driver ownership alone does not.
[Low-latency architecture](https://learn.microsoft.com/en-us/windows-hardware/drivers/audio/low-latency-audio).

No architecture can obtain arbitrary application samples before that application
submits them. The objective is earlier acquisition and forwarding after Windows
receives the audio, with application buffering reported separately.

## 3. Production endpoint, routing and ownership

Use the existing per-enrolled-phone endpoint model. Keep privileged device
management in `DeskFuse.AudioBroker`; C# remains a control/lifecycle layer, never
the PCM clock, packet pacer or realtime media queue.

Implement this session sequence:

1. Authenticate the Android session using the existing enrollment/security path.
2. Acquire the exact enrolled phone's endpoint lease through the broker.
3. Resolve the returned endpoint to its PnP instance, driver identity and source
   capabilities. Do not accept a friendly-name-only match.
4. Open the source against that explicit identity. Production source selection
   never calls `GetDefaultAudioEndpoint` as a fallback.
5. Start period ownership/acquisition and establish a new source generation.
6. Let the user explicitly select DeskFuse as the application or system output.
   Selecting the Windows default can be a product action, but never a concealed
   prerequisite or automatic fallback during qualification.
7. Stop publication/acquisition before releasing the lease; wait for outstanding
   source access to end before removing only the device this session owns.

Reuse the existing takeover-generation and connection-lease cleanup semantics.
An old session must not remove the new session's endpoint. Broker loss, source
failure, endpoint removal and format changes must retire the corresponding
source context instead of capturing another output.

For the initial lab integration, call the narrow broker contract directly.
Do not invoke `BrokerPhoneSpeakerProvider.ProvisionAsync` merely as a probe:
that product path also attempts prior-default restoration. Add an explicit
no-routing provisioning option if sharing that implementation is preferable.

Keep simultaneous local monitoring out of the acquisition path. It is an
optional extra renderer with a separate sink clock and delay, never a dependency
for phone audio. No automatic MM-1 routing or speaker duplication.

Driver installation/replacement, trust/signing configuration and default-device
or physical-speaker routing changes require the authorization specified by the
original task. Prepare the concrete package/endpoint action before asking.

## 4. Make a genuinely short Windows engine period correct

### 4.1 Driver prerequisites

Retain the useful PortCls/WaveRT endpoint and broker code. Do not rewrite the
driver wholesale. Fix these specific timing assumptions before relying on
fractional-millisecond periods:

- `AllocateBufferWithNotification` currently floors buffer and notification
  durations to milliseconds.
- `UpdatePosition` currently advances through whole-millisecond arithmetic.
- `TimerNotifyRT` currently advances the packet count once per serviced
  interval, which is inadequate after a delay spanning several periods.

The replacement clock uses a 64-bit source-frame cursor and QPC correlation.
Perform overflow-safe quotient/remainder arithmetic; keep positions aligned to
whole PCM frames. Compute elapsed nominal frames from QPC and derive completed
packet boundaries from that cursor. A 128-frame period at 48 kHz is 2.666... ms;
it must never become a repeating 2 ms or 3 ms approximation.

The virtual device still needs a nominal consumption clock for Windows refill
semantics. Prefer a high-resolution one-shot notification armed for the next
actual packet boundary rather than the sample's perpetual 1 ms wake. Round due
times so a completion is never announced before its nominal boundary. On a late
wake, advance to the truthful current position/count, signal that progress and
record missed intervals; do not replay a burst of fictitious completions or
freeze the virtual clock behind wall time. Preserve partial EOS, pause/resume,
STOP resets and presentation-position consistency.

This driver clock is **not a network audio pacer**. PCM export happens on valid
commit; it never waits for this emulated consumption event. Phone scheduling,
QUIC congestion and export-ring occupancy never drive the Windows device clock.

Retain default PortCls serialization initially. Do not enable `UsePositionLock`
as a casual optimization; that changes concurrency with state/property calls
and requires a separate demonstrated need and race audit.
[Serialization contract](https://learn.microsoft.com/en-us/windows-hardware/drivers/audio/usepositionlock).

### 4.2 Period selection and ownership

First qualify the driver's existing legal range. **128 frames at 48 kHz is an
initial target, not a hardcoded supported value or final performance ceiling.**
Query the actual compatible format/mode with `IAudioClient3`. Request a legal
period on a dedicated render client associated with our endpoint. Keep its
stream silent; service it through audio events. Initialize the loopback
comparator through its existing documented API, rather than assuming that
`InitializeSharedAudioStream` supports a LOOPBACK flag combination.

Required states:

- Reference/default period: query and report the actual period.
- Shorter legal period granted: use and report it.
- Engine already at the requested short period: join/retain it and report
  already-active; do not manufacture a new speed-up claim.
- Unsupported period, incompatible format/mode, locked engine, or a different
  granted period: report the exact condition. No silent substitution.

Read back the actual graph period after relevant opens/startup. Release only
owned streams on shutdown; never force another application's period state.
Initially change period only between sessions.
[Period-selection contract](https://learn.microsoft.com/en-us/windows/win32/api/audioclient/nf-audioclient-iaudioclient3-initializesharedaudiostream).

After 128 frames is correct and useful, qualify finer periods on a separate
driver candidate if the software endpoint has no genuine 128-frame processing
constraint. Candidates such as 96 frames/2 ms must be justified by the complete
driver clock, buffer and scheduling contract, then queried at runtime. Changing
an advertised minimum is not an implementation of that capability.
[Driver packet constraints](https://learn.microsoft.com/en-us/windows-hardware/drivers/ddi/ksmedia/ns-ksmedia-_ksaudio_packetsize_constraints2).

## 5. Immediate committed-PCM export

### 5.1 Exact source boundary and stream coverage

The planned publication point is a successfully accepted
`CMiniportWaveRTStream::SetWritePacket`, after packet/state/EOS validation and
before the OS can reuse that accepted packet's cyclic storage. Microsoft defines
this call as notification that valid PCM has been written. Publishing from
`TimerNotifyRT` would wait for virtual consumption and lose the intended benefit.
[Valid-data contract](https://learn.microsoft.com/en-us/windows-hardware/drivers/ddi/portcls/nf-portcls-iminiportwavertoutputstream-setwritepacket).

Copy only the accepted packet's valid range into independently owned export
storage. Handle packet-number wrap, duplicate/late/overrun rejection, partial
and zero-length EOS, and state transitions. An export drop must not cause an
otherwise valid Windows render operation to fail or wait.

Do not assume all six currently advertised system pin instances are one final
mix. First establish the actual shared engine's host stream and processing
mode. The initial supported contract is a single Windows-mixed DEFAULT stream
on the dedicated endpoint. Prove concurrent applications are included. If
multiple independent host streams appear, fail direct export visibly or use
the complete same-endpoint loopback backend. Do not concatenate them, select an
arbitrary stream, or add a second kernel mixer. A narrowed one-host-stream
descriptor may be used only after shared multi-application compatibility is
proved; exclusive/raw/offload coverage stays explicit.

### 5.2 Bounded ownership and efficient user access

Selected optimized handoff: driver-owned fixed packet storage, mapped read-only
into the authorized Rust sender process, with an event and a small validated
acknowledgement/control interface. Do not map the live Windows WaveRT cyclic
buffer into the sender. One bounded PCM copy at accepted commit buys stable
ownership; removing that copy is not worth ambiguous lifetime or torn samples.

Initial developer limits:

- At most eight outstanding packet descriptors.
- At most 960 outstanding PCM frames in total, irrespective of descriptor
  storage allocation: 20 ms at 48 kHz. This is a ceiling, never a fill target.
- At most 960 frames in one export record; the optimized period profile should
  normally produce much smaller records. A larger negotiated packet makes
  this source profile unavailable; never truncate it silently.
- One authorized consumer and one selected source stream per lease.

Allocate and zero dedicated nonpaged media pages before streaming. Kernel-only
state owns slot sequences, bounds and acknowledgement progress. Mapped media
and descriptor pages are read-only/non-executable for the consumer. A producer
copies payload and immutable metadata, then release-publishes a sequence; the
consumer acquire-loads that sequence before reading. It acknowledges only after
copying needed data into its existing bounded packet assembly/QUIC ownership.
Never let quiche retain pointers to a slot after acknowledgement.

A full pool causes an explicit source-frame drop; it never blocks commit or
overwrites an unacknowledged slot. A delayed consumer discards obsolete records
when it resumes, acknowledges them, and retains visible gaps. Bounded storage
does not guarantee that a descheduled process will run before a deadline.

Use one event with drain/recheck semantics. The acknowledgement/rearm operation
must atomically decide whether the ring is empty relative to publication, so
there is no reset-event/lost-wakeup race. Event state is only a wake hint;
authoritative sequences determine whether work exists.

The private PCM interface must not inherit the audio device's broad render ACL.
The broker authorizes the endpoint/lease and supplies a narrowly scoped handle
to the authenticated local sender process. Resolve client PID from the OS pipe
connection, not a client-provided number. Perform user mapping in that process's
context, track that process lifetime, and validate every ACK against published
sequence/generation. No writable kernel descriptors, raw kernel pointers or
unchecked consumer indices.

Keep a separate kernel-owned control snapshot outside the media budget. RUN
clock correlation, STOP/removal, rights changes, format and generation changes
must remain readable and wakeable even when every media descriptor/frame credit
is occupied. Otherwise pre-RUN media waiting for correlation can deadlock the
correlation needed to consume it. Control updates never overwrite active media
slots. Use aligned native atomic loads for read-only mappings; an interlocked
read-modify-write is not a valid consumer read of a read-only page.

Advance accepted commit sequence and source end-frame for every successfully
accepted WaveRT packet, even when export capacity causes a media drop. Recovery
fences snapshot that accepted frontier, not merely the published descriptors.
The next published span retains the resulting source-frame gap and cumulative
drop accounting. Media acknowledgements return only the credits of immutable
published slots; they cannot redefine source progress.

On cleanup/removal: revoke publication, advance/retire the export generation,
cancel waits, acquire producer rundown, unmap in the correct process context,
then free pages. Do not reuse the same mapped page storage for a new phone while
an old process retains access. Rights changes must stop protected-data export
before subsequent publication and retire affected outstanding records.
[Read-only mapping requirements](https://learn.microsoft.com/en-us/windows-hardware/drivers/ddi/wdm/nf-wdm-mmmaplockedpagesspecifycache),
[audio content rights](https://learn.microsoft.com/en-us/windows-hardware/drivers/audio/content-ids-and-content-rights).

If this mapping cannot be made correct, use bounded preposted cancel-safe
direct-I/O reads as an explicitly measured alternative. Do not call it equivalent
performance or surround it with another unconstrained queue.

### 5.3 Remove the redundant user-mode handoff on the direct path

For committed-driver capture, Rust should read the authorized export mapping
directly. Do not add driver ring → C++ SPSC → pipe → Rust queue behind it. Keep
that existing path for the loopback comparator and protected references.

Extend the Windows reactor so one connection owner can wait for source-ready,
socket readiness, shutdown and its existing actual protocol/transmission/expiry
deadline. A documented `WSAEventSelect`/network-event wait is a viable Windows
adapter. Preserve correct `FD_READ`, `FD_WRITE`, event reenabling and WouldBlock
behavior; do not replace the reactor with a 1 ms poll or periodic audio timer.
Keep the existing sealed-ciphertext ownership and plaintext-expiry rules.
[Winsock event semantics](https://learn.microsoft.com/en-us/windows/win32/api/winsock2/nf-winsock2-wsaeventselect).

### 5.4 Format, gain and fidelity

The first acquisition comparison keeps 48 kHz stereo PCM16 and unchanged Android
DSP. The current driver is PCM16; a direct tap does not recover higher-depth
samples already quantized by Windows. Do not claim arbitrary-source bit-perfect
or higher-resolution fidelity.

Verify per-application/session gain, endpoint master gain and mute separately.
The driver's simulated hardware volume currently stores values; copying its
buffer does not prove master attenuation has been applied. Retain existing
Windows-to-phone master-volume mirroring where it is the single intended
authority, and prove it is applied exactly once. If samples are already scaled,
do not mirror the same gain again. Unity-gain acquisition comparison precedes
production gain/mute qualification. Muting must never leave audible old PCM.

Higher-depth end-to-end formats are a separate coordinated fidelity tranche,
not an incidental driver change during the earlier-capture comparison.

## 6. Timing, freshness and authenticated recovery

This is a required part of the source implementation, not optional diagnostics.
The existing single WASAPI QPC field cannot silently acquire a new meaning.

Define a versioned local `SourceSpan`/driver ABI with fixed-width fields:

| Evidence | Meaning |
|---|---|
| Source kind and format identity | WASAPI-loopback or committed-WaveRT; exact rate/channels/sample format |
| Endpoint/stream identity and generation | Which authorized source produced the bytes; retired generations never revive |
| 64-bit original commit sequence | Monotonic valid-commit identity, independent of network sequence and OS 32-bit wrap |
| Original committed first frame/count and fragment offset | Exact source range and valid packet membership |
| Original commit QPC | When that original range was accepted as committed; never refreshed at read/copy/send |
| Source-clock correlation and validity | Frame/QPC mapping for the virtual render timeline; distinct from availability |
| Publication/read/assembly observations | Optional bounded boundary measurements; never substituted for source evidence |

For a transport block spanning commits, preserve the oldest contributing commit
QPC and enough commit-range evidence to enforce recovery. A fragment keeps its
original commit identity. Assembly cannot cross a source gap, epoch, format,
recovery fence or incompatible timing mapping.

Rules at ring consumption, assembly, QUIC admission and every plaintext expiry
boundary:

1. Commit QPC must be genuine, nonzero, monotonic for accepted sequence, and not
   spuriously in the future. Queue residence is bounded from original commit.
2. Preserve the 60 ms maximum stale-source rule where a source timeline is
   valid; also reject after 60 ms since the oldest original commit. Use the
   earlier applicable expiry, not a new 60 ms lease at each handoff.
3. Future *presentation* is distinct from future *commit*. Validate presentation
   lead against actual valid committed-buffer geometry and the qualified clock
   correlation. Keep the first optimized profile within the existing 10 ms
   future-lead ceiling. If it cannot fit, redesign/qualify that profile rather
   than raising a global tolerance.
4. Do not apply the WASAPI second-240-frame-fragment exception to driver data.
   Dispatch validation by explicit source kind.
5. STOP/format/QPC-correlation reset creates a new source context. Pre-RUN commits
   can be held only within the fixed budget; their original commit times remain
   unchanged. A RUN correlation record establishes their timeline. Data without
   a valid required correlation is not guessed or sent as freshly timestamped.
   Pause/removal/restart cannot reuse stale spans under the new generation.

Virtual-device presentation is a nominal Windows timeline, not proof that the
phone has sounded those frames. Never advance driver presentation at commit or
in response to network acknowledgements.
[WaveRT presentation contract](https://learn.microsoft.com/en-us/windows-hardware/drivers/ddi/portcls/nf-portcls-iminiportwavertoutputstream-getoutputstreampresentationposition).

**Recovery change:** current `fresh_anchor.rs` infers fresh capture from WASAPI
timeline QPC. Driver data may be committed early for future nominal playback,
so that inference is insufficient.

On an authenticated recovery request, snapshot a driver commit fence under the
same synchronization that assigns accepted commit sequences. The fence records
generation, last accepted commit sequence and end frame, including commits
already accepted but not yet published. Eligible recovery data must be beyond
that fence, committed after the request, beyond the receiver rejection frontier,
and from the same authorized current context. Drop partially assembled blocks
containing any pre-fence commit. A future presentation timestamp cannot prove a
new commit. Preserve nonce/context binding and existing recovery deadlines.

Do not promise this reveals when an application created its samples; it proves
post-request **driver commitment**, which is the boundary this backend controls.

Keep the old DFWC v2 WASAPI route as an explicit legacy/comparison contract.
Introduce a fail-closed new descriptor/handshake for the new source. Kernel,
source adapter and Rust must agree before PCM interpretation. Windows-local
timing evidence need not change the existing Android audio packet by itself.

## 7. Preserve capture gains through packetization

The current transport assembles 240-frame/5 ms blocks. A 128-frame source cannot
emit its first complete 240-frame block after only one 128-frame arrival.
Therefore record **source-owned time** and **transport-block-ready time**
separately. A shorter engine period is not automatically a 2.67 ms network path.

Use this two-step implementation:

1. Keep the current 240-frame wire contract for the first source comparison,
   to isolate acquisition and preserve Android behavior. Assemble immediately
   when contiguous samples suffice, without a timer, batching target or sleep.
2. If the residual assembly delay materially limits the acquired gain, implement
   negotiated source-aligned variable-frame packets as the next dependency of
   this workstream. This is not accomplished by changing one constant.

For step 2: retain 240 as maximum frames per audio datagram; send valid source
fragments as available instead of waiting to fill that maximum. Negotiate exact
allowed sizes and a packet-rate bound derived from the minimum qualified source
period. Audit parser/configuration, fixed byte-length assumptions, plaintext
identity, reorder storage, source-frontier advancement, concealment, statistics
and native ingress. Reorder/latency limits must remain frame/time bounded, not
accidentally become larger or smaller when packets shrink. Bound descriptor
count independently. A missing packet's frame interval comes from source
positions, not `missing_sequence_count * 240`.

The render callback, sinc quality, drift law, reserve, fades and hardware route
remain fixed in this transport-framing comparison. Old peers must reject an
unnegotiated contract; retain the old matched reference set. Measure increased
packet/CPU/congestion cost before promoting smaller packets.

This dependent packet work is conditional on measured assembly cost. Do not
declare earlier Windows capture complete at a driver boundary if the sender
then loses the entire gain waiting for a full transport block.

## 8. Windows implementation work packages and gates

| Order | Concrete work | Completion gate |
|---|---|---|
| W-E0 | Reconcile actual package/sys identity and source; preserve references and unfinished draft; freeze source descriptor/state/fence contracts | Exact active candidate is identifiable; no installed/source-version assumptions; no premature improvement claim |
| W-E1 | Integrate broker-owned explicit DeskFuse source session, safe teardown and user-selected routing; retain current endpoint loopback as first usable backend | Ordinary application audio from our endpoint reaches the existing phone pipeline; no physical-output dependency or implicit routing |
| W-E2 | Correct fractional driver timing and delayed notifications; implement reviewed legal-period owner/query/readback | Truthful position/count/EOS/state behavior at actual queried short period; concurrent shared apps remain complete |
| W-E3 | Add immediate committed-PCM export, bounded authorized mapping, ownership and recovery-fence contract | Valid accepted bytes only; no torn/reused data, blocking publication, stale-generation acceptance or freshness reset |
| W-E4 | Add direct Rust source adapter and source/socket readiness integration; carry immutable freshness through existing QUIC expiry | No redundant pipe/reader queue on direct path; event-driven progress, bounded pressure, authenticated recovery and fail-closed compatibility |
| W-E5 | Compare default/short periods and loopback/direct backends under matched conditions | Earlier identifiable PCM reaches source owner AND sender; continuity/fidelity and tail behavior meet the acceptance rules below |
| W-E6, if needed | Source-aligned bounded packet framing | Sender retains the source gain without unacceptable packet-rate/CPU/continuity cost |

Required completion/review of the existing period-owner draft: legal and
already-active period states; actual format/mode matching; explicit endpoint
identity; start/stop/error/rundown ownership; event-driven silence service;
granted-period readback; no forced restoration of another client's engine;
and failure-injection checks through the existing Windows tests. Keep its
default path equivalent to the built reference. This work is useful only as
part of acquiring from DeskFuse's own endpoint, not as another physical-speaker
capacity experiment.

W-E1 can establish real own-endpoint audio at its existing period before W-E2,
but cannot be called earlier capture at that point. W-E2 and W-E3 must not be
combined into one unexplained performance result. Each package is independently
reviewable; no general architecture rewrite or unrelated UI work.

Principal files:

- Driver: `windows/driver/DeskFuseAudio/src/minwavertstream.cpp/.h`,
  `minipairs.h`, `speakerwavtable.h`, `adapter.cpp`, plus a small new export
  implementation and shared versioned ABI header; INF/security/package metadata.
- Endpoint control: `windows/audio/DeskFuse.AudioBroker/Program.cs`,
  `DeviceInstance.cs`, `EndpointResolver.cs`; `DeskFuse.Audio` broker contract
  and client. Product default/volume behavior is in
  `windows/agent/FocusWindow.Agent/PhoneSpeakerProvider.cs`.
- Native comparator: `vnext/m1/src/wasapi_capture.cpp` and existing CMake/tests.
  Extract only reusable acquisition/metadata seams needed by this work.
- Sender: new Windows committed-source module beside `wasapi_pipe.rs`;
  `source_age.rs`, `fresh_anchor.rs`, `plaintext_expiry.rs`, `reactor.rs` and
  `src/bin/continuous-quic-lab.rs`.
- Existing qualification/build runner and standalone Lab: add only typed source,
  endpoint/backend/period selection, actual results and package/artifact identity.
  No new dashboard, profiler service or duplicate runner.

### Required engineering checks

Use existing test projects/harnesses and extend them where necessary:

- Rational frame/packet timing at 128 and other legal counts; fractional phase;
  very long runs; 32-bit packet wrap; delayed callbacks; EOS; pause/STOP/resume.
- Publication versus cleanup/removal, full/slow consumer, process death,
  acknowledgement replay/invalid ranges, wake/rearm races, retired mappings.
- Exact source bytes and frame accounting across fragments, gaps, epoch changes,
  100 ms scheduling stalls and ring exhaustion.
- Pre-request commits with deliberately future presentation times cannot satisfy
  recovery; in-progress pre-fence commits remain ineligible after publication.
- Original age survives every queue/assembly/QUIC stage; missing/invalid metadata
  and incompatible binaries fail closed.
- Driver-specific static analysis/Verifier on an approved development target
  before trusting a new kernel package; no installation or signing changes are
  authorized by this planning document.

### Minimum performance evidence, using existing infrastructure

Use a controlled known PCM/marker source through the real render API, with
submission QPC recorded in the existing bounded experiment harness. Ordinary
music and listening establish continuity, not the application-submission time.
Do not build a general recording or telemetry platform.

Correlate the same identifiable sample/range across submission, accepted commit,
user ownership, 240-frame assembly and QUIC admission. Capture observations into
bounded preallocated memory and report after stopping. Count omitted samples;
do not present rate-limited logs as a complete latency distribution.

Compare, independently:

1. Dedicated endpoint at its baseline period, software loopback.
2. Same endpoint/format/backend at the qualified short period.
3. Same endpoint/format/short period, direct committed export.
4. Only then any packetization change, holding the acquisition backend fixed.

Physical speakers are an optional external comparison. Do not compare unrelated
device-frame counters or subtract Windows QPC and Android monotonic clocks.

Report matched p50/p95/p99 and worst observed delay, uncertainty/omissions,
missing/duplicated frames, CPU/wake cost, source gaps, recovery and user listening.
Improvement requires a repeatable positive sender-ready latency gain greater
than measurement uncertainty, with no fidelity regression or unacceptable
continuity/tail regression. A smaller requested period is insufficient. Keep
all descheduling incidents in the full results; a separate explanatory view may
classify them but must not hide them.

If short-period virtual loopback is best, select it. If direct export is better,
select it. If both fail to improve sender-ready timing, stop promoting that
candidate, identify the measured boundary consuming the gain, and consider the
narrow APO/cooperative-source alternative rather than adding queues or relabeling
measurements. Do not repeat an open-ended architecture-review cycle.

## 9. Independent workstream: Android hardware output

This follows usable Windows acquisition and has its own reference. Do not
reimplement the already-built options in section 1.

Retain one native renderer/RenderCore. Experiment order:

1. Shared/default usage/default hardware buffer on the same actual device.
2. Exclusive request only; read actual grant and performance mode. Shared
   fallback is a fallback, never successful exclusive qualification.
3. GAME usage only.
4. Combine exclusive/GAME only after the individual observations justify it.
5. With that route/settings fixed, compare default buffer with two bursts.
   Treat three bursts as a continuity candidate; a smaller number is not
   automatically a better output path.

Record actual route/device ID, sample rate/format, burst size, usable buffer and
capacity, callback distribution, app underruns/reprimes and hardware xruns.
Neither LOW_LATENCY requested nor zero xruns proves a fast or uninterrupted path.
Preserve the existing bracketed AAudio timestamp observations. Accept them only
in STARTED state with monotonic same-generation progress; reset qualification on
route/state changes. Nominal buffer capacity is not current queued latency or
acoustic latency. Optional callback timing measures wall time including
descheduling, not CPU time.

Missing implementation to add after those independent settings are qualified:

- Enumerate supported output devices through the Android application/control
  layer's `AudioManager.getDevices(GET_DEVICES_OUTPUTS)`; AAudio does not enumerate
  them. Use session-local device IDs and current capabilities, not persistent
  assumptions about a Xiaomi model.
- Pass an explicitly selected, validated ID to the AAudio builder. Log the actual
  device opened; report unavailable/fallback conditions clearly.
- Route changes happen on the owner: stop/close/reopen, new output generation,
  callback-owned recovery/fresh anchor and reset timestamp qualification. No
  route operations or blocking control work in the callback.
- Keep 48 kHz/stereo/PCM16 as the first source/output contract. An optional
  unspecified-rate request may be compared only if the actual opened rate is
  still 48 kHz. Supporting a different actual rate needs a separately reviewed
  input/output-rate and sinc-ratio design; do not mislabel frames.
- Automatic hardware-buffer adjustment remains separate from native-network
  reserve adaptation. Initially keep hardware policies session-fixed. A later
  burst-based tuner requires actual xrun evidence, bounded limits and its own
  comparison, with no resampler/drift-controller changes.

Do not migrate wholesale to Oboe or replace the renderer as a shortcut. If a
device-specific AAudio issue justifies an Oboe adapter, reuse the same RenderCore
and compare the adapter alone.
[AAudio configuration](https://developer.android.com/ndk/guides/audio/aaudio/aaudio),
[low-latency guidance](https://developer.android.com/games/sdk/oboe/low-latency-audio).

Hardware-output completion means a measured per-route configuration that
improves output delay without unacceptable continuity/fidelity loss, not merely
another selectable setting. Physical output confirmation still belongs to the
user's Lab-operated listening/comparison pass.

## 10. Independent workstream: native reserve and DeskFuse Game Mode

**Android GAME usage and DeskFuse Game Mode are different controls.** One is an
Android stream attribute; the other is our latency/continuity policy.

The fixed reserve controls already exist. First qualify and expose them as safe
startup policies, with reference 1440 frames and developer values
1200/960/720/480. Reduce from 30 ms to 25 ms first, with Windows source, Android
route, hardware buffer and DSP unchanged. Stop reducing when continuity worsens;
do not run every value mechanically or silently compensate through concealment.

Reserve is raw future source inventory after callback demand. With the current
96-frame future-support guard, reserve 1440 implies about 28 ms nominal playable
remainder, not 30 ms total output delay. Keep priming demand plus reserve and the
follower target consistent. Every callback size must fit reserve/history within
the existing 3840-frame storage; impossible requests remain explicit terminal
unsupported combinations, not a reason to grow the ring.

Keep sinc128, fades96, concealment maximum480, drift ±400 ppm and slew100 ppm/s
unchanged during reserve qualification. Lower reserve changes latency/continuity
risk; it does not improve signal-processing fidelity.

Only after fixed values show a usable envelope, implement optional bounded
adaptation:

1. Disabled by default; choose only among qualified fixed reserve values.
2. Make the recovery/unprimed callback boundary the authority for applying a
   higher target. Reset the follower consistently there. The owner requests a
   versioned policy change; the callback applies/acknowledges it without locks
   or allocation.
3. Initially prohibit decreasing reserve during active playback. A lower target
   needs an explicit restart. Do not skip queued samples or run an aggressive
   second resampling controller to shed inventory.
4. A genuine inventory-starvation recovery may propose one bounded upward step;
   do not repeatedly increase for one outstanding recovery or infer jitter from
   every lost network packet. Keep explicit maximum and applied-reason records.
5. Preserve source frontiers, original age, authenticated recovery, existing
   concealment/fade limits and fixed memory. Stop/return to the explicit reference
   if no qualified target supports the actual callback demand.

Do not feed a moving network-jitter target into the clock follower. At 400 ppm,
shedding 20 ms through drift alone takes about 50 seconds; it is not a responsive
latency-transition mechanism. Hardware xrun-buffer adjustment and native reserve
adaptation remain separate authorities and experiments.

Do not change recovery timeouts, fades or concealment together with reserve. A
specific recovery defect may require correction, but prove it through the
existing actual-core tests and compare that correction independently.

## 11. Reference parameters and final implementation discipline

| Parameter | Retained reference / first implementation policy |
|---|---|
| Production Windows source | Explicit enrolled phone's DeskFuse endpoint; no physical-device fallback |
| Protected historical comparison | Existing matching artifact/reference sets, physical loopback only when explicitly selected |
| Windows backend | Explicit selection; virtual loopback comparator, committed export candidate |
| Engine period | Actual queried default; first short target128 frames only if legal; qualified finer values later |
| PCM contract | 48 kHz stereo PCM16 for this acquisition tranche |
| Network block | 240 frames initially; source-aligned framing only through its explicit dependent gate |
| Export budget | ≤8 descriptors and ≤960 outstanding frames; never a target buffer fill |
| Stale-source ceiling | Existing60 ms plus original-commit residence cap; no timestamp refresh |
| Future presentation | Bounded by validated source geometry; initial optimized profile stays within10 ms |
| Android sharing / usage | Shared / platform default until independently qualified |
| Android hardware buffer | Policy0/platform default; report actual;2/3-burst experiments separate |
| Native raw reserve |1440 frames; developer1200/960/720/480; session-fixed initially |
| Reconstruction | Existing streaming sinc128, with unchanged support/phase rules |
| Fades / concealment |96 frames / maximum480 frames |
| Drift authority | Android output demand; ±400 ppm,100 ppm/s slew |
| Receiver storage | Existing3840 frames; no concealed capacity expansion |
| Adaptation | Off until fixed policies are qualified; increases only at recovery boundary |

These are reference values and bounded developer controls, not universal optimum
claims. Any promoted profile records the actual route, granted modes, format,
driver/package, source backend and measured tradeoff. Preserve a continuity-first
reference and allow an explicitly selected latency preference without changing
resampler quality behind the user's back.

Implementation can proceed at High reasoning using the work packages above.
Return for focused architecture review only if evidence defeats a stated gate:
ambiguous final-mix ownership, invalid clock/commit correlation, export slower
than loopback, unsupported low-period scheduling, or a necessary protocol change
that violates current receiver invariants. Do not burn Ultra on routine edits,
builds or repeated listening prompts.

The user operates listening tests through the existing Lab. Do not launch
playback or silently change routing as part of implementation. Keep Android
hardware experiments and reserve changes out of the Windows-source comparison.
Planning is complete enough to start implementation after the user's go-ahead;
no further general design paper or configuration framework is a prerequisite.

## WIP checkpoint - 2026-10-25

Work in progress, not ready for deployment. The current branch connects the
corrected WaveRT clock, committed-PCM export, broker grants, authenticated native
phone session, direct Rust source, and source/period reactor handling. No driver
installation, endpoint activation, routing change or phone playback was performed.

Remaining before treating this pipeline as complete:

- Resolve the exact endpoint capability lookup: the connected adapter may expose
  the topology filter, while the new timing/source-instance property currently
  belongs to the Wave filter. Keep verification tied to the actual source image.
- Finish the source-buffer stability proof for all legal producer/reuse paths.
  The bounded copy now holds the position lock and requires two notifications;
  synchronous SetWritePacket return alone is not the safety argument.
- Review kernel revocation, process-exit, free-buffer, rundown and section-view
  lifetime paths, including accepted commits in flight and successor sessions.
- Rebuild the latest edits: the dedicated same-thread WASAPI period owner,
  process-handle/nonce broker binding, and latest driver credit/lifetime changes
  postdate some successful builds. Check source/period wakeups and teardown.
- Qualify an authorized corrected driver and the complete own-endpoint-to-phone
  path. Actual mix ownership, granted period, continuity and latency remain
  unproven on hardware; same-endpoint loopback remains the comparator.

Evidence already obtained during this implementation: unsigned WDK driver,
Agent/Broker Release, Windows Rust Release sender and matching Android APK builds;
four M1 CTests, ten focused .NET tests, and 41 Rust library plus six sender tests
passed. These results do not imply that every latest edit was rebuilt or that
physical playback/latency has been qualified. Keep native capture opt-in, explicit
endpoint identity, original timestamps, 60 ms freshness, generation fences and
bounded storage. Next action after this checkpoint is the focused remaining
integration/correctness pass, not unrelated tuning or UI work.
