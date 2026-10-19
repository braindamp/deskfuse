package com.focuswindow.client.media

import android.content.Context
import android.media.AudioAttributes
import android.media.AudioFocusRequest
import android.media.AudioFormat
import android.media.AudioManager
import android.media.AudioTrack
import com.focuswindow.client.transport.PcmAudioChunk

/**
 * One opt-in, signed-16-bit stereo PC output stream. Android owns the audio
 * endpoint and automatically follows headphones/Bluetooth routing.
 * Never opens Android's microphone or changes Windows audio devices.
 */
class PcAudioOutput(context: Context) {
    private val manager = context.applicationContext.getSystemService(
        Context.AUDIO_SERVICE,
    ) as AudioManager
    private val attributes = AudioAttributes.Builder()
        .setUsage(AudioAttributes.USAGE_MEDIA)
        .setContentType(AudioAttributes.CONTENT_TYPE_MUSIC)
        .build()
    @Volatile private var track: AudioTrack? = null
    @Volatile private var streamId: Int = -1
    private var focus: AudioFocusRequest? = null

    fun start(id: Int, sampleRate: Int, channels: Int, format: String): Boolean {
        stop()
        if (sampleRate !in 8_000..192_000 || channels != 2 || format != "pcm_s16le") return false
        val channelMask = AudioFormat.CHANNEL_OUT_STEREO
        val encoding = AudioFormat.ENCODING_PCM_16BIT
        val min = AudioTrack.getMinBufferSize(sampleRate, channelMask, encoding)
        if (min <= 0) return false
        val focusRequest = AudioFocusRequest.Builder(AudioManager.AUDIOFOCUS_GAIN)
            .setAudioAttributes(attributes)
            .setOnAudioFocusChangeListener { change ->
                val current = track ?: return@setOnAudioFocusChangeListener
                if (change == AudioManager.AUDIOFOCUS_GAIN) current.play()
                else current.pause()
            }
            .build()
        if (manager.requestAudioFocus(focusRequest) != AudioManager.AUDIOFOCUS_REQUEST_GRANTED)
            return false
        focus = focusRequest
        return try {
            val audio = AudioTrack.Builder()
                .setAudioAttributes(attributes)
                .setAudioFormat(
                    AudioFormat.Builder().setEncoding(encoding)
                        .setSampleRate(sampleRate).setChannelMask(channelMask).build(),
                )
                .setTransferMode(AudioTrack.MODE_STREAM)
                .setBufferSizeInBytes(maxOf(min * 2, sampleRate * channels * 2 / 10))
                .build()
            if (audio.state != AudioTrack.STATE_INITIALIZED) {
                audio.release()
                stop()
                false
            } else {
                streamId = id
                track = audio
                audio.play()
                true
            }
        } catch (e: IllegalArgumentException) {
            stop()
            false
        } catch (e: IllegalStateException) {
            stop()
            false
        }
    }

    /** Write from an IO coroutine, never the Compose/render thread. */
    fun write(chunk: PcmAudioChunk) {
        if (chunk.streamId != streamId) return
        val active = track ?: return
        var written = 0
        while (written < chunk.pcm.size && streamId == chunk.streamId) {
            val count = active.write(
                chunk.pcm, written, chunk.pcm.size - written, AudioTrack.WRITE_BLOCKING,
            )
            if (count <= 0) break
            written += count
        }
    }

    fun stop() {
        streamId = -1
        val old = track
        track = null
        runCatching { old?.pause() }
        runCatching { old?.flush() }
        runCatching { old?.release() }
        focus?.let { manager.abandonAudioFocusRequest(it) }
        focus = null
    }
}
