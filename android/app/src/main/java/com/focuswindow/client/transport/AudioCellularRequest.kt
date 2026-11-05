package com.focuswindow.client.transport

import android.content.Context
import android.net.ConnectivityManager
import android.net.LinkProperties
import android.net.Network
import android.net.NetworkCapabilities
import android.net.NetworkRequest
import android.os.SystemClock
import java.io.FileDescriptor
import java.net.InetAddress

/** Immutable inputs from local consent/route policy and passive network snapshots.
 * UNCONFIRMED/VPN_REQUIRED never permit a direct cellular socket. These inputs
 * are not host authentication; the actual native TLS/root-proof checks still apply.
 */
internal data class AudioCellularAccess(
    val mobileDataConsent: Boolean,
    val allowMetered: Boolean,
    val allowRoaming: Boolean,
    val backgroundDataAllowed: Boolean,
    val route: Route,
) {
    enum class Route { UNCONFIRMED, DIRECT_ALLOWED, VPN_REQUIRED }
    fun admitted() = mobileDataConsent && backgroundDataAllowed && route == Route.DIRECT_ALLOWED
}

/** Hint from an explicitly configured cellular-reachable route. Neither DNS
 * nor this value authenticates a host or creates a relay/NAT route. */
internal data class AudioCellularTarget(val host: String, val port: Int) {
    init { require(host.isNotBlank() && host.length <= 253 && '\u0000' !in host && port in 1..65535) }
}

/** One owned requestNetwork callback for one candidate/active path. It is not
 * tied to an Activity, workspace controller or default-route lifetime. The
 * independent audio owner supplies explicit policy, schedules deadline(), and
 * handles changes by closing/joining its own native/socket job when necessary.
 * changed must only post/coalesce bounded owner work, never perform I/O or
 * positive authorization on a ConnectivityManager callback thread.
 */
