# Native broker I/O and candidate completion — source contract

30 October 2026. Component implemented; listener/source-owner runtime composition
is still open. Production authority remains blocked. This extends the existing
process-bound pipe and [control RPC contract](Audio_Native_Control_RPC.md).

`broker_control::windows::channel::BrokerControlClient::connect` retains the
existing SYSTEM owner/token, exact descriptor/client mask, installed broker
image path, service process/creation and HELLO registration/boot checks. Its
private start factory is used by production only after that bootstrap. The
synthetic test factory is compiled only for tests and does not weaken connect.

One cold reader and one ordered writer own the same retained pipe/server handles.
There are eight regular write slots, one independent negative slot and eight
bounded internal-observation slots. Class capacity includes the actual in-flight
write until completion; no caller cancellation removes its buffer. The writer
preserves FIFO admission, including already numbered connection requests. The
source's worker-wide Forwarding owner separately retains actual RPC credits
until broker completion/actual TLS reply admission, including closed routes.

The reader performs bounded parsing/routing into at most 16 incoming frames. A
trusted Stop, closed projection, malformed frame, server death or transport
failure latches terminal state and wakes the source independently of writer
progress and positive proof work. Closed projections are checked against the
independent HELLO boot/worker. No networking, signatures, source creation, device
or PCM operation runs under this mailbox gate. Concrete listener configuration
must be validated by its codec before any socket action; that runtime is not
implemented merely by making the bounded I/O channel accept its frame kind.

Control-event publication/reset share the mailbox gate, avoiding a lost wake
between empty-check and reset. Actual write completion also wakes the control
turn so held, not-yet-submitted frames can retry when capacity returns. An empty
control wake is a hint, never an authority ACK, readiness or lease renewal.
The terminal event is separate, persistent and monotone.

Cancellation sets a persistent event in addition to CancelIoEx. Each native
overlapped operation waits on its own event, the actual server process and this
cancel event; it calls GetOverlappedResult to join cancellation/late completion
before releasing buffer, OVERLAPPED or event storage. Persistence covers the
gap between header/body transfers, where cancelling only currently submitted
I/O can miss a later read. Shutdown joins both actual threads before handle
release; there is no reconnect on an opened/failed channel.

The Windows socket/source/control wait can additionally borrow the actual
candidate thread completion handle. The wait uses a fixed stack handle array,
keeps control/terminal precedence, rejects null/duplicate handles and preserves
the existing source/network event behavior. This handles logical finish wakes
which race actual thread exit without a polling timer or extra joiner thread.
The source must still recheck terminal/authority and join setup before moving
its connection; a ready handle alone grants no authority.

Verified with actual Windows synthetic local pipes (no installed authority): a
blocked ordered writer while Stop is read, reserved negative write capacity,
partial-header/body cancellation, real write-capacity wake without a fake ACK,
and actual thread-handle completion/terminal precedence. The complete offline
native library suite passed 73 checks, including source freshness, immutable
ciphertext, receive floors, native mapping/ABI and built DLL exports using only
invalid/no-device open inputs. No physical audio or installed service ran.

The H2a native entry now uses this client as the sole post-bootstrap reader
and ordered writer. It retains one validated listener socket/configuration/ACK
in a serialized runtime, checks source identity across grants, and waits for
actual write completion through a narrow receipt before acknowledging success.
Closed/Stop/cutoff/service failure retires the listener and joins pipe work.
Source/media observations are not inferred from this metadata. The UDP receive
owner, candidate dispatch/transfer, global forwarding and actual N4 source/
rundown integration are still pending; N3 and handover are not complete.
H2a Windows tests are added but NOT RUN in this environment.
