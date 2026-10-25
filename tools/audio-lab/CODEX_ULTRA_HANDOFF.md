> Historical imported handover, predating native checkpoint 2421b68. Current integration and executable gates are described in README.md and enforced by code; statements below that timing/export/enrollment are unimplemented are superseded.

# Codex Ultra handoff — Audio Lab v0.6 Windows integration

Base native commit audited: `3d6eb2e0fb93316bd08b9f35eb1a58b908065df8` on `experiment/vnext-audio-latency`. The lab work is a separate branch and confined to `tools/audio-lab/`; it does **not** change WASAPI capture, the broker, endpoint ownership, product enrollment, Android binaries or the driver.

## Implemented in the standalone Lab

- MM-1 physical loopback vs own-endpoint loopback vs future WaveRT/direct PCM export cards, with application boundaries, impact hints and honest availability.
- MM-1 `-WindowsEnginePeriod reference|minimum` and `-WindowsCapturePolicy reference|minimum` test staging. A single session may differ from the protected audio reference in at most one factor; it always passes explicit `-EndpointId` and never changes Windows default routing.
- Read-only broker named-pipe reachability probe; **no** broker `create`, `release`, `list`, elevation or driver commands from preflight.
- Independent four-role Release receipt verification by SHA-256: `windows_rust`, `android_rust`, `android_native`, `windows_capture`; receipt capabilities include ABI 8, `windows_endpoint_lease`, and both Windows policy lists. Native PowerShell runner performs the final verification independently.
- `GET /api/windows-integration`: integration metadata, broker availability, SHA-256/capability receipt state and deliberate dedicated-identity block. Results are cached and are not recomputed while audio is running.
- Source-specific runner startup metadata and parsing of native `WASAPI_CONFIG` actual `current_period_frames` and `allocated_frames`, kept distinct from acoustic latency.
- Existing 155-entry Atlas, personal settings drafts, listening notebook and original supported Android/DSP physical experiments retained.

## Deliberately unimplemented/blocked

`server-deskfuse` **cannot be launched from this laboratory**. There is no verified authenticated product enrollment device ID -> `DevicePeerRegistry.Identities(enrolledDeviceId).Speakers` -> allowed QUIC client-certificate binding. A 32-hex-byte string, certificate filename, user-entered label or DER equality to an operator-selected file is not sufficient to establish product enrollment. This must be implemented/reviewed on the trusted native/product side before lifting the gate; do not add a manual peer hash field.

No attempts were made to modify `BrokerPhoneSpeakerProvider.ProvisionAsync`, `PhoneSpeakerDefaultRestorer`, `EndpointBroker`, `DeviceInstance.RemoveAsync`, `EndpointLease`, the virtual audio driver or low-period timing. Their existing generation/lifetime and explicit routing contracts are retained.

## Integration directions

1. Review the separate lab branch against experimental native HEAD; land or cherry-pick only the `tools/audio-lab/` directory. Avoid mixing unrelated native changes while your branch moves.
2. Supply a trusted, narrow enrollment-to-mTLS authorization contract from the production control plane. Specify how the authenticated selected device and DER fingerprint are transmitted securely to `server-deskfuse`; no user-supplied hash and no auto-default routing.
3. Keep `server-deskfuse` authentication-before-capture, `endpoint_lease.rs` broker reply checks and `wasapi_pipe.rs` stop/join/capture-before-lease-release semantics. Do not bypass broker takeover generations.
4. Once a trusted contract is present, replace `source_preflight`'s unconditional dedicated gate, and separately update `build_command`/`Runner.start` to use the authenticated peer selection; require a receipt capability indicating that exact integration. Implement defensive unit tests before enabling the UI Start button.
5. Complete the **native** Windows W-E1/W-E2/W-E3/W-E4 work and actual endpoint qualification separately. This lab does not claim driver readiness, short period attainment or physical speedup.
6. Configure the existing local host using `config.example.json` (explicit MM-1 endpoint ID, Release binary locations, receipt, ADB path, credentials). Do not commit `config.json`, key material, device IDs or physical listening evidence.

## Validation performed here

10 local Python unit/API tests passed; Python bytecode compilation and `node --check` for the new JavaScript passed. Windows broker/runner execution, Android/Xiaomi audio playback, actual browser visual QA, native builds, driver operations and acoustic measurement **were not performed**. They require the user's local Windows environment, installed driver and explicit test authorization.
