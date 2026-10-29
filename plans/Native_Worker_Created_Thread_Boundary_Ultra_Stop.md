# Focused High stop — worker-created thread security

**Resolved by the user-accepted Ultra decision, implemented on High.** Continue
from [Native_Worker_Thread_Launch_High_Decision.md](Native_Worker_Thread_Launch_High_Decision.md).
The source facts below describe the earlier stopping point; installed qualification
remains open. Preserve this finding as historical context.

**29 October 2026. Original stop; not a replacement architecture.** The High
durability/setup continuation reached this source-level isolation gap while
starting E6/N1. No Rust worker, new native capture path or thread-security remedy
was implemented. No installed access/injection probes or machine changes ran.
Production authority remains refused by `DeskFuse.AudioBroker/Program.cs`.

Continue from [Enrollment_Durable_Completion_Implementation_Status.md](Enrollment_Durable_Completion_Implementation_Status.md)
and preserve all uncommitted work in `D:\Coding\DeskFuse\vnext-audio-latency`,
`experiment/vnext-audio-latency`, HEAD `2f181cf2da9c4efdde3013b115108ecac650d1ab`.
The durability design is implemented in source; this stop concerns the next
protected native-worker integration, not a new persistence question.

## Confirmed source facts

- `windows/audio/DeskFuse.AudioBroker/BrokerWorkerLauncher.cs:66–108` obtains
  the WTS user's token and passes explicit SYSTEM-owned process and initial-thread
  descriptors to `CreateProcessAsUserW`. It verifies those two retained objects.
  It does not set a private token default DACL for objects created later.
- Its job-at-creation, no-children and image/extension-point mitigations remain
  useful, but do not select the security descriptor of every later thread.
- `vnext/m1/src/source_control.cpp:19–22` creates the COM/period owner with
  `std::thread`. `vnext/m2a/quiche-lab/src/committed_source.rs:160–165` creates
  setup work through `std::thread::spawn`. The planned bounded control reader
  and in-process WASAPI capture owner also require threads.
- A managed medium enrollment transport also creates runtime/thread-pool threads.
  Any solution must cover runtime/library-created threads, not just one explicit
  Rust/C++ call. The SYSTEM service and high-integrity UI are distinct profiles;
  do not flatten all three into one privilege change.

## Verified Windows rules and limit of this finding

[CreateProcessAsUserW](https://learn.microsoft.com/en-us/windows/win32/api/processthreadsapi/nf-processthreadsapi-createprocessasuserw)
creates a process and its primary thread; the explicit thread attributes cover
that created thread. [CreateThread](https://learn.microsoft.com/en-us/windows/win32/api/processthreadsapi/nf-processthreadsapi-createthread)
documents token-derived default thread security when no descriptor is supplied.
[Thread security/access rights](https://learn.microsoft.com/en-us/windows/win32/procthread/thread-security-and-access-rights)
documents separate thread-object access checks and rights for changing context,
suspension, termination and DACL changes.

Therefore the existing initial-thread check does not establish the required
ordinary-same-user isolation for later threads. This is a missing enforced
boundary in source. The actual installed token default DACL and runtime threads
have not been inspected, and no exploit or successful injection is claimed.
Do not describe these ordinary processes as Windows protected processes/PPL.

## Narrow decision required before E6/N1–N6 proceeds

Choose and document a supported **private-at-creation** policy for all objects
whose control would undermine the protected medium worker, with explicit thread
coverage and creator/default-owner rights. In particular:

1. Decide whether an independently duplicated worker token with an appropriate
   default object DACL/owner-rights policy can protect native, CRT/COM and managed
   runtime-created threads before any untrusted handle can be acquired. Establish
   exact access and ownership semantics; do not assume that removing an allow ACE
   removes implicit owner rights or that changing a default affects old objects.
2. Determine compatibility with Rust/CRT thread creation, COM/RPC, MMCSS, WASAPI,
   managed runtime helpers, self-access, joins and cancellation. Never mutate the
   shared WTS/logon token or weaken the distinct high-UI and medium-worker checks.
3. If that approach is unsupported or incompatible, select the smallest supported
   alternative within the existing ownership contract. Do not silently move
   network/media parsing into SYSTEM, elevate the native worker, relax the
   no-children profile, or add a new PCM forwarding stage.
4. Protection applied after creation or a later thread enumeration is not a
   substitute for preventing prior attacker-held handles. Keep existing retained
   process/job and source/kernel rundown obligations.
5. Specify focused offline/source checks and a separately authorized installed
   SYSTEM qualification: a genuine same-user/cross-logon adversary must be denied
   relevant thread/context/APC/security access, including during new-thread
   creation and library/runtime initialization. This document authorizes none
   of those installed probes.

Expected artifact: a focused decision/High handoff for this boundary, identifying
the exact token/object policy and affected launcher/runtime code. Review and plan
on Ultra; no broad v0.6 redesign or new media protocol research is required.
After the decision, High should implement it and continue the existing N1–N6
order. Pending service composition remains blocked on the complete chain.

The established Rust/QUIC ownership, in-process C++ capture reuse, one bounded
queue, separate source/delivery lifetimes and cold-only persistence/signatures
remain the target. No change to those choices is accepted by this stop.
