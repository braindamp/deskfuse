# Quarantined native candidate owner — source contract

30 October 2026. Component implemented and source checked; the configured
listener/source-owner composition and physical handover are still open.
See [current implementation status](Audio_Control_Handover_Implementation_Status.md)
and the [reviewed design](Audio_Handover_Reviewed_High_Decision.md).

`audio_candidate::CandidateSetup` owns one cold thread and one fresh native TLS
connection. The listener supplies an Initial from its sole UDP reader and a
shared nonblocking socket. The setup thread only sends on that socket. Active
packets continue through the source owner's borrowed receive path; only matching
candidate ciphertext enters the bounded mailbox. Fixed peer/local addresses and
the current 16-byte native delivery CID are checked before mailbox/budget use.
The current phone TLS generator creates 16-byte CIDs. CID routing is a hint;
QUIC/TLS and the approved exact delivery remain the authentication boundary.

The source supplies every `SetupLimits` value explicitly: warm expiry in the
Windows boot clock, QUIC idle timeout, packet mailbox capacity, total packets,
total bytes, total public control messages, packet work per turn and control
work per turn. There are no operating defaults. Packet/byte budgets count both
receive and successful send; send limits are checked before the UDP operation.
Control budget includes the public observation NOTICE, requests and replies.
Internal pin/transfer commands have a separate single bounded slot. Replies
retain eight regular slots and a separate negative slot, including a negative
ERROR response; not-yet-submitted negative replies have priority over blocked
regular replies. Events retain at most 32 bounded records. Incoming and outgoing
control work share the explicit per-turn budget. The phone opens stream 0 with
public INFO before waiting for the server's observation NOTICE.

Unauthorized incoming DATAGRAMs are discarded in bounded work without PCM
parsing. QUIC timers, mailbox signals and writable notifications from the single
socket readiness owner wake the thread; no production polling/sleep loop exists.
The candidate-only egress uses the existing immutable sealed slot and bounded
maintenance hooks; active media reactor behavior is unchanged. On Windows, the
cold thread uses CPU `THREAD_PRIORITY_BELOW_NORMAL`, keeping shared-socket I/O
resource scheduling unchanged. This is an implementation choice informed by
[Microsoft's scheduling guidance](https://learn.microsoft.com/en-us/windows/win32/procthread/scheduling-priorities)
and [SetThreadPriority resource-sharing guidance](https://learn.microsoft.com/en-us/windows/win32/api/processthreadsapi/nf-processthreadsapi-setthreadpriority).
It is not a measured latency guarantee.

Before a possibly committing ISSUE, the source owner sends a pin using its
actual current protected broker projection and exact N2 RequestId, then waits
for the actual Pinned event before forwarding issuance. The candidate verifies
the descriptor scope and independently retained HELLO service boot. Pinning
admits no media and reserves no positive proof. It replaces warm-only expiry
with the current source cutoff; duplicate pin never moves that cutoff. A
different request cannot overwrite an owned pin. Source Stop/expiry still win.

After logical commit, the serialized source owner must apply the grant, fence
old delivery sends, discard old assembly/ledger and retire old QUIC/Egress
**before** calling `try_promote_after_barrier`. The candidate checks exact
pinned RequestId, actual TLS observation, canonical delivery, source and actual
bootstrap boot, then returns the same owned connection after its thread exits.
The component does not implement the caller's old-egress barrier. The new media
owner must join actual setup completion, move that one value once, preserve the
source/packet/freshness floors and rebind readiness before any new PCM.
Promotion requires initial readiness false/output generation zero. Late queued
ciphertext and replies move with the connection as the existing bounded setup
mailboxes, rather than being dropped at transfer. The caller adopts them into
its control turn before media admission; staged packet receive explicitly drops
quarantine DATAGRAM residue even after the TLS value has been approved. Complete
broker forwarding credits only when a staged reply is actually queued (or its
closed route's completion is consumed), not merely when transfer is requested.

Captured events precede closure/transfer: `take_finished` refuses transfer while
events remain unread. Failures return the owned retired connection and pinned
identity in `CandidateOutcome`; they do not silently release an uncertain
operation's source/network lease. The source owner must retain that outcome and
its broker forwarding credits until actual outcome/closure is reconciled.
Cancellation joins real work, including in-progress TLS setup; Source Stop does
not manufacture completion. `Drop` means source shutdown and joins the thread.
Unused unpinned retirement and explicit cancellation must likewise consume
captured events before publishing route closure. Transferring/dropping setup
never closes the listener or retires the separately moved active connection.
The Windows caller includes the borrowed candidate thread completion handle in
its readiness wait. A logical wake is not proof of thread termination; waiting
on that actual handle avoids polling or losing a finish-edge notification.

Verified with actual loopback mutual TLS/control and source fixtures: public
metadata exchange, pin and one-value transfer, cutoff stability past warm TTL,
wrong bootstrap boot, bounded datagram discard, exhausted send/control budgets,
final captured event before closure, eight regular requests with a negative
ERROR reply, late staged input/reply transfer and actual cancellation/join. Test peer
scheduling uses short waits; it is not product media behavior. No source,
endpoint, driver, AAudio renderer, network handover or installed authority ran.
