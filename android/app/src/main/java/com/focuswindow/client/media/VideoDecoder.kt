package com.focuswindow.client.media

import android.media.MediaCodec
import android.media.MediaCodecInfo
import android.media.MediaCodecList
import android.media.MediaFormat
import android.os.Handler
import android.os.HandlerThread
import android.view.Surface
import com.focuswindow.client.transport.WireFrame
import java.util.concurrent.atomic.AtomicBoolean
import java.util.concurrent.atomic.AtomicLong

/**
 * Hardware video decode straight into the [Surface] of the SurfaceView.
 *
 * NFR-PIX-01 — there is no intermediate bitmap, no ImageReader, and no scaling
 * step. The decoder writes into the surface at the stream's native size, which
 * the host has already made equal to this phone's pixels. Any scale operation
 * here would silently undo the entire architecture.
 */
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
        /** NFR-LAT-01 — wire-arrival to presented, the half of the budget we own. */
        val decodeLatencyMs: Double,
        val hardwareAccelerated: Boolean,
    )

    private var codec: MediaCodec? = null
    private var thread: HandlerThread? = null
    private var handler: Handler? = null
    private var surface: Surface? = null

    private val configured = AtomicBoolean(false)
    private val decoded = AtomicLong(0)
    private val dropped = AtomicLong(0)
    private val stale = AtomicLong(0)

    private var codecName: String = "—"
    private var hardware = false
    private var width = 0
    private var height = 0
    private var mime = MIME_H264

    /** Only frames for the target whose MediaCodec geometry was configured are decoded. */
    private val syncGate = VideoSyncGate()

    /** Arrival timestamps keyed by presentation time, for the latency estimate. */
    private val pending = java.util.concurrent.ConcurrentHashMap<Long, Long>()

    fun start(
        surface: Surface, mimeType: String, widthPx: Int, heightPx: Int, targetVersion: Long,
        colorRange: String? = null, framesPerSecond: Int = 60,
    ) {
        stop()
        this.surface = surface
        this.mime = mimeType
        this.width = widthPx
        this.height = heightPx
        syncGate.reset(targetVersion)

        val t = HandlerThread("fw-decoder").also { it.start() }
        thread = t
        handler = Handler(t.looper)

        val candidates = selectDecoders(mimeType, widthPx, heightPx)
        if (candidates.isEmpty()) {
            onFatal(
                "decoder",
                "This device has no decoder for $mimeType at ${widthPx}×${heightPx} pixels.",
                "The negotiated native resolution is unsupported by the device codec.",
            )
            stop()
            return
        }

        // Hardware codecs can advertise support for a geometry yet reject
        // optional low-latency/operating-rate keys at configure(). A single
        // bad vendor decoder must not prevent another available decoder from
        // opening the real native-resolution session.
        val failures = mutableListOf<String>()
        for (name in candidates) {
            for (lowLatency in listOf(true, false)) {
                var candidate: MediaCodec? = null
                try {
                    val format = MediaFormat.createVideoFormat(mimeType, widthPx, heightPx).apply {
                        if (colorRange == "BT.709 full range") {
                            setInteger(MediaFormat.KEY_COLOR_STANDARD, MediaFormat.COLOR_STANDARD_BT709)
                            setInteger(MediaFormat.KEY_COLOR_RANGE, MediaFormat.COLOR_RANGE_FULL)
                            setInteger(MediaFormat.KEY_COLOR_TRANSFER, MediaFormat.COLOR_TRANSFER_SDR_VIDEO)
                        }
                        if (lowLatency) {
                            setInteger(MediaFormat.KEY_LOW_LATENCY, 1)
                            setInteger(MediaFormat.KEY_PRIORITY, 0)
                            setInteger(MediaFormat.KEY_OPERATING_RATE, framesPerSecond.coerceIn(1, 240))
                        }
                    }
                    candidate = MediaCodec.createByCodecName(name)
                    candidate.setCallback(callback, handler)
                    candidate.configure(format, surface, null, 0)
                    // Set codec before start() so callbacks can identify the
                    // current instance without mistaking it for a retired one.
                    codec = candidate
                    configured.set(true)
                    candidate.start()
                    codecName = name
                    hardware = decoderIsHardware(name)
                    requestKeyframe()
                    return
                } catch (t: Exception) {
                    configured.set(false)
                    codec = null
                    inputQueue.clear()
                    runCatching { candidate?.stop() }
                    runCatching { candidate?.release() }
                    failures += "$name (${if (lowLatency) "low latency" else "basic"}): ${t.javaClass.simpleName}"
                }
            }
        }
        onFatal(
            "decoder",
            "No Android codec could start $mimeType at ${widthPx}×${heightPx}. " + failures.joinToString("; "),
            "Update the device codecs or use a phone with native-resolution H.264 support.",
        )
        stop()
    }

    /**
     * FR-ENC-05 — the client drops frames it cannot safely render. A frame that
     * belongs to an older target rectangle is NOT shown; showing it would mean
     * rendering the wrong geometry, which is worse than a brief freeze.
     */
    fun submit(frame: WireFrame) {
        if (!configured.get()) return

        val index = inputQueue.poll()
        val decision = syncGate.decide(
            frame.header.targetVersion, frame.header.keyframe, index != null,
            android.os.SystemClock.elapsedRealtime(),
        )
        if (decision != VideoSyncGate.Decision.QUEUE) {
            if (index != null) inputQueue.offer(index)
            if (decision == VideoSyncGate.Decision.REQUEST_KEYFRAME) requestKeyframe()
            if (decision == VideoSyncGate.Decision.DROP) stale.incrementAndGet()
            else dropped.incrementAndGet()
            return
        }

        val mc = codec ?: return
        try {
            val buffer = mc.getInputBuffer(index!!) ?: run {
                syncGate.lostFrame()
                requestKeyframe()
                return
            }
            if (frame.payload.size > buffer.capacity()) {
                syncGate.lostFrame()
                onFatal("decode", "H.264 access unit ${frame.payload.size} exceeds decoder input " +
                    "capacity ${buffer.capacity()}", "Reduce host bitrate or use another hardware codec.")
                return
            }
            buffer.clear()
            buffer.put(frame.payload)
            mc.queueInputBuffer(
                index, 0, frame.payload.size, frame.header.ptsUs,
                if (frame.header.keyframe) MediaCodec.BUFFER_FLAG_KEY_FRAME else 0,
            )
            syncGate.queued(frame.header.keyframe)
            pending[frame.header.ptsUs] = frame.arrivedAtNanos
        } catch (_: IllegalStateException) {
            syncGate.lostFrame()
            dropped.incrementAndGet()
            requestKeyframe()
        }
    }

    private val inputQueue = java.util.concurrent.ConcurrentLinkedQueue<Int>()

    private val callback = object : MediaCodec.Callback() {
        override fun onInputBufferAvailable(mc: MediaCodec, index: Int) {
            if (mc === codec && configured.get()) inputQueue.offer(index)
        }

        override fun onOutputBufferAvailable(
            mc: MediaCodec,
            index: Int,
            info: MediaCodec.BufferInfo,
        ) {
            try {
                if (mc !== codec || !configured.get()) return
                val arrival = pending.remove(info.presentationTimeUs)
                // render = true pushes straight to the Surface, no copy.
                mc.releaseOutputBuffer(index, true)
                val count = decoded.incrementAndGet()

                if (arrival != null) {
                    val ms = (System.nanoTime() - arrival) / 1_000_000.0
                    latencySum += ms
                    latencyCount++
                }
                if (count % STATS_EVERY == 0L) emitStats()
            } catch (_: IllegalStateException) {
                // Codec was released mid-flight; nothing to do.
            }
        }

        override fun onOutputFormatChanged(mc: MediaCodec, format: MediaFormat) {
            if (mc !== codec || !configured.get()) return
            width = format.getInteger(MediaFormat.KEY_WIDTH, width)
            height = format.getInteger(MediaFormat.KEY_HEIGHT, height)
            emitStats()
        }

        override fun onError(mc: MediaCodec, e: MediaCodec.CodecException) {
            if (mc !== codec || !configured.get()) return
            if (e.isRecoverable) {
                runCatching { mc.stop(); mc.start() }
                syncGate.lostFrame()
                requestKeyframe()
            } else {
                onFatal(
                    "decode",
                    e.diagnosticInfo,
                    if (e.isTransient) "Momentary decoder pressure — retrying." else
                        "Lower the refresh rate or switch the host to H.264.",
                )
            }
        }
    }

    private var latencySum = 0.0
    private var latencyCount = 0L

    private fun emitStats() {
        onStats(
            DecoderStats(
                codecName = codecName,
                widthPx = width,
                heightPx = height,
                framesDecoded = decoded.get(),
                framesDropped = dropped.get(),
                framesStale = stale.get(),
                decodeLatencyMs = if (latencyCount == 0L) 0.0 else latencySum / latencyCount,
                hardwareAccelerated = hardware,
            ),
        )
    }

    fun stop() {
        configured.set(false)
        inputQueue.clear()
        pending.clear()
        runCatching { codec?.stop() }
        runCatching { codec?.release() }
        codec = null
        thread?.quitSafely()
        thread = null
        handler = null
        surface = null
        syncGate.reset(-1)
    }

    /** Only select a decoder that actually supports the target dimensions.
     *  Support at 320x240 says nothing about a 1440x3120 portrait surface.
     */
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
