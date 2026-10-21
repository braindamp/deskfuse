package com.focuswindow.client.media

import kotlin.math.abs

/**
 * Repairs only a clearly discontinuous join between successive PCM16 packets.
 *
 * The authenticated Windows stream already contains the genuine OS mix.
 * This does not synthesize sounds, resample, delay or buffer packets. On an
 * abrupt video seek, an old waveform tail can be followed by an unrelated
 * nonzero sample, producing a one-frame click. If the boundary jump is much
 * larger than the local waveform derivatives, bridge only the first 1ms of
 * the new packet. Normal continuous PCM and genuine musical transients keep
 * their original samples.
 *
 * One Android playback coroutine owns this object. A new stream resets it.
 */
internal class PcmSeamGuard(
    private val channels: Int,
    sampleRate: Int,
) {
    init {
        require(channels in 1..8)
        require(sampleRate in 8_000..192_000)
    }

    private val bridgeFrames = (sampleRate / 1000).coerceAtLeast(1)
    private val last = IntArray(channels)
    private val lastSlope = IntArray(channels)
    private var hasLast = false

    var repaired: Long = 0
        private set

    fun apply(pcm: ByteArray): Boolean {
        val stride = channels * 2
        if (pcm.size % stride != 0) return false
        val frames = pcm.size / stride
        if (frames < 2) return false

        // Inspect the original tail before possibly modifying the head.
        // A 10ms chunk is much longer than the 1ms correction window.
        var abrupt = false
        if (hasLast && frames > bridgeFrames + 1) {
            for (ch in 0 until channels) {
                val first = sample(pcm, ch * 2)
                val second = sample(pcm, stride + ch * 2)
                val jump = abs(first - last[ch])
                val naturalDerivative = maxOf(
                    1536,
                    abs(lastSlope[ch]),
                    abs(second - first),
                )
                // Detect a digital splice, not an ordinary high-frequency
                // waveform or a subtle difference at a normal packet seam.
                if (jump >= 8192 && jump > naturalDerivative * 6) {
                    abrupt = true
                    break
                }
            }
        }

        // Preserve the true sample history even if the packet is repaired:
        // continuity checking must not compound earlier correction.
        for (ch in 0 until channels) {
            val end = sample(pcm, (frames - 1) * stride + ch * 2)
            val beforeEnd = sample(pcm, (frames - 2) * stride + ch * 2)
            lastSlope[ch] = end - beforeEnd
        }

        if (abrupt) {
            for (frame in 0 until bridgeFrames) {
                val mixNew = frame + 1
                val mixOld = bridgeFrames - mixNew
                for (ch in 0 until channels) {
                    val offset = frame * stride + ch * 2
                    val current = sample(pcm, offset)
                    // Linear bridge in 32-bit arithmetic; values remain
                    // within signed 16-bit because this is a convex blend.
                    val blended = (last[ch] * mixOld + current * mixNew) / bridgeFrames
                    writeSample(pcm, offset, blended)
                }
            }
            repaired++
        }

        for (ch in 0 until channels) {
            last[ch] = sample(pcm, (frames - 1) * stride + ch * 2)
        }
        hasLast = true
        return abrupt
    }

    private fun sample(pcm: ByteArray, offset: Int): Int =
        (((pcm[offset].toInt() and 0xff) or
            ((pcm[offset + 1].toInt() and 0xff) shl 8))).toShort().toInt()

    private fun writeSample(pcm: ByteArray, offset: Int, value: Int) {
        pcm[offset] = value.toByte()
        pcm[offset + 1] = (value shr 8).toByte()
    }
}
