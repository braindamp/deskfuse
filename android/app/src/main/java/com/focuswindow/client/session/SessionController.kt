package com.focuswindow.client.session

import android.view.Surface
import android.util.Base64
import java.io.InputStream
import java.io.IOException
import java.io.OutputStream
import java.security.MessageDigest
import java.util.UUID
import java.util.concurrent.atomic.AtomicBoolean
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.CancellationException
import com.focuswindow.client.media.VideoDecoder
import com.focuswindow.client.media.PcAudioOutput
import com.focuswindow.client.media.PhoneMicrophoneCapture
import com.focuswindow.client.profile.PhoneProfile
import com.focuswindow.client.protocol.ClientMessage
import com.focuswindow.client.protocol.DensityPreset
import com.focuswindow.client.protocol.InputEvent
import com.focuswindow.client.protocol.ServerMessage
import com.focuswindow.client.protocol.TargetRecord
import com.focuswindow.client.protocol.WindowEntry
import com.focuswindow.client.protocol.RemoteFileEntry
import com.focuswindow.client.render.PhoneSurfaceView
import com.focuswindow.client.transport.LanTransport
import com.focuswindow.client.transport.PairingPayload
import com.focuswindow.client.transport.PairingRepository
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Job
import kotlinx.coroutines.delay
import kotlinx.coroutines.flow.MutableSharedFlow
import kotlinx.coroutines.flow.asSharedFlow
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.flow.update
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext
import kotlin.math.min

/**
 * Orchestrates one session. Owns the state machine; the UI is a pure function
 * of [state] and [advisories].
 *
 * Design rule carried from the plan: this class never *infers* that something
 * is fine. Either the host told us, or we measured it, or we report that we
 * do not know.
 */
