package com.focuswindow.client.session

import com.focuswindow.client.media.VideoDecoder
import com.focuswindow.client.profile.PhoneProfile
import com.focuswindow.client.protocol.TargetRecord
import com.focuswindow.client.render.PhoneSurfaceView

/**
 * FR-OPS-03 — "no black screen without an explanation".
 *
 * Black-screen-on-connect is the signature failure of driver+codec products and
 * one of the largest negative themes in the category. The defence is structural:
 * the UI renders from this sealed hierarchy, so there is no code path that can
 * produce an unexplained blank surface. Every non-streaming state carries a
 * stage, a human-readable detail, and — where one exists — a remediation.
 */
sealed interface SessionState {

    val stageLabel: String

    data object Idle : SessionState {
        override val stageLabel = "Not connected"
    }

    /** FR-TRUST-02 — scanning a pairing QR. */
    data object AwaitingPairing : SessionState {
        override val stageLabel = "Scan the pairing code"
    }

    data class Connecting(val host: String, val port: Int) : SessionState {
        override val stageLabel = "Connecting to $host"
    }

    /**
     * The user compares a 6-digit code on both screens. This is the step that
     * defeats an active attacker on the LAN, so it is explicit, not skipped.
     */
    data class Verifying(val code: String, val hostName: String) : SessionState {
        override val stageLabel = "Confirm the code on $hostName"
    }

    /** The host is creating the virtual display and negotiating density. */
    data class PreparingTarget(
        val stage: String,
        val detail: String,
        val progress: Float,
    ) : SessionState {
        override val stageLabel = stage
    }

    data class Streaming(
        val record: TargetRecord,
        val profile: PhoneProfile,
        val presentation: PhoneSurfaceView.Presentation,
        val stats: VideoDecoder.DecoderStats?,
        val linkRttMs: Double,
        val invariantClean: Boolean,
        val invariantDrift: List<String>,
    ) : SessionState {
        override val stageLabel = "Streaming"

        /** FR-DPI-12 — measured, not assumed. */
        val tokenDp: Float get() = record.predictedTokenDp(profile)

        val readable: Boolean get() = tokenDp >= PhoneProfile.READABLE_TOKEN_DP

        val oneToOne: Boolean
            get() = presentation.mode == PhoneSurfaceView.Presentation.Mode.ONE_TO_ONE

        /** NFR-LAT-01 — the number the user actually feels. */
        val glassToGlassMs: Double get() = linkRttMs / 2 + (stats?.decodeLatencyMs ?: 0.0)
    }

    /**
     * FR-REF-05 — the phone slept, the network switched, or the host blipped.
     * We keep the last frame visible, say so, and reattach to the SAME
     * document rather than opening a second copy.
     */
    data class Reconnecting(
        val attempt: Int,
        val maxAttempts: Int,
        val reason: String,
        val nextRetryInMs: Long,
    ) : SessionState {
        override val stageLabel = "Reconnecting ($attempt/$maxAttempts)"
    }

    /**
     * The target still exists but is not producing frames: minimised, a
     * protected/DRM surface, or an app that stopped rendering. We show the last
     * frame dimmed with a named reason — never a frozen image pretending to be
     * live.
     */
    data class Stale(val reason: String, val sinceMs: Long) : SessionState {
        override val stageLabel = "Paused — $reason"
    }

    /**
     * FR-STATE-02 — divergence is surfaced, never silently forked.
     */
    data class ConflictDetected(
        val summary: String,
        val sourceLabel: String,
        val cloneLabel: String,
    ) : SessionState {
        override val stageLabel = "Document conflict"
    }

    data class Failed(
        val stage: String,
        val detail: String,
        val remediation: String?,
        val recoverable: Boolean,
    ) : SessionState {
        override val stageLabel = "Couldn't start — $stage"
    }

    data class Ended(val reason: String) : SessionState {
        override val stageLabel = "Session ended"
    }
}

/**
 * The warnings the session chrome shows alongside a live stream. These are
 * deliberately separate from [SessionState]: a session can be perfectly healthy
 * and still be worth a caveat.
 */
data class SessionAdvisory(
    val id: String,
    val severity: Severity,
    val message: String,
    val action: String? = null,
) {
    enum class Severity { INFO, WARN, CRITICAL }

    companion object {
        /** FR-DPI-04 — the host could not reach our density. Say so, loudly. */
        fun notNative(method: TargetRecord.DensityMethod, reason: String?,
                      pixelsOneToOne: Boolean) = SessionAdvisory(
            id = "density",
            severity = Severity.WARN,
            message = reason ?: if (pixelsOneToOne) {
                "Source pixels match the phone exactly, but Windows DPI is not verified ($method). " +
                    "Text may be the wrong physical size; the video is not being rescaled."
            } else {
                "Windows DPI and the pixel dimensions do not match the phone ($method)."
            },
            action = "Why?",
        )

        /** FR-DPI-12 — density is not design: a 9 dp label is 9 dp at any DPR. */
        fun smallTokens(tokenDp: Float) = SessionAdvisory(
            id = "tokens",
            severity = Severity.WARN,
            message = "Text measures ~${"%.1f".format(tokenDp)} dp. The render is native, but " +
                "this app's own UI is small. Raise the app's zoom on the PC for comfort.",
            action = "Open app zoom",
        )

        /** FR-TGT-01 — the active tier is never hidden. */
        fun fallbackTier(tier: String, why: String) = SessionAdvisory(
            id = "tier",
            severity = Severity.INFO,
            message = "$tier — $why",
        )

        /** FR-ENC-04 — a quality change is reported, never swallowed. */
        fun qualityDrop(format: String, reason: String) = SessionAdvisory(
            id = "quality",
            severity = Severity.INFO,
            message = "Colour format dropped to $format ($reason). Text may look softer.",
        )

        /** FR-TGT-03 — if the host ever reports source drift, that is critical. */
        fun invariantDrift(drift: List<String>) = SessionAdvisory(
            id = "invariant",
            severity = Severity.CRITICAL,
            message = "The host reported changes to your PC window: ${drift.joinToString()}. " +
                "This should never happen — please report it.",
            action = "Report",
        )

        fun relayActive(hostName: String) = SessionAdvisory(
            id = "relay",
            severity = Severity.INFO,
            message = "Connected to $hostName through a relay. Traffic is still end-to-end " +
                "encrypted, but it is not a direct LAN connection.",
        )
    }
}
