# Android cellular request custody — H3 implementation contract

5 November 2026. Implements the request/socket-custody portion of the accepted
[reviewed H3 plan](Audio_Handover_Reviewed_High_Decision.md). This is not a new
handover design or a completed independent phone audio owner. Production remains
disabled; no automatic preparation, route, operating defaults or physical
network/audio qualification is enabled by these components.

`AudioCellularRequest` owns one `ConnectivityManager.requestNetwork` callback
for the candidate and its eventual active path. It requires explicit acquisition
and warm durations, a configured cellular-reachable target hint and local access
inputs. Absent mobile consent, background-data permission or confirmed direct
route policy denies preparation before a request. A VPN-required/unconfirmed
route cannot use this direct cellular adapter. This adapter does not implement
VPN routing or establish that a WAN route actually exists.

The request specifies CELLULAR and INTERNET, with a bounded acquisition timeout;
it does not request mutable VALIDATED. `onAvailable` alone permits no setup.
Capability, link and blocked-status snapshots must arrive for the same selected
Network. Metered/roaming inputs, blocking and VPN capabilities are checked before
work. Snapshot callbacks do not synchronously query ConnectivityManager.
The independent owner must supply and update actual consent, Data Saver and
VPN/default-policy observations; those passive subscriptions are still open.
See the primary [requestNetwork API](https://developer.android.com/reference/android/net/ConnectivityManager#requestNetwork(android.net.NetworkRequest,%20android.net.ConnectivityManager.NetworkCallback,%20int))
and [callback snapshot contract](https://developer.android.com/reference/android/net/ConnectivityManager.NetworkCallback#onCapabilitiesChanged(android.net.Network,%20android.net.NetworkCapabilities)).

`runNative` retains one synchronous dependent job through actual return. Its
binding resolves only the configured target on the selected Network and binds
a fresh unconnected native UDP descriptor through
[Network.bindSocket](https://developer.android.com/reference/android/net/Network#bindSocket(java.io.FileDescriptor)).
The caller runs this on its cold I/O worker and closes/joins all dependent
DNS/socket/JNI work before returning. Cancellation of a waiting coroutine is
not actual completion. No process binding, incumbent socket rebinding, default
network DNS, media copy or media-processing callback is introduced.

`AudioCellularCustody` supplies fake-clock-checkable transitions:

- Acquisition/warm deadlines use Android BOOTTIME. Exact availability retries
  do not re-anchor them; expired unused preparation retires.
- Pin the exact N2 RequestId before a potentially committing operation. Exact
  retries retain the same opaque pin; another request cannot replace it. A pin
  reserves custody only, with no proof issuance, readiness or media authority.
- Preparation cancellation after pin enters Reconciliation and keeps the
  callback/request. Warm TTL no longer independently releases it. Confirmed
  installation promotes that same lease into active custody, including when
  receipt reconciliation completes after transport loss.
- Network replacement never moves an existing socket/TLS owner. Network/policy
  denial is latched for this candidate, so late positive snapshots cannot revive
  it. A confirmed remote commit may still transfer its retained custody; it
  cannot make the failed socket usable or authorize media.
- Local Stop/expiry retires admission first. Unregister waits for the actual
  dependent job and any in-flight requestNetwork call. Android onUnavailable's
  automatic release is accounted for. Explicit unregister happens once; failure
  latches Quarantined and retains the exact owner instead of claiming cleanup.

The event sink only posts/coalesces bounded owner work, identifying the exact
request object. The outer independent source owner still must enforce one
automatic candidate/owned cellular request, generation-tag callbacks, schedule
the exposed deadline without polling, retain the real operation through uncertain
results, provide actual native shutdown and root-receipt installation, and
observe final release before replacement. Promotion here is **network custody**,
never a substitute for verified receipt/native authority. No legacy
`SpeakerController` default-route behavior was added to this path.

Focused checks use fake clocks, retained local threads and Robolectric Android
callback/socket simulations. They exercise cancellation during registration,
exact deadline boundaries, immutable pins, lost/changed networks, stale positive
snapshots, policy denial, actual blocked-work return, promotion/reconciliation,
pre-start Stop, simulated per-socket binding and exact callback release. No
actual DNS, cellular request, radio, JNI playback or physical device is used.
