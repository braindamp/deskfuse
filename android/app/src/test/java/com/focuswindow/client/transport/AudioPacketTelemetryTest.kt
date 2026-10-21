package com.focuswindow.client.transport

import org.junit.Assert.assertTrue
import org.junit.Test

class AudioPacketTelemetryTest {
    @Test
    fun arrival_meter_records_real_receive_gaps() {
        val meter = AudioArrivalMeter()
        meter.arrived(1_000_000_000L)
        meter.arrived(1_010_000_000L)
        meter.arrived(1_036_000_000L)
        meter.arrived(1_101_000_000L)
        val first = meter.report()
        assertTrue(first.contains("rxPackets=4"))
        assertTrue(first.contains("rxGapMaxMs=65"))
        assertTrue(first.contains("rxGap>20ms=2"))
        assertTrue(first.contains("rxGap>50ms=1"))
        val next = meter.report()
        assertTrue(next.contains("rxPackets=4"))
        assertTrue(next.contains("rxGapMaxMs=0"))
        assertTrue(next.contains("rxGap>50ms=0"))
    }

    @Test
    fun consume_meter_reports_missing_sharedflow_chunks_and_queue_age() {
        val meter = AudioConsumptionMeter()
        meter.consumed(11, 1_000_000_000L, 1_006_000_000L)
        meter.consumed(14, 1_010_000_000L, 1_051_000_000L)
        val first = meter.report()
        assertTrue(first.contains("collectPackets=2"))
        assertTrue(first.contains("collectMissing=2"))
        assertTrue(first.contains("collectAgeMaxMs=41"))
        assertTrue(first.contains("collectAge>20ms=1"))
        val next = meter.report()
        assertTrue(next.contains("collectMissing=2"))
        assertTrue(next.contains("collectAgeMaxMs=0"))
    }
}
