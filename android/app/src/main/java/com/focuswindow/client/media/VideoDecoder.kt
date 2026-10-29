package com.focuswindow.client.media

import android.media.MediaCodec
import android.media.MediaCodecInfo
import android.media.MediaCodecList
import android.media.MediaFormat
import android.os.Handler
import android.os.HandlerThread
import android.os.Looper
import android.view.Surface
import com.focuswindow.client.transport.WireFrame
import java.util.concurrent.atomic.AtomicLong
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.CompletableDeferred
import kotlinx.coroutines.Deferred
import kotlinx.coroutines.NonCancellable
import kotlinx.coroutines.android.asCoroutineDispatcher
import kotlinx.coroutines.withContext

/** One codec owner; encoded bytes go directly into MediaCodec and its native-sized Surface. */
class VideoDecoder(
    private val onStats: (DecoderStats) -> Unit,
    private val onFatal: (stage: String, detail: String, remediation: String?) -> Unit,
    private val requestKeyframe: () -> Unit,
) {
    data class DecoderStats(
        val codecName: String,
        val widthPx: Int,
        val heightPx: Int,
        val framesDecoded: Long,
        val framesDropped: Long,
        val framesStale: Long,
        /** Legacy boundary: parsed frame to output release, never panel presentation. */
        val decodeLatencyMs: Double,
        val hardwareAccelerated: Boolean,
        val metricsVersion: Int = 2,
        val recordToReleaseMs: Double = 0.0,
        val decryptMs: Double = 0.0,
        val admissionMs: Double = 0.0,
        val inputCopyMs: Double = 0.0,
        val codecResidenceMs: Double = 0.0,
        val recordToSurfaceMs: Double? = null,
        val timingMisses: Long = 0,
        val timingSamples: Long = 0,
        val surfaceTimingSamples: Long = 0,
    )

    private data class StartSpec(
        val surface: Surface, val mime: String, val width: Int, val height: Int,
        val version: Long, val colorRange: String?, val fps: Int, val connectionGeneration: Long,
    )
    private data class Timing(
        val ready: Long, val parsed: Long, val decryptStart: Long, val decrypted: Long,
        val ownerEntry: Long, val copyStart: Long, val copyEnd: Long,
        val queueStarted: Long, var outputSeen: Boolean = false,
    )

    private val thread = HandlerThread("fw-decoder").also { it.start() }
    private val handler = Handler(thread.looper)
    private val dispatcher = handler.asCoroutineDispatcher("fw-decoder")
    private val main = Handler(Looper.getMainLooper())
    private val admissionEpoch = AtomicLong(0)
    @Volatile private var closed = false
    private val closeCompletion = CompletableDeferred<Unit>()
    // Everything below is confined to the decoder owner.
    private var ownerEpoch = 0L
    private var codec: MediaCodec? = null
    private var spec: StartSpec? = null
    private var configured = false
    private var codecName = "—"
    private var hardware = false
    private var width = 0
    private var height = 0
    private val syncGate = VideoSyncGate()
    private val inputQueue = ArrayDeque<Int>()
    private val pending = LinkedHashMap<Long, Timing>()
    private var decoded = 0L
    private var dropped = 0L
    private var stale = 0L
    private var timingMisses = 0L
    private val sums = LongArray(6)
    private var latencyCount = 0L
    private var surfaceSum = 0L
    private var surfaceCount = 0L

    private fun current(epoch: Long) = !closed && epoch == admissionEpoch.get()
    private fun notice(epoch: Long, action: () -> Unit) {
        main.post { if (current(epoch)) action() }
    }
    private fun keyframe() = notice(ownerEpoch, requestKeyframe)

    fun start(
        surface: Surface, mimeType: String, widthPx: Int, heightPx: Int, targetVersion: Long,
        colorRange: String? = null, framesPerSecond: Int = 60,
        connectionGeneration: Long = 0,
    ) {
        check(!closed) { "Decoder is closed" }
        val epoch = admissionEpoch.incrementAndGet()
        val next = StartSpec(surface, mimeType, widthPx, heightPx, targetVersion, colorRange, framesPerSecond, connectionGeneration)
        handler.post {
            if (current(epoch)) {
                ownerEpoch = epoch
                spec = next
                try {
                    retireCodec()
                    configureSafely(next)
                } catch (failure: Exception) { reportFailure(epoch, failure) }
            }
        }
    }

    private fun reportFailure(epoch: Long, failure: Exception) {
        configured = false
        notice(epoch) { onFatal("decoder", "Decoder transition failed: ${failure.javaClass.simpleName}",
            "Reconnect to retry codec retirement and configuration.") }
    }

    private fun configureSafely(next: StartSpec) {
        try { configure(next) }
        catch (failure: Exception) { reportFailure(ownerEpoch, failure) }
    }

    private fun recoverOnOwner() {
        val saved = spec ?: return
        val previous = ownerEpoch
        if (!admissionEpoch.compareAndSet(previous, previous + 1)) return
        ownerEpoch = previous + 1
        try {
            retireCodec()
            if (current(ownerEpoch)) configureSafely(saved)
        } catch (failure: Exception) { reportFailure(ownerEpoch, failure) }
    }

    private fun configure(next: StartSpec) {
        width = next.width
        height = next.height
        syncGate.reset(next.version)
        sums.fill(0)
        latencyCount = 0
        surfaceSum = 0
        surfaceCount = 0
        val candidates = selectDecoders(next.mime, next.width, next.height)
        val failures = mutableListOf<String>()
        for (name in candidates) {
            for (lowLatency in listOf(true, false)) {
                if (!current(ownerEpoch)) return
                var candidate: MediaCodec? = null
                try {
                    val format = MediaFormat.createVideoFormat(next.mime, next.width, next.height).apply {
                        if (next.colorRange == "BT.709 full range") {
                            setInteger(MediaFormat.KEY_COLOR_STANDARD, MediaFormat.COLOR_STANDARD_BT709)
                            setInteger(MediaFormat.KEY_COLOR_RANGE, MediaFormat.COLOR_RANGE_FULL)
                            setInteger(MediaFormat.KEY_COLOR_TRANSFER, MediaFormat.COLOR_TRANSFER_SDR_VIDEO)
                        }
                        if (lowLatency) {
                            setInteger(MediaFormat.KEY_LOW_LATENCY, 1)
                            setInteger(MediaFormat.KEY_PRIORITY, 0)
                            setInteger(MediaFormat.KEY_OPERATING_RATE, next.fps.coerceIn(1, 240))
                        }
                    }
                    val created = MediaCodec.createByCodecName(name)
                    candidate = created
                    created.setCallback(callback, handler)
                    created.configure(format, next.surface, null, 0)
                    // Rendering timestamps are optional diagnostics; failure cannot disable video.
                    runCatching {
                        created.setOnFrameRenderedListener({ mc, pts, nanoTime ->
                            if (mc === codec && current(ownerEpoch)) {
                                val timing = pending.remove(pts)
                                if (timing != null && nanoTime >= timing.ready) {
                                    surfaceSum += nanoTime - timing.ready
                                    surfaceCount++
                                    if (surfaceCount == 1L) emitStats()
                                } else timingMisses++
                            }
                        }, handler)
                    }
                    codec = created
                    configured = true
                    codecName = name
                    hardware = decoderIsHardware(name)
                    created.start()
                    keyframe()
                    return
                } catch (failure: Exception) {
                    configured = false
                    inputQueue.clear()
                    pending.clear()
                    runCatching { candidate?.stop() }
                    try { candidate?.release() }
                    catch (cleanup: Exception) { codec = candidate; throw cleanup }
                    codec = null
                    failures += "$name (${if (lowLatency) "low latency" else "basic"}): ${failure.javaClass.simpleName}"
                }
            }
        }
        val epoch = ownerEpoch
        notice(epoch) {
            onFatal("decoder", "No Android codec could start ${next.mime} at ${next.width}×${next.height}. " +
                failures.joinToString("; "), "The negotiated native resolution must be supported by the device codec.")
        }
    }

    /** The caller awaits one command, not an input-buffer callback; there is no second media FIFO. */
    suspend fun submit(frame: WireFrame) {
        val epoch = admissionEpoch.get()
        var processed = false // accessed only on the owner, including cancellation reconciliation
        try {
            withContext(dispatcher) {
                if (current(epoch)) {
                    submitOnOwner(frame)
                    processed = true
                }
            }
        } catch (cancelled: CancellationException) {
            if (!closed) withContext(NonCancellable + dispatcher) {
                if (!processed && current(epoch)) {
                    syncGate.lostFrame()
                    keyframe()
                }
            }
            throw cancelled
        }
    }

    private fun submitOnOwner(frame: WireFrame) {
        if (!configured || ownerEpoch != admissionEpoch.get()) return
        if (frame.connectionGeneration != spec?.connectionGeneration) { stale++; return }
        val ownerEntry = System.nanoTime()
        val index = inputQueue.removeFirstOrNull()
        val decision = syncGate.decide(frame.header.targetVersion, frame.header.keyframe,
            index != null, android.os.SystemClock.elapsedRealtime())
        if (decision != VideoSyncGate.Decision.QUEUE) {
            if (index != null) inputQueue.addFirst(index)
            if (decision == VideoSyncGate.Decision.REQUEST_KEYFRAME) keyframe()
            if (decision == VideoSyncGate.Decision.DROP) stale++ else dropped++
            return
        }
        val mc = codec ?: return
        try {
            val buffer = mc.getInputBuffer(index!!) ?: error("Codec input buffer unavailable")
            if (frame.payloadLength > buffer.capacity()) {
                syncGate.lostFrame()
                val epoch = ownerEpoch
                retireCodec()
                notice(epoch) { onFatal("decode", "H.264 access unit ${frame.payloadLength} exceeds decoder input capacity ${buffer.capacity()}",
                    "Reduce host bitrate or use another hardware codec.") }
                return
            }
            val copyStart = System.nanoTime()
            buffer.clear()
            frame.copyPayloadTo(buffer)
            val copyEnd = System.nanoTime()
            if (pending.size >= MAX_TIMINGS) {
                pending.remove(pending.keys.first())
                timingMisses++
            }
            pending[frame.header.ptsUs] = Timing(frame.recordReadyAtNanos, frame.arrivedAtNanos,
                frame.decryptStartedAtNanos, frame.decryptedAtNanos, ownerEntry, copyStart, copyEnd, copyEnd)
            if (!current(ownerEpoch)) { pending.remove(frame.header.ptsUs); return }
            mc.queueInputBuffer(index, 0, frame.payloadLength, frame.header.ptsUs,
                if (frame.header.keyframe) MediaCodec.BUFFER_FLAG_KEY_FRAME else 0)
            syncGate.queued(frame.header.keyframe)
        } catch (_: IllegalStateException) {
            pending.remove(frame.header.ptsUs)
            syncGate.lostFrame()
            dropped++
            recoverOnOwner()
        }
    }

    private val callback = object : MediaCodec.Callback() {
        override fun onInputBufferAvailable(mc: MediaCodec, index: Int) {
            if (mc === codec && configured && current(ownerEpoch)) inputQueue.addLast(index)
        }
        override fun onOutputBufferAvailable(mc: MediaCodec, index: Int, info: MediaCodec.BufferInfo) {
            if (mc !== codec || !configured || !current(ownerEpoch)) return
            try {
                val timing = pending[info.presentationTimeUs]
                val firstMeasured = latencyCount == 0L && timing != null && !timing.outputSeen
                mc.releaseOutputBuffer(index, true)
                val released = System.nanoTime()
                decoded++
                if (timing != null && !timing.outputSeen) {
                    timing.outputSeen = true
                    sums[0] += released - timing.parsed
                    sums[1] += released - timing.ready
                    sums[2] += timing.decrypted - timing.decryptStart
                    sums[3] += timing.ownerEntry - timing.parsed
                    sums[4] += timing.copyEnd - timing.copyStart
                    sums[5] += released - timing.queueStarted
                    latencyCount++
                } else timingMisses++
                if (firstMeasured || decoded % STATS_EVERY == 0L) emitStats()
            } catch (_: IllegalStateException) {
                syncGate.lostFrame()
                keyframe()
            }
        }
        override fun onOutputFormatChanged(mc: MediaCodec, format: MediaFormat) {
            if (mc !== codec || !configured || !current(ownerEpoch)) return
            width = format.getInteger(MediaFormat.KEY_WIDTH, width)
            height = format.getInteger(MediaFormat.KEY_HEIGHT, height)
            emitStats()
        }
        override fun onError(mc: MediaCodec, e: MediaCodec.CodecException) {
            if (mc !== codec || !configured || !current(ownerEpoch)) return
            if (e.isRecoverable) {
                recoverOnOwner()
            } else {
                configured = false
                val epoch = ownerEpoch
                notice(epoch) { onFatal("decode", e.diagnosticInfo,
                    if (e.isTransient) "Momentary decoder pressure — reconnect." else "Use a supported hardware decoder.") }
            }
        }
    }

    private fun emitStats() {
        fun mean(value: Long) = if (latencyCount == 0L) 0.0 else value / latencyCount / 1_000_000.0
        val snapshot = DecoderStats(codecName, width, height, decoded, dropped, stale, mean(sums[0]), hardware,
            recordToReleaseMs = mean(sums[1]), decryptMs = mean(sums[2]), admissionMs = mean(sums[3]),
            inputCopyMs = mean(sums[4]), codecResidenceMs = mean(sums[5]),
            recordToSurfaceMs = if (surfaceCount == 0L) null else surfaceSum / surfaceCount / 1_000_000.0,
            timingMisses = timingMisses, timingSamples = latencyCount, surfaceTimingSamples = surfaceCount)
        notice(ownerEpoch) { onStats(snapshot) }
    }

    private fun retireCodec() {
        configured = false
        val retired = codec
        inputQueue.clear()
        pending.clear()
        syncGate.reset(-1)
        runCatching { retired?.stop() }
        // A failed release retains the object and prevents replacement ownership.
        retired?.release()
        codec = null
    }

    fun stop(): Deferred<Unit> {
        if (closed) return closeCompletion
        val completed = CompletableDeferred<Unit>()
        val epoch = admissionEpoch.incrementAndGet()
        handler.post {
            try {
                if (ownerEpoch <= epoch) {
                    retireCodec()
                    ownerEpoch = epoch
                    spec = null
                }
                completed.complete(Unit)
            } catch (failure: Exception) {
                completed.completeExceptionally(failure)
                reportFailure(epoch, failure)
            }
        }
        return completed
    }

    @Synchronized fun close(): Deferred<Unit> {
        if (closed) return closeCompletion
        closed = true
        admissionEpoch.incrementAndGet()
        handler.post {
            try {
                retireCodec()
                spec = null
                closeCompletion.complete(Unit)
            } catch (failure: Exception) { closeCompletion.completeExceptionally(failure) }
            finally { thread.quitSafely() }
        }
        return closeCompletion
    }

    private fun selectDecoders(mimeType: String, widthPx: Int, heightPx: Int): List<String> {
        val list = MediaCodecList(MediaCodecList.REGULAR_CODECS)
        return list.codecInfos
            .asSequence()
            .filter { !it.isEncoder && it.supportedTypes.any { t -> t.equals(mimeType, true) } }
            .filter { info ->
                runCatching {
                    info.getCapabilitiesForType(mimeType).videoCapabilities
                        ?.isSizeSupported(widthPx, heightPx) == true
                }.getOrDefault(false)
            }
            .sortedByDescending { decoderIsHardware(it.name) }
            .map { it.name }
            .toList()
    }

    private fun decoderIsHardware(name: String): Boolean {
        val info = MediaCodecList(MediaCodecList.ALL_CODECS).codecInfos.firstOrNull { it.name == name }
            ?: return false
        return if (android.os.Build.VERSION.SDK_INT >= 29) info.isHardwareAccelerated else {
            val n = name.lowercase()
            !(n.startsWith("omx.google") || n.startsWith("c2.android") || n.contains("software"))
        }
    }

    companion object {
        const val MIME_H264 = MediaFormat.MIMETYPE_VIDEO_AVC
        const val MIME_HEVC = MediaFormat.MIMETYPE_VIDEO_HEVC
        const val MIME_AV1 = MediaFormat.MIMETYPE_VIDEO_AV1
        private const val STATS_EVERY = 30L
        private const val MAX_TIMINGS = 256

        /**
         * FR-ENC-02 — report what this device can actually decode so the host
         * can pick the top rung of the codec ladder it can serve. 4:4:4 and
         * 10-bit matter here: 4:2:0 chroma is why text looks smeared.
         */
        fun capabilities(): List<String> {
            val list = MediaCodecList(MediaCodecList.REGULAR_CODECS)
            val out = mutableListOf<String>()
            for (mime in listOf(MIME_AV1, MIME_HEVC, MIME_H264)) {
                val info = list.codecInfos.firstOrNull {
                    !it.isEncoder && it.supportedTypes.any { t -> t.equals(mime, true) }
                } ?: continue
                val caps = runCatching { info.getCapabilitiesForType(mime) }.getOrNull() ?: continue
                val profiles = caps.profileLevels.map { it.profile }.toSet()
                val tenBit = when (mime) {
                    MIME_HEVC -> profiles.contains(
                        MediaCodecInfo.CodecProfileLevel.HEVCProfileMain10,
                    )
                    MIME_AV1 -> profiles.contains(
                        MediaCodecInfo.CodecProfileLevel.AV1ProfileMain10,
                    )
                    else -> false
                }
                out += buildString {
                    append(mime.substringAfter("video/"))
                    if (tenBit) append(":10bit")
                }
            }
            return out
        }
    }
}
