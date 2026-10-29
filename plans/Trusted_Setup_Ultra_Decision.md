# Narrow Ultra stop — trusted enrollment bootstrap

**Resolved in planning, 29 October 2026:** use
[Trusted_Enrollment_Bootstrap_High_Handoff.md](Trusted_Enrollment_Bootstrap_High_Handoff.md)
for the settled identities, ceremony, durable commit and ordered implementation.
The original stop below is retained as historical context. No implementation or
build was performed by that resolution.

28 October 2026. Continues the existing v0.6 / Ultra Core High handoff.
The focused M0–M5 media implementation is separate and is implemented in source.
This is not a request to review or redesign the media architecture again.

## Exact continuation boundary

`Ultra_Core_High_Handoff.md`, next High items 2–5, require trusted installer
binding, independently verified phone/host enrollment and the installed-worker
chain before broker v2 activation. Do not wire legacy v1 into that guarantee.

The source provides the protected store, independent phone signer, canonical
operation proofs, retained worker launch and caller binding. Its setup inputs
still depend on facts a real production ceremony must establish:

- `windows/audio/DeskFuse.AudioBroker/BrokerEnrollmentState.cs`:
  `ProtectedBrokerEnrollments` accepts a caller-supplied 32-byte
  `protectedHostIdentity`; `AddFromTrustedSetup` accepts the already-approved
  transcript digest. Its comment correctly says SYSTEM identity proves the
  writer, not human consent.
- `android/.../transport/AndroidBrokerDeviceSigner.kt` accepts the selected host
  identity hash and enrollment ID when preparing the independent signing key.
  It is not a complete host/phone confirmation ceremony.
- The handoff requires independent verification of both the phone signing key
  and selected host identity, and forbids deriving that authority merely from
  the old same-user host or pairing secret. The architectural document states
  the required binding; the inspected handoffs do not select a complete
  production bootstrap/confirmation transcript and its origin.

## Decision needed before High wires authority

Specify the concrete, implementable ceremony:

1. Origin, custody and independently displayed/verified representation of the
   protected host identity. Define how it relates to the existing host identity
   and what explicit trust upgrade preserves or rotates.
2. A versioned canonical enrollment transcript: participants/roles, host and
   phone identities, phone signing key, enrollment ID, scopes, freshness and
   operator confirmation. Define exactly what its stored digest certifies and
   how each side rejects substitution or replay.
3. Trusted local entrypoint and bounded messages/UI steps that establish those
   facts, write the private namespace/configuration records and admit the
   selected installed worker profile. Define cancellation, partial setup,
   restart and key-loss outcomes without exposing a network enrollment writer.

These are trust-boundary choices, not a missing build tool or hardware result.
High must not substitute a source receipt, arbitrary JSON/hash, legacy pairing
secret or an unverified user-host-nominated key for independent approval.

After Ultra settles this small contract, High can implement the trusted setup,
worker/v2 integration and then the existing separate qualification gates.
Do not broaden this into new media machinery, kernel infrastructure or a
replacement for the proven DPI/video/audio paths.

## Work preserved at this stop

The runtime media changes are built; Windows host/native GPU and Rust sender
builds passed. The Android APK includes the updated native ARM64 receiver.
Windows crypto/ownership checks and actual Kotlin interoperability passed;
the affected Android codec-owner/reconnect checks and Rust freshness/egress
checks passed. Physical NVENC behavior, Surface/panel performance, speaker
categories and installed SYSTEM/driver qualification remain separate open gates.
No service/driver install, phone deployment, machine-route change, commit or
push was performed.
