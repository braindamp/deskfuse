package com.focuswindow.client.media

object VideoSyncGateCheck {
    @JvmStatic fun main(args: Array<String>) {
        val gate = VideoSyncGate()
        gate.reset(8)
        check(gate.decide(7, true, true, 100) == VideoSyncGate.Decision.DROP)
        check(gate.decide(9, true, true, 100) == VideoSyncGate.Decision.DROP) // new Target not configured
        check(gate.decide(8, false, true, 100) == VideoSyncGate.Decision.REQUEST_KEYFRAME)
        check(gate.decide(8, false, true, 101) == VideoSyncGate.Decision.DROP) // debounce
        check(gate.decide(8, true, false, 200) == VideoSyncGate.Decision.DROP) // initial IDR not queued
        check(gate.decide(8, false, true, 600) == VideoSyncGate.Decision.REQUEST_KEYFRAME)
        check(gate.decide(8, true, true, 601) == VideoSyncGate.Decision.QUEUE)
        gate.queued(true)
        check(gate.decide(8, false, true, 602) == VideoSyncGate.Decision.QUEUE)
        gate.lostFrame()
        check(gate.decide(8, false, true, 1200) == VideoSyncGate.Decision.REQUEST_KEYFRAME)
        check(gate.decide(8, true, true, 1201) == VideoSyncGate.Decision.QUEUE)
        gate.queued(true)
        gate.reset(9)
        check(gate.decide(8, true, true, 1300) == VideoSyncGate.Decision.DROP)
        check(gate.decide(9, false, true, 1300) == VideoSyncGate.Decision.REQUEST_KEYFRAME)
        println("KOTLIN_MEDIA_SYNC_PASS: target versions, initial IDR, backpressure, dropped-packet resync")
    }
}
