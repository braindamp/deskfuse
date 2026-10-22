package com.focuswindow.client.transport

import android.content.Context
import android.net.ConnectivityManager
import android.net.Network

/**
 * Tracks Android's currently selected network, not a socket timeout or a
 * fixed retry budget. Airplane mode / no default network is a normal,
 * user-controlled suspension: it must never become a connection failure.
 *
 * In this LAN implementation "available" means an Android default network
 * exists, not that the host is reachable across the internet. Local Wi-Fi can
 * reach a DeskFuse PC even if it lacks validated public internet access.
 *
 * onChanged fires for BOTH route replacement and loss/restoration. The
 * controllers retire their existing TCP session and reauthenticate only
 * when there is a usable route. No background polling or extra permissions.
 */
internal class DefaultNetworkHandoff(
    context: Context,
    private val onChanged: (Boolean) -> Unit,
) : AutoCloseable {
    private val manager = context.getSystemService(ConnectivityManager::class.java)
    private val gate = Any()
    private var current: Network? = manager.activeNetwork
    private var closed = false

    val isAvailable: Boolean get() = synchronized(gate) { current != null }

    private val callback = object : ConnectivityManager.NetworkCallback() {
        override fun onAvailable(network: Network) {
            val changed = synchronized(gate) {
                if (closed || current == network) false
                else {
                    current = network
                    true
                }
            }
            if (changed) onChanged(true)
        }

        override fun onLost(network: Network) {
            val changed = synchronized(gate) {
                if (closed || current != network) false
                else {
                    current = null
                    true
                }
            }
            if (changed) onChanged(false)
        }
    }

    init {
        manager.registerDefaultNetworkCallback(callback)
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
