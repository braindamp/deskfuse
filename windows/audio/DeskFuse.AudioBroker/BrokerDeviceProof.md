# Broker device proof v1 — unconnected implementation contract

`BrokerDeviceProof.cs` implements bounded one-use challenges and P-256 ECDSA
verification. It does not enable a broker operation or establish enrollment.
The broker must obtain the key and all context from independently protected
enrollment and registered process objects, recheck them on consume, and fence
revocation against the eventual privileged action. Never accept these values as
authority from a same-user pipe client.

The phone signs the exact broker-provided transcript with ECDSA P-256 / SHA-256;
the signature is a canonical DER SEQUENCE of two positive, in-range integers.
The enrolled P-256 SPKI must be canonical DER with an uncompressed public point.
The 32-byte challenge is random, expires according to the broker's monotonic
clock and is consumed before signature verification. The absolute expiry is an
opaque broker clock value; the phone does not compare it with its wall clock.
Restart drops all challenges. Current limits are 256 outstanding, 16 per broker
registration and at most 120 seconds of requested lifetime.

The transcript fields, in order, are:

1. UTF-8 ASCII domain `DeskFuse.BrokerProof.v1` followed by a zero byte.
2. 32-byte nonce, 32-byte enrolled peer identity and 32-byte resource identity.
3. One-byte operation and one-byte capability scope.
4. 16-byte registration UUID in RFC 4122/network byte order.
5. One-byte length followed by binary Windows user SID; one-byte length followed
   by binary Windows logon SID.
6. Big-endian unsigned 64-bit logon authentication ID; big-endian unsigned
   32-bit session ID and process ID; big-endian unsigned 64-bit process creation
   FILETIME and expected owner generation; big-endian signed 64-bit expiry in
   broker monotonic milliseconds (always nonnegative).

The test `Transcript_has_the_stable_network_order_golden_encoding_for_android_interop`
contains a deterministic 212-byte vector. Android Keystore key enrollment,
transcript transport and end-to-end cross-language verification remain future
work. No hardware-backed key claim follows from successful proof verification.
