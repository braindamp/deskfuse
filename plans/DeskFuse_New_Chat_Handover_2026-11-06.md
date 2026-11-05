# DeskFuse new-chat handover

Snapshot taken 6 November 2026. Checkout `D:/Coding/DeskFuse/vnext-audio-latency`, branch `experiment/vnext-audio-latency`, remote `braindamp/deskfuse`. The clean local checkout and GitHub branch both pointed to `353abd467a35927d81e1df5505dcdecf80d43796` before this handover was added. Use that commit as the runtime evidence anchor and check the current branch tip and CI before resuming.

## What we are building

DeskFuse is a trusted Windows–Android workspace. A paired Android device should be able to use ordinary Windows apps at phone-appropriate native DPI and physical pixels, interact with their real Windows tasks, and reconnect to the same running work while preserving the local desktop. The broader product includes specialized providers such as ConPTY and CEF, scoped file and clipboard actions, and independent per-device speaker, microphone, camera and input capabilities. The audio work below is one product workstream. Generic apps and their usability, fidelity and performance remain first-class deliverables.

The v0.6 design separates trust and scoped grants, running app identity, presentation, transport, and Android consumers. Narrow Windows brokers own privileged device access. Audio has an independent owner with separate source, delivery and output lifetimes so leaving a visual task or losing a transport need not kill healthy resources. Explicit Stop, revocation and expiry still cut off playback. The F3 goal is a normal Windows speaker feeding Android with safe routing, proven source provenance, real renewals and bounded recovery. Ordinary speaker category/RAW behavior and physical latency have their own qualification gates.

## Read these documents in order

1. **`DeskFuse_Architecture_v0.6_and_Implementation_Programme.md`** is the authoritative architecture and implementation programme. It is outside the Git checkout at `D:/Coding/DeskFuse/v0.4-architecture-design/DeskFuse_Architecture_v0.6_and_Implementation_Programme.md`. Attach the actual file alongside this handover if the new chat cannot access the local workspace. Read B1 for ownership, B7 for audio, C for the product capability register, D1–D4 for milestones and release gates, and E3 for increment-level completion criteria.
2. **Architecture implementation evidence.** Use the status log to track source, build and installed evidence across the wider programme. The product deliverables and one-codebase plan carry earlier requirements and integration history. v0.6 controls where they differ. [Architecture v0.6 implementation status](Architecture_v0.6_Implementation_Status.md) · [Product deliverables](DeskFuse_Product_Deliverables_2026-10-20.md) · [One-codebase integration plan](DeskFuse_One_Codebase_Integration_Implementation_Plan_2026-10-20.md)
3. **Native audio ownership.** Section 8 defines N1–N6. Sections 4–7 cover grant and receipt lifetimes, TLS, capture fallback and terminal cutoff. [Native audio owner boundary](Native_Audio_Owner_Boundary_High_Handoff.md)
4. **Reviewed audio handover decision.** Section 6 defines H1–H5. Sections 2, 4 and 5 cover Android network custody, the native retirement barrier and exact outcome recovery. The implementation status is chronological and predates the latest first-delivery checkpoint in places. [Reviewed High decision](Audio_Handover_Reviewed_High_Decision.md) · [Audio handover implementation history](Audio_Control_Handover_Implementation_Status.md) · [Android cellular custody](Audio_Android_Cellular_Custody.md)
5. **Protocol and candidate details.** Consult these when working near request retention, candidate ownership or pipe routing. The preceding local handoff at `D:/Coding/DeskFuse/v0.4-architecture-design/state_saving/Audio_First_Attachment_Final_Checkpoint_Handoff.md` explains the older checkpoint and its review questions. This document supersedes its next-step list. [N2 bounded request decision](Audio_N2_Bounded_Request_Retention_High_Decision.md) · [Native candidate owner](Audio_Native_Candidate_Owner.md) · [Native broker I/O](Audio_Native_Broker_IO.md)

## Where we are in the plans

