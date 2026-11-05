# ChatGPT implementation task: H2a native runtime bootstrap and listener ownership

Prepared 5 November 2026. **Implement code, not another review or planning report.**
Source baseline: `2ed1bfc96fca32ce1f4c15f20a8ee5a790107d49` on
`experiment/vnext-audio-latency`, repository `braindamp/deskfuse`.
Work branch: `implementation/chatgpt-h2a-native-runtime` (prepared by Codex).

This task decomposes the existing Ultra-approved H2 integration work into a
bounded first implementation slice. It selects no new architecture or operating
policy. It does not finish all of H2. Use GitHub exclusively for project context;
no uploads or local Windows folder access are required for reading the inputs.

## Access and delivery

Read the work branch and verify it descends from the source baseline. Read this
entire task and the relevant actual source before editing. Inspect applicable
AGENTS.md instructions if any. Confirm tool capabilities with actual reads:
repository access is not evidence of write access or a compiler environment.

Implement on the work branch. If GitHub write tools are available, commit the
changes there and open a draft PR targeting `experiment/vnext-audio-latency`.
Do not merge, force-push, alter the target branch, install services or deploy.
Use a coherent commit with a concrete description of behavior and validation.
Recheck branch HEAD before publication and preserve concurrent changes.

If tools provide repository reads but cannot write, still IMPLEMENT: return one
complete, applicable unified git diff, with full content for new files, plus the
exact baseline and a list of changed paths. No pseudocode, omitted sections,
ellipsis, TODO-only implementation or instructions asking Codex to write the code.
Do not claim a branch, commit, PR or test execution that did not actually happen.

If compilation/execution is unavailable, add the meaningful tests as source and
explicitly mark them NOT RUN. A source-complete unvalidated patch is acceptable
for local verification by Codex; an unimplemented plan is not the deliverable.

## Read in this order

All paths are repository-relative:

1. `plans/Audio_Control_Handover_Implementation_Status.md` (latest implemented state).
2. `plans/Audio_Handover_Reviewed_High_Decision.md` (accepted H2–H5 decisions).
3. `plans/Audio_Stop_Game_And_Handover_High_Handoff.md` (Stop/Game and negative lane).
4. `plans/Audio_Native_Control_RPC.md` (existing ConfigureListener/ACK encoding).
5. `plans/Audio_Native_Broker_IO.md` and `plans/Audio_Native_Candidate_Owner.md`.
6. Relevant ownership/bootstrap sections of
   `plans/Native_Audio_Owner_Boundary_High_Handoff.md` and
   `plans/Native_Worker_Thread_Launch_High_Decision.md`.

Inspect at least:

- `vnext/m2a/quiche-lab/src/bin/deskfuse-audio-worker.rs`
- `vnext/m2a/quiche-lab/src/broker_control.rs`
- `vnext/m2a/quiche-lab/src/broker_control/windows.rs`
- `vnext/m2a/quiche-lab/src/broker_control/windows/channel.rs`
- `vnext/m2a/quiche-lab/src/audio_authority.rs`
- `vnext/m2a/quiche-lab/src/audio_listener_config.rs`
- `vnext/m2a/quiche-lab/src/reactor.rs`
- `vnext/m2a/quiche-lab/src/lib.rs` and `Cargo.toml`
- `windows/audio/DeskFuse.AudioBroker/AudioWorkerControl.cs`
- `windows/audio/DeskFuse.AudioBroker/AudioWorkerControlPump.cs`
- `windows/audio/DeskFuse.AudioBroker/AudioListenerProtocol.cs`
- Existing synthetic pipe tests in `channel.rs`, listener tests in
  `audio_listener_config.rs`, and broker control/listener tests under
  `windows/agent/FocusWindow.Tests/`.

Fetch additional dependencies by exact path from GitHub when needed. Historical
documents contain older test counts and statements; reconcile them against the
latest status and actual source. Do not infer implemented behavior from a plan.

## Concrete implementation scope

The production Windows `broker_control::windows::run` still uses synchronous
`Control::read/write` and only processes authority projections. The retained
`BrokerControlClient`, listener configuration codec/bind helper and broker ACK
handling are already implemented. Connect these existing parts into the actual
native entrypoint rather than adding another unused parallel implementation.

1. Use `BrokerControlClient::connect` for production bootstrap. Preserve every
   SYSTEM/descriptor/image/incarnation/HELLO check. Keep exactly one post-bootstrap
   pipe reader and one ordered writer, owned by that client. Never reconnect an
   established channel or expose the test bootstrap factory in release builds.
2. Retain one immutable listener configuration and its actual socket in the
   serialized native runtime. Validate the existing codec and exact worker/source
   relationship before bind. Cross-check any already retained authority source;
   later grants must likewise match the configured source. Configuration may
   precede the first positive authority grant and confers no source privilege.
