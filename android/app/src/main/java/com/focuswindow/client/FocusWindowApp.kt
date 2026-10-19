package com.focuswindow.client

import android.app.Application
import android.util.Log
import com.focuswindow.client.media.VideoDecoder

/**
 * Application entry point.
 *
 * FR-OPS-02 — the client knows which host build it is paired with and can show
 * a changelog the PHONE user can read. The person writing the one-star review
 * is holding the phone, not sitting at the PC.
 */
class FocusWindowApp : Application() {

    /** FR-ENC-02 — reported to the host so it can pick the top codec rung. */
    lateinit var decoderCapabilities: List<String>
        private set

    override fun onCreate() {
        super.onCreate()

        decoderCapabilities = runCatching { VideoDecoder.capabilities() }
            .getOrElse { emptyList() }

        Log.i(
            TAG,
            "Focus Window ${BuildConfig.VERSION_NAME} (${BuildConfig.BUILD_TYPE}) " +
                "decoders=$decoderCapabilities",
        )

        // NFR-SEC-01 — a loud, early assertion that the posture claim on the
        // store page is true of this binary. Debug-only so a release build is
        // never slowed by it, but CI runs the same check on the release APK.
        if (BuildConfig.DEBUG) assertTrustPosture()
    }

    /**
     * FR-TRUST-04 — "no accessibility service, no ADB, no root" is a product
     * promise, so it is verified rather than remembered.
     */
    private fun assertTrustPosture() {
        val info = packageManager.getPackageInfo(
            packageName,
            android.content.pm.PackageManager.GET_PERMISSIONS or
                android.content.pm.PackageManager.GET_SERVICES,
        )

        val forbiddenPermissions = setOf(
            "android.permission.BIND_ACCESSIBILITY_SERVICE",
            "android.permission.WRITE_SECURE_SETTINGS",
            "android.permission.QUERY_ALL_PACKAGES",
            "android.permission.READ_PHONE_STATE",
            "android.permission.GET_ACCOUNTS",
        )

        val declared = info.requestedPermissions?.toSet().orEmpty()
        val violations = declared intersect forbiddenPermissions
        check(violations.isEmpty()) {
            "Trust posture violated: this build declares $violations"
        }

        val accessibilityServices = info.services.orEmpty().filter {
            it.permission == "android.permission.BIND_ACCESSIBILITY_SERVICE"
        }
        check(accessibilityServices.isEmpty()) {
            "Trust posture violated: accessibility service declared"
        }
    }

    private companion object {
        const val TAG = "FocusWindow"
    }
}
