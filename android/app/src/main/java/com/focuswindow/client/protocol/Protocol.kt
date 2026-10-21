package com.focuswindow.client.protocol

import com.focuswindow.client.profile.PhoneProfile
import kotlinx.serialization.SerialName
import kotlinx.serialization.Serializable

const val PROTOCOL_VERSION = 3

/**
 * The host's description of what it is about to send.
 *
 * FR-DPI-08 / FR-ENC-04 — the client must be able to display the real density
 * and the real colour format at all times. A density product that hides its
 * density cannot be trusted when it is wrong.
 *
 * Every frame references [version]; a frame whose version the client has not
 * seen is DROPPED rather than rendered against a stale rectangle (FR-ENC-05).
 */
@Serializable
data class TargetRecord(
    val version: Long,

    /** FR-TGT-01 — which occupant is on screen. Always shown to the user. */
    val occupant: Occupant,

    /** Exact pixels the host is encoding. Must equal our surface (NFR-PIX-01). */
    val widthPx: Int,
    val heightPx: Int,

    /** FR-DPI-04 — the VERIFIED dpi of the target window, not the requested one. */
    val dpi: Int,

    /** Logical canvas the remote app laid itself out in. */
    val canvasDpW: Int,
    val canvasDpH: Int,

    /**
     * FR-DPI-04 — false means the host could not reach the phone's density and
     * fell back. The client MUST label the session; silent degradation is how a
     * native promise becomes a soft screenshot.
     */
    val native: Boolean,
    val densityMethod: DensityMethod,

    val codec: String,
    val pixelFormat: String,
    val refreshHz: Int,

    /** Populated when [native] is false, or when a tier was downgraded. */
    val degradedReason: String? = null,

    /** FR-STATE-01 — declared before the first frame, shown to the user. */
    val stateModel: StateModel,

    /** Human-readable app/document identity for the session chrome. */
    val appLabel: String,
    val documentLabel: String? = null,
    /** Actual encoded VUI contract; end-to-end display fidelity is separately measured. */
    val colorRange: String? = null,
    /** Ephemeral source identity, never a reusable raw Windows HWND. */
    val sourceId: String? = null,
) {
    @Serializable
    enum class Occupant {
        @SerialName("canvas") CANVAS,
        @SerialName("clone") CLONE,
        @SerialName("window") WINDOW,
        @SerialName("session") SESSION,
    }

    @Serializable
    enum class DensityMethod {
        @SerialName("already_correct") ALREADY_CORRECT,
        @SerialName("iddscale") IDD_SCALE,
        @SerialName("permonitor_override") PER_MONITOR_OVERRIDE,
        @SerialName("fallback_scaled") FALLBACK_SCALED,
    }

    @Serializable
    enum class StateModel {
        @SerialName("shared_view") SHARED_VIEW,
        @SerialName("coauthored") COAUTHORED,
        @SerialName("read_only_clone") READ_ONLY_CLONE,
        @SerialName("save_back") SAVE_BACK,
    }

    /** FR-DPI-12 — predicted on-screen size of a reference body token. */
    fun predictedTokenDp(profile: PhoneProfile): Float {
        // Pixels were reduced and the phone upscales them: the apparent size survives (that is the
        // point of that fallback), glyph detail does not.
        if (!native && !isOneToOne(profile)) {
            return PhoneProfile.REFERENCE_TOKEN_DP
        }
        // From here the stream is 1:1 with the panel, so the size is whatever DPI the host REALLY
        // used — including a host that could only reach 250% for a 262.5% phone, or 250% for a
        // phone that needs 350%. Reporting 14 dp there would be a lie; this reports 13.3 / 10.
        val hostScale = dpi / 96f
        val tokenPx = PhoneProfile.REFERENCE_TOKEN_DP * hostScale
        return tokenPx / profile.dpr
    }

    /** NFR-PIX-01 — true only when one source pixel maps to one device pixel. */
    fun isOneToOne(profile: PhoneProfile): Boolean =
        (widthPx == profile.physicalW && heightPx == profile.physicalH) ||
        (widthPx == profile.surfaceW && heightPx == profile.surfaceH)

    fun tierLabel(profile: PhoneProfile? = null): String = when (occupant) {
        Occupant.CLONE -> if (native) "Tier A · shared view, DPI verified" else "Tier A · DPI unverified"
        Occupant.CANVAS -> when {
            native -> "Tier B · desktop canvas, DPI verified"
            profile != null && isOneToOne(profile) -> "Tier B · 1:1 pixels, DPI unverified"
            else -> "Tier B · reduced pixels"
        }
        Occupant.WINDOW -> "Tier D · window capture, DPI unverified"
        Occupant.SESSION -> "Tier C · remote session"
    }
}

