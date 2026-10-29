# DeskFuse vNext — Future Releases: Network Access & Developer Diagnostics

**Status:** Product-development backlog + current Windows-to-Xiaomi laboratory runbook; **not** a claim that the future tool is implemented.

**Scope:** CP-001 / MP-001 Windows native WASAPI source → authenticated Rust/quiche QUIC DATAGRAM → Xiaomi Android sink. The working signed drivers, installed Android APK, physical endpoints, and protected reference branch are out of scope for firewall troubleshooting.

## 1. Verified network contract (current audio-only lab)

| Connection / purpose | Direction / protocol | Port | Scope and permission | Status |
| --- | --- | --- | --- | --- |
| Windows QUIC source listening for Xiaomi | **Inbound Windows UDP** | **48235**, on Windows LAN IPv4 | **Required** for this exact lab invocation. Restrict to **Private** profile, the Windows sender executable, current Windows LAN address, and the currently authorized Android device's LAN address. | Current lab contract, **not** a globally reserved DeskFuse port |
| Android QUIC client initiates connection to Windows | Android **outbound UDP** to Windows:48235 | Client **ephemeral UDP source port** selected by OS; e.g. 47020 or 59988 in October 23 logs | Permit the session and its normal stateful return traffic. **Do not** create a fixed inbound Android-port rule from the observed ephemeral port. | Required for current client-initiated flow |
| Windows QUIC response and ongoing encrypted media/control | Windows **outbound UDP** back to Android ephemeral source port, with connection-tracking return traffic | Remote ephemeral UDP | Normal stateful outbound/return behavior; no broad inbound allow needed beyond Windows UDP:48235 for this topology | Required by flow; usually permitted under default outbound policy |
| Android Debug Bridge (ADB) host server for lab deployment | **Windows loopback TCP** (ADB server) | **5037** on 127.0.0.1 | Development tooling only; no Internet or LAN inbound exposure needed for **USB ADB**. ADB over Wi-Fi, if later authorized, has its own pairing/connectivity design and must not inherit the QUIC rule. | No LAN firewall exposure required |
| Android deployed lab executable and native renderer library | ADB file push / shell to `/data/local/tmp` | **No separate DeskFuse network port** | Device-side diagnostic workflow; `-VerifyOnly` does not start AAudio or replace installed APK | Laboratory only |
| NordVPN NordLynx / Meshnet | VPN adapter + vendor-specific filtering | **Not a DeskFuse port requirement** | Meshnet's all-ports inbound rule is unrelated to the QUIC listener. VPN kill-switch, LAN discovery/block, split tunneling and Windows Filtering Platform (WFP) callouts can affect reachability even with a Windows Firewall allow rule. | Diagnostic environmental dependency, not yet established as root cause |
| Display streaming, future discovery/pairing, WAN/relays, additional sinks | TBD | **No ports approved or committed here** | Define separate authenticated roles, least-privilege scope and feature-specific port policy when implemented. No universal open-port requirement should be inferred from the audio lab. | Future design |

**Current lab addresses only:** Windows Ethernet **192.168.2.107**, Xiaomi WLAN **192.168.2.114**, UDP server **192.168.2.107:48235**. These are **not release defaults**, must be rediscovered/validated each session, and may change with DHCP or network topology. QUIC uses mutually authenticated TLS; a firewall allow rule does not replace credential verification. Source/receiver run parameters still set the authenticated peer allow-IP and lab identities.

**Executable path to scope on this machine:** `D:\Coding\DeskFuse\vnext-wp001\vnext\m2a\quiche-lab\target\debug\continuous-quic-lab.exe`. A future installed release requires a path-aware rule for its **actual installed signed executable**, not a stale developer path.

### Least-privilege Windows inbound rule (administrator-approved, current lab only)

Do not re-enable the old `continuous-quic-lab` broad rule as a workaround. Inspect any matching rules first, then (only with operator approval and elevation) create a *separate* scoped rule:

```powershell
$exe = 'D:\Coding\DeskFuse\vnext-wp001\vnext\m2a\quiche-lab\target\debug\continuous-quic-lab.exe'
$name = 'DeskFuse vNext Xiaomi QUIC UDP 48235 Private'
Get-NetFirewallRule -DisplayName 'continuous-quic-lab',$name -ErrorAction SilentlyContinue |
    Select-Object DisplayName,Enabled,Direction,Action,Profile
# Administrator PowerShell, only when the scoped rule does not already exist:
New-NetFirewallRule -DisplayName $name -Enabled True -Direction Inbound -Action Allow `
    -Profile Private -Protocol UDP -LocalAddress '192.168.2.107' `
    -LocalPort 48235 -RemoteAddress '192.168.2.114' -Program $exe
```

