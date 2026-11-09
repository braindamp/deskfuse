# DeskFuse handover for a new chat

**Snapshot date** 9 November 2026
**Repository** `D:/Coding/DeskFuse/vnext-audio-latency`
**Branch** `main`
**Head at snapshot** `4185cff` (`Warm one alternate Android audio route during trusted playback`)

This note records the repository and audio work as reported or verified in the preceding conversation. Confirm `git status`, `git log`, and hosted checks before making new changes.

## Start here

1. Read the authoritative v0.6 programme at `D:/Coding/DeskFuse/v0.4-architecture-design/DeskFuse_Architecture_v0.6_and_Implementation_Programme.md`. The phase descriptions F0a–F6 are in section D1.
2. Read [`Architecture_v0.6_Implementation_Status.md`](Architecture_v0.6_Implementation_Status.md) for historical implementation records and qualification gaps. Some later audio changes described below postdate this log.
3. Read [`Audio_H2_H4_H5_Execution_Plan.md`](Audio_H2_H4_H5_Execution_Plan.md) and [`Audio_Handover_Reviewed_High_Decision.md`](Audio_Handover_Reviewed_High_Decision.md) for the current audio sequence and accepted handover design.
4. Consult [`Native_Audio_Owner_Boundary_High_Handoff.md`](Native_Audio_Owner_Boundary_High_Handoff.md), [`Audio_N2_Bounded_Request_Retention_High_Decision.md`](Audio_N2_Bounded_Request_Retention_High_Decision.md), and [`Audio_Native_Candidate_Owner.md`](Audio_Native_Candidate_Owner.md) for the relevant native-owner and request-retention decisions.

[`DeskFuse_New_Chat_Handover_2026-11-06.md`](DeskFuse_New_Chat_Handover_2026-11-06.md) is an earlier snapshot. Its resume instructions and CI status predate the commits listed here.

## Repository and preserved work

- The active audio work is on `main`. At the snapshot, the working tree was clean and `main` matched `origin/main` at `4185cff`.
- `experiment/vnext-audio-latency` remains at `08ee965`. Its history is an ancestor of `main`, so the audio work from that branch was preserved in `main`.
- The audio source changes were committed and pushed to `main`. The latest audio commits are:

| Commit | Delivered source change |
|---|---|
| `ad8084b` | Added the gated independent Android first-delivery service. |
| `306d4ca` | Added the Windows old-egress retirement barrier before replacement acknowledgement. |
| `61b54b3` | Added same-route trusted audio renewal while retaining playback. |
| `3214772` | Added an opt-in Windows handover lane with pinned incumbent and target checks. |
| `4185cff` | Added debug Android alternate-route warm-up during trusted playback. |

Other changes in the preceding tranche include first-output readiness (`353abd4`), receipt and TLS evidence binding (`ef8925f`), maintenance bundle hashing (`a5e2e30`), and the H2/H4/H5 execution plan (`60f207b`).

## What is implemented

- **Windows authority and worker source:** bounded control, protected enrollment and broker components, source ownership, the first-delivery path, output readiness, and a gated candidate replacement path exist in source.
- **Android trusted audio source:** `AuthorityAudioService` runs in the separate `:audio` process. It owns enrollment handoff, selected-network sockets, AAudio playback, Stop, and same-route renewal. The debug UI exposes explicit Start and Stop.
- **Android candidate warm-up:** while trusted playback is active, the debug UI can open one candidate on an alternate route. The candidate checks a source descriptor and TLS observation, then retains its native connection during a bounded warm interval. Candidate work does not issue the Supersede operation.
- **Video memory path:** prior source work moved owned encoded buffers into the ordered encrypted writer. The status log describes synthetic loopback evidence for reduced managed allocation. No physical video performance conclusion follows from that comparison.
- **CI and maintenance:** the maintenance-owner publish blocker was addressed in `a5e2e30`. The Windows build, Android pair, and Rust workflows passed for `4185cff` in the hosted checks inspected on 6 November.

## What remains open