class SessionController(
    private val scope: CoroutineScope,
    private val clientVersion: String,
    private val audioPreferences: PairingRepository? = null,
) {

    private val _state = MutableStateFlow<SessionState>(SessionState.Idle)
    val state: StateFlow<SessionState> = _state.asStateFlow()

    private val _advisories = MutableStateFlow<List<SessionAdvisory>>(emptyList())
    val advisories: StateFlow<List<SessionAdvisory>> = _advisories.asStateFlow()

    // The foreground Activity decides whether to accept a requested PC clipboard result.
    // No background auto-sync or clipboard surveillance.
    private val _clipboardFromPc = MutableSharedFlow<String>(extraBufferCapacity = 1)
    val clipboardFromPc = _clipboardFromPc.asSharedFlow()
    private var pendingClipboardRead = false
    private val _fileStatus = MutableSharedFlow<String>(extraBufferCapacity = 4)
    val fileStatus = _fileStatus.asSharedFlow()
    private var uploadJob: Job? = null
    private val _files = MutableStateFlow(RemoteFilesState())
    val files: StateFlow<RemoteFilesState> = _files.asStateFlow()
    @Volatile private var incomingDownload: IncomingDownload? = null
    private val downloadBusy = AtomicBoolean(false)
    private val _windows = MutableStateFlow<List<WindowEntry>>(emptyList())
    val windows: StateFlow<List<WindowEntry>> = _windows.asStateFlow()
    private val _pcAudioStatus = MutableStateFlow("Off")
    val pcAudioStatus: StateFlow<String> = _pcAudioStatus.asStateFlow()
    // Features requested by a companion device are distinct from OS-provisioned
    // endpoints. All activeCapabilities stay empty until real providers exist.
    private val _bridgeState = MutableStateFlow<ServerMessage.BridgeState?>(null)
    val bridgeState: StateFlow<ServerMessage.BridgeState?> = _bridgeState.asStateFlow()
    private var pcAudioRequested = false
    private var activePcAudioStreamId = -1
    private var pcAudioDesired = false
    private var pcAudioFailedOnConnection = false
    private var pcAudioForceStereo = false
    private var hostPcAudioAvailable = false
    private var pcAudioOutput: PcAudioOutput? = null

    // Independent Android -> Windows input track. A saved user preference is
    // never a grant to bypass Android mic permission or host endpoint approval.
    private val _phoneMicStatus = MutableStateFlow("Off")
    val phoneMicStatus: StateFlow<String> = _phoneMicStatus.asStateFlow()
    private var phoneMicCapture: PhoneMicrophoneCapture? = null
    private var phoneMicDesired = false
    private var hostMicAvailable = false
    private var phoneMicForeground = false
    private var phoneMicRequested = false
    private var phoneMicFailed = false
    private var phoneMicStreamId = 0
    private var phoneMicJob: Job? = null
    private val transport = LanTransport(scope)
    private lateinit var decoder: VideoDecoder

    private var surfaceView: PhoneSurfaceView? = null
    private var surface: Surface? = null

    private var profile: PhoneProfile? = null
    private var payload: PairingPayload? = null
    private var record: TargetRecord? = null
    private var stats: VideoDecoder.DecoderStats? = null

    private var pumpJob: Job? = null
    private var reconnectJob: Job? = null
    private var preparingWatchdog: Job? = null
    private var staleJob: Job? = null
    private var attempt = 0
    private var lastFrameAtMs = 0L
    private var rttMs = 0.0

    /** FR-DPI-07 — user preference, persisted by the caller. */
    var densityPreset: DensityPreset = DensityPreset.LOGICAL
        private set

    fun attachPcAudioOutput(output: PcAudioOutput) {
        pcAudioOutput = output
    }

    fun attachPhoneMicrophone(capture: PhoneMicrophoneCapture) {
        phoneMicCapture = capture
    }

    /** Foreground-only permission boundary; no implicit background recording. */
    fun setPhoneMicrophoneForeground(foreground: Boolean) {
        phoneMicForeground = foreground
        if (!foreground) {
            val wasRequested = phoneMicRequested
            stopPhoneMicrophoneLocal()
            if (wasRequested) scope.launch { transport.send(ClientMessage.Microphone("stop")) }
        } else requestPreferredPhoneMicrophone()
    }

    /** Only called by Activity after runtime Android permission is granted. */
    fun togglePhoneMicrophone() {
        if (_state.value !is SessionState.Streaming) return
        // Even if the Windows endpoint has since vanished, users can revoke
        // their remembered on-state without waiting for that endpoint to return.
        if (!hostMicAvailable && phoneMicDesired) {
            phoneMicDesired = false
            payload?.let { audioPreferences?.setAudioWanted(it, "phone_mic", false) }
            stopPhoneMicrophoneLocal()
            return
        }
        if (!hostMicAvailable) {
            _phoneMicStatus.value = "Unavailable"
            return
        }
        if (!phoneMicDesired && phoneMicCapture?.permitted() != true) {
            _phoneMicStatus.value = "Permission needed"
            return
        }
        phoneMicDesired = !phoneMicDesired
        payload?.let { audioPreferences?.setAudioWanted(it, "phone_mic", phoneMicDesired) }
        if (!phoneMicDesired) {
            val wasRequested = phoneMicRequested
            stopPhoneMicrophoneLocal()
            if (wasRequested) scope.launch { transport.send(ClientMessage.Microphone("stop")) }
        } else {
            phoneMicFailed = false
            requestPreferredPhoneMicrophone()
        }
    }

    private fun requestPreferredPhoneMicrophone() {
        if (!phoneMicDesired || !phoneMicForeground || !hostMicAvailable ||
            phoneMicRequested || phoneMicFailed || _state.value !is SessionState.Streaming) return
        if (phoneMicCapture?.permitted() != true) {
            _phoneMicStatus.value = "Permission needed"
            return
        }
        phoneMicRequested = true
        _phoneMicStatus.value = "Starting"
        scope.launch { transport.send(ClientMessage.Microphone("start")) }
    }

    private fun stopPhoneMicrophoneLocal() {
        phoneMicRequested = false
        phoneMicStreamId = 0
        phoneMicCapture?.stop()
        phoneMicJob?.cancel()
        phoneMicJob = null
        _phoneMicStatus.value = "Off"
    }

    private fun onMicrophoneState(msg: ServerMessage.MicrophoneState) {
        when (msg.state) {
            "started" -> {
                if (!phoneMicRequested || !phoneMicDesired || !phoneMicForeground ||
                    phoneMicCapture?.permitted() != true ||
                    msg.streamId <= 0 || msg.sampleRate != 16000 ||
                    msg.channels != 1 || msg.format != "pcm_s16le") {
                    stopPhoneMicrophoneLocal()
                    scope.launch { transport.send(ClientMessage.Microphone("stop")) }
                    return
                }
                phoneMicStreamId = msg.streamId
                val capture = phoneMicCapture ?: return
                _phoneMicStatus.value = "On"
                phoneMicJob?.cancel()
                phoneMicJob = scope.launch {
                    try {
                        capture.capture { pcm ->
                            if (!phoneMicForeground || !phoneMicRequested ||
                                phoneMicStreamId != msg.streamId ||
                                !transport.sendPhoneMicrophone(msg.streamId, pcm)) {
                                throw IOException("Phone microphone transport ended")
                            }
                        }
                        if (phoneMicStreamId == msg.streamId) {
                            phoneMicFailed = true
                            stopPhoneMicrophoneLocal()
                            _phoneMicStatus.value = "Unavailable"
                            scope.launch { transport.send(ClientMessage.Microphone("stop")) }
                        }
                    } catch (cancelled: CancellationException) {
                        throw cancelled
                    } catch (ex: Exception) {
                        if (phoneMicStreamId == msg.streamId) {
                            phoneMicFailed = true
                            stopPhoneMicrophoneLocal()
                            _phoneMicStatus.value = "Unavailable"
                            scope.launch { transport.send(ClientMessage.Microphone("stop")) }
                        }
                    }
                }
            }
            "stopped" -> {
                if (!phoneMicDesired) stopPhoneMicrophoneLocal()
                else if (msg.streamId > 0 && phoneMicStreamId == msg.streamId) {
                    phoneMicFailed = true
                    stopPhoneMicrophoneLocal()
                    _phoneMicStatus.value = "Unavailable"
                }
            }
            "unavailable", "error" -> {
                phoneMicFailed = true
                stopPhoneMicrophoneLocal()
                _phoneMicStatus.value = "Unavailable"
            }
        }
    }

    /** Last user-selected PC playback route, scoped to this paired Windows host.
     *  This mirrors PC output to Android; it is NOT a selectable Windows speaker.
     */
    fun togglePcAudio() {
        if (_state.value !is SessionState.Streaming) return
        if (!hostPcAudioAvailable) {
            _pcAudioStatus.value = "Unavailable"
            return
        }
        val enabled = !pcAudioDesired
        pcAudioDesired = enabled
        payload?.let { audioPreferences?.setAudioWanted(it, "pc_sound", enabled) }
        if (!enabled) {
            val wasRequested = pcAudioRequested
            stopPcAudioLocal()
            if (wasRequested) scope.launch { transport.send(ClientMessage.Audio("stop_pc")) }
        } else {
            // An explicit user tap can retry after a previous audio error.
            pcAudioFailedOnConnection = false
            pcAudioForceStereo = false
            requestPreferredPcAudio()
        }
    }

    /** Called only after streaming is ready, not on unapproved Hello.
     *  A transport reconnection resets pcAudioRequested, but not the saved preference.
     */
    private fun requestPreferredPcAudio() {
        if (!pcAudioDesired || !hostPcAudioAvailable || pcAudioRequested ||
            pcAudioFailedOnConnection || _state.value !is SessionState.Streaming) return
        pcAudioRequested = true
        _pcAudioStatus.value = "Starting"
        val offers = if (pcAudioForceStereo) listOf("stereo")
            else pcAudioOutput?.offeredLayouts() ?: listOf("stereo")
        scope.launch { transport.send(ClientMessage.Audio("start_pc", offers)) }
    }

    /** If the multichannel peripheral vanishes, start a newly negotiated
     * stereo route. Never rely on Android silently downmixing 5.1.
     */
    private fun fallbackPcAudioToStereo() {
        if (pcAudioForceStereo || !pcAudioDesired) {
            pcAudioFailedOnConnection = true
            stopPcAudioLocal()
            _pcAudioStatus.value = "Unavailable"
            scope.launch { transport.send(ClientMessage.Audio("stop_pc")) }
            return
        }
        pcAudioForceStereo = true
        stopPcAudioLocal()
        _pcAudioStatus.value = "Starting"
        scope.launch {
            transport.send(ClientMessage.Audio("stop_pc"))
            if (pcAudioDesired && hostPcAudioAvailable &&
                _state.value is SessionState.Streaming) {
                pcAudioRequested = true
                transport.send(ClientMessage.Audio("start_pc", listOf("stereo")))
            }
        }
    }

    private fun stopPcAudioLocal() {
        pcAudioRequested = false
        activePcAudioStreamId = -1
        pcAudioOutput?.stop()
        _pcAudioStatus.value = "Off"
    }

    fun attachSurface(view: PhoneSurfaceView) {
        surfaceView = view
        view.awaitSurface(
            onReady = { s ->
                surface = s
                // If the target is already known we can start decoding at once.
                record?.let { startDecoder(it) }
            },
            onLost = {
                surface = null
                decoderOrNull()?.stop()
            },
        )
    }

    fun start(payload: PairingPayload, profile: PhoneProfile) {
        android.util.Log.i(
            "DeskFuseSession",
            "start: host=${payload.host}:${payload.port} profile=${profile.deviceLabel} " +
                "dpr=${profile.dpr} transportRunning=${transport.isRunning}",
        )
        hostPcAudioAvailable = false
        pcAudioFailedOnConnection = false
        _bridgeState.value = null
        stopPcAudioLocal()
        pcAudioDesired = audioPreferences?.audioWanted(payload, "pc_sound") ?: false
        pcAudioForceStereo = false
        hostMicAvailable = false
        phoneMicFailed = false
        stopPhoneMicrophoneLocal()
        phoneMicDesired = audioPreferences?.audioWanted(payload, "phone_mic") ?: false
        this.payload = payload
        this.profile = profile
        attempt = 0

        val problems = profile.validate()
        if (problems.isNotEmpty()) {
            android.util.Log.i("DeskFuseSession", "profile problems: " + problems.joinToString("; "))
            _state.value = SessionState.Failed(
                stage = "profile",
                detail = problems.joinToString("; "),
                remediation = "This device reports an unusual display configuration. " +
                    "Please send a diagnostics report.",
                recoverable = false,
            )
            return
        }

        decoder = VideoDecoder(
            onStats = { s ->
                stats = s
                refreshStreamingState()
            },
            onFatal = { stage, detail, remediation ->
                _state.value = SessionState.Failed(stage, detail, remediation, recoverable = true)
            },
            requestKeyframe = {
                scope.launch { transport.send(ClientMessage.RequestKeyframe) }
            },
        )

        if (payload.viaRelay) {
            addAdvisory(SessionAdvisory.relayActive(payload.hostName))
        }

        pumpJob?.cancel()
        pumpJob = scope.launch {
            launch { pumpControl() }
            launch { pumpFrames() }
            launch { pumpAudio() }
            launch { pumpEvents() }
            transport.connect(payload)
        }
    }

    private suspend fun pumpEvents() {
        transport.events.collect { event ->
            when (event) {
                is LanTransport.TransportEvent.Connecting ->
                    _state.value = SessionState.Connecting(event.host, event.port)

                is LanTransport.TransportEvent.Verified ->
                    _state.value = SessionState.Verifying(event.code, event.hostName)

                is LanTransport.TransportEvent.Ready -> {
                    _state.value = SessionState.PreparingTarget(
                        stage = "Preparing your display",
                        detail = "Setting up the phone-sized workspace.",
                        progress = 0.35f,
                    )
                    profile?.let {
                        transport.send(ClientMessage.Hello(clientVersion = clientVersion, profile = it))
                        transport.send(ClientMessage.Density(densityPreset))
                    }
                }

                is LanTransport.TransportEvent.Failed -> {
                    if (event.terminal) {
                        reconnectJob?.cancel()
                        staleJob?.cancel()
                        record = null // late decoder stats must not revive a failed session
                        decoderOrNull()?.stop()
                    }
                    hostMicAvailable = false
                    phoneMicFailed = false
                    stopPhoneMicrophoneLocal()
                    hostPcAudioAvailable = false
                    pcAudioFailedOnConnection = false
                    stopPcAudioLocal()
                    uploadJob?.cancel()
                    abortDownload("Connection lost; incomplete download discarded")
                    _state.value = SessionState.Failed(
                        stage = event.stage,
                        detail = event.detail,
                        remediation = event.remediation,
                        recoverable = true,
                    )
                }

                is LanTransport.TransportEvent.Closed -> {
                    hostMicAvailable = false
                    phoneMicFailed = false
                    stopPhoneMicrophoneLocal()
                    hostPcAudioAvailable = false
                    pcAudioFailedOnConnection = false
                    stopPcAudioLocal()
                    uploadJob?.cancel()
                    abortDownload("Connection closed; incomplete download discarded")
                    if (event.recoverable) scheduleReconnect(event.reason)
                    else _state.value = SessionState.Ended(event.reason)
                }
            }
        }
    }

    private suspend fun pumpControl() {
        transport.control.collect { msg ->
            when (msg) {
                is ServerMessage.Hello -> {
                    hostPcAudioAvailable = msg.pcAudioAvailable
                    hostMicAvailable = msg.phoneMicrophoneBridgeAvailable
                    phoneMicFailed = false
                    if (!hostMicAvailable) _phoneMicStatus.value = "Unavailable"
                    pcAudioFailedOnConnection = false
                    pcAudioForceStereo = false
                    _pcAudioStatus.value = if (msg.pcAudioAvailable) "Off" else "Unavailable"
                    requestPreferredPcAudio()
                    requestPreferredPhoneMicrophone()
                }

                is ServerMessage.MicrophoneState -> onMicrophoneState(msg)

                is ServerMessage.Stage -> {
                    if (msg.stage == "file" || msg.stage == "files") _fileStatus.emit(msg.detail)
                    if (msg.fatal) {
                        _state.value = SessionState.Failed(
                            msg.stage, msg.detail, msg.remediation, recoverable = true,
                            technical = msg.technical,
                        )
                    } else if (_state.value !is SessionState.Streaming) {
                        _state.value = SessionState.PreparingTarget(
                            stage = msg.stage,
                            detail = msg.detail,
                            progress = 0.6f,
                        )
                    }
                }

                is ServerMessage.Target -> onTarget(msg.record)

                is ServerMessage.Quality -> {
                    record = record?.copy(
                        codec = msg.codec,
                        pixelFormat = msg.pixelFormat,
                        refreshHz = msg.refreshHz,
                    )
                    addAdvisory(SessionAdvisory.qualityDrop(msg.pixelFormat, msg.reason))
                    refreshStreamingState()
                }

                is ServerMessage.Conflict ->
                    _state.value = SessionState.ConflictDetected(
                        msg.summary, msg.sourceLabel, msg.cloneLabel,
                    )

                is ServerMessage.Invariant -> {
                    if (!msg.clean) addAdvisory(SessionAdvisory.invariantDrift(msg.drift))
                    refreshStreamingState()
                }

                is ServerMessage.FileList ->
                    _files.value = RemoteFilesState(msg.path, msg.parent, msg.entries, msg.truncated, msg.offset, msg.nextOffset)

                is ServerMessage.FileData -> receiveFileData(msg)

                is ServerMessage.WindowList -> _windows.value = msg.windows

                is ServerMessage.BridgeState -> _bridgeState.value = msg

                is ServerMessage.AudioVolume -> Unit

                is ServerMessage.AudioClock -> Unit

                is ServerMessage.AudioState -> {
                    when (msg.state) {
                        "started" -> {
                            if (!pcAudioDesired || !pcAudioRequested) {
                                // User switched Off while host was opening the
                                // previous request. Reject the late success.
                                stopPcAudioLocal()
                                transport.send(ClientMessage.Audio("stop_pc"))
                            } else if (pcAudioOutput?.start(
                                    msg.streamId, msg.sampleRate, msg.channels, msg.format,
                                    msg.layout,
                                ) == true) {
                                activePcAudioStreamId = msg.streamId
                                _pcAudioStatus.value = when (msg.layout) {
                                    "5.1_back", "5.1_side" -> "On · 5.1"
                                    "7.1_surround" -> "On · 7.1"
                                    else -> "On"
                                }
                            } else {
                                fallbackPcAudioToStereo()
                            }
                        }
                        "stopped" -> {
                            // A late stop acknowledgement must not override a
                            // newer stream after a rapid off/on toggle.
                            if (!pcAudioDesired) {
                                stopPcAudioLocal()
                            } else if (msg.streamId == activePcAudioStreamId) {
                                // Host unexpectedly stopped the active stream.
                                // Retain user preference, but avoid a tight retry
                                // loop on an unavailable output endpoint.
                                pcAudioFailedOnConnection = true
                                stopPcAudioLocal()
                                _pcAudioStatus.value = "Unavailable"
                            }
                        }
                        "error" -> {
                            pcAudioFailedOnConnection = true
                            stopPcAudioLocal()
                            _pcAudioStatus.value = "Unavailable"
                        }
                    }
                }

                is ServerMessage.Clipboard -> {
                    if (pendingClipboardRead && msg.mime == "text/plain" && msg.text != null &&
                        msg.text.toByteArray(Charsets.UTF_8).size <= 64 * 1024) {
                        pendingClipboardRead = false
                        _clipboardFromPc.emit(msg.text)
                    }
                }
            }
        }
    }

    /**
     * FR-DPI-04 / FR-TGT-01 — the moment the contract is checked. We refuse to
     * present a target as native unless the host proved it, and we always show
     * the tier.
     */
    private fun onTarget(incoming: TargetRecord) {
        val p = profile ?: return
        record = incoming
        attempt = 0

        clearAdvisory("density")
        clearAdvisory("tokens")
        clearAdvisory("tier")

        if (!incoming.native) {
            addAdvisory(
                SessionAdvisory.notNative(incoming.densityMethod, incoming.degradedReason,
                    incoming.isOneToOne(p)),
            )
        }
        if (incoming.occupant != TargetRecord.Occupant.CLONE) {
            addAdvisory(
                SessionAdvisory.fallbackTier(
                    incoming.tierLabel(p),
                    when (incoming.occupant) {
                        TargetRecord.Occupant.CANVAS ->
                            "This app cannot open a second view, so you are seeing the whole desktop."
                        TargetRecord.Occupant.WINDOW ->
                            "Window capture: the layout is the PC's, not a phone layout."
                        else -> "Running in a remote session."
                    },
                ),
            )
        }

        val tokenDp = incoming.predictedTokenDp(p)
        if (incoming.native && tokenDp < PhoneProfile.READABLE_TOKEN_DP) {
            addAdvisory(SessionAdvisory.smallTokens(tokenDp))
        }

        startDecoder(incoming)
        refreshStreamingState()
    }

    private fun startDecoder(target: TargetRecord) {
        val view = surfaceView ?: return
        val s = surface ?: return
        val p = profile ?: return
        val dec = decoderOrNull() ?: return

        // Configure the surface BEFORE the decoder so the buffer is already
        // the right size when the first frame lands (NFR-PIX-01).
        view.configureFor(target, p)
        dec.stop()
        dec.start(
            surface = s,
            mimeType = mimeFor(target.codec),
            widthPx = target.widthPx,
            heightPx = target.heightPx,
            targetVersion = target.version,
            colorRange = target.colorRange,
            framesPerSecond = target.refreshHz,
        )
    }

    private suspend fun pumpFrames() {
        transport.frames.collect { frame ->
            lastFrameAtMs = System.currentTimeMillis()
            if (_state.value is SessionState.Stale) refreshStreamingState()
            decoderOrNull()?.submit(frame)
        }
    }

    /**
     * FR-REF-02 — the host sends nothing while the target is static, so an
     * absence of frames is NORMAL. Only call it stale when the host has also
     * stopped answering, which the transport's ping/pong detects.
     */
    fun noteHostIdle(reason: String) {
        staleJob?.cancel()
        staleJob = scope.launch {
            delay(STALE_GRACE_MS)
            if (_state.value is SessionState.Streaming) {
                _state.value = SessionState.Stale(reason, System.currentTimeMillis())
            }
        }
    }

    private fun refreshStreamingState() {
        val p = profile ?: return
        val r = record ?: return
        val pres = surfaceView?.presentation ?: return
        val drift = _advisories.value.firstOrNull { it.id == "invariant" }

        _state.value = SessionState.Streaming(
            record = r,
            profile = p,
            presentation = pres,
            stats = stats,
            linkRttMs = rttMs,
            invariantClean = drift == null,
            invariantDrift = emptyList(),
        )
        requestPreferredPcAudio()
        requestPreferredPhoneMicrophone()
    }

    /**
     * The host confirms the picture or it does not. A screen that waits
     * forever for a confirmation that will never come is a desync between
     * the phone and the PC, and the person sees a stuck screen.
     */
    private fun armPreparingWatchdog() {
        preparingWatchdog?.cancel()
        preparingWatchdog = scope.launch {
            delay(PREPARING_TIMEOUT_MS)
            if (_state.value is SessionState.PreparingTarget) {
                _state.value = SessionState.Failed(
                    stage = "preparing",
                    detail = "Your PC could not open a window for this phone.",
                    remediation = "Try again. If it keeps happening, ask the person at the PC to check DeskFuse.",
                    recoverable = true,
                    technical = "The host did not confirm a target within ${PREPARING_TIMEOUT_MS}ms.",
                )
            }
        }
    }

    private fun scheduleReconnect(reason: String) {
        if (reconnectJob?.isActive == true) return
        val pl = payload ?: return
        val pf = profile ?: return

        reconnectJob = scope.launch {
            while (attempt < MAX_RECONNECT) {
                attempt++
                // Exponential backoff, capped. Keeps a sleeping phone from
                // hammering the host when it wakes on a dead network.
                val wait = min(MAX_BACKOFF_MS, BASE_BACKOFF_MS * (1L shl (attempt - 1)))
                _state.value = SessionState.Reconnecting(attempt, MAX_RECONNECT, reason, wait)
                delay(wait)
                transport.connect(pl)
                // Give the handshake a window before trying again.
                delay(HANDSHAKE_WINDOW_MS)
                if (_state.value is SessionState.Streaming) {
                    // FR-ENC-05 — resume needs a fresh keyframe.
                    transport.send(ClientMessage.RequestKeyframe)
                    transport.send(ClientMessage.Profile(pf))
                    return@launch
                }
            }
            _state.value = SessionState.Failed(
                stage = "reconnect",
                detail = "Lost the connection to ${pl.hostName} and could not get it back.",
                remediation = "Check the PC is awake and on the same network, then reconnect.",
                recoverable = true,
            )
        }
    }

    private suspend fun pumpAudio() {
        transport.audio.collect { chunk ->
            if (_pcAudioStatus.value.startsWith("On") &&
                chunk.streamId == activePcAudioStreamId) {
                val received = withContext(Dispatchers.IO) {
                    pcAudioOutput?.write(chunk) ?: false
                }
                if (!received) fallbackPcAudioToStereo()
            }
        }
    }

    /** FR-DPI-09 — rotation/fold re-derives the TARGET, never the source window. */
    fun onProfileChanged(next: PhoneProfile) {
        val previous = profile
        profile = next
        if (previous?.geometryKey() == next.geometryKey()) return
        scope.launch { transport.send(ClientMessage.Profile(next)) }
    }

    fun setDensityPreset(preset: DensityPreset) {
        densityPreset = preset
        scope.launch { transport.send(ClientMessage.Density(preset)) }
    }

    /** FR-REF-01 — refresh is a user choice, bounded by the panel. */
    fun setRefresh(hz: Int) {
        val capped = min(hz, profile?.maxRefreshHz ?: hz)
        scope.launch { transport.send(ClientMessage.Refresh(capped)) }
    }

    /** FR-TGT-01 — let the user ask for the better tier explicitly. */
    fun requestOccupant(want: TargetRecord.Occupant) {
        scope.launch { transport.send(ClientMessage.Occupant(want)) }
    }

    fun sendInput(event: InputEvent) {
        scope.launch { transport.send(ClientMessage.Input(event)) }
    }

    fun requestWindows() {
        if (_state.value !is SessionState.Streaming) return
        scope.launch { transport.send(ClientMessage.Windows("list")) }
    }

    fun activateWindow(id: String) {
        if (_state.value !is SessionState.Streaming) return
        scope.launch { transport.send(ClientMessage.Windows("focus", id)) }
    }

    /** Browse using the approved Windows user's account, including mapped drives and UNC folders. */
    fun browsePc(path: String? = null, offset: Int = 0) {
        if (_state.value !is SessionState.Streaming) return
        scope.launch { transport.send(ClientMessage.Files("list", path = path, offset = offset)) }
    }

    /** Android SAF output URI was explicitly chosen before asking Windows for any bytes. */
    fun downloadFile(path: String, open: () -> OutputStream?, deleteIncomplete: () -> Unit) {
        if (_state.value !is SessionState.Streaming || !downloadBusy.compareAndSet(false, true)) return
        scope.launch(Dispatchers.IO) {
            val id = UUID.randomUUID().toString().replace("-", "")
            try {
                val output = open() ?: error("Cannot open selected Android document")
                incomingDownload = IncomingDownload(id, output, deleteIncomplete)
                transport.send(ClientMessage.Files("get", path, id))
            } catch (e: Exception) {
                abortDownload("Download could not start: ${e.message ?: "document not writable"}")
                downloadBusy.set(false)
                runCatching { deleteIncomplete() }
            }
        }
    }

    fun cancelDownload() {
        val transfer = incomingDownload ?: return
        scope.launch { transport.send(ClientMessage.Files("cancel", id = transfer.id)) }
        abortDownload("Download cancelled; incomplete document discarded")
    }

    private suspend fun receiveFileData(message: ServerMessage.FileData) {
        val transfer = incomingDownload ?: return
        if (message.id != transfer.id) return
        try {
            val status = kotlinx.coroutines.withContext(Dispatchers.IO) { transfer.apply(message) }
            if (status != null) {
                incomingDownload = null
                downloadBusy.set(false)
                _fileStatus.emit(status)
            }
        } catch (e: Exception) {
            abortDownload("Download failed: ${e.message ?: "file mismatch"}")
        }
    }

    private fun abortDownload(status: String) {
        val transfer = incomingDownload
        incomingDownload = null
        downloadBusy.set(false)
        if (transfer == null) return
        transfer.abort()
        scope.launch { _fileStatus.emit(status) }
    }

    fun sendClipboard(text: String) {
        if (_state.value !is SessionState.Streaming) return
        if (text.toByteArray(Charsets.UTF_8).size > 64 * 1024) return
        scope.launch { transport.send(ClientMessage.Clipboard("text/plain", text = text)) }
    }

    fun requestHostClipboard() {
        if (_state.value !is SessionState.Streaming) return
        pendingClipboardRead = true
        scope.launch { transport.send(ClientMessage.Clipboard("text/plain")) }
    }

    /** Android SAF stream; no raw path or storage permission is sent to Windows. */
    fun uploadFile(name: String, size: Long, open: () -> InputStream?,
                   destination: String? = null, openOnPc: Boolean = false) {
        if (_state.value !is SessionState.Streaming || uploadJob?.isActive == true) return
        uploadJob = scope.launch(Dispatchers.IO) {
            val id = UUID.randomUUID().toString().replace("-", "")
            var begun = false
            try {
                require(size in -1L..MAX_FILE_BYTES) { "File exceeds 32 MiB" }
                val safeName = name.trim()
                require(safeName.isNotEmpty()) { "File has no name" }
                val source = open() ?: error("Cannot open selected document")
                source.use { input ->
                    transport.send(ClientMessage.File("begin", id, name = safeName, size = size,
                        path = if (openOnPc) null else destination, openOnPc = openOnPc))
                    begun = true
                    val digest = MessageDigest.getInstance("SHA-256")
                    val buffer = ByteArray(FILE_CHUNK_BYTES)
                    var total = 0L
                    while (true) {
                        val n = input.read(buffer)
                        if (n < 0) break
                        if (n == 0) continue
                        total += n
                        require(total <= MAX_FILE_BYTES) { "File exceeds 32 MiB" }
                        digest.update(buffer, 0, n)
                        val encoded = Base64.encodeToString(buffer, 0, n, Base64.NO_WRAP)
                        transport.send(ClientMessage.File("chunk", id, base64 = encoded))
                    }
                    require(size < 0 || total == size) { "The selected document changed during upload" }
                    val sha = digest.digest().joinToString("") { "%02x".format(it.toInt() and 0xff) }
                    transport.send(ClientMessage.File("end", id, sha256 = sha))
                    begun = false
                }
            } catch (cancel: CancellationException) {
                if (begun) transport.send(ClientMessage.File("cancel", id))
                throw cancel
            } catch (e: Exception) {
                if (begun) transport.send(ClientMessage.File("cancel", id))
                _fileStatus.emit("Upload stopped: " + (e.message ?: "read error"))
            }
        }
    }

    fun stop(reason: String = "user ended the session") {
        stopPhoneMicrophoneLocal()
        hostMicAvailable = false
        _bridgeState.value = null
        stopPcAudioLocal()
        pendingClipboardRead = false
        uploadJob?.cancel()
        abortDownload("Disconnected; incomplete download discarded")
        reconnectJob?.cancel()
        staleJob?.cancel()
        pumpJob?.cancel()
        decoderOrNull()?.stop()
        scope.launch { transport.close(reason) }
        _state.value = SessionState.Ended(reason)
        _advisories.value = emptyList()
    }

    private fun decoderOrNull(): VideoDecoder? = if (::decoder.isInitialized) decoder else null

    private fun addAdvisory(advisory: SessionAdvisory) {
        _advisories.update { current ->
            current.filterNot { it.id == advisory.id } + advisory
        }
    }

    private fun clearAdvisory(id: String) {
        _advisories.update { current -> current.filterNot { it.id == id } }
    }

    private fun mimeFor(codec: String): String = when (codec.lowercase()) {
        "av1" -> VideoDecoder.MIME_AV1
        "hevc", "h265" -> VideoDecoder.MIME_HEVC
        else -> VideoDecoder.MIME_H264
    }

    private companion object {
        /** How long the phone waits for the host to confirm a picture. */
        const val PREPARING_TIMEOUT_MS = 30_000L
        const val MAX_FILE_BYTES = 32L * 1024 * 1024
        const val FILE_CHUNK_BYTES = 24 * 1024
        const val MAX_RECONNECT = 6
        const val BASE_BACKOFF_MS = 400L
        const val MAX_BACKOFF_MS = 8_000L
        const val HANDSHAKE_WINDOW_MS = 2_500L
        const val STALE_GRACE_MS = 2_000L
    }
}


