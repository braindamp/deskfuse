package com.focuswindow.client.media

import org.junit.Assert.assertArrayEquals
import org.junit.Assert.assertEquals
import org.junit.Assert.assertTrue
import org.junit.Test

class PcmSilenceEdgesTest {
    private fun mono(samples: IntArray): ByteArray =
        ByteArray(samples.size * 2).also { bytes ->
            samples.forEachIndexed { i, value ->
                bytes[2 * i] = value.toByte()
                bytes[2 * i + 1] = (value shr 8).toByte()
            }
        }

    private fun value(pcm: ByteArray, frame: Int): Int =
        ((pcm[frame * 2].toInt() and 255) or
            ((pcm[frame * 2 + 1].toInt() and 255) shl 8)).toShort().toInt()

    @Test
    fun first_nonzero_pcm_is_gently_started_without_delaying_data() {
        val guard = PcmSilenceEdges(1, 48_000)
        val pcm = mono(IntArray(480) { 12_000 })
        guard.apply(pcm, 1_000_000_000)
        assertEquals(1, guard.onsets)
        assertEquals(960, pcm.size)
        assertTrue(value(pcm, 0) in 1..1_000)
        assertEquals(12_000, value(pcm, 47))
        assertEquals(12_000, value(pcm, 48))
    }

    @Test
    fun audible_digital_onset_inside_silent_packet_is_faded_not_preceding_silence() {
        val guard = PcmSilenceEdges(1, 48_000)
        val pcm = mono(IntArray(480) { if (it < 100) 0 else 12_000 })
        guard.apply(pcm)
        assertEquals(1, guard.onsets)
        assertEquals(0, value(pcm, 99))
        assertTrue(value(pcm, 100) in 1..1_000)
        assertEquals(12_000, value(pcm, 147))
        assertEquals(12_000, value(pcm, 148))
    }

    @Test
    fun abrupt_cut_to_digital_silence_inside_pcm_fades_end_of_audio() {
        val guard = PcmSilenceEdges(1, 48_000)
        val pcm = mono(IntArray(480) { if (it < 200) 10_000 else 0 })
        guard.apply(pcm)
        assertEquals(1, guard.releases)
        assertEquals(10_000, value(pcm, 151))
        assertEquals(0, value(pcm, 199))
        assertEquals(0, value(pcm, 200))
        assertEquals(0, value(pcm, 479))
    }

    @Test
    fun naturally_ramped_media_and_continuous_waveform_are_bit_exact() {
        val guard = PcmSilenceEdges(1, 48_000)
        val ramp = mono(IntArray(480) { i ->
            if (i < 100) i * 120 else 12_000
        })
        val original = ramp.clone()
        guard.apply(ramp)
        assertArrayEquals(original, ramp)
        assertEquals(0, guard.onsets)
        assertEquals(0, guard.releases)
        val continuation = mono(IntArray(480) { 12_000 })
        val originalContinuation = continuation.clone()
        guard.apply(continuation)
        assertArrayEquals(originalContinuation, continuation)
    }

    @Test
    fun legitimate_high_frequency_waveform_is_not_faded_between_packets() {
        val guard = PcmSilenceEdges(1, 48_000)
        val pcm = mono(IntArray(480) { if (it % 2 == 0) 16_000 else -16_000 })
        guard.apply(pcm)
        val next = mono(IntArray(480) { if (it % 2 == 0) 16_000 else -16_000 })
        val copy = next.clone()
        guard.apply(next)
        assertArrayEquals(copy, next)
        assertEquals(0, guard.releases)
    }

    @Test
    fun long_idle_gap_rearms_one_millisecond_fade_not_a_delayed_queue() {
        val guard = PcmSilenceEdges(1, 48_000)
        guard.apply(mono(IntArray(480) { 10_000 }), 1_000_000_000)
        val afterGap = mono(IntArray(480) { 11_000 })
        guard.apply(afterGap, 1_500_000_000)
        assertEquals(1, guard.resumed)
        assertEquals(2, guard.onsets)
        assertTrue(value(afterGap, 0) < 500)
        assertEquals(11_000, value(afterGap, 47))
    }

    @Test
    fun cross_packet_hard_stop_is_measured_not_misrepresented_as_repaired() {
        val guard = PcmSilenceEdges(1, 48_000)
        guard.apply(mono(IntArray(480) { 10_000 }))
        val silent = mono(IntArray(480) { 0 })
        val unchanged = silent.clone()
        guard.apply(silent)
        assertEquals(1, guard.boundaryCuts)
        assertEquals(0, guard.releases)
        assertArrayEquals(unchanged, silent)
    }
}
