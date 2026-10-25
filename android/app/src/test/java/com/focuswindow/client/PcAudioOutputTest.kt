package com.focuswindow.client

import android.content.Context
import android.media.AudioManager
import android.media.AudioTrack
import com.focuswindow.client.media.PcAudioOutput
import org.junit.Assert.*
import org.junit.Test
import org.junit.runner.RunWith
import org.robolectric.RobolectricTestRunner
import org.robolectric.RuntimeEnvironment
import org.robolectric.annotation.Config
import org.robolectric.annotation.Implementation
import org.robolectric.annotation.Implements
import org.robolectric.shadow.api.Shadow
import org.robolectric.shadows.ShadowAudioTrack
import org.robolectric.util.ReflectionHelpers

@RunWith(RobolectricTestRunner::class)
@Config(sdk = [34], shadows = [PcAudioOutputTest.PlaybackShadow::class])
class PcAudioOutputTest {
    // Observe the gain reaching Android; the JVM cannot model an actual
    // mixer or speaker. Hardware buffer tuning requires a device test.
    @Implements(AudioTrack::class)
    class PlaybackShadow : ShadowAudioTrack() {
        var gain = -1f

        @Implementation
        fun setVolume(value: Float): Int { gain = value; return AudioTrack.SUCCESS }

    }

    private val context: Context get() = RuntimeEnvironment.getApplication()
    private val manager: AudioManager
        get() = context.getSystemService(Context.AUDIO_SERVICE) as AudioManager

    private fun playback(output: PcAudioOutput): PlaybackShadow =
        Shadow.extract(ReflectionHelpers.getField<AudioTrack>(output, "track"))

    @Test
    fun volume_before_and_after_start_uses_system_level_without_extra_attenuation() {
        val output = PcAudioOutput(context)
        try {
            output.setVolume(50)
            assertTrue(output.start(1, 48000, 2, "pcm_s16le"))
            assertEquals(1f, playback(output).gain, 0f)
            val max = manager.getStreamMaxVolume(AudioManager.STREAM_MUSIC)
            assertEquals(kotlin.math.round(max * 0.5).toInt(), output.systemVolumeIndex())
            output.setVolume(20)
            assertEquals(1f, playback(output).gain, 0f)
            output.setVolume(0)
            assertEquals(0, output.systemVolumeIndex())
            assertEquals(1f, playback(output).gain, 0f)
        } finally { output.stop() }
    }

    @Test
    fun phone_slider_round_trip_does_not_lower_the_volume_or_reset_it_on_restart() {
        val output = PcAudioOutput(context)
        try {
            val max = manager.getStreamMaxVolume(AudioManager.STREAM_MUSIC)
            manager.setStreamVolume(AudioManager.STREAM_MUSIC, max / 2, 0)
            val changed = output.systemVolumeChanged()!!
            output.setVolume(changed)
            assertEquals(max / 2, output.systemVolumeIndex())
            assertNull(output.systemVolumeChanged())
            assertTrue(output.start(1, 48000, 2, "pcm_s16le"))
            assertEquals(max / 2, output.systemVolumeIndex())
            assertEquals(1f, playback(output).gain, 0f)
        } finally { output.stop() }
    }

    @Test
    fun native_volume_reads_back_system_state_without_opening_a_track() {
        val output = PcAudioOutput(context)
        assertTrue(output.applyNativeVolume(0))
        assertEquals(0, output.systemVolumeIndex())
        assertNull(ReflectionHelpers.getField<AudioTrack?>(output, "track"))
        assertTrue(output.applyNativeVolume(60))
        // A separate user/system change invalidates a previously cached index.
        manager.setStreamVolume(AudioManager.STREAM_MUSIC, 0, 0)
        assertTrue(output.applyNativeVolume(60))
        assertTrue(output.systemVolumeIndex()!! > 0)
        assertFalse(output.applyNativeVolume(101))
        assertTrue(output.applyNativeVolume(0))
        assertEquals(0, output.systemVolumeIndex())
    }
}
