package com.focuswindow.client.transport

import android.util.Base64
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.withContext
import java.net.DatagramPacket
import java.net.DatagramSocket
import java.net.InetAddress
import java.net.SocketTimeoutException

internal data class TrustedSetupLocation(val address: String, val port: Int, val name: String)

/** Public setup locators only. Neither discovery nor the displayed name establishes trust. */
internal object TrustedSetupDiscovery {
    suspend fun scan(): List<TrustedSetupLocation> = withContext(Dispatchers.IO) {
        val found = linkedMapOf<String, TrustedSetupLocation>()
        DatagramSocket().use { socket ->
            socket.broadcast = true; socket.soTimeout = 250
            val query = "DESKFUSE_TRUSTED_SETUP_V1".toByteArray(Charsets.US_ASCII)
            repeat(2) {
                socket.send(DatagramPacket(query, query.size, InetAddress.getByName("255.255.255.255"), 47813))
                val end = System.nanoTime() + 700_000_000
                while (System.nanoTime() < end) {
                    val bytes = ByteArray(512); val packet = DatagramPacket(bytes, bytes.size)
                    try { socket.receive(packet) } catch (_: SocketTimeoutException) { continue }
                    val location = parse(packet) ?: continue
                    if (found.size < 64) found["${location.address}:${location.port}"] = location
                }
            }
        }
        found.values.toList()
    }
    /** Resolve only the selected public address. Its reply still confers no trust. */
    suspend fun locate(host: String): TrustedSetupLocation? = withContext(Dispatchers.IO) {
        val address = InetAddress.getByName(host)
        DatagramSocket().use { socket ->
            socket.soTimeout = 250
            val query = "DESKFUSE_TRUSTED_SETUP_V1".toByteArray(Charsets.US_ASCII)
            repeat(2) {
                socket.send(DatagramPacket(query, query.size, address, 47813))
                val until = System.nanoTime() + 750_000_000L
                while (System.nanoTime() < until) {
                    val packet = DatagramPacket(ByteArray(512), 512)
                    try { socket.receive(packet) } catch (_: SocketTimeoutException) { continue }
                    if (packet.address != address || packet.port != 47813) continue
                    parse(packet)?.let { return@withContext it }
                }
            }
        }
        null
    }

    private fun parse(packet: DatagramPacket): TrustedSetupLocation? {
        val parts = String(packet.data, packet.offset, packet.length, Charsets.US_ASCII).split('|')
        if (parts.size != 3 || parts[0] != "DESKFUSE_TRUSTED_SETUP_HOST_V1") return null
        val port = parts[2].toIntOrNull()?.takeIf { it in 1..65535 } ?: return null
        val bytes = runCatching { Base64.decode(parts[1], Base64.NO_WRAP) }.getOrNull()?.takeIf { it.size <= 128 } ?: return null
        val name = String(bytes, Charsets.UTF_8).filterNot { it.isISOControl() || it in '\u202a'..'\u202e' || it in '\u2066'..'\u2069' }.take(80)
        return TrustedSetupLocation(packet.address.hostAddress ?: return null, port, name)
    }

}