internal class AudioCellularRequest(
    context: Context,
    private val acquisitionMilliseconds: Int,
    warmMilliseconds: Int,
    private val target: AudioCellularTarget,
    initialAccess: AudioCellularAccess,
    private val changed: (AudioCellularRequest) -> Unit,
) {
    private val manager = context.applicationContext.getSystemService(ConnectivityManager::class.java)
    private val gate = Any()
    private val custody = AudioCellularCustody(acquisitionMilliseconds, warmMilliseconds, SystemClock::elapsedRealtimeNanos)
    private var access = initialAccess
    private var started = false
    private var selected: Network? = null
    private var capabilities: NetworkCapabilities? = null
    private var linkUsable: Boolean? = null
    private var blocked: Boolean? = null
    @Volatile var failure: Exception? = null
        private set
    val phase get() = custody.phase()
    fun deadline() = custody.deadline()

    private val callback = object : ConnectivityManager.NetworkCallback() {
        override fun onAvailable(network: Network) {
            synchronized(gate) {
                if (custody.available(network.networkHandle) && selected == null) selected = network
            }
            publish()
        }
        override fun onCapabilitiesChanged(network: Network, value: NetworkCapabilities) {
            synchronized(gate) {
                if (selected == network) { capabilities = NetworkCapabilities(value); observeLocked() }
            }
            publish()
        }
        override fun onLinkPropertiesChanged(network: Network, value: LinkProperties) {
            synchronized(gate) {
                if (selected == network) {
                    linkUsable = value.linkAddresses.isNotEmpty() && value.routes.isNotEmpty(); observeLocked()
                }
            }
            publish()
        }
        override fun onBlockedStatusChanged(network: Network, value: Boolean) {
            synchronized(gate) {
                if (selected == network) { blocked = value; observeLocked() }
            }
            publish()
        }
        override fun onLost(network: Network) { custody.lost(network.networkHandle); publish() }
        override fun onUnavailable() { custody.unavailable(); publish() }
    }
    fun start(): Boolean {
        synchronized(gate) { check(!started); started = true }
        if (!synchronized(gate) { access.admitted() } || custody.phase() != AudioCellularCustody.Phase.ACQUIRING) {
            custody.registrationFinished(false); publish(); return false
        }
        try {
            // VALIDATED is mutable callback information, never a requested capability.
            val request = NetworkRequest.Builder().addTransportType(NetworkCapabilities.TRANSPORT_CELLULAR)
                .addCapability(NetworkCapabilities.NET_CAPABILITY_INTERNET).build()
            manager.requestNetwork(request, callback, acquisitionMilliseconds)
            custody.registrationFinished(true)
        } catch (error: Exception) {
            failure = error; custody.registrationFinished(false); publish(); return false
        }
        publish(); return true
    }
    /** Called on consent, Data Saver or VPN/default-network policy changes.
     * Callback snapshots above never synchronously query ConnectivityManager. */
    fun updateAccess(value: AudioCellularAccess) {
        synchronized(gate) {
            access = value
            if (!value.admitted()) custody.cancelPreparation()
            observeLocked()
        }
        publish()
    }
    private fun observeLocked() {
        val network = selected ?: return
        val caps = capabilities ?: return
        // Wait for the ordered callback snapshots, rather than treating onAvailable
        // as permission to use a network or querying potentially stale snapshots.
        val isBlocked = blocked ?: return
        val hasLink = linkUsable ?: return
        val allowed = access.admitted() && !isBlocked &&
            caps.hasTransport(NetworkCapabilities.TRANSPORT_CELLULAR) &&
            !caps.hasTransport(NetworkCapabilities.TRANSPORT_VPN) &&
            caps.hasCapability(NetworkCapabilities.NET_CAPABILITY_NOT_VPN) &&
            caps.hasCapability(NetworkCapabilities.NET_CAPABILITY_INTERNET) &&
            (access.allowMetered || caps.hasCapability(NetworkCapabilities.NET_CAPABILITY_NOT_METERED)) &&
            (access.allowRoaming || caps.hasCapability(NetworkCapabilities.NET_CAPABILITY_NOT_ROAMING)) &&
            hasLink
        custody.observation(network.networkHandle, allowed)
    }
    /** Synchronous actual DNS/socket/native work on the caller's cold I/O worker.
     * Coroutine cancellation alone must not return from this scope while JNI/DNS
     * still runs. The finally block is the actual dependent-I/O completion barrier.
     */
    fun runNative(action: (Binding) -> Int): Int {
        val (network, work) = synchronized(gate) {
            val network = checkNotNull(selected)
            network to custody.beginWork(network.networkHandle)
        }
        return try { action(Binding(network, work)) }
        finally { custody.workCompleted(work); publish() }
    }
    inner class Binding internal constructor(private val network: Network, private val work: AudioCellularCustody.Work) {
        fun requireCurrent() = custody.requireWork(work, network.networkHandle)
        val port get() = target.port
        fun resolveTarget(): Array<InetAddress> {
            requireCurrent()
            return network.getAllByName(target.host).also { requireCurrent() }
        }
        /** Native creates a fresh unconnected UDP descriptor and calls this
         * before connect. Never pass the incumbent socket or bind the process. */
        fun bindUnconnectedSocket(fd: FileDescriptor) {
            requireCurrent(); network.bindSocket(fd); requireCurrent()
        }
        fun pin(requestId: ByteArray): AudioCellularCustody.Pin = custody.pin(work, requestId)
    }
    /** Retain the same request after commit; this cannot authorize media. */
    fun promote(pin: AudioCellularCustody.Pin) { custody.promote(pin); publish() }
    fun cancelPreparation() { custody.cancelPreparation(); publish() }
    fun checkDeadline() { custody.phase(); publish() }
    fun retire() { custody.retire(); publish() }
    private fun publish() {
        synchronized(gate) {
            if (!started && custody.phase() == AudioCellularCustody.Phase.RETIRING) {
                started = true; custody.registrationFinished(false)
            }
        }
        val registered = custody.beginRelease()
        if (registered != null) {
            var succeeded = false
            try { if (registered) manager.unregisterNetworkCallback(callback); succeeded = true }
            catch (error: Exception) { failure = error }
            finally { custody.releaseCompleted(succeeded) }
        }
        changed(this)
    }
}