F0a covers reproducibility and CI, F0b driver evidence and recovery, F1 authority and input, F2 video efficiency and secure diagnostics, **F3 normal speaker and independent native audio**, F4 generic app presentation, F5 specialized providers and bridges, and F6 broader hardware/network reach and release qualification. These workstreams overlap. The active checkpoint is in F3, with a remaining F0a Windows CI issue. Use the v0.6 programme and status log for other workstreams.

| F3 native milestone | Status at `353abd4` |
| --- | --- |
| N1 — Protected Windows native worker | Rust worker and process-bound broker pipe implemented and exercised offline. Installed qualification remains open. |
| N2 — Concrete broker authority | Parent/source and delivery grants, bounded requests, proof and exact signed receipts implemented and exercised offline. |
| N3 — Native TLS and control | First delivery uses authenticated QUIC/TLS, exact candidate pinning, scoped control and receipt-bound Android readiness. Renewal, replacement and installed execution remain open. |
| N4 — Committed source and egress | First committed source and actual native egress wired in the gated path. The Windows fence opens for fresh PCM only on matching readiness. Replacement and retirement of old egress remain open. |
| N5 — In-process WASAPI fallback | Native capture foundation exists. Product integration and qualification remain open. |
| N6 — Renewal and recovery | Long renewal, retained source across reconnect and output-only recovery remain open. |

| Audio handover milestone | Status at `353abd4` |
| --- | --- |
| H1 — Exact outcome recovery | Core bounded recovery implemented, including same-route exact applied-result retrieval for a lost first receipt. Exercise it in integrated replacement under H4. |
| H2 — Native lifecycle | First attachment and first `OutputReady` implemented and tested offline. Remaining work includes the replacement path and a barrier that retires old egress before replacement can publish. |
| H3 — Independent Android coordinator | `AuthorityPhoneAudio` is an internal entry packaged in the APK. Cellular request and custody groundwork exists. The independent `:audio` service, explicit Start/Stop integration and selected-network socket ownership are next. |
| H4 — Integrated handover | Current-delivery nomination, one just-in-time Supersede, preserved source and renderer, Stop preemption and Wi-Fi/cellular replacement are open. |
| H5 — Qualification | Installed/device/network/audio qualification is open and separately authorized. |

## Last delivered behavior and evidence

