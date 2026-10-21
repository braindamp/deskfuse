package com.focuswindow.client.media

import org.junit.Assert.assertArrayEquals
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test

class PcmSeamGuardTest {
    private fun mono(samples: IntArray): ByteArray =
        ByteArray(samples.size * 2).also { pcm ->
            samples.forEachIndexed { i, value ->
                pcm[i * 2] = value.toByte()
                pcm[i * 2 + 1] = (value shr 8).toByte()
            }
        }

    private fun sample(pcm: ByteArray, frame: Int): Int =
        ((pcm[frame * 2].toInt() and 0xff) or
            ((pcm[frame * 2 + 1].toInt() and 0xff) shl 8)).toShort().toInt()

    @Test
    fun abrupt_clip_join_is_bridged_without_delaying_or_resizing_packets() {
        val guard = PcmSeamGuard(1, 48000)
        val old = mono(IntArray(480) { 10_000 })
        val next = mono(IntArray(480) { -10_000 })
        assertFalse(guard.apply(old))
        assertTrue(guard.apply(next))
        assertEquals(1L, guard.repaired)
        assertEquals(960, next.size)
        assertTrue(sample(next, 0) > 9_000)
        assertEquals(-10_000, sample(next, 47))
        assertEquals(-10_000, sample(next, 48))
        assertEquals(-10_000, sample(next, 479))
    }

    @Test
    fun continuous_or_small_seam_is_bitwise_untouched() {
        val guard = PcmSeamGuard(1, 48000)
        assertFalse(guard.apply(mono(IntArray(480) { 8000 })))
        val next = mono(IntArray(480) { 8400 })
        val copy = next.clone()
        assertFalse(guard.apply(next))
        assertArrayEquals(copy, next)
        assertEquals(0L, guard.repaired)
    }

    @Test
    fun high_frequency_waveform_does_not_look_like_a_splice() {
        val guard = PcmSeamGuard(1, 48000)
        val earlier = mono(IntArray(480) { if (it % 2 == 0) 16_000 else -16_000 })
        val next = mono(IntArray(480) { if (it % 2 == 0) 16_000 else -16_000 })
        assertFalse(guard.apply(earlier))
        val copy = next.clone()
        assertFalse(guard.apply(next))
        assertArrayEquals(copy, next)
    }

    @Test
    fun first_packet_is_never_modified_and_stream_reset_clears_history() {
        val first = mono(IntArray(480) { 26_000 })
        val copy = first.clone()
        val guard = PcmSeamGuard(1, 48000)
        assertFalse(guard.apply(first))
        assertArrayEquals(copy, first)

        // Every new Android stream instantiates a fresh seam guard.
        val newStream = PcmSeamGuard(1, 48000)
        val next = mono(IntArray(480) { -26_000 })
        val untouched = next.clone()
        assertFalse(newStream.apply(next))
        assertArrayEquals(untouched, next)
    }
}