/** Windows filesystem navigation state; paths are Windows host paths, never Android local paths. */
data class RemoteFilesState(
    val path: String = "", val parent: String? = null,
    val entries: List<RemoteFileEntry> = emptyList(), val truncated: Boolean = false,
    val offset: Int = 0, val nextOffset: Int = 0,
)

/** One PC -> Android download. Only a user-chosen SAF output stream is ever written. */
private class IncomingDownload(
    val id: String,
    private val output: OutputStream,
    private val deleteIncomplete: () -> Unit,
) {
    private val digest = MessageDigest.getInstance("SHA-256")
    private var started = false
    private var expected = -1L
    private var written = 0L
    private var closed = false

    @Synchronized fun apply(message: ServerMessage.FileData): String? {
        check(!closed) { "Download already closed" }
        when (message.action) {
            "begin" -> {
                check(!started) { "Duplicate download start" }
                val size = message.size ?: error("Missing download size")
                require(size >= 0L) { "Invalid download size" }
                expected = size
                started = true
            }
            "chunk" -> {
                check(started) { "Download chunk before start" }
                val encoded = message.base64 ?: error("Missing chunk")
                require(encoded.length <= 32768) { "Chunk too large" }
                val bytes = Base64.decode(encoded, Base64.NO_WRAP)
                require(bytes.isNotEmpty() && bytes.size <= 24 * 1024) { "Bad chunk size" }
                require(written <= expected - bytes.size) { "Download exceeded expected length" }
                output.write(bytes)
                digest.update(bytes)
                written += bytes.size
            }
            "end" -> {
                check(started && written == expected) { "Download length mismatch" }
                val actual = digest.digest().joinToString("") { "%02x".format(it.toInt() and 255) }
                require(message.sha256?.equals(actual, ignoreCase = true) == true) {
                    "Download checksum mismatch"
                }
                output.flush()
                output.close()
                closed = true
                return "PC file downloaded (${written} bytes)"
            }
            "error" -> error(message.error ?: "Host could not read file")
            else -> error("Unknown download action")
        }
        return null
    }

    @Synchronized fun abort() {
        if (closed) return
        closed = true
        runCatching { output.close() }
        runCatching { deleteIncomplete() }
    }
}
