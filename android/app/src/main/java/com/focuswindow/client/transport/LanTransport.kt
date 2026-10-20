package com.focuswindow.client.transport

import com.focuswindow.client.protocol.ClientMessage
import com.focuswindow.client.protocol.FrameHeader
import com.focuswindow.client.protocol.ServerMessage
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.channels.BufferOverflow
import kotlinx.coroutines.flow.MutableSharedFlow
import kotlinx.coroutines.flow.SharedFlow
import kotlinx.coroutines.flow.asSharedFlow
import kotlinx.coroutines.isActive
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext
import kotlinx.serialization.json.Json
import java.io.BufferedInputStream
import java.io.BufferedOutputStream
import java.io.DataInputStream
import java.io.DataOutputStream
import java.net.InetSocketAddress
import java.net.Socket
import java.util.concurrent.atomic.AtomicBoolean

/** A decoded-but-not-yet-rendered frame, straight off the wire. */
class WireFrame(
    val header: FrameHeader,
    val payload: ByteArray,
    val arrivedAtNanos: Long = System.nanoTime(),
)

/** Audio PCM payloads are independent and may be dropped to avoid old sound. */
data class PcmAudioChunk(val streamId: Int, val pcm: ByteArray)

/**
 * FR-TRUST-03 — direct LAN transport is the default and the only path required
 * for the free tier. One TCP connection carries two logical channels:
 *
 *   * control — reliable, ordered, JSON, encrypted (small)
 *   * frames  — the encoded video payload, encrypted, paced by the host
 *
 * Frames are tagged with the target version so a late frame is dropped rather
 * than rendered against a stale rectangle (FR-ENC-05).
 *
 * TCP is used rather than a datagram protocol because screen content is not
 * loss-tolerant the way camera video is: a dropped slice of text is a visible
 * corruption that persists until the next keyframe. The pacer on the host side
 * is what keeps latency bounded (FR-REF-02).
 */
