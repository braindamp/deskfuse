package com.focuswindow.client.session

import android.util.Log
import com.focuswindow.client.media.PcAudioOutput
import com.focuswindow.client.profile.PhoneProfile
import com.focuswindow.client.protocol.ClientMessage
import com.focuswindow.client.protocol.ServerMessage
import com.focuswindow.client.transport.AudioConsumptionMeter
import com.focuswindow.client.transport.LanTransport
import com.focuswindow.client.transport.PairingPayload
import kotlinx.coroutines.CancellationException
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
    private val nativeAudio: com.focuswindow.client.media.NativeAudioSession? = null,
    private val transport: LanTransport = LanTransport(scope),
) {
    sealed interface State {
        data object Idle : State
        data class Connecting(val host: String) : State
        data class Waiting(val detail: String) : State
        data class Playing(val endpoint: String) : State
        // Android has no default network (e.g. airplane mode). Silence is a
        // deliberate suspension, not a user-visible failure.
        data object Suspended : State
        data class Failed(val detail: String) : State
    }

    private val _state = MutableStateFlow<State>(State.Idle)
    val state: StateFlow<State> = _state.asStateFlow()

    private var pumpJob: Job? = null
    private var audioJob: Job? = null
    private var reconnectJob: Job? = null
    private var reconnectGraceJob: Job? = null
    // Keep the last "Playing" status briefly during a network handoff, but
    // never mistake that old status for successful recovery.
    @Volatile private var recovering = false
    @Volatile private var networkAvailable = true
    private var payload: PairingPayload? = null
    private var attempt: Int = 0
    private var profile: PhoneProfile? = null

    fun start(payload: PairingPayload, phoneProfile: PhoneProfile) {
        reconnectJob?.cancel()
        reconnectJob = null
        reconnectGraceJob?.cancel()
        reconnectGraceJob = null
        recovering = false
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
            if (networkAvailable) transport.connect(payload)
            else _state.value = State.Suspended
        }
    }

    fun stop() {
        // Cancellation does not interrupt a thread blocked on Socket.read().
        // Explicitly tear down the authenticated TCP connection before the
        // Service immediately cancels its scope.
        reconnectJob?.cancel()
        reconnectJob = null
        reconnectGraceJob?.cancel()
        reconnectGraceJob = null
        recovering = false
        pumpJob?.cancel()
        audioJob?.cancel()
        pumpJob = null
        audioJob = null
        transport.disconnectNow()
        nativeAudio?.stop()
        output.stop()
        payload = null
        _state.value = State.Idle
    }

    private suspend fun pumpEvents() {
        transport.events.collect { event ->
            when (event) {
                is LanTransport.TransportEvent.Connecting -> {
                    // Repeated attempts are not presented as new sessions.
                    if (!recovering && networkAvailable) _state.value = State.Connecting(event.host)
                }

                is LanTransport.TransportEvent.Ready -> {
                    // The accessory hello: no display is acquired, and the
                    // only capability requested is the phone's speakers.
                    val current = profile ?: return@collect
                    transport.send(
                        ClientMessage.BridgeHello(
                            clientVersion = clientVersion,
                            profile = current,
                            capabilities = listOf("speakers"),
                            nativeAudioVersion = if (nativeAudio?.available == true) 2 else 0,
                        )
                    )
                    if (!recovering) {
                        _state.value = State.Waiting("Waiting for the PC to create the phone speaker endpoint.")
                    }
                }

                is LanTransport.TransportEvent.Closed ->
                    endConnection(event.reason, event.recoverable)

                is LanTransport.TransportEvent.Failed ->
                    endConnection(event.detail, event.retryable)

                else -> Unit
            }
        }
    }

    private suspend fun endConnection(detail: String, retry: Boolean) {
        nativeAudio?.retire()
        output.stop()
        transport.disconnectNow()
        if (payload == null) return
        if (retry) scheduleReconnect()
        else {
            recovering = false
            reconnectJob?.cancel(); reconnectJob = null
            reconnectGraceJob?.cancel(); reconnectGraceJob = null
            _state.value = State.Failed(detail)
        }
    }

    /**
     * Called by the foreground service for both default-route replacement
     * and loss/restoration. Also called once before start(), to prevent any
     * TCP attempt while the phone is already in airplane mode.
     *
     * No network: close the old socket, pause indefinitely without polling,
     * leave Android's own airplane-mode indicator to explain the situation.
     * New network: reauthenticate with the saved phone credential silently.
     */
    fun onDefaultNetworkChanged(available: Boolean) {
        networkAvailable = available
        if (payload == null || _state.value is State.Idle) return
        Log.i(TAG, "default network " + if (available) "available: recovering" else "unavailable: suspended")
        reconnectJob?.cancel()
        reconnectJob = null
        reconnectGraceJob?.cancel()
        reconnectGraceJob = null
        attempt = 0
        transport.disconnectNow()
        nativeAudio?.stop()
        output.stop()
        recovering = false
        if (available) scheduleReconnect()
        else _state.value = State.Suspended
    }

    /** Network loss never consumes an attempt; reachable-network retries do not expire. */
    private fun scheduleReconnect() {
        if (!networkAvailable) {
            _state.value = State.Suspended
            return
        }
        if (reconnectJob?.isActive == true) return
        val pl = payload ?: return
        recovering = true
        reconnectGraceJob?.cancel()
        reconnectGraceJob = scope.launch {
            delay(RECONNECT_GRACE_MS)
            if (recovering && networkAvailable) _state.value = State.Waiting("Connecting to the PC…")
        }
        reconnectJob = scope.launch {
            while (recovering && networkAvailable) {
                attempt = (attempt + 1).coerceAtMost(RECONNECT_DELAYS_MS.size)
                val wait = RECONNECT_DELAYS_MS[attempt - 1]
                delay(wait)
                if (!recovering || !networkAvailable) return@launch
                transport.connect(pl)
                // Only a new authenticated AudioState(started) clears recovery.
                delay(HANDSHAKE_WINDOW_MS)
            }
        }
    }

    private suspend fun pumpControl() {
        transport.control.collect { message ->
            try { when (message) {
                is ServerMessage.NativeAudioChallenge -> {
                    val native = nativeAudio ?: error("Native receiver unavailable")
                    output.stop()
                    check(output.applyNativeVolume(0)) { "This output cannot enforce native volume/mute." }
                    transport.send(native.challenge(message))
                }
                is ServerMessage.NativeAudioVolume -> {
                    val native = nativeAudio ?: error("Native receiver unavailable")
                    if (native.acceptsVolume(message.grant)) {
                        val applied = output.applyNativeVolume(message.level)
                        transport.send(ClientMessage.NativeAudioVolumeApplied(message.grant, message.sequence, message.level, applied))
                        check(applied) { "Native volume/mute could not be applied." }
                    }
                }
                is ServerMessage.NativeAudioGrant -> {
                    val native = nativeAudio ?: error("Native receiver unavailable")
                    native.start(message, payload?.host ?: error("No authenticated host")) {
                        scope.launch {
                            // Check at final dispatch, after any controller/session replacement.
                            if (native.isCurrent()) endConnection("Native audio session ended.", retry = true)
                        }
                    }
                    recovering = false
                    _state.value = State.Waiting("Native audio authenticated; select DeskFuse in Windows.")
                }
                is ServerMessage.BridgeState -> {
                    Log.i(TAG, "bridge state: requested=" + message.requestedCapabilities
                        + " active=" + message.activeCapabilities)
                    if (message.activeCapabilities.contains("speakers")) {
                        transport.send(ClientMessage.Audio("start_phone_speakers"))
                        if (!recovering) _state.value = State.Waiting("The PC is sending sound to this phone.")
                    } else {
                        if (!recovering) _state.value = State.Waiting(
                            "The PC has not created a phone speaker endpoint yet.",
                        )
                    }
                }

                is ServerMessage.AudioState -> handleAudioState(message)

                is ServerMessage.AudioVolume -> output.setVolume(message.level)

                is ServerMessage.Stage ->
                    if (message.stage == "speakers") _state.value = State.Failed(message.detail)

                else -> Unit
            } } catch (cancel: CancellationException) { throw cancel }
            catch (error: Exception) { endConnection(error.message ?: "Native audio initialization failed.", retry = true) }
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
                recovering = false
                reconnectGraceJob?.cancel()
                reconnectGraceJob = null
                attempt = 0
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
                nativeAudio?.stop()
        output.stop()
                _state.value = State.Waiting("The PC stopped sending sound.")
            }

            "error" -> {
                nativeAudio?.stop()
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
        // UX-only grace; retries start immediately. Do not hide a truly
        // interrupted audio route or pretend previously queued sound is live.
        const val RECONNECT_GRACE_MS = 4_000L
        val RECONNECT_DELAYS_MS = longArrayOf(250, 1_000, 2_000, 4_000, 8_000, 15_000, 15_000, 15_000)
         const val HANDSHAKE_WINDOW_MS = 2_500L
    }
}
