package com.focuswindow.client.session

import android.util.Log
import com.focuswindow.client.media.PcAudioOutput
import com.focuswindow.client.profile.PhoneProfile
import com.focuswindow.client.protocol.ClientMessage
import com.focuswindow.client.protocol.ServerMessage
import com.focuswindow.client.transport.AudioConsumptionMeter
import com.focuswindow.client.transport.LanTransport
import com.focuswindow.client.transport.PairingPayload
import kotlin.math.min
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Job
import kotlinx.coroutines.delay
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.isActive
import kotlinx.coroutines.launch

/**
 * The phone speaker client.
 *
 * It authenticates with the same per-device credentials as any other
 * session, but asks for one capability only: speakers. There is no virtual
 * monitor, no surface and no video: the phone is a speaker.
 *
 * The host provisions a real Windows playback endpoint for this phone. The
 * user selects that endpoint in Windows, and whatever the PC plays into it
 * arrives here and is played on the phone's own speaker. The Windows volume
 * control is mirrored onto the phone.
 */
class SpeakerController(
    private val scope: CoroutineScope,
    private val clientVersion: String,
    private val output: PcAudioOutput,
    private val transport: LanTransport = LanTransport(scope),
) {
    sealed interface State {
        data object Idle : State
        data class Connecting(val host: String) : State
        data class Waiting(val detail: String) : State
        data class Playing(val endpoint: String) : State
        data class Failed(val detail: String) : State
    }

    private val _state = MutableStateFlow<State>(State.Idle)
    val state: StateFlow<State> = _state.asStateFlow()

    private var pumpJob: Job? = null
    private var audioJob: Job? = null
    private var reconnectJob: Job? = null
    private var payload: PairingPayload? = null
    private var attempt: Int = 0
    private var profile: PhoneProfile? = null

    fun start(payload: PairingPayload, phoneProfile: PhoneProfile) {
        this.payload = payload
        attempt = 0
        profile = phoneProfile
        pumpJob?.cancel()
        audioJob?.cancel()
        pumpJob = scope.launch {
            launch { pumpControl() }
            launch { pumpAudio() }
            launch { pumpVolume() }
            launch { pumpEvents() }
            transport.connect(payload)
        }
    }

    fun stop() {
        // Cancellation does not interrupt a thread blocked on Socket.read().
        // Explicitly tear down the authenticated TCP connection before the
        // Service immediately cancels its scope.
        reconnectJob?.cancel()
        reconnectJob = null
        pumpJob?.cancel()
        audioJob?.cancel()
        pumpJob = null
        audioJob = null
        transport.disconnectNow()
        output.stop()
        _state.value = State.Idle
    }

    private suspend fun pumpEvents() {
        transport.events.collect { event ->
            when (event) {
                is LanTransport.TransportEvent.Connecting ->
                    _state.value = State.Connecting(event.host)

                is LanTransport.TransportEvent.Ready -> {
                    // The accessory hello: no display is acquired, and the
                    // only capability requested is the phone's speakers.
                    val current = profile ?: return@collect
                    transport.send(
                        ClientMessage.BridgeHello(
                            clientVersion = clientVersion,
                            profile = current,
                            capabilities = listOf("speakers"),
                        )
                    )
                    _state.value = State.Waiting("Waiting for the PC to create the phone speaker endpoint.")
                }

                is LanTransport.TransportEvent.Closed -> {
                    // The PC can go away and come back. Keep trying, the
                    // same way the workspace session does.
                    if (event.recoverable) scheduleReconnect(event.reason)
                    else _state.value = State.Failed(event.reason)
                }

                is LanTransport.TransportEvent.Failed -> {
                    if (event.retryable) scheduleReconnect(event.detail)
                    else _state.value = State.Failed(event.detail)
                }

                else -> Unit
            }
        }
    }

    /** Reconnect to the PC after it goes away, with a bounded backoff. */
    private fun scheduleReconnect(reason: String) {
        if (reconnectJob?.isActive == true) return
        val pl = payload ?: return
        val pf = profile ?: return
        reconnectJob = scope.launch {
            while (attempt < MAX_RECONNECT) {
                attempt++
                val wait = min(MAX_BACKOFF_MS, BASE_BACKOFF_MS * (1L shl (attempt - 1)))
                _state.value = State.Waiting("Reconnecting to the PC.")
                delay(wait)
                transport.connect(pl)
                delay(HANDSHAKE_WINDOW_MS)
                if (_state.value is State.Playing) {
                    transport.send(ClientMessage.Audio("start_phone_speakers"))
                    return@launch
                }
            }
            _state.value = State.Failed(
                "Couldn't reach the PC. Check the network connection and that DeskFuse is running on Windows."
            )
        }
    }

    private suspend fun pumpControl() {
        transport.control.collect { message ->
            when (message) {
                is ServerMessage.BridgeState -> {
                    Log.i(TAG, "bridge state: requested=" + message.requestedCapabilities
                        + " active=" + message.activeCapabilities)
                    if (message.activeCapabilities.contains("speakers")) {
                        transport.send(ClientMessage.Audio("start_phone_speakers"))
                        _state.value = State.Waiting("The PC is sending sound to this phone.")
                    } else {
                        _state.value = State.Waiting(
                            "The PC has not created a phone speaker endpoint yet.",
                        )
                    }
                }

                is ServerMessage.AudioState -> handleAudioState(message)

                is ServerMessage.AudioVolume -> output.setVolume(message.level)

                is ServerMessage.Stage ->
                    if (message.stage == "speakers") _state.value = State.Failed(message.detail)

                else -> Unit
            }
        }
    }

    private fun handleAudioState(message: ServerMessage.AudioState) {
        when (message.state) {
            "started" -> {
                val speaker = output.builtInSpeaker()
                if (speaker == null) {
                    _state.value = State.Failed("This phone has no built-in speaker to play on.")
                    return
                }
                Log.i(TAG, "starting the phone audio output")
                val started = output.start(
                    id = message.streamId,
                    sampleRate = message.sampleRate,
                    channels = message.channels,
                    format = message.format,
                    layout = message.layout,
                    device = speaker,
                )
                if (!started) {
                    _state.value = State.Failed("The phone could not start the audio output.")
                    return
                }
                _state.value = State.Playing("Playing the PC's sound on this phone.")
                // Android resolves the route asynchronously: confirm after a
                // moment that the sound really leaves through the speaker.
                scope.launch {
                    kotlinx.coroutines.delay(600)
                    if (!output.isRoutedTo(speaker)) {
                        _state.value = State.Failed(
                            "The phone's sound is not leaving through its speaker.",
                        )
                    }
                }
            }

            "stopped" -> {
                output.stop()
                _state.value = State.Waiting("The PC stopped sending sound.")
            }

            "error" -> {
                output.stop()
                _state.value = State.Failed(message.detail ?: "The PC could not send sound to this phone.")
            }
        }
    }

    /**
     * The phone's own volume slider is the same control as the PC's. When
     * the user moves it, the PC is told, so the endpoint follows.
     */
    private suspend fun pumpVolume() {
        while (scope.isActive) {
            val level = output.systemVolumeChanged()
            if (level != null) {
                transport.send(ClientMessage.AudioVolume(level))
            }
            kotlinx.coroutines.delay(400)
        }
    }

    private suspend fun pumpAudio() {
        var reported = 0L
        val consumed = AudioConsumptionMeter()
        transport.audio.collect { chunk ->
            consumed.consumed(chunk.receiveSequence, chunk.receivedAtNanos, System.nanoTime())
            if (!output.write(chunk)) {
                _state.value = State.Failed("The phone lost the audio route.")
                return@collect
            }
            // Aggregate after each completed write. Never log per PCM packet.
            val now = System.currentTimeMillis()
            if (now - reported > 2000) {
                reported = now
                Log.i(TAG, "audio path: ${output.stats()} | " +
                    transport.audioArrivalHealth() + " | " + consumed.report())
            }
        }
    }

    private companion object {
        const val TAG = "DeskFuseSpeaker"
        const val MAX_RECONNECT = 6
        const val BASE_BACKOFF_MS = 400L
        const val MAX_BACKOFF_MS = 8_000L
        const val HANDSHAKE_WINDOW_MS = 2_500L
    }
}
