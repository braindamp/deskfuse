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
data class PcmAudioChunk(
    val streamId: Int,
    val pcm: ByteArray,
    val receiveSequence: Long = 0L,
    val receivedAtNanos: Long = 0L,
)

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
    private val audioArrival = AudioArrivalMeter()

    /** Local receive stats. Queue gaps are measured at its consumer. */
    fun audioArrivalHealth(): String = audioArrival.report()

    private val _frames = MutableSharedFlow<WireFrame>(
        replay = 0,
        extraBufferCapacity = 8,
        // H.264 inter frames depend on preceding frames. Silent DROP_OLDEST causes
        // decode corruption: MediaCodec cannot detect which reference was lost.
        // Backpressure the reader instead; the host's bounded send gate then
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
    // A retired receive loop must never close a newer authenticated socket.
    // Both a foreground-service Stop and an Android network handover can race
    // with a blocking connect(), handshake, or readFully().
    private val socketGate = Any()
    private var socketGeneration = 0L

    private fun isCurrent(generation: Long): Boolean = synchronized(socketGate) {
        socketGeneration == generation && running.get()
    }

    private fun closeCurrent(generation: Long): Boolean = synchronized(socketGate) {
        if (socketGeneration != generation || !running.get()) false
        else {
            socketGeneration++
            running.set(false)
            closeQuietly()
            true
        }
    }

    sealed interface TransportEvent {
        data class Connecting(val host: String, val port: Int) : TransportEvent
        data class Verified(val code: String, val hostName: String) : TransportEvent
        data object Ready : TransportEvent
        data class Closed(val reason: String, val recoverable: Boolean) : TransportEvent
        data class Failed(val stage: String, val detail: String, val remediation: String?,
                          val terminal: Boolean = false,
                          val retryable: Boolean = false) :
            TransportEvent
    }

    val verificationCode: String? get() = channel?.verificationCode

    /** True while a connection attempt or session is live. */
    val isRunning: Boolean get() = running.get()

    suspend fun connect(payload: PairingPayload) = withContext(Dispatchers.IO) {
        val generation = synchronized(socketGate) {
            if (running.get()) 0L
            else {
                running.set(true)
                ++socketGeneration
            }
        }
        if (generation == 0L) {
            android.util.Log.i("DeskFuseSession", "connect skipped: a connection is already live")
            return@withContext
        }
        android.util.Log.i("DeskFuseSession", "connecting to ${payload.host}:${payload.port}")
        _events.emit(TransportEvent.Connecting(payload.host, payload.port))

        var attemptedSocket: Socket? = null
        try {
            // Publish before connect(): disconnectNow() must be able to close
            // a TCP connect blocked on the old Wi-Fi interface.
            val s = Socket().apply {
                tcpNoDelay = true
                keepAlive = true
                soTimeout = READ_TIMEOUT_MS
            }
            attemptedSocket = s
            val installed = synchronized(socketGate) {
                if (socketGeneration != generation || !running.get()) false
                else {
                    socket = s
                    true
                }
            }
            if (!installed) {
                s.close()
                return@withContext
            }
            s.connect(InetSocketAddress(payload.host, payload.port), CONNECT_TIMEOUT_MS)
            val inStream = DataInputStream(BufferedInputStream(s.getInputStream(), 1 shl 16))
            val outStream = DataOutputStream(BufferedOutputStream(s.getOutputStream(), 1 shl 15))
            if (!isCurrent(generation)) {
                s.close()
                return@withContext
            }

            val secure = SecureChannel.handshake(
                input = inStream,
                output = outStream,
                hostPublicKeyB64 = payload.hostPublicKeyB64,
                pairingSecret = payload.pairingSecret,
            )
            val admitted = synchronized(socketGate) {
                if (socketGeneration != generation || !running.get()) false
                else {
                    input = inStream
                    output = outStream
                    channel = secure
                    writer = SecureRecordWriter(outStream, secure)
                    true
                }
            }
            if (!admitted) {
                s.close()
                return@withContext
            }
            if (!isCurrent(generation)) return@withContext
            _events.emit(TransportEvent.Verified(secure.verificationCode, payload.hostName))
            if (!isCurrent(generation)) return@withContext
            _events.emit(TransportEvent.Ready)
            scope.launch(Dispatchers.IO) { readLoop(inStream, secure, generation) }
        } catch (t: Throwable) {
            runCatching { attemptedSocket?.close() }
            if (closeCurrent(generation)) {
                // Low-level socket diagnostics belong in developer logs.
                android.util.Log.i("DeskFuseTransport",
                    "connection attempt failed: ${t.javaClass.simpleName}: ${t.message}")
                _events.emit(diagnose(t, payload))
            }
        }
    }

    private suspend fun readLoop(inStream: DataInputStream, secure: SecureChannel,
                                 generation: Long) {
        try {
            while (scope.isActive && isCurrent(generation)) {
                when (val kind = inStream.readByte().toInt()) {
                    KIND_CONTROL -> {
                        val len = inStream.readInt()
                        require(len in 1..MAX_CONTROL_BYTES) { "control frame too large: $len" }
                        val sealed = ByteArray(len).also { inStream.readFully(it) }
                        val plain = secure.open(sealed)
                        val msg = try {
                            json.decodeFromString(
                                ServerMessage.serializer(),
                                plain.decodeToString(),
                            )
                        } catch (e: Exception) {
                            // A message this build does not know about must not
                            // end the session: the host may send things this
                            // phone's version does not have yet.
                            android.util.Log.i(
                                "DeskFuseTransport",
                                "ignoring a control message this build does not understand",
                            )
                            continue
                        }
                        if (!dispatchControl(msg, generation)) return
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
                        val nowNs = System.nanoTime()
                        val receiveSequence = audioArrival.arrived(nowNs)
                        _audio.emit(PcmAudioChunk(
                            streamId, plain.copyOfRange(4, plain.size),
                            receiveSequence, nowNs,
                        ))
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
            if (closeCurrent(generation)) {
                _events.emit(TransportEvent.Closed(t.message ?: "connection lost", recoverable = true))
            }
        }
    }

    // A host-declared failure needs a manual retry, not another monitor/encoder
    // allocation when the socket closes. Deliver it on the lifecycle stream so
    // it cannot race a Closed event from a separate control collector.
    internal suspend fun dispatchControl(
        msg: ServerMessage, generation: Long = synchronized(socketGate) { socketGeneration }
    ): Boolean {
        if (msg is ServerMessage.Stage && msg.fatal) {
            if (closeCurrent(generation)) {
                _events.emit(TransportEvent.Failed(msg.stage, msg.detail, msg.remediation, terminal = true))
            }
            return false
        }
        if (!isCurrent(generation)) return false
        _control.emit(msg)
        return true
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
        val (sender, generation) = synchronized(socketGate) {
            (writer ?: return@withContext) to socketGeneration
        }
        val bytes = json.encodeToString(ClientMessage.serializer(), message).toByteArray()
        try {
            sender.control(bytes)
        } catch (t: java.io.IOException) {
            // Never silently swallow a failed Hello or keyframe request.
            if (closeCurrent(generation)) {
                _events.emit(TransportEvent.Closed(t.message ?: "send failed", recoverable = true))
            }
        }
    }

    /** Forward only host-authorized microphone stream packets.
     *  Returns false if the authenticated transport disappeared.
     */
    suspend fun sendPhoneMicrophone(streamId: Int, pcm16le: ByteArray): Boolean =
        withContext(Dispatchers.IO) {
            val (sender, generation) = synchronized(socketGate) {
                (writer ?: return@withContext false) to socketGeneration
            }
            if (!isCurrent(generation)) return@withContext false
            try {
                sender.microphone(streamId, pcm16le)
                true
            } catch (ex: java.io.IOException) {
                if (closeCurrent(generation)) _events.emit(TransportEvent.Closed(
                    ex.message ?: "microphone send failed", recoverable = true))
                false
            }
        }

    suspend fun close(reason: String) {
        if (!running.get()) return
        runCatching { send(ClientMessage.Bye(reason)) }
        disconnectNow()
        _events.emit(TransportEvent.Closed(reason, recoverable = false))
    }

    /**
     * A foreground Service can be destroyed immediately after the user taps
     * speaker Off. Its CoroutineScope is canceled before a suspending Bye
     * could run, so close the actual socket SYNCHRONOUSLY instead. This also
     * wakes the blocking receive thread rather than leaving an orphan host
     * connection for the full socket-read timeout.
     */
    fun disconnectNow() = synchronized(socketGate) {
        socketGeneration++
        running.set(false)
        closeQuietly()
    }

    /** Call only while holding socketGate. */
    private fun closeQuietly() {
        // Close socket FIRST to unblock a pending reader or handshake.
        runCatching { socket?.close() }
        runCatching { input?.close() }
        runCatching { output?.close() }
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
    internal fun diagnose(t: Throwable, payload: PairingPayload): TransportEvent.Failed = when (t) {
        is java.net.SocketTimeoutException, is java.net.ConnectException,
        is java.net.NoRouteToHostException, is java.net.SocketException ->
            TransportEvent.Failed(
                stage = "connect",
                detail = "The PC isn't reachable right now.",
                remediation = "Check your network connection and that DeskFuse is running on the PC.",
                retryable = true,
            )

        is java.net.UnknownHostException -> TransportEvent.Failed(
            stage = "resolve",
            detail = "The PC isn't reachable right now.",
            remediation = "Check your network connection and the PC's address.",
            retryable = true,
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
