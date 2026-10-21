package com.focuswindow.client.media

import android.content.Context
import android.media.AudioAttributes
import android.media.AudioDeviceInfo
import android.media.AudioFocusRequest
import android.media.AudioFormat
import android.media.AudioManager
import android.media.AudioTrack
import android.os.SystemClock
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
    @Volatile private var requestedVolume = 1f
    private var focus: AudioFocusRequest? = null
    private var nativeRoute: AudioDeviceInfo? = null
    private var carry: ByteArray = ByteArray(0)
    private var packets: Long = 0
    private var partialWrites: Long = 0
    private var lastAppliedSystemIndex: Int = -1
    private var activeLayout = "stereo"
    private var routingRequestedAtMs = 0L

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
        device: AudioDeviceInfo? = null,
    ): Boolean {
        stop()
        val selected = (listOf(stereo) + surroundLayouts).firstOrNull { it.name == layout }
            ?: return false
        if (sampleRate !in 8_000..192_000 || channels != selected.channels ||
            format != "pcm_s16le" || id <= 0) return false

        val route = when {
            device != null -> device
            layout == "stereo" -> null
            else -> supportingDevice(selected, sampleRate) ?: return false
        }
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
                carry = ByteArray(0)
                track = audio
                activeLayout = layout
                nativeRoute = route
                routingRequestedAtMs = SystemClock.elapsedRealtime()
                audio.setVolume(requestedVolume)
                audio.play()
                // Android resolves AudioTrack routing asynchronously. The IO
                // reader verifies it after the route settles, then triggers an
                // explicit stereo renegotiation if this peripheral disappears.
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

    private fun routeStillSupportsSurround(): Boolean {
        if (activeLayout == "stereo") return true
        val selected = surroundLayouts.firstOrNull { it.name == activeLayout } ?: return false
        val route = nativeRoute ?: return false
        val routed = track?.routedDevice
        if (routed == null)
            return SystemClock.elapsedRealtime() - routingRequestedAtMs < 500
        return routed.id == route.id && routed.channelMasks.contains(selected.mask)
    }

    /**
     * The phone's own speaker, chosen explicitly. When the PC's sound is
     * sent to the phone, the phone must play it on its own speaker and not
     * hand it back to the PC over Bluetooth.
     */
    fun builtInSpeaker(): AudioDeviceInfo? =
        manager.getDevices(AudioManager.GET_DEVICES_OUTPUTS)
            .firstOrNull { it.type == AudioDeviceInfo.TYPE_BUILTIN_SPEAKER }

    /**
     * True when the audio is really leaving through the requested device.
     * Android resolves the route asynchronously, so this is checked after
     * playback has started rather than at creation time.
     */
    fun isRoutedTo(device: AudioDeviceInfo): Boolean {
        val routed = track?.routedDevice ?: return false
        return routed.id == device.id
    }

    /** The playback level, 0..100, as the user set it on the PC. */
    fun setVolume(level: Int) {
        val clamped = level.coerceIn(0, 100)
        val changed = clamped != (requestedVolume * 100f).toInt()
        requestedVolume = clamped / 100f
        lastAppliedSystemIndex = -1
        applyVolumeToSystem()
        val current = track ?: return
        runCatching { current.setVolume(requestedVolume) }
        // A speaker tells you it heard you: the same short tone a real device
        // makes when its volume is changed.
        if (changed) ding()
    }

    /**
     * The short tone a speaker makes when the volume is changed. It is
     * written into the same track, so it arrives with the music and at the
     * new level, exactly like a real device.
     */
    private fun ding() {
        val current = track ?: return
        val sampleRate = current.sampleRate
        if (sampleRate <= 0) return
        val channels = current.channelCount.coerceAtLeast(1)
        val frameCount = (sampleRate * 0.12).toInt()
        val pcm = ByteArray(frameCount * channels * 2)
        repeat(frameCount) { index ->
            val t = index.toDouble() / sampleRate
            // Two quick partials, like a bell, fading out.
            val envelope = Math.exp(-t * 14.0)
            val value = (Math.sin(2 * Math.PI * 1180 * t) * 0.5 +
                Math.sin(2 * Math.PI * 1760 * t) * 0.25) * envelope * 9000
            val sample = value.toInt().coerceIn(-32768, 32767).toShort()
            repeat(channels) { ch ->
                val at = (index * channels + ch) * 2
                pcm[at] = (sample.toInt() and 0xFF).toByte()
                pcm[at + 1] = ((sample.toInt() shr 8) and 0xFF).toByte()
            }
        }
        var offset = 0
        while (offset < pcm.size) {
            val written = runCatching {
                current.write(pcm, offset, pcm.size - offset, AudioTrack.WRITE_NON_BLOCKING)
            }.getOrDefault(-1)
            if (written <= 0) break
            offset += written
        }
    }

    /**
     * The Windows volume control is the phone's volume control: the level is
     * applied to the media stream itself, so the phone's own slider moves
     * with it. The track keeps its own gain at unity and the system stream
     * does the work, exactly like a wired speaker.
     */
    private fun applyVolumeToSystem() {
        val index = (requestedVolume * maxSystemIndex).toInt()
        if (index == lastAppliedSystemIndex) return
        if (!canChangeSystemVolume) return
        runCatching {
            // No system panel: the phone's own slider moves, quietly.
            manager.setStreamVolume(AudioManager.STREAM_MUSIC, index, 0)
            lastAppliedSystemIndex = index
        }
    }

    /**
     * The phone's current media volume, so the PC can follow the phone's
     * slider. Returns null when the phone cannot change it.
     */
    fun systemVolumeIndex(): Int? = runCatching {
        manager.getStreamVolume(AudioManager.STREAM_MUSIC)
    }.getOrNull()

    /**
     * The level the user just set on the phone's own slider, 0..100, or
     * null when the phone's volume still matches what the PC asked for.
     */
    fun systemVolumeChanged(): Int? {
        val index = systemVolumeIndex() ?: return null
        if (index == lastAppliedSystemIndex) return null
        lastAppliedSystemIndex = index
        val level = (index * 100f / maxSystemIndex).toInt().coerceIn(0, 100)
        return level
    }

    /** True when this app may move the phone's own volume slider. */
    val canChangeSystemVolume: Boolean =
        runCatching { manager.getStreamVolume(AudioManager.STREAM_MUSIC) }.isSuccess

    private val maxSystemIndex: Int
        get() = runCatching {
            manager.getStreamMaxVolume(AudioManager.STREAM_MUSIC)
        }.getOrDefault(15).coerceAtLeast(1)

    /**
     * One packet in, one packet out.
     *
     * A nonblocking write can be *partial*: Android returns how many bytes
     * it actually took and the rest of the packet would be thrown away,
     * which is an audible click. The remainder is therefore carried over to
     * the next packet, and only genuinely stale audio is dropped.
     */
    fun write(chunk: PcmAudioChunk): Boolean {
        if (chunk.streamId != streamId) return true
        val current = track ?: return false
        if (!routeStillSupportsSurround()) return false
        val pcm = chunk.pcm
        var offset = 0
        // Anything left over from a previous partial write goes first.
        if (carry.isNotEmpty()) {
            val need = carry.size + pcm.size
            val buffer = ByteArray(need)
            carry.copyInto(buffer, 0)
            pcm.copyInto(buffer, carry.size)
            carry = ByteArray(0)
            val written = writeAll(current, buffer)
            if (written < 0) return false
            if (written < buffer.size) {
                carry = buffer.copyOfRange(written, buffer.size)
                partialWrites++
            }
            packets++
            return true
        }
        val written = writeAll(current, pcm)
        if (written < 0) return false
        if (written < pcm.size) {
            carry = pcm.copyOfRange(written, pcm.size)
            partialWrites++
        }
        packets++
        return true
    }

    /** Writes as much as the track takes, without blocking the transport. */
    private fun writeAll(current: AudioTrack, pcm: ByteArray): Int {
        var total = 0
        while (total < pcm.size) {
            val count = runCatching {
                current.write(pcm, total, pcm.size - total, AudioTrack.WRITE_NON_BLOCKING)
            }.getOrDefault(-1)
            if (count <= 0) break
            total += count
        }
        return total
    }

    /** How the audio path is behaving, for the diagnostics. */
    fun stats(): String = "packets=$packets partial=$partialWrites carry=${carry.size}B"

    /** Called when the track is restarted, so the carry does not cross streams. */
    fun resetCarry() {
        carry = ByteArray(0)
    }

    fun stop() {
        streamId = -1
        activeLayout = "stereo"
        nativeRoute = null
        routingRequestedAtMs = 0L
        val old = track
        track = null
        runCatching { old?.pause() }
        runCatching { old?.flush() }
        runCatching { old?.release() }
        focus?.let { manager.abandonAudioFocusRequest(it) }
        focus = null
    }
}