class LanTransport(
    private val scope: CoroutineScope,
    private val json: Json = Json {
        ignoreUnknownKeys = true
        encodeDefaults = true
        classDiscriminator = "type"
    },
) {

    private val _control = MutableSharedFlow<ServerMessage>(
        replay = 0,
        extraBufferCapacity = 64,
        onBufferOverflow = BufferOverflow.SUSPEND, // never discard approval/target/control state
    )
    val control: SharedFlow<ServerMessage> = _control.asSharedFlow()
    private val _audio = MutableSharedFlow<PcmAudioChunk>(
        extraBufferCapacity = 8,
        onBufferOverflow = BufferOverflow.DROP_OLDEST,
    )
    val audio: SharedFlow<PcmAudioChunk> = _audio.asSharedFlow()

    private val _frames = MutableSharedFlow<WireFrame>(
        replay = 0,
        extraBufferCapacity = 8,
        // H.264 inter frames depend on preceding frames. Silent DROP_OLDEST causes
        // decode corruption: MediaCodec cannot detect which reference was lost.
        // Backpressure the reader instead; the host's four-frame send gate then
        // forces an IDR resync rather than hiding a loss.
        onBufferOverflow = BufferOverflow.SUSPEND,
    )
    val frames: SharedFlow<WireFrame> = _frames.asSharedFlow()

    private val _events = MutableSharedFlow<TransportEvent>(
        replay = 1,
        extraBufferCapacity = 16,
        onBufferOverflow = BufferOverflow.DROP_OLDEST,
    )
    val events: SharedFlow<TransportEvent> = _events.asSharedFlow()

    private var socket: Socket? = null
    private var input: DataInputStream? = null
    private var output: DataOutputStream? = null
    private var channel: SecureChannel? = null
    @Volatile private var writer: SecureRecordWriter? = null
    private val running = AtomicBoolean(false)

    sealed interface TransportEvent {
        data class Connecting(val host: String, val port: Int) : TransportEvent
        data class Verified(val code: String, val hostName: String) : TransportEvent
        data object Ready : TransportEvent
        data class Closed(val reason: String, val recoverable: Boolean) : TransportEvent
        data class Failed(val stage: String, val detail: String, val remediation: String?) :
            TransportEvent
    }

    val verificationCode: String? get() = channel?.verificationCode

    suspend fun connect(payload: PairingPayload) = withContext(Dispatchers.IO) {
        if (!running.compareAndSet(false, true)) return@withContext
        _events.emit(TransportEvent.Connecting(payload.host, payload.port))

        try {
            val s = Socket().apply {
                tcpNoDelay = true            // latency over throughput
                keepAlive = true
                soTimeout = READ_TIMEOUT_MS
                connect(InetSocketAddress(payload.host, payload.port), CONNECT_TIMEOUT_MS)
            }
            socket = s
            val inStream = DataInputStream(BufferedInputStream(s.getInputStream(), 1 shl 16))
            val outStream = DataOutputStream(BufferedOutputStream(s.getOutputStream(), 1 shl 15))
            input = inStream
            output = outStream

            val secure = SecureChannel.handshake(
                input = inStream,
                output = outStream,
                hostPublicKeyB64 = payload.hostPublicKeyB64,
                pairingSecret = payload.pairingSecret,
            )
            channel = secure
            writer = SecureRecordWriter(outStream, secure)
            _events.emit(TransportEvent.Verified(secure.verificationCode, payload.hostName))
            _events.emit(TransportEvent.Ready)

            scope.launch(Dispatchers.IO) { readLoop(inStream, secure) }
        } catch (t: Throwable) {
            running.set(false)
            // FR-OPS-03 — a failure is a NAMED stage with a remediation, never
            // a black screen.
            _events.emit(diagnose(t, payload))
            closeQuietly()
        }
    }

    private suspend fun readLoop(inStream: DataInputStream, secure: SecureChannel) {
        try {
            while (scope.isActive && running.get()) {
                when (val kind = inStream.readByte().toInt()) {
                    KIND_CONTROL -> {
                        val len = inStream.readInt()
                        require(len in 1..MAX_CONTROL_BYTES) { "control frame too large: $len" }
                        val sealed = ByteArray(len).also { inStream.readFully(it) }
                        val plain = secure.open(sealed)
                        val msg = json.decodeFromString(
                            ServerMessage.serializer(),
                            plain.decodeToString(),
                        )
                        _control.emit(msg)
                    }

                    KIND_FRAME -> {
                        val len = inStream.readInt()
                        require(len in 1..MAX_FRAME_BYTES) { "video frame too large: $len" }
                        val sealed = ByteArray(len).also { inStream.readFully(it) }
                        val plain = secure.open(sealed)
                        _frames.emit(parseFrame(plain))
                    }

                    KIND_AUDIO -> {
                        val len = inStream.readInt()
                        require(len in 30..32796) { "audio frame too large: $len" }
                        val sealed = ByteArray(len).also { inStream.readFully(it) }
                        val plain = secure.open(sealed)
                        require(plain.size in 8..32772 && (plain.size - 4) % 2 == 0) {
                            "invalid PCM packet"
                        }
                        val streamId = java.nio.ByteBuffer.wrap(plain, 0, 4)
                            .order(java.nio.ByteOrder.BIG_ENDIAN).int
                        _audio.emit(PcmAudioChunk(streamId, plain.copyOfRange(4, plain.size)))
                    }

                    KIND_PING -> {
                        // Keeps NAT/firewall state alive on long idle sessions
                        // where the pacer is sending 0 fps (FR-REF-02).
                        writer?.pong()
                    }

                    -1 -> throw java.io.EOFException("host closed the connection")
                    else -> throw IllegalStateException("unknown frame kind: $kind")
                }
            }
        } catch (t: Throwable) {
            // Clear running BEFORE notifying the session controller. Otherwise its
            // reconnect attempts see running=true and return without connecting.
            val lost = running.getAndSet(false)
            closeQuietly()
            if (lost) {
                _events.emit(TransportEvent.Closed(t.message ?: "connection lost", recoverable = true))
            }
        }
    }

    private fun parseFrame(plain: ByteArray): WireFrame {
        require(plain.size >= FrameHeader.SIZE_BYTES) { "truncated frame header" }
        var i = 0
        fun u16(): Int = ((plain[i++].toInt() and 0xFF) shl 8) or (plain[i++].toInt() and 0xFF)
        fun u32(): Long {
            var v = 0L
            repeat(4) { v = (v shl 8) or (plain[i++].toLong() and 0xFF) }
            return v
        }
        fun u64(): Long {
            var v = 0L
            repeat(8) { v = (v shl 8) or (plain[i++].toLong() and 0xFF) }
            return v
        }

        val magic = u16()
        require(magic == FrameHeader.MAGIC) { "bad frame magic" }
        val flags = u16()
        val targetVersion = u32()
        val ptsUs = u64()
        val len = u32().toInt()
        require(len >= 0 && i + len == plain.size) { "frame length mismatch" }

        return WireFrame(
            header = FrameHeader(
                keyframe = flags and FrameHeader.FLAG_KEYFRAME != 0,
                targetVersion = targetVersion,
                ptsUs = ptsUs,
                payloadLength = len,
            ),
            payload = plain.copyOfRange(i, i + len),
        )
    }

    suspend fun send(message: ClientMessage) = withContext(Dispatchers.IO) {
        val sender = writer ?: return@withContext
        val bytes = json.encodeToString(ClientMessage.serializer(), message).toByteArray()
        try {
            sender.control(bytes)
        } catch (t: java.io.IOException) {
            // Never silently swallow a failed Hello or keyframe request.
            val lost = running.getAndSet(false)
            closeQuietly()
            if (lost) _events.emit(TransportEvent.Closed(t.message ?: "send failed", recoverable = true))
        }
    }

    /** Forward only host-authorized microphone stream packets.
     *  Returns false if the authenticated transport disappeared.
     */
    suspend fun sendPhoneMicrophone(streamId: Int, pcm16le: ByteArray): Boolean =
        withContext(Dispatchers.IO) {
            val sender = writer ?: return@withContext false
            if (!running.get()) return@withContext false
            try {
                sender.microphone(streamId, pcm16le)
                true
            } catch (ex: java.io.IOException) {
                val lost = running.getAndSet(false)
                closeQuietly()
                if (lost) _events.emit(TransportEvent.Closed(
                    ex.message ?: "microphone send failed", recoverable = true))
                false
            }
        }

    suspend fun close(reason: String) {
        if (!running.get()) return
        runCatching { send(ClientMessage.Bye(reason)) }
        running.set(false)
        closeQuietly()
        _events.emit(TransportEvent.Closed(reason, recoverable = false))
    }

    private fun closeQuietly() {
        runCatching { input?.close() }
        runCatching { output?.close() }
        runCatching { socket?.close() }
        input = null
        output = null
        socket = null
        channel = null
        writer = null
    }

    /**
     * Turns an exception into something a person can act on. This is the single
     * highest-leverage piece of UX in a driver+codec product: "black screen on
     * connect" is the category's signature failure.
     */
    private fun diagnose(t: Throwable, payload: PairingPayload): TransportEvent.Failed = when (t) {
        is java.net.SocketTimeoutException -> TransportEvent.Failed(
            stage = "connect",
            detail = "${payload.host} did not answer on port ${payload.port}.",
            remediation = "Check the host agent is running and that both devices are on the same network.",
        )

        is java.net.ConnectException -> TransportEvent.Failed(
            stage = "connect",
            detail = "Connection refused by ${payload.host}:${payload.port}.",
            remediation = "The agent may have stopped, or a firewall is blocking the port.",
        )

        is java.net.UnknownHostException -> TransportEvent.Failed(
            stage = "resolve",
            detail = "Cannot resolve ${payload.host}.",
            remediation = "Choose the PC again; its network address may have changed.",
        )

        is IllegalArgumentException -> TransportEvent.Failed(
            stage = "verify",
            detail = t.message ?: "The host failed verification.",
            remediation = "Pair this phone with the PC again.",
        )

        else -> TransportEvent.Failed(
            stage = "handshake",
            detail = t.message ?: t::class.java.simpleName,
            remediation = "Restart the host agent, then re-pair.",
        )
    }

    private companion object {
        const val KIND_CONTROL = 1
        const val KIND_FRAME = 2
        const val KIND_PING = 3
        const val KIND_AUDIO = 5

        const val CONNECT_TIMEOUT_MS = 4_000
        const val READ_TIMEOUT_MS = 20_000
        const val MAX_CONTROL_BYTES = 1 shl 20
        const val MAX_FRAME_BYTES = 1 shl 24
    }
}