**Security constraints:** never set all firewall profiles off, allow arbitrary remote IPs or all ports, enable broad Public rules, grant blanket exclusions in Defender, override vendor VPN policy, modify other app rules, or manage credentials silently. Explicit *Block* rules and third-party WFP filters may override an allow; investigate before adding redundant permits. Revisit scoped rule when IP/path/profile changes, ideally remove stale lab-only rules after use.

## 2. Incident record: October 23, 2026 — UDP blocked before QUIC authentication

**Source evidence (local, not committed):**
- `audit-20261023-170119-330.json` and `audit-20261023-170228-921.json`: PC Security Sentinel; both recorded a Private Ethernet interface, NordLynx adapter, Windows Firewall enabled, and an existing enabled **broad** inbound rule named `continuous-quic-lab` pointing at the Windows debug sender executable. The rule was flagged for overbroad Private/Public access.
- Uploaded `fixes.zip` / `9ec2c070f9574804b7539f8eb265bd0e.json`: the **20:46:18–20:46:40 CEST** `broad-firewall-rules` fix reported **Succeeded**, recording *Disabled inbound firewall rule 'continuous-quic-lab'* alongside `physical-quic-lab`, `FocusWindow.Agent`, `testhost`, and `adb.exe`. The rules were **disabled rather than deleted**. A separate follow-up run found no broad enabled development rules. Other fixes (SMB signing, LLMNR, local-account/password and autologon policies, Defender Network Protection) are not established as the direct cause of lost UDP.
- `D:\DeskFuseLocalData\cp001\20261023T192925Z-mm1-qpc-v2-verify-f1c1r1`: initial silent 20-second attempt. Windows printed `READY` but **no `AUTH`**, Android failed before observation-window completion; capture never started.
- `D:\DeskFuseLocalData\cp001\20261023T194806Z-preauth-udp-diagnostic-f1c1r1`: pre-auth trace on source revision `e257b1d3d10f292421e750bbabedb31e0d1396be`. Android reported `TRACE Android PREAUTH_UDP_TX accepted_by_kernel=1 dest=192.168.2.107:48235 total_packets=2`; Windows logged `READY` but **no** `PREAUTH_UDP_RX`, `PREAUTH_QUIC_ACCEPT` or `AUTH`. Android QUIC timed out. Android `ip route get` selected wlan0 with 192.168.2.114 as source.

**Causal assessment:** **High-confidence, not yet experimentally confirmed:** Security Sentinel disabled the previously broad sender inbound allow rule before the two failing tests. That change plausibly prevented the phone's UDP Initial reaching the Windows sender. Kernel acceptance of Android's outbound UDP is **not proof of delivery**. Existing evidence cannot exclude NordVPN traffic filtering, explicit Windows Block rules, or another network cause. A narrow Private-profile UDP 48235 rule followed by an otherwise identical silent test is the discriminating experiment. No evidence implicates WASAPI capture or post-auth source-completion handling in these *pre-authentication* failures.

**Do not rewrite history:** a post-rule-change successful run is needed before saying the firewall was proven responsible. Timestamp local CEST and evidence SHA/path must accompany conclusions. The security audits ran *before* the fixes, so they do not show post-fix effective rule state.

## 3. Future release: Developer Mode → Connectivity Analyzer

The product should provide a **read-only preflight and active authenticated test**, with an *opt-in, least-privilege* remediation pathway—not a blanket firewall bypass.

### Proposed diagnostic stages

