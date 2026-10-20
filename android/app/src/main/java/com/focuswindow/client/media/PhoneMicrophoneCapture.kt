package com.focuswindow.client.media

import android.Manifest
import android.content.Context
import android.content.pm.PackageManager
import android.media.AudioFormat
import android.media.AudioRecord
import android.media.MediaRecorder
import androidx.core.content.ContextCompat
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.currentCoroutineContext
import kotlinx.coroutines.isActive
import kotlinx.coroutines.withContext

/**
 * Explicit phone microphone producer; never starts on pairing alone.
 *
 * Capture is limited to an activity-foreground session, using Android's
 * runtime RECORD_AUDIO permission and system microphone privacy indicator.
 * This class never persists samples, bypasses permission, or tries to resume
 * microphone use while backgrounded. A future foreground-service microphone
 * mode requires its own user-visible lifecycle and Android service type.
 */
class PhoneMicrophoneCapture(private val context: Context) {
    companion object {
        const val SAMPLE_RATE = 16_000
        const val CHANNELS = 1
        const val PCM_BYTES_PER_20MS = 640
    }

    @Volatile private var recording: AudioRecord? = null

    fun permitted(): Boolean = ContextCompat.checkSelfPermission(
        context, Manifest.permission.RECORD_AUDIO,
    ) == PackageManager.PERMISSION_GRANTED

    /** Suspends until stopped/disconnected. Frames are mono 16-bit little endian. */
    suspend fun capture(send: suspend (ByteArray) -> Unit) = withContext(Dispatchers.IO) {
        if (!permitted()) throw SecurityException("Microphone permission not granted")
        val min = AudioRecord.getMinBufferSize(
            SAMPLE_RATE, AudioFormat.CHANNEL_IN_MONO, AudioFormat.ENCODING_PCM_16BIT,
        )
        require(min > 0) { "Phone does not support 16k mono microphone capture" }
        val audio = AudioRecord.Builder()
            .setAudioSource(MediaRecorder.AudioSource.VOICE_RECOGNITION)
            .setAudioFormat(
                AudioFormat.Builder()
                    .setSampleRate(SAMPLE_RATE)
                    .setEncoding(AudioFormat.ENCODING_PCM_16BIT)
                    .setChannelMask(AudioFormat.CHANNEL_IN_MONO)
                    .build(),
            )
            .setBufferSizeInBytes(maxOf(min * 2, PCM_BYTES_PER_20MS * 4))
            .build()
        try {
            check(audio.state == AudioRecord.STATE_INITIALIZED) {
                "Android microphone could not initialize"
            }
            recording = audio
            audio.startRecording()
            check(audio.recordingState == AudioRecord.RECORDSTATE_RECORDING) {
                "Android blocked microphone capture"
            }
            val buffer = ByteArray(PCM_BYTES_PER_20MS)
            while (currentCoroutineContext().isActive && recording === audio) {
                // Permission can be revoked while a session is running.
                if (!permitted()) break
                val read = audio.read(buffer, 0, buffer.size, AudioRecord.READ_BLOCKING)
                if (read < 0) break
                val aligned = read and -2
                if (aligned > 0) send(buffer.copyOf(aligned))
            }
        } catch (ex: CancellationException) {
            throw ex
        } finally {
            if (recording === audio) recording = null
            runCatching {
                if (audio.recordingState == AudioRecord.RECORDSTATE_RECORDING) audio.stop()
            }
            audio.release()
        }
    }

    fun stop() {
        val audio = recording
        recording = null
        runCatching { audio?.stop() }
    }
}