/** Host → client. Reliable, ordered control channel. */
@Serializable
sealed interface ServerMessage {
    @Serializable @SerialName("hello")
    data class Hello(
        val protocolVersion: Int,
        val hostVersion: String,
        val hostName: String,
        /** FR-OPS-01 — the build the user can pin / roll back to. */
        val channel: String,
        val pcAudioAvailable: Boolean = false,
        val phoneMicrophoneBridgeAvailable: Boolean = false,
    ) : ServerMessage

    @Serializable @SerialName("target")
    data class Target(val record: TargetRecord) : ServerMessage

    /** FR-OPS-03 — every non-streaming state is named. Never a blank surface. */
    @Serializable @SerialName("stage")
    data class Stage(
        val stage: String,
        val detail: String,
        val remediation: String? = null,
        val fatal: Boolean = false,
        /**
         * How it actually went wrong, for the diagnostics. Never shown as
         * the headline: the person using the phone has no use for it.
         */
        val technical: String? = null,
    ) : ServerMessage

    /** FR-ENC-04 — quality changed, and why. Shown, not swallowed. */
    @Serializable @SerialName("quality")
    data class Quality(
        val codec: String,
        val pixelFormat: String,
        val bitrateKbps: Int,
        val refreshHz: Int,
        val reason: String,
    ) : ServerMessage

    /** FR-STATE-02 — divergence must be surfaced, never silently forked. */
    @Serializable @SerialName("conflict")
    data class Conflict(val summary: String, val sourceLabel: String, val cloneLabel: String) :
        ServerMessage

    @Serializable @SerialName("file_list")
    data class FileList(
        val path: String,
        val parent: String? = null,
        val entries: List<RemoteFileEntry>,
        val truncated: Boolean = false,
        val offset: Int = 0,
        val nextOffset: Int = 0,
    ) : ServerMessage

    @Serializable @SerialName("file_data")
    data class FileData(
        val action: String, val id: String,
        val name: String? = null, val size: Long? = null,
        val base64: String? = null, val sha256: String? = null,
        val error: String? = null,
    ) : ServerMessage

    @Serializable @SerialName("window_list")
    data class WindowList(val windows: List<WindowEntry>) : ServerMessage

    @Serializable @SerialName("microphone_state")
    data class MicrophoneState(
        val state: String,
        val streamId: Int = 0,
        val sampleRate: Int = 16000,
        val channels: Int = 1,
        val format: String = "pcm_s16le",
        val detail: String? = null,
    ) : ServerMessage

    @Serializable @SerialName("bridge_state")
    data class BridgeState(
        val requestedCapabilities: List<String> = emptyList(),
        val activeCapabilities: List<String> = emptyList(),
    ) : ServerMessage

    @Serializable @SerialName("audio_state")
    data class AudioState(
        val state: String, val streamId: Int,
        val sampleRate: Int = 0, val channels: Int = 0,
        val format: String = "pcm_s16le", val detail: String? = null,
        val layout: String = "stereo",
        /** For the diagnostics only. */
        val technical: String? = null,
    ) : ServerMessage

