> Historical imported handover, predating native checkpoint 2421b68. Current integration and executable gates are described in README.md and enforced by code; statements below that timing/export/enrollment are unimplemented are superseded.

# DeskFuse Audio Lab v0.7 — Windows early-capture website preparation

**Basis:** independently maintained website files from Audio Lab v0.6, previously staged in `tools/audio-lab/` on PR #31; audited native checkpoint `experiment/vnext-audio-latency` commit `3d6eb2e`. No native driver/session/security code is changed in this standalone package. No actual Windows device, driver, phone, or enrollment state was observed.

## Scope completed

- Add a **W0 MM-1 / W1 DeskFuse endpoint loopback / W2 accepted-PCM direct export** capture architecture selector and side-by-side source-dependency narrative. W2 is design-only; W1 is blocked pending trusted enrollment/mTLS link and own-endpoint qualification.
- Add **five explicit implementation gates**: own-endpoint evidence (W-E1), corrected fractional driver timing and installed-image identity (W-E2), valid SetWritePacket export (W-E3), bounded direct Rust acquisition and commit-based freshness/recovery (W-E4), and DevicePeerRegistry-to-mTLS identity binding.
- Add a side-by-side **measurement provenance comparison**, with unknown values left blank. Existing native period and pre-QUIC gap counters are surfaced only if an actual MM-1 session record contains them. Commitment timestamp, sender-ready timestamp, QPC correlation, source-fence evidence and acoustic delay remain unmeasured.
- Add a **planning-only JSON export** of the selected architecture, its dependency status and measurement schema, with no private keys, peer hashes, claims of hardware readiness or execution authorization.
- Add six **Atlas registry options/readouts** (155 → 161) for timing identity, own-endpoint activation, capture backend, export credits, commit-to-sender delta and freshness policy; no new option is marked `physical`.
- **Close the unsafe path:** The local bridge rejects `-WindowsEnginePeriod minimum` even if its build receipt advertises that policy. Corrected, identified **installed** driver timing must be verified and a reviewed trusted attestation contract added before that policy is made runnable. This is enforced in `bridge.build_command()` and `integration.source_preflight()`, not just CSS or a disabled HTML control.
- Preserve the **existing** native reference MM-1 runner, Android selections, one-factor limit, Release artifact role/hash verification, broker read-only probe, localhost and same-origin constraints. Dedicated-source launch remains blocked, not converted into a manually entered hash.

## Files changed vs v0.6

`index.html`, `windows-lab.css`, `windows-lab.js`, `atlas-controls.json`, regenerated `atlas-data.js`, `bridge.py`, `integration.py`, `live.js`, `tests/test_integration.py`, plus `tests/test_windows_v07.py`, `V0_7_CAPTURE_ROADMAP_HANDOFF.md`, and `README.md`.

## Codex Ultra integration guidance

1. **Do not apply this standalone package blindly** over concurrent PR #31 or Ultra's changes. Inspect the branch first, then apply only the changed lab files after merging/reconciling the base. No native files should be modified by this package.
2. The gate states are **fixed to the 3d6eb2e audit checkpoint**, not inferred from a new Rust/driver commit. Upgrade those statuses only after confirming implementation and artifact/device evidence separately.
3. To safely unlock a short-period physical test, implement an authoritative check binding the corrected driver image to its installed/active instance **and** actual negotiated engine-period/format support. A capability claim inside `audio-pair-build.json` alone cannot unlock it. Update both `bridge.build_command` and `integration.source_preflight` with a reviewed contract and tests.
4. To unlock W1, supply an authenticated control-plane handoff that binds `DevicePeerRegistry.Endpoints.Speakers` to the allowed QUIC client's public certificate, then preserve the existing broker lease and capture-before-release ordering.
5. To implement W2, correct WaveRT fractional frame timing; export only accepted/valid committed PCM via bounded ownership and credits; add Rust source consumption preserving original commit age, generation/fences and existing 60 ms freshness policy. Compare against W1 at the **same actual endpoint/period/format**. The website does not claim these features exist.
6. The standalone lab's physical runner cannot be tested in this Linux sandbox; its Python unit tests and Chromium visual/interaction preview passed. Physical listening evidence has not been generated.

## Starting the standalone website

See `README.md`. Extract the ZIP outside any active Git checkout or reconcile changes intentionally; configure `config.json` locally from `config.example.json`; run `start-lab.ps1` on Windows. Do not put credentials in GitHub or grant test permission merely by selecting W1/W2 in the UI.
