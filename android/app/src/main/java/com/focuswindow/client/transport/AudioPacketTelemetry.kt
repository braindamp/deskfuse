package com.focuswindow.client.transport

import java.util.concurrent.atomic.AtomicLong

/**
 * Local-only statistics for PCM delivery. No wire format, buffering or
 * playback-policy changes. One receive coroutine owns [arrived].
 */
internal class AudioArrivalMeter {
    private val received = AtomicLong()
    private val lastArrivalNs = AtomicLong()
    private val windowMaxGapMs = AtomicLong()
    private val windowGapOver20 = AtomicLong()
    private val windowGapOver50 = AtomicLong()

    fun arrived(nowNs: Long): Long {
        val previous = lastArrivalNs.getAndSet(nowNs)
        if (previous != 0L && nowNs > previous) {
            val gapMs = (nowNs - previous) / 1_000_000
            windowMaxGapMs.accumulateAndGet(gapMs, java.lang.Long::max)
            if (gapMs > 20) windowGapOver20.incrementAndGet()
            if (gapMs > 50) windowGapOver50.incrementAndGet()
        }
        return received.incrementAndGet()
    }

    fun report(): String =
        "rxPackets=${received.get()} " +
            "rxGapMaxMs=${windowMaxGapMs.getAndSet(0)} " +
            "rxGap>20ms=${windowGapOver20.getAndSet(0)} " +
            "rxGap>50ms=${windowGapOver50.getAndSet(0)}"
}

/**
 * Each decrypted packet gets a local receive sequence before entering the
 * existing DROP_OLDEST SharedFlow. The one speaker subscriber can infer
 * missing packets; no on-wire sequence or protocol change is required.
 * An initial subscriber gap is intentionally not counted as an eviction.
 */
internal class AudioConsumptionMeter {
    private var lastSequence = 0L
    private var consumed = 0L
    private var missing = 0L
    private var windowMaxAgeMs = 0L
    private var windowAgeOver20 = 0L

    fun consumed(sequence: Long, receivedAtNs: Long, nowNs: Long) {
        if (lastSequence != 0L && sequence > lastSequence + 1) {
            missing += sequence - lastSequence - 1
        }
        if (sequence > lastSequence) lastSequence = sequence
        consumed++
        if (receivedAtNs > 0 && nowNs >= receivedAtNs) {
            val ageMs = (nowNs - receivedAtNs) / 1_000_000
            if (ageMs > windowMaxAgeMs) windowMaxAgeMs = ageMs
            if (ageMs > 20) windowAgeOver20++
        }
    }

    fun report(): String {
        val text = "collectPackets=$consumed collectMissing=$missing " +
            "collectAgeMaxMs=$windowMaxAgeMs collectAge>20ms=$windowAgeOver20"
        windowMaxAgeMs = 0
        windowAgeOver20 = 0
        return text
    }
}
