package com.focuswindow.client

import com.focuswindow.client.media.writePcmPacket
import org.junit.Assert.*
import org.junit.Test

class PcmPacketWriterTest {
    @Test
    fun final_short_packet_is_fully_written_without_waiting_for_more_audio() {
        val bytes = ByteArray(20) { it.toByte() }
        val received = mutableListOf<Byte>()
        assertTrue(writePcmPacket(bytes.size) { offset, remaining ->
            val accepted = minOf(6, remaining)
            received.addAll(bytes.copyOfRange(offset, offset + accepted).toList())
            accepted
        })
        assertArrayEquals(bytes, received.toByteArray())
    }

    @Test
    fun failed_track_write_is_reported_instead_of_saved_or_busy_retried() {
        for (error in listOf(0, -6)) {
            var calls = 0
            assertFalse(writePcmPacket(20) { _, _ -> calls++; error })
            assertEquals(1, calls)
        }
    }
}
