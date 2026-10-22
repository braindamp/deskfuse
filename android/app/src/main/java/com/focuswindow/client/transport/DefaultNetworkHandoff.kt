package com.focuswindow.client.transport

import android.content.Context
import android.net.ConnectivityManager
import android.net.Network
import android.os.SystemClock

/**
 * Observes the OS-selected default network. There is no polling, location
 * permission, VPN, background scan, or Wi-Fi-only assumption.
 *
 * Wi-Fi -> mobile data creates a new default Network, but the old TCP socket
 * does NOT move with it. The caller must close the old socket, then establish
 * a newly authenticated session over the replacement network.
 *
 * The listener reacts to network change, NOT "internet availability": we
 * currently only support reachable LAN/VPN endpoints. A future DeskFuse-owned
 * rendezvous/relay transport supplies an address valid across networks.
 */
internal class DefaultNetworkHandoff(
    context: Context,
    private val onChanged: () -> Unit,
) : AutoCloseable {
    private val manager = context.getSystemService(ConnectivityManager::class.java)
    private val gate = Any()
    private var current: Network? = manager.activeNetwork
    private var closed = false
    private var lastNotifiedMs = Long.MIN_VALUE / 2
    private val callback = object : ConnectivityManager.NetworkCallback() {
        override fun onAvailable(network: Network) {
            val changed = synchronized(gate) {
                if (closed || current == network) false
                else {
                    current = network
                    true
                }
            }
            if (changed) notifyChanged()
        }

        override fun onLost(network: Network) {
            val changed = synchronized(gate) {
                if (closed || current != network) false
                else {
                    current = null
                    true
                }
            }
            if (changed) notifyChanged()
        }
    }

    init {
        manager.registerDefaultNetworkCallback(callback)
    }

    private fun notifyChanged() {
        val shouldNotify = synchronized(gate) {
            if (closed) false
            else {
                val now = SystemClock.elapsedRealtime()
                if (now - lastNotifiedMs < 300L) false
                else {
                    lastNotifiedMs = now
                    true
                }
            }
        }
        if (shouldNotify) onChanged()
    }

    override fun close() {
        val wasOpen = synchronized(gate) {
            if (closed) false else {
                closed = true
                true
            }
        }
        if (wasOpen) manager.unregisterNetworkCallback(callback)
    }
}
