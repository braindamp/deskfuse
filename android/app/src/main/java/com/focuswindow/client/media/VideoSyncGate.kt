package com.focuswindow.client.media

/**
 * P2.3 decoder safety: never feed inter frames until an IDR has actually been
 * QUEUED; never decode frames for an unannounced target/geometry. A dropped
 * access unit invalidates prediction until the next keyframe.
 * Pure Kotlin: can be checked without an Android emulator.
 */
internal class VideoSyncGate {
    internal enum class Decision { QUEUE, DROP, REQUEST_KEYFRAME }
    private var targetVersion = -1L
    private var needsKeyframe = true
    private var lastRequestMs = Long.MIN_VALUE

    @Synchronized fun reset(version: Long) {
        targetVersion = version
        needsKeyframe = true
        lastRequestMs = Long.MIN_VALUE
    }

    @Synchronized fun decide(version: Long, keyframe: Boolean, inputAvailable: Boolean, nowMs: Long): Decision {
        if (version != targetVersion || version < 0) return Decision.DROP
        if (!inputAvailable) needsKeyframe = true
        if (!inputAvailable || (needsKeyframe && !keyframe)) {
            // Throttle requests, but always allow the first request after reset.
            if (lastRequestMs == Long.MIN_VALUE || nowMs - lastRequestMs >= 500) {
                lastRequestMs = nowMs
                return Decision.REQUEST_KEYFRAME
            }
            return Decision.DROP
        }
        return Decision.QUEUE
    }

    /** Called only AFTER MediaCodec.queueInputBuffer completed without error. */
    @Synchronized fun queued(keyframe: Boolean) {
        if (keyframe) needsKeyframe = false
    }

    /** Call on queue exception, decoder reset or any missing inter frame. */
    @Synchronized fun lostFrame() { needsKeyframe = true }
}