    /**
     * The playback volume of the phone's Windows endpoint, 0..100. The
     * phone applies it to its own output, so the Windows volume control and
     * the phone volume control are the same control.
     */
    @Serializable @SerialName("audio_volume")
    data class AudioVolume(val level: Int) : ServerMessage

    /**
     * Where an audio stream sits on the host's clock. The phone uses it
     * to measure how long the sound took to arrive and to align it with
     * the picture.
     */
    @Serializable @SerialName("audio_clock")
    data class AudioClock(
        val streamId: Int = 0,
        val hostUs: Long = 0,
        val sampleRate: Int = 0,
    ) : ServerMessage

    @Serializable @SerialName("clipboard")
    data class Clipboard(val mime: String, val text: String? = null, val base64: String? = null) :
        ServerMessage

    /** FR-TGT-03 — the invariant probe's continuous report on the source window. */
    @Serializable @SerialName("invariant")
    data class Invariant(val clean: Boolean, val drift: List<String>) : ServerMessage
}

@Serializable
data class RemoteFileEntry(val path: String, val name: String, val directory: Boolean, val size: Long = 0)

@Serializable
data class WindowEntry(val id: String, val title: String, val appLabel: String)

/** Client → host. The phone is the display authority. */
@Serializable
sealed interface ClientMessage {
    @Serializable @SerialName("hello")
    data class Hello(
        val protocolVersion: Int = PROTOCOL_VERSION,
        val clientVersion: String,
        val profile: PhoneProfile,
        val capabilities: List<String> = emptyList(),
    ) : ClientMessage

    // Never send a role flag in ordinary "hello": pre-bridge Windows hosts
    // would ignore it and start a virtual monitor. Older hosts reject this
    // distinct first-message type without acquiring the display.
    @Serializable @SerialName("bridge_hello")
    data class BridgeHello(
        val protocolVersion: Int = PROTOCOL_VERSION,
        val clientVersion: String,
        val profile: PhoneProfile,
        val capabilities: List<String> = emptyList(),
    ) : ClientMessage

    /** FR-DPI-09 — rotation/fold/font-scale re-derive the TARGET, nothing else. */
    @Serializable @SerialName("profile")
    data class Profile(val profile: PhoneProfile) : ClientMessage

    /** FR-DPI-07 — logical / native / one-to-one presets. */
    @Serializable @SerialName("density")
    data class Density(val preset: DensityPreset) : ClientMessage

    /** FR-REF-01 — refresh is a user choice, constrained by panel and link. */
    @Serializable @SerialName("refresh")
    data class Refresh(val hz: Int) : ClientMessage

    /** FR-TGT-01 — ask for a different occupant (e.g. clone instead of canvas). */
    @Serializable @SerialName("occupant")
    data class Occupant(val want: TargetRecord.Occupant) : ClientMessage

    @Serializable @SerialName("input")
    data class Input(val event: InputEvent) : ClientMessage

    @Serializable @SerialName("clipboard")
    data class Clipboard(val mime: String, val text: String? = null, val base64: String? = null) :
        ClientMessage

    /** FR-ENC-05 — ask for a keyframe after a drop or a resume. */
    @Serializable @SerialName("keyframe")
    data object RequestKeyframe : ClientMessage

    @Serializable @SerialName("windows")
    data class Windows(val action: String, val id: String? = null) : ClientMessage

    @Serializable @SerialName("audio")
    data class Audio(
        val action: String,
        val layouts: List<String> = emptyList(),
        val maxPcmBytes: Int = 32768,
    ) : ClientMessage

    @Serializable @SerialName("microphone")
    data class Microphone(val action: String) : ClientMessage

    /**
     * The phone's own media volume, 0..100, after the user moved the
     * phone's slider. The PC applies it to its endpoint, so the two
     * controls stay the same control.
     */
    @Serializable @SerialName("audio_volume")
    data class AudioVolume(val level: Int) : ClientMessage

    /** One explicitly selected Android document, delivered in bounded encrypted chunks. */
    @Serializable @SerialName("files")
    data class Files(val action: String, val path: String? = null, val id: String? = null, val offset: Int = 0) : ClientMessage

