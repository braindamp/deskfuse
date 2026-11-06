package com.focuswindow.client.transport

import android.content.BroadcastReceiver
import android.content.Context
import android.content.Intent
import android.content.IntentFilter
import android.net.ConnectivityManager
import android.net.Network
import android.net.NetworkCapabilities
import android.net.NetworkRequest
import android.os.Handler
import android.os.Looper
import androidx.core.content.ContextCompat
import java.net.Inet6Address
import java.net.InetAddress
import java.util.concurrent.CountDownLatch
import java.util.concurrent.TimeUnit
import java.util.concurrent.atomic.AtomicBoolean

internal class AuthorityAudioRun {
    private val gate = Any()
    val wake = CountDownLatch(1)
    private var fence: (() -> Unit)? = null
    @Volatile var isStopped = false
        private set

    fun attach(stopAudio: () -> Unit) = synchronized(gate) {
        check(fence == null)
        if (isStopped) stopAudio() else fence = stopAudio
    }

    fun requireLive() = check(!isStopped) { "Audio run stopped" }

    fun stop() = synchronized(gate) {
        if (isStopped) return@synchronized
        isStopped = true
        fence?.invoke()
        wake.countDown()
    }
}

internal class AuthorityAudioRoutes(private val context: Context) {
    private val manager = context.getSystemService(ConnectivityManager::class.java)
    private val main = Handler(Looper.getMainLooper())

    fun lan(target: AudioCellularTarget, run: AuthorityAudioRun, play: (String, Long, () -> Unit) -> Unit) {
        val network = checkNotNull(manager.activeNetwork) { "No selected LAN network" }
        val gate = Any()
        var eligible = false
        var blocked: Boolean? = null
        var admitted = false
        var retired = false
        fun retire() {
            if (retired) return
            retired = true
            run.wake.countDown()
            main.post { run.stop() }
        }
        fun observe() {
            if (blocked == true || admitted && !eligible) retire()
            else if (!retired && eligible && blocked == false) run.wake.countDown()
        }
        val callback = object : ConnectivityManager.NetworkCallback() {
            override fun onCapabilitiesChanged(value: Network, caps: NetworkCapabilities) {
                if (value != network) return
                synchronized(gate) {
                    eligible = caps.hasTransport(NetworkCapabilities.TRANSPORT_WIFI) ||
                        caps.hasTransport(NetworkCapabilities.TRANSPORT_ETHERNET) ||
                        caps.hasTransport(NetworkCapabilities.TRANSPORT_VPN)
                    observe()
                }
            }
            override fun onBlockedStatusChanged(value: Network, valueBlocked: Boolean) {
                if (value != network) return
                synchronized(gate) { blocked = valueBlocked; observe() }
            }
            override fun onLost(value: Network) {
                if (value != network) return
                synchronized(gate) { retire() }
            }
        }
        manager.registerNetworkCallback(NetworkRequest.Builder().clearCapabilities().build(), callback)
        try {
            check(run.wake.await(ACQUIRE_MS.toLong(), TimeUnit.MILLISECONDS)) { "LAN network unavailable" }
            val requireRoute = {
                run.requireLive()
                synchronized(gate) { check(!retired && eligible && blocked == false) }
            }
            synchronized(gate) { requireRoute(); admitted = true }
            val address = network.getAllByName(target.host).first()
            requireRoute()
            play(endpoint(address, target.port), network.networkHandle, requireRoute)
        } finally {
            manager.unregisterNetworkCallback(callback)
        }
    }

    fun cellular(target: AudioCellularTarget, run: AuthorityAudioRun,
        play: (String, Long, () -> Unit, (ByteArray) -> Unit, () -> Unit) -> Unit) {
        val entered = AtomicBoolean()
        fun access(): AudioCellularAccess {
            val active = manager.activeNetwork?.let(manager::getNetworkCapabilities)
            val direct = active?.hasCapability(NetworkCapabilities.NET_CAPABILITY_NOT_VPN) ?: entered.get()
            return AudioCellularAccess(
                mobileDataConsent = true, allowMetered = true, allowRoaming = false,
                backgroundDataAllowed = manager.restrictBackgroundStatus == ConnectivityManager.RESTRICT_BACKGROUND_STATUS_DISABLED,
                route = if (direct) AudioCellularAccess.Route.DIRECT_ALLOWED else AudioCellularAccess.Route.UNCONFIRMED,
            )
        }
        val request = AudioCellularRequest(context, ACQUIRE_MS, WARM_MS, target, access()) {
            if (it.usable()) run.wake.countDown()
            else if (entered.get()) main.post { run.stop() }
            else if (it.phase in setOf(AudioCellularCustody.Phase.RETIRING, AudioCellularCustody.Phase.RELEASED,
                    AudioCellularCustody.Phase.QUARANTINED)) run.wake.countDown()
        }
        fun refresh() { main.post { request.updateAccess(access()) } }
        val policy = object : ConnectivityManager.NetworkCallback() {
            override fun onAvailable(network: Network) = refresh()
            override fun onLost(network: Network) = refresh()
            override fun onCapabilitiesChanged(network: Network, caps: NetworkCapabilities) = refresh()
        }
        val dataSaver = object : BroadcastReceiver() {
            override fun onReceive(context: Context, intent: Intent) = refresh()
        }
        manager.registerDefaultNetworkCallback(policy)
        try {
            ContextCompat.registerReceiver(context, dataSaver,
                IntentFilter(ConnectivityManager.ACTION_RESTRICT_BACKGROUND_CHANGED),
                ContextCompat.RECEIVER_NOT_EXPORTED)
            try {
                run.requireLive()
                check(request.start())
                check(run.wake.await(ACQUIRE_MS.toLong(), TimeUnit.MILLISECONDS)) { "Cellular network unavailable" }
                run.requireLive()
                request.runNative { binding ->
                    entered.set(true)
                    try {
                        val requireRoute = {
                            run.requireLive()
                            check(access().admitted()) { "Cellular policy changed" }
                            binding.requireCurrent()
                        }
                        requireRoute()
                        val address = binding.resolveTarget().first()
                        var pin: AudioCellularCustody.Pin? = null
                        play(endpoint(address, binding.port), binding.networkHandle, requireRoute,
                            { id -> pin = binding.pin(id) }, { request.promote(checkNotNull(pin)) })
                        0
                    } finally { entered.set(false) }
                }
            } finally {
                context.unregisterReceiver(dataSaver)
            }
        } finally {
            request.retire()
            manager.unregisterNetworkCallback(policy)
        }
    }

    private fun endpoint(address: InetAddress, port: Int): String {
        if (address !is Inet6Address) return "${address.hostAddress}:$port"
        val literal = InetAddress.getByAddress(address.address).hostAddress
        val scope = if (address.scopeId == 0) "" else "%${address.scopeId}"
        return "[$literal$scope]:$port"
    }

    private companion object {
        const val ACQUIRE_MS = 5_000
        const val WARM_MS = 15_000
    }
}
