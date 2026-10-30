# Native audio control RPC — implementation contract

30 October 2026. Unpublished product control framing; production authority is
still disabled. Source, delivery, proof, receipt, PCM and ALPN formats retain the
accepted contracts. This document records the concrete encoding used by the
broker dispatcher and native codec, not a new authorization scheme.

The post-bootstrap pipe retains its u32 big-endian length and one-byte frame
kind. NetworkRequest=10 and NetworkReply=11 contain Request u64, actual observed
Connection UUID[16] (network byte order), Kind u8, then Body. Request is the
connection's control correlation number, independent of the signed N2 RequestId.
The 25-byte envelope leaves at most 8166 body bytes inside the 8192-byte pipe
frame. Zero request/connection, trailing fixed-field data and unknown kinds fail.

ConnectionObserved=8 is the existing 112-byte native observation: Connection,
native certificate SHA-256, phone certificate SHA-256, native nonce. All are
nonzero. ConnectionClosed=9 contains only that actual Connection UUID. Only the
retained registered worker's process-bound pipe may publish these observations.
Peer-supplied process facts or authentication booleans are never accepted.

| Request kind | Body | Reply body (kind=request+128) |
| --- | --- | --- |
| INFO=1 | Empty for source; byte 1 for prepared binding | Existing canonical descriptor |
| PREPARE=2 | Delivery generation u64, N2 RequestId[32], target Connection[16] | Existing canonical delivery descriptor |
| ISSUE=3 | Operation u8, RequestId[32], proof lifetime milliseconds u32, target Connection[16] | Existing signed envelope of the canonical proof and root challenge signature |
| PROOF=4 | Broker nonce[32], signature length u8, exact DER signature | Existing exact root-signed grant receipt |
| STATUS=5 | Fresh query nonce[32] | Existing root-signed N2 status |
| OUTPUT=6 | Delivery digest[32], delivery generation u64, output incarnation u64, readiness byte 0/1 | Empty |
| DETACH=7 | Delivery digest[32], delivery generation u64 | Empty |
| STOP=8 | Exact source UUID[16], generation u64, digest[32], StopIntentId[32] | Denied byte 1; no physical cleanup claim |
| SIGNED_SOURCE_STOP=9 | Existing bounded SourceStop signed envelope | Denied byte 1; no physical cleanup claim |
| SOURCE_STOP_STATUS=10 | Existing exact target plus fresh query nonce encoding | Existing query-bound root-signed SourceStopStatus |
| APPLIED_RESULT=11 | N2 RequestId[32], broker nonce[32], signature length u8, exact retained phone signature | Exact retained root-signed grant receipt, never Issue/Admit fallback |

ISSUE target is zero only for AcquireSource/Renew and nonzero only for
AttachDelivery/SupersedeDelivery. PREPARE nomination is accepted only through
eligible current ingress and uses the target's actual native TLS observation;
the caller cannot supply replacement certificate hashes/nonces. One bounded
nomination is retained. A nomination does not reserve N2's positive slot or
authorize PCM. Proof signatures remain 8–72 bytes; lifetimes retain the existing
1–120000 ms bound and all source-policy/deadline checks.

ERROR=255 carries one non-authorizing byte: Denied=1, Busy=2, Invalid=3,
Unavailable=4. Network errors expose no exception text or protected paths.
Malformed actual-worker framing is terminal to that worker channel. Public
candidate overload is handled at the native forwarding budget before the pipe;
it must not overflow the broker or close healthy incumbent source authority.

The broker/native forwarding budget is eight regular jobs and one independent
negative job, worker-wide. Pipe replies retain the same separate capacity,
including ERROR replies to a negative request. Captured signed Stop pins its
connection facts before asynchronous verification. Exact fast Stop fences on
ingress without crypto. The positive queue never holds the negative job.

TLS closure does not release an outstanding forwarding credit. The broker sends
its pipe completion even after TLS disappears; native then releases that credit
and drops the undeliverable network reply. Actual jobs/writes are joined before
process/caller custody retires. Restrictive publication is independent of a
positive ACK. Normal publications coalesce to the latest source revision.

The dispatcher has mandatory actual-source preparation and native publication
dependencies. Unit routing fixtures use explicit simulated observations; they
do not qualify native setup, media installation, endpoint/kernel rundown or
physical delivery. Listener/rundown framing and native owner composition remain
separate implementation work, tracked in the current implementation status.