    @Serializable @SerialName("file")
    data class File(
        val action: String,
        val id: String,
        val name: String? = null,
        val size: Long? = null,
        val base64: String? = null,
        val sha256: String? = null,
        val path: String? = null,
        val openOnPc: Boolean = false,
    ) : ClientMessage

    @Serializable @SerialName("bye")
    data class Bye(val reason: String) : ClientMessage
}

@Serializable
enum class DensityPreset {
    /** Canvas sized in dp so the app lays out like a phone. The default. */
    @SerialName("logical") LOGICAL,

    /** Exactly the phone's physical pixels, 1:1, no safe-area subtraction. */
    @SerialName("native") NATIVE,

    /** A user-pinned device profile (e.g. "as my tablet"). */
    @SerialName("one_to_one") ONE_TO_ONE,
}

/**
 * FR-IN-01..05. Coordinates are in TARGET CLIENT PIXELS.
 *
 * In the native path the stream is 1:1, so the client sends the touch point
 * unchanged and the host needs no inverse transform. That is the whole reason
 * the coordinate bugs in this category disappear.
 */
@Serializable
sealed interface InputEvent {
    @Serializable @SerialName("pointer")
    data class Pointer(
        val x: Int,
        val y: Int,
        val action: PointerAction,
        val button: PointerButton = PointerButton.LEFT,
        /** FR-IN-05 — pen pressure (0..1) and tilt in degrees. */
        val pressure: Float = 0f,
        val tiltX: Float = 0f,
        val tiltY: Float = 0f,
        val isStylus: Boolean = false,
    ) : InputEvent

    @Serializable @SerialName("relative")
    data class Relative(val dx: Int, val dy: Int, val button: PointerButton = PointerButton.LEFT) :
        InputEvent

    @Serializable @SerialName("scroll")
    data class Scroll(val x: Int, val y: Int, val dx: Int, val dy: Int) : InputEvent

    /** FR-IN-02 — scancode pass-through, not characters. */
    @Serializable @SerialName("key")
    data class Key(
        val scancode: Int,
        val keyCode: Int,
        val down: Boolean,
        val modifiers: Int,
        val repeat: Int = 0,
    ) : InputEvent

    /** IME composition and non-Latin layouts still need a text path. */
    @Serializable @SerialName("text")
    data class Text(val text: String) : InputEvent
}

@Serializable
enum class PointerAction {
    @SerialName("down") DOWN,
    @SerialName("move") MOVE,
    @SerialName("up") UP,
    @SerialName("hover") HOVER,
}

@Serializable
enum class PointerButton {
    @SerialName("left") LEFT,
    @SerialName("right") RIGHT,
    @SerialName("middle") MIDDLE,
}

/** Modifier bit flags shared with the host (FR-IN-03). */
object Modifiers {
    const val NONE = 0
    const val CTRL = 1 shl 0
    const val ALT = 1 shl 1
    const val SHIFT = 1 shl 2
    const val META = 1 shl 3

    fun describe(mask: Int): String = buildList {
        if (mask and CTRL != 0) add("Ctrl")
        if (mask and ALT != 0) add("Alt")
        if (mask and SHIFT != 0) add("Shift")
        if (mask and META != 0) add("Win")
    }.joinToString("+")
}

/**
 * Binary frame header that precedes every encoded video payload.
 *
 * Kept deliberately tiny and fixed-size: this is on the hot path.
 * Layout (big endian): magic u16 | flags u16 | targetVersion u32 | ptsUs u64 | len u32
 */
data class FrameHeader(
    val keyframe: Boolean,
    val targetVersion: Long,
    val ptsUs: Long,
    val payloadLength: Int,
) {
    companion object {
        /** 'F' 'W' */
        const val MAGIC = 0x4657
        const val SIZE_BYTES = 20
        const val FLAG_KEYFRAME = 1 shl 0
        const val FLAG_CONFIG = 1 shl 1
    }
}
