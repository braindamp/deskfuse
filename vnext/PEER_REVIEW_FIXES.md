# Recovery and plaintext ownership correction

Base: `809537e33224216c1726f6846c042cd24af1c8b7`.

## Corrected contracts

- A bounded atomic ownership handoff separates producer publication from
  callback recovery. A callback never spins or waits for a producer; it emits
  defined silence while a pending reset cannot yet claim ownership.
- Recovery discards the published prefix and releases its storage before
  acknowledging completion. Active playout identity is checked before output.
- The new ABI v5 PCM publication entry point requires the exact recovery ticket
  authorized by an authenticated fresh-anchor exchange. The callback cannot
  complete another recovery during guarded publication. Diagnostic snapshots
  have no control authority and may still be dropped safely.
- Anchor replies and media authorization both enforce the original 1.2-second
  deadline before mutating/completing the exchange, including after owner stalls.
- Plaintext maintenance runs even while sealed output is paced/blocked.
  Metadata is reserved before quiche admission, rolled back on rejection, and
  reconciled independently of construction. Full queues cause accounted drops.
- Original-QPC plaintext expiry is an independent one-shot wake obligation.
  The sealed slot is immutable across WouldBlock; no second packet is built
  behind it. Diagnostic post-seal policy A remains unchanged.

## Interface and deployment

Rebuild the Android Rust executable and native shared library together for
ABI v5. Earlier library versions are rejected before struct access. Historical
entry points and statistics/snapshot record layouts remain unchanged; the
production receiver uses only guarded v5 publication. This change does not
deploy binaries, change installed APKs/drivers, or move the audible reference.

## Regression evidence

Host tests exercise the same RenderCore used by AAudio, with test-only pauses
at publication commit and recovery tail release. Coverage includes an in-flight
producer during underrun/reset, active-generation rejection in both render
modes, exact authorization tickets, and mixed callback sizes during concurrent
publication. There is no test hook in the production Android build.

Rust tests exercise the production anchor acceptance methods at expiry, and an
authenticated quiche connection with a real sealed packet, injected repeated
WouldBlock, five fresh admissions, controlled full-queue rejection, independent
plaintext expiry and subsequent reliable-stream progress on the same connection.

Validated locally: three native MSVC host tests, Windows Rust test suite,
Android ARM64 native library build, and Android-target Rust FFI metadata check.
No physical Xiaomi playback, acoustic timing, production ASRC, or ecosystem
qualification is established by these checks. Those remain separate gates.
