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
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.ensureActive
import kotlinx.coroutines.withContext
import kotlin.math.roundToInt

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
    private var packets: Long = 0
    private var partialWrites: Long = 0
    private var writeMaxMs: Long = 0
    private var writeOver10Ms: Long = 0
    private var writeOver30Ms: Long = 0
    private var writtenFrames: Long = 0
    private var bytesPerFrame = 4
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
                .setPerformanceMode(AudioTrack.PERFORMANCE_MODE_LOW_LATENCY)
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
                track = audio
                streamId = id
                writtenFrames = 0
                packets = 0
                partialWrites = 0
                writeMaxMs = 0
                writeOver10Ms = 0
                writeOver30Ms = 0
                bytesPerFrame = channels * 2
                activeLayout = layout
                nativeRoute = route
                routingRequestedAtMs = SystemClock.elapsedRealtime()
                applyVolume()
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
        requestedVolume = clamped / 100f
        applyVolume()
    }

    /**
     * The Windows volume control is the phone's volume control: the level is
     * applied to the media stream itself, so the phone's own slider moves
     * with it. The track keeps its own gain at unity and the system stream
     * does the work, exactly like a wired speaker.
     */
    private fun applyVolume() {
        val systemApplied = applyVolumeToSystem()
        // Apply the requested attenuation once. Fixed-volume devices and
        // denied system-volume changes still need a working track control.
        runCatching { track?.setVolume(if (systemApplied) 1f else requestedVolume) }
    }

    private fun applyVolumeToSystem(): Boolean {
        if (!canChangeSystemVolume) return false
        val index = (requestedVolume * maxSystemIndex).roundToInt()
        if (index == lastAppliedSystemIndex) return true
        return runCatching {
            // No system panel: the phone's own slider moves, quietly.
            manager.setStreamVolume(AudioManager.STREAM_MUSIC, index, 0)
            lastAppliedSystemIndex = index
            true
        }.getOrDefault(false)
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
        if (!canChangeSystemVolume) return null
        val index = systemVolumeIndex() ?: return null
        if (index == lastAppliedSystemIndex) return null
        lastAppliedSystemIndex = index
        val level = (index * 100f / maxSystemIndex).roundToInt().coerceIn(0, 100)
        requestedVolume = level / 100f
        return level
    }

    /** True when this app may move the phone's own volume slider. */
    val canChangeSystemVolume: Boolean
        get() = runCatching { !manager.isVolumeFixed }.getOrDefault(false)

    private val maxSystemIndex: Int
        get() = runCatching {
            manager.getStreamMaxVolume(AudioManager.STREAM_MUSIC)
        }.getOrDefault(15).coerceAtLeast(1)

    /**
     * Finish each packet on the playback worker, including a final short
     * sound. The transport's bounded, dropping audio flow stays independent;
     * no growing carry buffer can accumulate seconds of stale playback.
     */
    suspend fun write(chunk: PcmAudioChunk): Boolean = withContext(Dispatchers.IO) {
        if (chunk.streamId != streamId) return@withContext true
        val current = track ?: return@withContext false
        if (!routeStillSupportsSurround()) return@withContext false
        val completed = writePcmPacket(chunk.pcm.size) { offset, count ->
            ensureActive()
            if (track !== current || streamId != chunk.streamId) return@writePcmPacket -1
            val writeStartedNs = System.nanoTime()
            val written = runCatching {
                current.write(chunk.pcm, offset, count, AudioTrack.WRITE_BLOCKING)
            }.getOrDefault(-1)
            val writeMs = (System.nanoTime() - writeStartedNs).coerceAtLeast(0) / 1_000_000
            if (writeMs > writeMaxMs) writeMaxMs = writeMs
            if (writeMs > 10) writeOver10Ms++
            if (writeMs > 30) writeOver30Ms++
            if (written > 0) {
                writtenFrames += written / bytesPerFrame
                if (offset + written < chunk.pcm.size) partialWrites++
            }
            written
        }
        if (completed) packets++
        completed
    }

    /** How the audio path is behaving, for the diagnostics. */
    fun stats(): String {
        val current = track
        val played = runCatching { current?.playbackHeadPosition?.toLong()?.and(0xffffffffL) ?: 0L }
            .getOrDefault(0L)
        val queued = (writtenFrames - played).coerceAtLeast(0)
        val rate = current?.sampleRate ?: 1
        val bufferMs = runCatching { (current?.bufferSizeInFrames ?: 0) * 1000 / rate }
            .getOrDefault(0)
        val underruns = runCatching { current?.underrunCount ?: 0 }.getOrDefault(0)
        val timing = "writeMaxMs=$writeMaxMs write>10ms=$writeOver10Ms " +
            "write>30ms=$writeOver30Ms"
        writeMaxMs = 0
        writeOver10Ms = 0
        writeOver30Ms = 0
        return "packets=$packets partial=$partialWrites queued=${queued * 1000 / rate}ms " +
            "buffer=${bufferMs}ms underruns=$underruns $timing"
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
