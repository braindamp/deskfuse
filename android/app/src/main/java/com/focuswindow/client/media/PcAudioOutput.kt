package com.focuswindow.client.media

import android.content.Context
import android.media.AudioAttributes
import android.media.AudioDeviceInfo
import android.media.AudioFocusRequest
import android.media.AudioFormat
import android.media.AudioManager
import android.media.AudioTrack
import com.focuswindow.client.transport.PcmAudioChunk

/**
 * Android playback of user-approved mixed PC audio (NOT a native Windows
 * DeskFuse speaker endpoint). Match the negotiated, position-aware Windows
 * output format to the *actual connected* Android audio route.
 *
 * Never advertise 5.1/7.1 from channel count alone. A 6-channel Android
 * endpoint with a different speaker mask is not the same 5.1 configuration.
 * Android owns routing/headphone focus; we do not hijack defaults.
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
    private var nativeRoute: AudioDeviceInfo? = null
    private var activeLayout = "stereo"

    private data class Layout(val name: String, val channels: Int, val mask: Int)
    private val stereo = Layout("stereo", 2, AudioFormat.CHANNEL_OUT_STEREO)
    private val surroundLayouts = listOf(
        Layout("7.1_surround", 8, AudioFormat.CHANNEL_OUT_7POINT1_SURROUND),
        Layout("5.1_back", 6, AudioFormat.CHANNEL_OUT_5POINT1),
        Layout("5.1_side", 6,
            AudioFormat.CHANNEL_OUT_FRONT_LEFT or AudioFormat.CHANNEL_OUT_FRONT_RIGHT or
                AudioFormat.CHANNEL_OUT_FRONT_CENTER or AudioFormat.CHANNEL_OUT_LOW_FREQUENCY or
                AudioFormat.CHANNEL_OUT_SIDE_LEFT or AudioFormat.CHANNEL_OUT_SIDE_RIGHT),
    )

    private fun outputDevices(): List<AudioDeviceInfo> =
        manager.getDevices(AudioManager.GET_DEVICES_OUTPUTS).toList()

    private fun supportingDevice(layout: Layout, sampleRate: Int? = null): AudioDeviceInfo? =
        outputDevices().firstOrNull { device ->
            // An empty Android channelMasks array means no *declared* layout,
            // not evidence for this particular 5.1/7.1 mapping.
            device.channelMasks.contains(layout.mask) &&
                (device.channelCounts.isEmpty() || device.channelCounts.contains(layout.channels)) &&
                (sampleRate == null || device.sampleRates.isEmpty() ||
                    device.sampleRates.contains(sampleRate))
        }

    /**
     * Offer only multichannel routes Android reports by exact position mask.
     * When no connected route can prove it, ask Windows for stereo instead.
     */
    fun offeredLayouts(): List<String> = buildList {
        surroundLayouts.forEach { if (supportingDevice(it) != null) add(it.name) }
        add(stereo.name)
    }

    fun start(
        id: Int, sampleRate: Int, channels: Int, format: String,
        layout: String = "stereo",
    ): Boolean {
        stop()
        val selected = (listOf(stereo) + surroundLayouts).firstOrNull { it.name == layout }
            ?: return false
        if (sampleRate !in 8_000..192_000 || channels != selected.channels ||
            format != "pcm_s16le" || id <= 0) return false

        val route = if (layout == "stereo") null
            else (supportingDevice(selected, sampleRate) ?: return false)
        val encoding = AudioFormat.ENCODING_PCM_16BIT
        val min = AudioTrack.getMinBufferSize(sampleRate, selected.mask, encoding)
        if (min <= 0) return false
        val focusRequest = AudioFocusRequest.Builder(AudioManager.AUDIOFOCUS_GAIN)
            .setAudioAttributes(attributes)
            .setOnAudioFocusChangeListener { change ->
                val current = track ?: return@setOnAudioFocusChangeListener
                runCatching {
                    if (change == AudioManager.AUDIOFOCUS_GAIN) current.play()
                    else current.pause()
                }
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
                        .setSampleRate(sampleRate).setChannelMask(selected.mask).build(),
                )
                .setTransferMode(AudioTrack.MODE_STREAM)
                // Android may enforce a larger hardware minimum. Never queue
                // multiple seconds of media simply to avoid a rare underrun.
                .setBufferSizeInBytes(maxOf(min, sampleRate * channels * 2 / 50))
                .build()
            if (audio.state != AudioTrack.STATE_INITIALIZED ||
                (route != null && !audio.setPreferredDevice(route))) {
                audio.release()
                stop()
                false
            } else {
                streamId = id
                track = audio
                activeLayout = layout
                nativeRoute = route
                audio.play()
                if (route != null && !routeStillSupportsSurround()) {
                    stop()
                    false
                } else true
            }
        } catch (e: IllegalArgumentException) {
            stop()
            false
        } catch (e: IllegalStateException) {
            stop()
            false
        }
    }

    private fun routeStillSupportsSurround(): Boolean {
        if (activeLayout == "stereo") return true
        val selected = surroundLayouts.firstOrNull { it.name == activeLayout } ?: return false
        val route = nativeRoute ?: return false
        val routed = track?.routedDevice
        return routed != null && routed.id == route.id &&
            routed.channelMasks.contains(selected.mask)
    }

    /**
     * One nonblocking write per audio packet; if Android's hardware output
     * cannot keep up, drop the packet rather than stalling network/video.
     * A route loss returns false so the controller can negotiate stereo.
     */
    fun write(chunk: PcmAudioChunk): Boolean {
        if (chunk.streamId != streamId) return true
        val current = track ?: return false
        if (!routeStillSupportsSurround()) return false
        val count = runCatching {
            current.write(chunk.pcm, 0, chunk.pcm.size, AudioTrack.WRITE_NON_BLOCKING)
        }.getOrDefault(-1)
        return count >= 0
    }

    fun stop() {
        streamId = -1
        activeLayout = "stereo"
        nativeRoute = null
        val old = track
        track = null
        runCatching { old?.pause() }
        runCatching { old?.flush() }
        runCatching { old?.release() }
        focus?.let { manager.abandonAudioFocusRequest(it) }
        focus = null
    }
}
