package com.focuswindow.client.media

/**
 * Measures how far the picture and the sound are apart.
 *
 * The picture and the sound travel by different routes: the picture is
 * captured, encoded, sent, decoded and composited, the sound is captured,
 * sent and played. Each arrives at the phone at its own time, and the two
 * do not arrive together.
 *
 * The host stamps both streams with one clock, so the phone can pair the
 * picture and the sound that belong together, and then compare when each
 * of them actually reached the phone. The phone's own clock does not need to
 * match the host's: the difference between the two measurements cancels it.
 *
 * This measures. It does not yet correct; the correction is the next step
 * and depends on the number this produces.
 */
object SyncMeasurement {
    /** One picture and one sound, and when each of them reached the phone. */
    private data class Pairing(
        /** The host's capture time of the picture, in host microseconds. */
        val pictureHostUs: Long,
        /** When the picture was put on the screen, in phone nanoseconds. */
        val pictureShownAtNanos: Long,
        /** When the sound that belongs with it was played, in phone nanoseconds. */
        val soundAtNanos: Long,
    )

    private val lock = Any()
    private var latest: Pairing? = null
    private var lastOffsetMs: Double? = null

    /**
     * The picture was put on the screen. [hostUs] is the host's capture
     * time of that frame, [shownAtNanos] is the phone's clock.
     */
    fun notePicture(hostUs: Long, shownAtNanos: Long) {
        synchronized(lock) {
            val sound = latest
            if (sound != null && sound.pictureHostUs == 0L) {
                latest = sound.copy(pictureHostUs = hostUs, pictureShownAtNanos = shownAtNanos)
            } else {
                latest = Pairing(hostUs, shownAtNanos, 0L)
            }
        }
    }

    /**
     * The sound was played. [hostUs] is the host's capture time of the
     * first sample of that sound, [playedAtNanos] is the phone's clock.
     */
    fun noteSound(hostUs: Long, playedAtNanos: Long) {
        val offset = synchronized(lock) {
            val picture = latest
            if (picture == null || picture.pictureShownAtNanos == 0L) {
                latest = (picture ?: Pairing(0L, 0L, 0L)).copy(soundAtNanos = playedAtNanos)
                null
            } else {
                // The sound reached the phone this much earlier than the
                // picture it belongs with. Positive means the sound leads.
                val ms = (picture.pictureShownAtNanos - playedAtNanos) / 1_000_000.0
                lastOffsetMs = ms
                latest = picture.copy(soundAtNanos = playedAtNanos)
                ms
            }
        }
        if (offset != null) {
            android.util.Log.i(
                "DeskFuseSync",
                "picture and sound are ${"%.1f".format(offset)} ms apart " +
                    "(positive: the sound arrives first)",
            )
        }
    }

    /** The last measured offset in milliseconds, or null before the first measurement. */
    fun offsetMs(): Double? = synchronized(lock) { lastOffsetMs }
}