1. **Show exact session tuple:** endpoint role, local bind IP:port, intended peer IP, actual executable path+hash/signature, Windows network category, interface/route, and transport protocol; explicitly distinguish fixed listener ports from ephemeral ports. No automatic public/WAN publication.
2. **Read effective access policy:** Windows Defender Firewall profiles; relevant rule action, enabled state, profile, program path, local/remote IP, protocol and port; effective Block overrides Allow; VPN adapter, NordVPN Meshnet/LAN and kill-switch mode; Defender network events when authorized. Gracefully report insufficient rights without silently elevating.
3. **Separate readiness from reachability:** listener process launched and bound; Android route to target; Android UDP `accepted_by_kernel`; Windows UDP observed; valid QUIC Initial accepted; TLS peer verified on **both** ends; PCM source starts only after TLS; encrypted PCM received; optional native AAudio output. Display a stage-by-stage pass/fail timeline.
4. **Failure classification:** `NO_LOCAL_LISTENER`, `UDP_NOT_SENT`, `UDP_NOT_OBSERVED`, `QUIC_INITIAL_REJECTED`, `HANDSHAKE_NOT_VERIFIED`, `CAPTURE_FAILED`, `PCM_NOT_RECEIVED`, `SINK_NOT_STARTED`, `OBSERVATION_INCOMPLETE`. Each must identify the first observed failing boundary and distinguish missing evidence from a confirmed block.
5. **Evidence package:** local-only redacted JSON with build SHA, per-artifact SHA256, generated UTC timestamps, interface/profile/rule snapshots, active VPN presence, packet counters and handshake state, startup/exit codes, test duration and path to session logs. Never export TLS private keys, audio samples, passwords or detailed unrelated account data.
6. **Remediation preview:** suggest only a dedicated, scoped Private-profile allow rule for the current signed application, agreed inbound port and paired device; require affirmative operator/elevation; show what will change, verify the resulting rules, and support rollback. No stealth rule edits or broad ADB/SMB/other-app exceptions.
7. **Qualification:** demonstrate (a) broad rule disabled and restricted allow missing → correctly identified blocked case, (b) explicitly allowed paired device → QUIC TLS and silent PCM pass, (c) unexpected peer/public profile denied, (d) NordVPN/filter/route disturbances separately diagnosed, (e) no audio/driver/APK modifications from diagnostics.

### Proposed developer-mode UI

`Network check → Local listener → Android UDP sent → Windows UDP received → QUIC accepted → Mutual TLS verified → Source PCM → Android PCM → Optional speaker`.

Each node displays timestamp, status, evidence confidence, local endpoint and safe corrective action. The UI must not infer successful reachability merely from a socket's `send_to` success. Clearly label `Silent verification` versus `Audible playback`.

## 4. What the **20-second test** actually is

This is a **short, real-transport regression diagnostic**: **20 seconds of Windows MM-1 WASAPI capture + native source-frame/QPC provenance + authenticated QUIC DATAGRAM transfer to the physical Xiaomi + Android receive/verify**. With `-VerifyOnly`, the Android executable validates incoming PCM and logs continuity **without opening AAudio or playing through the speaker**. It does not edit device defaults, drivers or the installed APK.

It was chosen to confirm the earlier first/second 240-frame split provenance correction, classify source drops versus network gaps, and stop before any listening test if basic transport is broken. It is **not** an endurance, acoustic, release, or WAN test. The current failure was **before TLS authentication** and therefore did not exercise the actual capture path.

**Invocation:** `vnext/qualification/run-physical-audio.ps1` with `-Seconds 20 -VerifyOnly -Modes f1c1r1 -Profile mm1-qpc-v2-verify`, the existing `-EndpointId`, `-AndroidExecutable`, `-AndroidNativeSo`, `-LabRoot`, and the current Windows/phone IPs. Evidence is stored in a new timestamped folder under `D:\DeskFuseLocalData\cp001`. Run only after the scoped network permission is installed and verified; no need to change the sender/receiver code to test a firewall hypothesis.

**Success gate:** both sides log authenticated QUIC; Windows logs actual WASAPI source/ingress/QPC counters, Android logs received PCM and sequence continuity, and both complete the full observation period with exit 0. Then analyze original WASAPI packet trace against live source-frame/QPC and transport drop counters. Only after clean silent evidence proceed to low-volume audible qualification, separately marked as acoustic evidence.

## 5. Design caution

The current port 48235 is an **implementation setting for the local physical lab**, not a promise that a production release must expose this static port. Future LAN discovery, QR pairing, interface hopping, app packaging, desktop audio/video, remote WAN and relay topologies require their own verified security/network contracts. Until those decisions exist, do not hard-code additional open ports or call an Internet-wide firewall rule a DeskFuse requirement.

## 6. Related design improvement: playback modes and network handover

The requested Game / Interactive / Streaming policies, mouse-triggered catch-up
and accelerated audio/video Live button are recorded as
[DI-001 in the design improvements backlog](../plans/Design_Improvements_Backlog.md).
That proposal also requires automatic Wi-Fi/cellular handover and explicit
content-retention/recovery limits. WAN reachability, path overlap and authenticated
replacement delivery still need design and implementation; this historical LAN
runbook does not establish them.