- The normal session still starts `SpeakerService`. The new `AuthorityAudioService` entry and its controls are gated by `BuildConfig.DEBUG`.
- There is no completed Android H4 switch. The candidate must be pinned to the exact retained request, the proof must commit through the incumbent, the signed receipt must be recovered on the target if the incumbent reply is lost, and the candidate socket and network lease must transfer into the running native audio owner. Keep the same AAudio renderer, source position, and accepted/presented floors.
- The Windows handover lane is separately gated. `BrokerProtectedAudioAcquisition.OpenAsync` accepts the handover option, but no call site was found in the checkout during this conversation. Verify activation wiring before treating the Windows host as handover-enabled.
- Exact H4 acceptance includes Stop during cutover, target readiness after the matching receipt and native barrier, old-route late-packet rejection, one source and renderer, and a single positive request in flight.
- No handset first-delivery or handover result, installed Windows/System qualification, physical acoustic latency result, prolonged renewal result, or F6 release qualification was produced in this conversation.
- Transport-loss recovery and automatic switching follow explicit healthy-incumbent switching in the execution plan. They remain open along with H5 device and physical qualification.

## Phase position

| Programme phase | Recorded position |
|---|---|
| **F0a — Reproducible source truth** | Current source and hosted checks passed for the snapshot commit. |
| **F0b — Driver evidence and recovery** | Recovery and maintenance source work exists. Installed driver, trusted SYSTEM, signed-package, and physical evidence remain open. |
| **F1 — Authority and input correctness** | Broker, enrollment, and input-safety foundations exist. Full integration and qualification remain open. |
| **F2 — Remove work and preserve timing truth** | Owned-buffer work and a synthetic allocation comparison exist. Full timing, diagnostic, and physical-performance qualification remain open. |
| **F3 — Normal speaker and independent native audio** | Active phase. Trusted first delivery and same-route renewal exist in gated source. Integrated H4 switching and handset qualification remain open. |
| **F4 — Complete generic presentation** | Not the active workstream in this audio continuation. Programme exit evidence remains open. |
| **F5 — Specialized providers and bridges** | Not the active workstream in this audio continuation. Programme exit evidence remains open. |
| **F6 — Broader product and release qualification** | Later programme phase. Broader hardware, network, sustained-use, and distribution qualification remain open. |

These are source and qualification statuses, not a claim that a release milestone has passed.

## Verification recorded in the conversation

- Windows Rust audio library suite: 114 passed.
- Focused .NET audio authority and listener protocol suite: 65 passed.
- Android ARM64 debug APK build succeeded.
- Focused Android audio and cellular custody tests: 12 passed.
- Hosted Unified Windows, M2a Rust, and CP-001 Android pair workflows passed for `4185cff`.
- No device installation or physical audio measurement was performed.

## Working instructions carried forward

- Continue on the existing `main` branch. Do not create a branch for this work.
- Commit and push each completed task to `main`.
- Keep audio production activation gated until its required qualification is complete.
- Keep changes focused on the next working product path. Tests are secondary to the code changes, with focused existing checks and normal hosted workflows used to verify commits.

## Immediate source path

Continue with **H4 explicit switching with a healthy incumbent**, following section 4.A of [`Audio_H2_H4_H5_Execution_Plan.md`](Audio_H2_H4_H5_Execution_Plan.md). The key source files are:

- Android owner: `android/app/src/main/java/com/focuswindow/client/transport/AuthorityPhoneAudio.kt`
- Android service and switch UI: `android/app/src/main/java/com/focuswindow/client/transport/AuthorityAudioService.kt`, `AuthorityAudioControl.kt`, and `android/app/src/main/java/com/focuswindow/client/ui/TrustedEnrollmentDialog.kt`
- Android network custody: `android/app/src/main/java/com/focuswindow/client/transport/AuthorityAudioRoutes.kt`, `AudioCellularRequest.kt`, and `AudioCellularCustody.kt`
- Android native run owner: `vnext/m2a/quiche-lab/src/phone_audio.rs`
- Windows worker handover: `vnext/m2a/quiche-lab/src/broker_control/windows/runtime.rs`
- Windows protected authority and pinned replacement validation: `windows/audio/DeskFuse.AudioBroker/BrokerAudioControlDispatcher.cs`, `BrokerAudioOwner.cs`, and `BrokerProtectedAudioAcquisition.cs`
