package com.focuswindow.client.transport

import android.content.Context
import android.net.ConnectivityManager
import android.net.NetworkCapabilities
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.withContext
import java.net.DatagramPacket
import java.net.DatagramSocket
import java.net.SocketTimeoutException
import java.util.UUID

/** Selected public locator and enrollment identity; preferences never serialize a grant or signing key. */
internal data class SelectedTrustedSpeaker(
    val enrollment: TrustedCommittedEnrollment,
    val hostHash: ByteArray,
    val id: UUID,
    val hostHint: String,
)

internal object TrustedSpeakerSelection {
    private const val STORE = "deskfuse_trusted_speaker_selection_v1"

    fun save(context: Context, committed: TrustedCommittedEnrollment, hostHint: String) {
        committed.requireCurrent()
        require(hostHint.length in 1..253 && hostHint.none { it.isISOControl() || it.isWhitespace() })
        val binding = TrustedEnrollmentProtocol.binding(committed.transcript())
        val hash = binding.hostHash.joinToString("") { "%02x".format(it.toInt() and 255) }
        check(context.getSharedPreferences(STORE, Context.MODE_PRIVATE).edit()
            .putString("hostHash", hash)
            .putString("enrollment", binding.enrollment.toString())
            .putString("hostHint", hostHint).commit()) { "Could not save selected trusted PC" }
    }

    fun resolve(context: Context): SelectedTrustedSpeaker? {
        val preferences = context.getSharedPreferences(STORE, Context.MODE_PRIVATE)
        val hash = preferences.getString("hostHash", null) ?: return null
        val id = preferences.getString("enrollment", null) ?: return null
        val host = preferences.getString("hostHint", null) ?: return null
        require(hash.length == 64 && hash.all { it in '0'..'9' || it in 'a'..'f' })
        require(host.length in 1..253 && host.none { it.isISOControl() || it.isWhitespace() })
        val digest = ByteArray(32) { index -> hash.substring(index * 2, index * 2 + 2).toInt(16).toByte() }
        val enrollmentId = UUID.fromString(id)
        val committed = TrustedEnrollmentRepository(AndroidTrustedEnrollmentStore(context))
            .committed(digest, enrollmentId)
        committed.requireCurrent()
        require(BrokerScope.SPEAKER in committed.transcript().proposal.scopes)
        return SelectedTrustedSpeaker(committed, digest, enrollmentId, host)
    }
}

/** Bounded public prepare/locate hint; host proof and PCM admission remain entirely in the native trusted path. */
internal object TrustedSpeakerLocator {
    internal data class Target(val host: String, val port: Int)

    suspend fun locate(context: Context, selected: SelectedTrustedSpeaker): Target? = withContext(Dispatchers.IO) {
        val connectivity = context.getSystemService(ConnectivityManager::class.java)
        val network = connectivity.activeNetwork ?: return@withContext null
        val capabilities = connectivity.getNetworkCapabilities(network) ?: return@withContext null
        if (!capabilities.hasTransport(NetworkCapabilities.TRANSPORT_WIFI) &&
            !capabilities.hasTransport(NetworkCapabilities.TRANSPORT_ETHERNET)) return@withContext null
        if (capabilities.hasTransport(NetworkCapabilities.TRANSPORT_VPN)) return@withContext null

        val payload = ("DESKFUSE_SPEAKER_PREPARE_V1|" + selected.id).toByteArray(Charsets.US_ASCII)
        val address = network.getByName(selected.hostHint)
        DatagramSocket().use { socket ->
            network.bindSocket(socket)
            socket.soTimeout = 400
            repeat(2) {
                socket.send(DatagramPacket(payload, payload.size, address, 47814))
                try {
                    val buffer = ByteArray(160)
                    val received = DatagramPacket(buffer, buffer.size)
                    socket.receive(received)
                    if (received.address != address) return@repeat
                    val parts = String(received.data, received.offset, received.length, Charsets.US_ASCII).split('|')
                    if (parts.size == 3 && parts[0] == "DESKFUSE_SPEAKER_READY_V1" &&
                        parts[1] == selected.id.toString()) {
                        val port = parts[2].toIntOrNull()
                        if (port != null && port in 1..65535)
                            return@withContext Target(received.address.hostAddress ?: return@repeat, port)
                    }
                } catch (_: SocketTimeoutException) { }
            }
            null
        }
    }
}
