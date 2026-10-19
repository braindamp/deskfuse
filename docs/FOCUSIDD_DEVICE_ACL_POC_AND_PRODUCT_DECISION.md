# DeskFuse — FocusIdd device access: POC elevation and consumer deployment

**Document ID:** DF-SEC-FOCUSIDD-001  
**Status:** POC operational guidance + product-security follow-up; does **not** revise accepted decision DD-001  
**Date:** 2026-10-20  
**Repository:** https://github.com/braindamp/deskfuse  
**Related:** [design_decisions.md — DD-001](../design_decisions.md), [FocusIdd.inf](../windows/driver/FocusIdd/FocusIdd.inf), [GPU/WGC POC v1.2](./DeskFuse_GPU_Phone_Workspace_WGC_POC_SPEC_v1.2.md), [window-first design](./WINDOW_FIRST_WORKSPACE_AND_RENDERING_PIPELINES.md)

## 1. Concrete finding: installed driver vs. current source

The independent GPU/WGC testing agent reports that the **installed** FocusIdd device ACL is:

```text
D:P(A;;GA;;;SY)(A;;GA;;;BA)(A;;GA;;;UD)
```

The driver-control interface can be opened by SYSTEM, built-in Administrators, and the special User-Mode Drivers SID, but not by an ordinary interactive process. Consequently `FocusWindow.Agent.exe density-probe` reports Access Denied unelevated and works in an elevated terminal.

**Crucial repository finding:** `windows/driver/FocusIdd/FocusIdd.inf` on current `main` **already includes** `(A;;GA;;;IU)`:

```text
D:P(A;;GA;;;SY)(A;;GA;;;BA)(A;;GA;;;UD)(A;;GA;;;IU)
```

And `design_decisions.md` already contains **DD-001 (accepted, 2026-10-19): interactive-user driver access, non-elevated normal agent**. The installed/observed ACL therefore appears to be from an older installed driver package, or another active security descriptor configuration. **Do not assume the running device has the access specified in the current source INF.** The source and installed driver must be verified independently at a future scheduled installation/integration step. Editing the INF alone does not change the installed ACL.

Reference: Microsoft's `Controlling Device Access` identifies `UD` as `SDDL_USER_MODE_DRIVERS`, SID `S-1-5-84-0-0-0-0-0`: https://learn.microsoft.com/en-us/windows-hardware/drivers/wdf/controlling-device-access . The general device-object security documentation: https://learn.microsoft.com/en-us/windows-hardware/drivers/kernel/sddl-for-device-objects .

## 2. Decision for the *current GPU/WGC POC*: run the guarded hardware part elevated

The hardware lab may start an **elevated terminal** via UAC and run its known-good FocusIdd test executable there. Work on C++, WGC, D3D11, tests, documentation, review and Git operations in the usual unelevated worktree.

**Do not** change the installed driver's ACL, rebuild/reinstall the driver, modify `FocusIdd.inf`, weaken device access with a runtime ACL command, change physical monitor configuration, use system-wide DPI/registry workarounds, or launch a SYSTEM background service in this POC. The installed driver is shared by all local Git worktrees, so the test agent and the DPI agent must not own it concurrently.

Windows PowerShell (manually open one elevated shell for the test, without changing the installed device):

```powershell
Start-Process powershell.exe -Verb RunAs
# In the elevated window:
cd D:\Coding\DeskFuse\dpr-gpu-wgc
# Use the POC runner from that branch, after its own preflight is implemented.
# Inspect the running device first; require monitor arrived 0.
```

Mandatory guards:
- Do not begin if FocusIdd is already displaying a monitor / another session owns it. Preserve driver protocol and known-good heartbeat/watchdog behavior.
- Confirm the new FocusIdd monitor by topology delta and verify the physical monitor snapshot before and after.
- Apply scaling only to the identified virtual monitor and **read back effective native HWND DPI and captured ruler pixels**; an apparent `-3 current=350%` is insufficient.
- Guarantee virtual monitor teardown on success, exception, cancellation and timeout. Keep user-visible evidence of cleanup and a `driver-status` showing `monitor arrived 0`.
- Distinguish `ACCESS_DENIED_EXPECTED_UNELEVATED` from unexpected failures; do not present it as a WGC, DPI or GPU blocker.
- Record run privilege/integrity state, the source INF ACL, the installed device-access behavior, and that no driver installation occurred.
- Keep the WGC capture/window worker in the interactive user's desktop/session; elevated POC execution is temporary and must **not** be generalized into product policy.

