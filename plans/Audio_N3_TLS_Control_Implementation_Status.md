# N3 native TLS/control — foundation checkpoint

29 October 2026, High. **N3 is partial, production authority disabled.**
The focused [Ultra design](Audio_Stop_Game_And_Handover_High_Handoff.md) now
selects negative-only source Stop, Game-only current mode admission and the
[reviewed cellular handover](Audio_Handover_Reviewed_High_Decision.md). All three
designs are selected for High; their implementation remains open. The original
v0.6 programme, native ownership
handoff and accepted N2 retention decision remain authoritative.

## Completed this iteration

- Committed and pushed N2 plus independent N5 foundations as
  `d57d7344f8535f4768b1894a672b290f5a805bc2`.
- `audio_tls.rs`: exact existing quiche 0.30.0 / Boring 5.3.0 backend, direct
  locked dependencies, delivery-local random P-256 keys and self-signed role
  certificates constructed in memory. No key path/import/export API. Bounded
  DER/SPKI, self-signature, non-CA/digital-signature/role-EKU profile checks use
  library APIs, including narrow read-only X.509 FFI. Mutual certificate presence
  and `df-audio-authority-2` are required. No early-data/session adoption API.
  Certificate lifetime bounds its profile, not runtime audio authority.
- `audio_control.rs`: bounded 8 KiB framing, one partial record, nonzero exact
  native connection/certificate/nonce observations; strict canonical source and
  delivery parsing, including format, SID, full request identity, ALPN and exact
  destination policy. This is an internal foundation, not a frozen or integrated
  public control protocol.
- `audio_transport.rs`: consumes each TLS identity into one connection; extracts
  actual TLS hashes; starts quarantined with a provisional absolute cutoff.
  One client-opened control stream, eight pending requests, bounded write/read
  storage and matching response kind/identity; a public INFO opens stream 0
  before the native observation can be published. Wrong destination paths are
  ignored; authority is never inferred from a CID or address. The separate
  trusted-native installation boundary checks source/binding/certificate/nonce,
  boot, revision and cutoff. Invalid/stale installation is terminal. Expiry and
  retirement clear pending control/egress state. There is no media-send API.
- `audio-transport-v1.json`: coherent canonical source, observed binding,
  delivery and control vector independently reproduced by the shipping C#
  encoders and consumed by Rust parsers/binding checks.

Identity construction and handshakes are cold setup work. Production integration
must place that work on a bounded setup owner, keeping crypto, signing and
descriptor parsing outside audio processing and short broker authority locks.
No source timestamp, PCM queue, capture adapter, packet format or renderer was
changed. No new measured latency/CPU/power claim is made.

## Verification actually run

- **60 native Rust library tests passed**, zero failures/skips. Includes actual
  in-memory mutual quiche TLS, fresh distinct identities, wrong-role/missing
  client/legacy-ALPN rejection, quarantined control, exact response correlation,
  request bound, provisional expiry, fixed route, trusted binding installation,
  stale terminal rejection, 13 context substitutions, descriptor truncation and
  trailing-byte rejection, and fragmented/oversized control records.
  Log: `out/n3-tls-checks/native-library-tests.log`.
- **One C# canonical interoperability test passed**, zero failures/skips. The
  fixture is reproduced through real `AudioAuthorityProtocol` encoders and
  SHA-256 digests, rather than duplicated expected-hash constants.
  Result: `out/n3-tls-checks/n3-canonical-binding.trx`.
- Windows fixed worker x64 Release and installed-profile DLL build/staging
  passed; staging is not installation/approval.
- Android `:app:compileDebugKotlin --offline` passed with an actual ARM64 native
  library rebuild. Six pre-existing lab/conditional-code warnings remain;
  none originate in the three new modules.

Build/test receipts are local under `out/n3-tls-checks`; the final source/artifact
hash receipt records this iteration's inputs separately from the N2 checkpoint.
All TLS exchange tests are in memory. No service or audio device was opened.

## Explicit remaining work

`broker_control/windows.rs::run` still processes only the existing N1/N2 pipe
projection. The broker does not dispatch these new controls and has no central
N3 receive demultiplexer yet. No production TLS listener, candidate scheduler,
rate limiter, source-open action, device-proof exchange, receipt/readiness flow
or Android native-owner/JNI integration is activated by these modules. The
phone wrapper remains quarantined; approved-phone control/media is N6 wiring.

The numeric Stop control framing does not define or implement source-release
authentication. Implement the selected contract in the focused Ultra handoff before
integrating network lifecycle dispatch. Existing internal Stop/revoke/expiry
fences remain intact. N4 actual source/egress/cutoff/rundown, N5 source adapter
wiring, N6 independent Android renewal/recovery, installed qualification and
physical performance/isolation gates all remain open. `Program.cs` continues to
refuse production authority.
