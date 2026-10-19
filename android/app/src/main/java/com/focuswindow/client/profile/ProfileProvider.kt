package com.focuswindow.client.profile

import android.app.Activity
import android.content.Context
import android.os.Build
import android.provider.Settings
import android.view.Display
import androidx.core.view.WindowInsetsCompat
import androidx.window.layout.WindowMetricsCalculator
import java.util.UUID
import kotlin.math.roundToInt
import kotlin.math.sqrt

/**
 * Builds the [PhoneProfile] from real Android APIs.
 *
 * FR-DPI-01 — one source of truth.
 * FR-MOB-01/02/03 — portrait, foldable and cutout handling all enter here,
 * because they are all just "the profile changed".
 */
class ProfileProvider(private val appContext: Context) {

    private val prefs = appContext.getSharedPreferences(PREFS, Context.MODE_PRIVATE)

    /**
     * A stable, app-scoped device id. Deliberately random and local:
     * we never ask for an account, an email or a hardware identifier
     * (FR-TRUST-01, NFR-SEC-01).
     */
    private val deviceId: String
        get() = prefs.getString(KEY_DEVICE_ID, null) ?: UUID.randomUUID().toString().also {
            prefs.edit().putString(KEY_DEVICE_ID, it).apply()
        }

    fun current(activity: Activity, insets: WindowInsetsCompat?): PhoneProfile {
        val metrics = WindowMetricsCalculator.getOrCreate()
            .computeCurrentWindowMetrics(activity)
        val bounds = metrics.bounds

        val widthPx = bounds.width()
        val heightPx = bounds.height()

        val density = activity.resources.displayMetrics.density
        val fontScale = activity.resources.configuration.fontScale

        // FR-MOB-03 — safe area comes from cutout + system bars, in physical px.
        val safe = insets?.let {
            val cutout = it.getInsets(WindowInsetsCompat.Type.displayCutout())
            val bars = it.getInsets(WindowInsetsCompat.Type.systemBars())
            Insets(
                top = maxOf(cutout.top, bars.top),
                bottom = maxOf(cutout.bottom, bars.bottom),
                left = maxOf(cutout.left, bars.left),
                right = maxOf(cutout.right, bars.right),
            )
        } ?: Insets(0, 0, 0, 0)

        val display = activity.display
        val maxRefresh = display?.supportedModes
            ?.maxOfOrNull { it.refreshRate }
            ?.roundToInt()
            ?: display?.refreshRate?.roundToInt()
            ?: 60

        return PhoneProfile(
            deviceId = deviceId,
            deviceLabel = "${Build.MANUFACTURER} ${Build.MODEL}".trim(),
            physicalW = widthPx,
            physicalH = heightPx,
            dpr = density,
            safeTop = safe.top,
            safeBottom = safe.bottom,
            safeLeft = safe.left,
            safeRight = safe.right,
            fontScale = fontScale,
            maxRefreshHz = maxRefresh,
            diagonalMm = estimateDiagonalMm(activity, widthPx, heightPx),
            orientation = if (heightPx >= widthPx) {
                PhoneProfile.Orientation.PORTRAIT
            } else {
                PhoneProfile.Orientation.LANDSCAPE
            },
            formFactor = detectFormFactor(activity, widthPx, heightPx, density),
        )
    }

    /**
     * FR-DPI-02 — the host needs a plausible physical size for the virtual
     * monitor's EDID. Derive it from the real panel density rather than
     * guessing, so Windows' own DPI arithmetic stays self-consistent.
     */
    private fun estimateDiagonalMm(activity: Activity, wPx: Int, hPx: Int): Int {
        val dm = activity.resources.displayMetrics
        val xdpi = if (dm.xdpi > 1f) dm.xdpi else dm.densityDpi.toFloat()
        val ydpi = if (dm.ydpi > 1f) dm.ydpi else dm.densityDpi.toFloat()
        val wIn = wPx / xdpi
        val hIn = hPx / ydpi
        val diagIn = sqrt(wIn * wIn + hIn * hIn)
        return (diagIn * 25.4f).roundToInt().coerceIn(60, 500)
    }

    private fun detectFormFactor(
        activity: Activity,
        wPx: Int,
        hPx: Int,
        density: Float,
    ): PhoneProfile.FormFactor {
        val shortestDp = minOf(wPx, hPx) / density
        val longestDp = maxOf(wPx, hPx) / density
        return when {
            shortestDp >= 600f -> PhoneProfile.FormFactor.TABLET
            // A tall, very narrow panel is the classic foldable cover screen.
            longestDp / shortestDp > 2.5f && shortestDp < 340f ->
                PhoneProfile.FormFactor.FOLDABLE_COVER
            shortestDp >= 500f -> PhoneProfile.FormFactor.FOLDABLE_INNER
            else -> PhoneProfile.FormFactor.PHONE
        }
    }

    /**
     * FR-REF-03 — honour the user's own timeout preference rather than holding
     * the screen on indefinitely. A keep-awake that ignores the system setting
     * is a live one-star theme in a leading competitor.
     */
    fun systemScreenOffTimeoutMs(): Int = Settings.System.getInt(
        appContext.contentResolver,
        Settings.System.SCREEN_OFF_TIMEOUT,
        DEFAULT_TIMEOUT_MS,
    )

    private data class Insets(val top: Int, val bottom: Int, val left: Int, val right: Int)

    private companion object {
        const val PREFS = "focuswindow.profile"
        const val KEY_DEVICE_ID = "device_id"
        const val DEFAULT_TIMEOUT_MS = 60_000
    }
}

/** Convenience for display-mode selection in the refresh ladder (FR-REF-01). */
fun Display.supportedRefreshRates(): List<Int> =
    supportedModes.map { it.refreshRate.roundToInt() }.distinct().sorted()
