package com.focuswindow.client.media

/**
 * Zero-buffer PCM16 anti-click for abrupt media start/stop edges.
 *
 * Unlike the older inter-packet seam guard, media seeks may cut to/from
 * DIGITAL SILENCE inside a captured 10ms packet. Only a large jump following
 * confirmed silence is faded in, or a large jump into sustained digital
 * silence is faded out inside that same packet. A genuine Windows ding with
 * a naturally ramped attack passes through unchanged.
 *
 * This intentionally cannot retroactively repair a sound-to-silence cut
 * exactly on a packet boundary: the previous packet has already played.
 * Such cuts are counted, not hidden by adding a packet of lookahead.
 */
internal class PcmSilenceEdges(private val channels: Int, sampleRate: Int) {
    init {
        require(channels in 1..8)
        require(sampleRate in 8_000..192_000)
    }

    private val rampFrames = (sampleRate / 1000).coerceAtLeast(1) // 1ms, no latency
    private val quietFramesNeeded = (sampleRate / 8000).coerceAtLeast(4)
    private var previousArrivalNs = 0L
    private var trailingQuiet = 0
    private var lastPeak = 0
    private var firstPacket = true
    private var attackFramesLeft = 0

    var onsets = 0L
        private set
    var releases = 0L
        private set
    var resumed = 0L
        private set
    var boundaryCuts = 0L
        private set

    fun apply(pcm: ByteArray, receivedAtNs: Long = 0L) {
        val stride = channels * 2
        if (pcm.size % stride != 0) return
        val frames = pcm.size / stride
        if (frames == 0) return

        if (receivedAtNs > 0L) {
            if (previousArrivalNs > 0L && receivedAtNs > previousArrivalNs &&
                receivedAtNs - previousArrivalNs > 250_000_000L
            ) {
                // The host went silent, or the receiver saw a long interruption.
                // Do not reuse stale waveform history on the next onset.
                firstPacket = true
                trailingQuiet = 0
                lastPeak = 0
                attackFramesLeft = 0
                resumed++
            }
            previousArrivalNs = receivedAtNs
        }

        val firstPeak = peak(pcm, 0, stride)
        if (!firstPacket && lastPeak >= 4096 && firstPeak <= 128 &&
            frames >= quietFramesNeeded && isQuietRun(pcm, 0, quietFramesNeeded, stride)
        ) {
            // The old tail was already sent. Measure rather than pretending
            // an in-place edit to this packet can fix a prior cutoff.
            boundaryCuts++
        }
        if (firstPeak >= 4096 && (firstPacket || trailingQuiet >= quietFramesNeeded) &&
            attackFramesLeft == 0
        ) {
            attackFramesLeft = rampFrames
            onsets++
        }

        var quietRun = trailingQuiet
        var precedingPeak = lastPeak
        for (frame in 0 until frames) {
            // Inspect ORIGINAL samples before any in-place attenuation.
            val amplitude = peak(pcm, frame, stride)
            val quiet = amplitude <= 128

            if (!quiet && amplitude >= 4096 && quietRun >= quietFramesNeeded &&
                attackFramesLeft == 0
            ) {
                attackFramesLeft = rampFrames
                onsets++
            }

            if (quiet && frame > 0 && precedingPeak >= 4096 &&
                frames - frame >= quietFramesNeeded &&
                isQuietRun(pcm, frame, quietFramesNeeded, stride)
            ) {
                fadeOutBefore(pcm, frame, stride)
                releases++
            }

            if (attackFramesLeft > 0) {
                val gainNumerator = rampFrames - attackFramesLeft + 1
                for (ch in 0 until channels) {
                    val offset = frame * stride + ch * 2
                    val value = read(pcm, offset)
                    write(pcm, offset, value * gainNumerator / rampFrames)
                }
                attackFramesLeft--
            }

            quietRun = if (quiet) (quietRun + 1).coerceAtMost(1_000_000) else 0
            precedingPeak = amplitude
        }

        trailingQuiet = quietRun
        lastPeak = precedingPeak
        firstPacket = false
    }

    private fun fadeOutBefore(pcm: ByteArray, firstSilentFrame: Int, stride: Int) {
        val span = minOf(firstSilentFrame, rampFrames)
        for (i in 0 until span) {
            val frame = firstSilentFrame - span + i
            val numerator = span - 1 - i
            for (ch in 0 until channels) {
                val offset = frame * stride + ch * 2
                write(pcm, offset, read(pcm, offset) * numerator / span)
            }
        }
    }

    private fun isQuietRun(pcm: ByteArray, start: Int, count: Int, stride: Int): Boolean {
        for (i in start until start + count) {
            if (peak(pcm, i, stride) > 128) return false
        }
        return true
    }

    private fun peak(pcm: ByteArray, frame: Int, stride: Int): Int {
        var max = 0
        for (ch in 0 until channels) {
            val sample = read(pcm, frame * stride + ch * 2)
            max = maxOf(max, kotlin.math.abs(sample))
        }
        return max
    }

    private fun read(pcm: ByteArray, offset: Int): Int =
        ((pcm[offset].toInt() and 255) or
            ((pcm[offset + 1].toInt() and 255) shl 8)).toShort().toInt()

    private fun write(pcm: ByteArray, offset: Int, sample: Int) {
        pcm[offset] = sample.toByte()
        pcm[offset + 1] = (sample shr 8).toByte()
    }
}