A high-integrity test host also differs from ordinary desktop apps with respect to process integrity and input; it cannot establish that the ordinary, non-elevated product UX already works.

## 3. Current accepted product decision and its limits

DD-001 was explicitly accepted: allow **interactive users (`IU`)** to open the FocusIdd control device so the normal UI/agent can run without UAC. **Do not silently supersede that product decision.** A future scheduled driver update should build/sign/install a package containing DD-001, verify its active device security, test ordinary-user driver access and all hardware safety checks, and follow the project's established rollback process. That step is **outside** this POC.

Security consequence: `GA` grants broad driver-device permissions to **any interactive local process**, not only the DeskFuse application and not only a phone that has been approved in DeskFuse. The on-console pairing prompt authorizes remote phones; it does **not** authorize or constrain arbitrary local processes directly opening the driver. Current driver commands may only be for virtual display operations, but attaching/removing displays and exhausting resources still require abuse-case testing. Audit all current and future IOCTLs, shared-memory/event ACLs, monitor ownership, resource caps and driver service behavior before shipping.

DD-001 itself says to revisit the trade-off if the threat model changes or the interface gains more privileged operations. The phone-first spatial workspace, many local sources, browser sessions and potential multiuser deployments make that review valuable, **but no decision is changed here**.

## 4. Future hardening candidate: a narrow privileged display broker

If a future owner explicitly supersedes DD-001, prefer a **small privileged service/broker for driver-control IOCTLs only**, separated from the normal interactive agent:

```text
Phone <-> authenticated DeskFuse user agent (ordinary interactive session)
                               |
                 authenticated local IPC
                               |
             narrowly privileged display broker
                               |
                  FocusIdd control interface
```

Suggested security requirements before implementation:
- Validate the caller's Windows user/logon session and apply an IPC ACL + caller impersonation/authorization rather than trusting arbitrary named-pipe clients or just a process name.
- Define a **typed allowlist of safe operations**, reject arbitrary IOCTL passthrough, enforce display/session ownership, bounded modes, refresh rates, pixel budgets, rate limits, deadlines, and explicit teardown.
- Keep network exposure, pairing state, screenshot/clipboard/media access, WGC, GPU encoding and UI **outside** the privileged service. Services run in Session 0 and should not be used to interact with ordinary user-session HWNDs.
- Review driver interface memory mapping permissions separately: a broker protecting IOCTLs does not automatically protect shared frame memory or named events.
- Validate fast user switching, logoff/crash recovery, multiple user sessions, UAC and remote logon boundaries.
- Compare complexity and maintenance costs of a broker against the accepted DD-001 IU approach. Do not assume a broker is necessary for the current POC or guaranteed to improve GPU capture.

Relevant Microsoft references:
- Device SDDL: https://learn.microsoft.com/en-us/windows-hardware/drivers/kernel/sddl-for-device-objects
- Device access: https://learn.microsoft.com/en-us/windows-hardware/drivers/wdf/controlling-device-access
- Interactive services restrictions: https://learn.microsoft.com/en-us/windows/win32/services/interactive-services

## 5. Required handback from the GPU testing agent

Add a short `focusidd-device-access` section to the POC evidence report:

1. `driver-status` outcome from non-elevated session (expected Access Denied if installed ACL still lacks IU), and from elevated session.
2. Source commit/hash and the exact device SDDL observed vs. source-INF SDDL; mark them **different** if so.
3. Confirmation that only POC hardware tests required UAC; ordinary development did not.
4. Confirmation the installed driver/INF/registry/physical monitors were untouched.
5. Driver monitor removed and physical topology restored at the end.
6. No changes to DD-001 or installation recommendations without explicit owner approval.

**Decision in one sentence:** *Proceed with elevated, guarded hardware tests; retain DD-001 and existing INF source unchanged; schedule installed-vs-source ACL reconciliation for a separate driver-integration milestone; revisit a privileged broker only as an explicit, threat-model-driven future decision.*
