# Worker thread protection — accepted decision and implementation

29 October 2026. The user accepted the focused Ultra decision inline and returned
to High for implementation. Preserve the existing v0.6/native ownership design.

## Accepted policy

Use a separately duplicated intended-user primary token for each medium native
or enrollment worker. Preserve its user/logon/authentication/session and medium
integrity; never modify the WTS/logon token. Before process creation, install this
default object ACL: SYSTEM/Administrators generic full access and OWNER RIGHTS
(`S-1-3-4`, SDDL `OW`) READ_CONTROL only. No ordinary-user/logon operational ACE.
The OWNER RIGHTS ACE removes implicit owner WRITE_DAC. `TokenDefaultDacl` carries
the ACL, not a descriptor's protected/inheritance control flags.

The token object is separately protected: SYSTEM owner, protected DACL, SYSTEM/
Administrators TOKEN_ALL_ACCESS, intended user TOKEN_QUERY/READ_CONTROL only,
OWNER RIGHTS READ_CONTROL only. The new anonymous token gets its restrictive
DACL during duplication. SYSTEM ownership is set through the retained WRITE_OWNER
handle before exposing it to a child. This avoids changing service privileges or
requiring SeRestorePrivilege for DuplicateTokenEx's explicit-owner parameter.
Any failure closes the unpublished copy and refuses launch.

Retain explicit process/initial-thread security, job-at-creation containment,
no-children policy, fixed image closure and observed logon checks. Inspect the
actual suspended child's token identity, token-object descriptor and default ACL
before resume. Keep creator-returned/duplicated self handles for thread control.
Do not repair exposed threads after creation or add broad compatibility grants.

Set broker-owned `DOTNET_EnableDiagnostics=0` before managed runtime startup.
This is also used by the separate high UI environment; its elevated token parser
is unchanged. Headless medium roles use CREATE_NO_WINDOW. None of these checks
adds work to PCM, packet or render processing.

## Source and executed checks

`BrokerWorkerToken.cs` implements the policy; `BrokerWorkerLauncher.cs` applies
and verifies it. The real MSVC SDK ABI fixture includes token access masks and
the TOKEN_DEFAULT_DACL layout/information class. Windows Authz checks use only
synthetic descriptors/SID contexts: owner thread-context/APC/mutation/security
rights and token mutation/duplication are denied; removing OWNER RIGHTS restores
the unsafe implicit owner WRITE_DAC in the regression check. This is not an
installed injection or token-launch probe.

N1 source is added: `AudioWorkerControl.cs`, Rust `broker_control` and fixed
`deskfuse-audio-worker` Cargo target. The native client retains the actual SYSTEM
service process, checks Session 0, installed sibling path, creation identity,
SYSTEM-owned protected pipe descriptor and restricted client rights. An exact
bounded bootstrap binds registration, service boot and observed incarnation.
Malformed/broken established control is terminal. Every overlapped request is
cancelled/completed and joined before releasing its storage. No audio authority
or media socket is granted by this bootstrap.

`tools/build-audio-worker.ps1` builds/stages the Cargo executable under the fixed
installed name `DeskFuse.AudioWorker.exe` and its C++ source-control DLL. Cargo
target names cannot contain a dot. The script selects an explicit x64 MSVC Cargo
target/output directory and checks both PE machine types before copying. The new
entrypoint has an explicit source exception to the repository's `bin/` ignore rule.
Staging/hash output is not trusted-installer
approval, signing, service publication or a complete approved dependency closure.

Executed: Windows broker/setup/enrollment builds passed without warnings/errors;
25 focused C# checks passed, none failed/skipped; three Rust control tests passed;
native worker and C++ DLL Debug and optimized x64 Release builds/staging passed.
Shared C#/Rust bootstrap/security
fixture is `tools/protocol3/audio-worker-control-v1.json`. Real SDK ABI fixtures
were rebuilt successfully. Installed runtime/Keystore/hardware gates remain open.

## Continuation and qualification

The focused design stop is resolved. Continue N2–N6 in
`Native_Audio_Owner_Boundary_High_Handoff.md`, then service composition. Preserve
the shared source/delivery deadline, cold-only persistence/signatures, in-process
capture and single bounded queue. Production authority stays refused by Program.

Before activation, separately authorize and run installed-SYSTEM qualification:
actual copied/effective token policy and all later runtime/native threads, real
same-user/cross-logon denial (including creation races), unavailable diagnostics
attach, Rust/CRT/.NET GC/async/COM/MMCSS/WASAPI compatibility and confirmed teardown.
Library-supplied explicit security descriptors bypass token defaults and must be
covered by the pinned closure and focused qualification. Enumeration is evidence,
not enforcement. Elevated administrators/SYSTEM are outside same-user isolation.

Primary references:

- [CreateThread](https://learn.microsoft.com/en-us/windows/win32/api/processthreadsapi/nf-processthreadsapi-createthread)
- [OWNER RIGHTS](https://learn.microsoft.com/en-us/windows-server/identity/ad-ds/manage/understand-security-identifiers)
- [DuplicateTokenEx](https://learn.microsoft.com/en-us/windows/win32/api/securitybaseapi/nf-securitybaseapi-duplicatetokenex)
- [Diagnostic port](https://learn.microsoft.com/en-us/dotnet/core/diagnostics/diagnostic-port)