3. Bind once using the existing helper. Return the existing ListenerConfigured
   ACK with exact configuration hash and actual assigned interface/port. Exact
   configuration retries return the retained original ACK, without another bind
   or a moved deadline. Conflicting configuration or source substitution is
   terminal. Add no wire fields, operating defaults or wildcard bind fallback.
4. Preserve the existing authority projection validation, boot-clock cutoffs,
   revisions and exact byte acknowledgements. Listener setup/ACK cannot establish
   source_observed, delivery_observed, output readiness or physical rundown.
5. Integrate event-driven waiting on the client's control/terminal signals and
   applicable authority deadline. Bound work per turn and check terminal/cutoff
   first. No polling/sleep loop, competing pipe read or unbounded pending queue.
   Queued configuration must not bind after a terminal fence wins.
6. Retain bounded outgoing ACK work through actual write completion. A successful
   `try_write` means queue admission, not completed I/O. Handle full capacity by
   retained event-driven retry. Do not enqueue an ACK and immediately cancel it
   through Drop/stop_and_join while reporting success. If a completion primitive
   is needed, extend the existing client narrowly, preserving FIFO, actual I/O
   custody, separate negative capacity and failure propagation. Terminal denial
   must precede any bounded control-write completion wait; failure must cancel
   AND join real operations, not abandon them or wait indefinitely.
7. On Stop, closed authority, expiry, malformed trusted control or service death,
   prevent later setup/publication, retire listener custody, and join actual pipe
   work before releasing handles. Preserve fail-closed error handling; no ACK is
   a claim that kernel/source/endpoint rundown completed.

Prefer a small concrete runtime owner. Add no generic actor/resource framework
and do not reformat unrelated minified/legacy source files wholesale.

## Deliberately pending after this slice

This slice establishes actual runtime bootstrap/configuration ownership. It does
not advertise a working media listener or complete handover. The subsequent
already-planned H2 work must add the sole UDP receive/routing owner, bounded
candidate dispatch, global RPC forwarding, nomination/pin/transfer, actual source
preparation and old-egress/assembly/ledger retirement barrier. Do not spawn a UDP
reader in this slice that a later source owner would have to race or replace.

Do not implement positive source/media activation, fake preparation observations,
phone receipt installation, H3 coordinator, H4 full integration, new modes, relay,
driver ABI changes or physical qualification. Keep `Program.cs --authority`
refused. Preserve the existing source timing, 60 ms gate, PCM/wire contracts,
renderer/source rejection floors and protected thread-launch policy.

## Required focused tests

Use actual synthetic local Windows pipes and loopback binds where supported,
with no installed SYSTEM authority, audio endpoint, driver or device operations.
Preserve release authentication: any synthetic bootstrap injection is cfg(test).

- Production run dispatch uses the existing client/sole-reader composition.
- Configuration opens exactly one loopback socket and ACKs its actual endpoint;
  exact retry preserves it, conflicting bytes fail without a second bind.
- Wrong worker/source/mode/malformed configuration fails before socket action,
  including source substitution relative to a prior or later authority grant.
- Valid projections still ACK exactly and retain revision/deadline semantics.
- Stop/closed grant and queued listener configuration cannot resurrect setup;
  authority cutoff/service death terminate an otherwise idle wait without polls.
- Blocked writer/cancellation retains actual ACK buffers, operations and handles
  until completion or joined cancellation; queue acceptance is never success.
- Existing reserved-negative capacity and terminal-wake behavior still hold.

Run appropriate checks if the environment supports them. Typical local commands:

    cargo test --manifest-path vnext/m2a/quiche-lab/Cargo.toml --locked --offline --lib
    powershell -File tools/build-audio-worker.ps1

Windows tests require an actual Windows execution environment/toolchain. Existing
DLL export checks may require DESKFUSE_LOOPBACK_TEST_DLL to point to the staged
DLL; do not invent a pass or silently discard those checks. If only another OS
is available, report Windows checks as NOT RUN. No new dependency or lockfile
churn should be necessary for this task.

## Finish

Update `plans/Audio_Control_Handover_Implementation_Status.md` with exactly what
was implemented, tests actually executed or NOT RUN, and what remains. Do not
overwrite previous checkpoints or claim H2/N3/physical completion. Keep the
native broker-I/O/RPC component docs consistent if their runtime status changes.

Return the implementation branch/commit/draft PR (or the complete unified diff),
changed-file summary, validation evidence and remaining integration obligations.
Stop after this slice. If an actual unresolved architecture/security-policy
decision prevents correctness, report the precise conflict and stop dependent
implementation; do not silently redesign accepted contracts.