- `221ac1c` prevents pinned issuance when native already knows the candidate worker has finished. `7eb3723` supplies the actual Kotlin protocol3 interop JAR to Unified Windows tests. `ef8925f` binds Android receipt windows to current authority and native TLS evidence. `353abd4` adds the gated first output path. [Runtime checkpoint](https://github.com/braindamp/deskfuse/commit/353abd467a35927d81e1df5505dcdecf80d43796)
- Android keeps the exact native TLS connection through setup. `AuthorityPhoneAudio` verifies the exact root-signed receipt and its suspend-inclusive `CLOCK_BOOTTIME` cutoff anchored at original request send. The JNI/Rust owner binds approval to retained connection observations, waits for AAudio `STARTED`, sends existing `OUTPUT`, and requires a correlated response and a still-valid local gate before playback. Stop and renderer loss close the first-delivery gate.
- Windows requires the exact TLS-observed `OUTPUT` to match the current broker projection and takes the existing `CommitFence` before first fresh PCM. The broker rechecks that the applied projection is still current after native publication. Preserve the existing `OUTPUT` identity fields (receipt digest, delivery generation, renderer incarnation and readiness), exact result recovery, immediate withdrawal, and terminal Stop/expiry ordering.
- The previous session built the Windows Rust release worker, published the standalone Windows broker with MSVC and `deskfuse-source-control.dll`, and built an ARM64 Android debug APK. Focused offline checks exercised real TLS loopback, readiness/withdrawal/Stop and first fresh PCM, plus the actual C#/Kotlin enrollment and receipt interop. The local APK was produced at `D:/Coding/DeskFuse/vnext-audio-latency/android/app/build/outputs/apk/debug/app-debug.apk`.
- Production activation is gated. The current Android `SpeakerService` uses the legacy path and has no `:audio` declaration. The packaged authority entry and offline checks establish a source checkpoint. Physical speaker routing, AAudio behavior on a handset, continuity and latency remain unqualified.

### Hosted CI at the snapshot

- **Unified Windows is still red.** `:protocol3:interopJar` completed, the standalone broker published, and Windows host tests passed (566 passed, 11 skipped). The later `maintenance-owner-publish` invocation of `powershell.exe -NoProfile -File tools/build-driver-test-owner.ps1` failed when `Get-FileHash` could not be resolved. The Windows build receipt subsequently reported the failed maintenance-owner check and missing `out/unified-windows`. Diagnose the cmdlet/module environment in that exact invocation before choosing a fix. [Windows job and log](https://github.com/braindamp/deskfuse/actions/runs/42131241080/job/127608097066)
- Unified Android APK/tests and source/provenance checks passed. The targeted Windows broker and Windows/Linux/both macOS Rust jobs passed. The Android AAudio and native AAudio/Rust pair jobs passed. [Unified CI](https://github.com/braindamp/deskfuse/actions/runs/42131241080) · [Targeted Rust and broker](https://github.com/braindamp/deskfuse/actions/runs/42131241357) · [Android native pair](https://github.com/braindamp/deskfuse/actions/runs/42131241223) · [Android AAudio](https://github.com/braindamp/deskfuse/actions/runs/42131241086)

## Resume from here

1. **Clear the Unified Windows build receipt.** Check the latest hosted results, then address the maintenance-owner publish failure in a focused change. Trace `.github/workflows/dpr-challenge-ci.yml` and `tools/build-driver-test-owner.ps1`. Exercise the affected publish and receipt checks and confirm a green hosted Windows receipt. Keep the already successful interop and standalone broker evidence.
2. **Start H3 with the first-delivery service integration.** Reuse `android/app/src/main/java/com/focuswindow/client/transport/AuthorityPhoneAudio.kt`, `AudioCellularRequest.kt` and `AudioCellularCustody.kt` in the independent Android `:audio` owner. Wire an explicit Start/Stop path and one owned lifecycle for the selected Network, socket and callbacks. Bind the socket before connect and use selected-network DNS where applicable. Keep trust and receipt verification inside the owner. Preserve healthy playback across workspace or default-network events and keep the legacy development path distinct. Cover Stop, owner death and stale callbacks before claiming the integration. [H3 policy and acceptance](Audio_Handover_Reviewed_High_Decision.md#6-high-implementation-order-and-acceptance)
3. **Finish the remaining H2 barrier before H4.** Demonstrate retirement of old paced/blocked egress, stale-close isolation and fail-closed transfer. Then integrate one exact Supersede and path replacement with a retained source and renderer under the accepted plan. A first device smoke test and full H5 measurements each require separate authorization and identified artifacts.

## Working instructions carried forward

- The user’s standing instruction is **always commit and push completed changes**. Reuse the current checkout and branch, keep commits scoped and allow normal push-triggered CI. Keep this work on the existing branch.
- Build under the accepted High plan. The user asked to implement without another review round. If evidence forces a change to an accepted authority boundary, wire contract, ownership or terminal ordering, isolate the exact decision for Extra High before changing it. Run focused checks on affected paths and use existing evidence for unaffected checks.
- Keep production activation gated. Installed drivers/services, ACLs, signing policy, default audio routing, Android/device changes and physical qualification require separate authorization. Report source, build, installed and physical results independently.

### Paste into the new chat

> Read this handover and the actual `DeskFuse_Architecture_v0.6_and_Implementation_Programme.md`. Read the linked N and H milestone plans. Continue DeskFuse on `experiment/vnext-audio-latency`. We are in F3, with the gated first-delivery checkpoint implemented under H2 and H3 service integration next. Check the current branch and CI, address the Unified Windows maintenance-owner publish blocker, then continue the first H3 slice within the accepted boundaries. Keep activation gated. Commit and push each completed change.
