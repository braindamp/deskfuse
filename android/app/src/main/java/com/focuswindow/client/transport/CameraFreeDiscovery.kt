package com.focuswindow.client.transport

import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.withContext
import java.net.DatagramPacket
import java.net.DatagramSocket
import java.net.InetAddress
import java.net.SocketTimeoutException
import android.util.Base64

/**
 * No account, camera, location, or Bluetooth permission.
 * Discovery is intentionally UNAUTHENTICATED: only the later Windows-owner
 * approval and matching short authentication string establish trust.
 */
data class DiscoveredPc(
    val address: String,
    val name: String,
    val sessionPort: Int,
    val enrollmentPort: Int,
) {
    companion object {
        fun manual(address: String, port: Int = 47810): DiscoveredPc {
            require(port in 1..65533)
            val host = address.trim().removePrefix("[").removeSuffix("]")
            require(host.isNotBlank() && host.length <= 253)
            return DiscoveredPc(host, host, port, port + 2)
        }
    }
}

object CameraFreeDiscovery {
    private const val PROBE = "DESKFUSE_DISCOVER_V1"
    private const val RESPONSE = "DESKFUSE_HOST_V1"
    private const val DISCOVERY_PORT = 47811

    suspend fun scan(): List<DiscoveredPc> = withContext(Dispatchers.IO) {
        val pcs = linkedMapOf<String, DiscoveredPc>()
        DatagramSocket().use { socket ->
            socket.broadcast = true
            socket.soTimeout = 350
            val query = PROBE.toByteArray(Charsets.US_ASCII)
            val to = InetAddress.getByName("255.255.255.255")
            // Repeat once because Wi-Fi power saving sometimes drops the first packet.
            repeat(2) {
                socket.send(DatagramPacket(query, query.size, to, DISCOVERY_PORT))
                val until = System.nanoTime() + 700_000_000L
                while (System.nanoTime() < until) {
                    val buffer = ByteArray(512)
                    val packet = DatagramPacket(buffer, buffer.size)
                    try { socket.receive(packet) } catch (_: SocketTimeoutException) { continue }
                    val text = String(packet.data, packet.offset, packet.length, Charsets.UTF_8)
                    val parts = text.split('|')
                    if (parts.size != 4 || parts[0] != RESPONSE) continue
                    val name = runCatching {
                        String(Base64.decode(parts[1], Base64.DEFAULT), Charsets.UTF_8)
                    }.getOrNull()?.take(100) ?: continue
                    val sessionPort = parts[2].toIntOrNull() ?: continue
                    val enrollPort = parts[3].toIntOrNull() ?: continue
                    if (sessionPort !in 1..65535 || enrollPort !in 1..65535) continue
                    val address = packet.address.hostAddress ?: continue
                    pcs["$address:$sessionPort"] = DiscoveredPc(address, name, sessionPort, enrollPort)
                }
            }
        }
        pcs.values.toList()
    }
}
