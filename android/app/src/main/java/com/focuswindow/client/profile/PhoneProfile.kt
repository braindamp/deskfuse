package com.focuswindow.client.profile

import kotlinx.serialization.Serializable
import kotlin.math.max
import kotlin.math.min
import kotlin.math.roundToInt
import kotlin.math.sqrt

/**
 * FR-DPI-01 — Single phone-profile source of truth.
 *
 * This is the ONLY thing the host learns about this device. Every host-side
 * decision (virtual display mode, EDID physical size, requested scale factor,
 * target client rect) is derived from this record and nothing else.
 *
 * Invariant: the host must never contain a hard-coded resolution, scale or
 * aspect ratio. If a field changes meaning, bump [VERSION] and handle both
 * on the host.
 */
@Serializable
data class PhoneProfile(
    val version: Int = VERSION,

    /** Stable per-device identifier. Not a user identity — no account (FR-TRUST-01). */
    val deviceId: String,
    val deviceLabel: String,

    /** Physical pixels of the display, in the CURRENT orientation. */
    val physicalW: Int,
    val physicalH: Int,

    /** Device pixel ratio, e.g. 3.5 on a 1440x3120 flagship. */
    val dpr: Float,

    /** Safe-area insets in physical pixels (cutout + system bars). FR-MOB-03. */
    val safeTop: Int,
    val safeBottom: Int,
    val safeLeft: Int,
    val safeRight: Int,

    /** Android user font scale. Informational for the host: a streamed Win32 app
     *  does NOT inherit it (see FR-DPI-12 — density is not design). */
    val fontScale: Float,

    /** Panel capability, used by the refresh ladder (FR-REF-01). */
    val maxRefreshHz: Int,

    /** Physical diagonal in millimetres; lets the host derive a plausible EDID
     *  physical size so Windows' DPI arithmetic is self-consistent (FR-DPI-02). */
    val diagonalMm: Int,

    val orientation: Orientation,
    val formFactor: FormFactor,
) {
    @Serializable
    enum class Orientation { PORTRAIT, LANDSCAPE }

    @Serializable
    enum class FormFactor { PHONE, FOLDABLE_INNER, FOLDABLE_COVER, TABLET }

    /**
     * The logical canvas the remote app should lay itself out in.
     *
     * This is the number that makes a 14 dp body token 14 dp on this phone:
     * the host asks Windows for (physical px @ dpr), so Windows reports this
     * many DIPs of room while rasterizing [physicalW] x [physicalH] pixels.
     */
    val canvasDpW: Int get() = ((physicalW - safeLeft - safeRight) / dpr).toInt()
    val canvasDpH: Int get() = ((physicalH - safeTop - safeBottom) / dpr).toInt()

    /** Pixels actually available to the remote surface after safe-area insets. */
    val surfaceW: Int get() = physicalW - safeLeft - safeRight
    val surfaceH: Int get() = physicalH - safeTop - safeBottom

    /**
     * FR-DPI-03 — the scale factor the host requests on the display path.
     * IddCx accepts 100..500 and treats it as a HINT; clamp rather than
     * rounding into an invalid value.
     */
    val requestedScaleFactor: Int get() = min(500, max(100, (dpr * 100f).roundToInt()))

    /** FR-DPI-04 — what GetDpiForWindow(target) must report, within ±1. */
    val expectedDpi: Int get() = (96f * dpr).roundToInt()

    /** Approximate pixels-per-inch; used for the readability floor (FR-DPI-12). */
    val ppi: Float
        get() {
            val diagPx = sqrt((physicalW.toFloat() * physicalW) + (physicalH.toFloat() * physicalH))
            val diagIn = diagonalMm / 25.4f
            return if (diagIn <= 0f) 160f else diagPx / diagIn
        }

    /**
     * FR-DPI-09 — rotation re-derives the target; it never touches the source
     * window. Safe-area insets are mapped by physical edge, not by the rotated
     * coordinate system, which is the usual source of off-by-a-notch bugs.
     */
    fun withOrientation(target: Orientation): PhoneProfile {
        if (target == orientation) return this
        return copy(
            orientation = target,
            physicalW = physicalH,
            physicalH = physicalW,
            safeLeft = safeBottom,
            safeRight = safeTop,
            safeTop = safeLeft,
            safeBottom = safeRight,
        )
    }

    /** Cheap identity for "has the target geometry actually changed?". */
    fun geometryKey(): String =
        "$physicalW:$physicalH:$dpr:$safeTop:$safeBottom:$safeLeft:$safeRight:$orientation"

    fun validate(): List<String> = buildList {
        if (physicalW <= 0 || physicalH <= 0) add("physical size must be positive")
        if (!dpr.isFinite() || dpr < 1.0f || dpr > 5.0f)
            add("missing or implausible Android display density: $dpr")
        if (maxRefreshHz < 24) add("implausible refresh: $maxRefreshHz")
        if (diagonalMm !in 60..500) add("implausible diagonal: ${diagonalMm}mm")
        if (safeTop < 0 || safeBottom < 0 || safeLeft < 0 || safeRight < 0) add("negative inset")
        if (surfaceW <= 0 || surfaceH <= 0) add("insets consume the whole surface")
        if (canvasDpW < 240) add("canvas too narrow for any desktop app: ${canvasDpW}dp")
    }

    companion object {
        const val VERSION = 1

        /** FR-DPI-12 — below this, the host should warn and offer the app's own zoom. */
        const val READABLE_TOKEN_DP = 12f

        /** The body-token size we assume when predicting readability. */
        const val REFERENCE_TOKEN_DP = 14f
    }
}
