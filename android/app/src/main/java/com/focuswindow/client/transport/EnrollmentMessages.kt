package com.focuswindow.client.transport

import java.io.EOFException
import java.net.SocketTimeoutException

/**
 * Raised only when Windows explicitly answers `denied` to an enrollment.
 * Kept separate so the UI can explain a refusal instead of printing exception text.
 */
class EnrollmentDeniedException : Exception("The Windows owner declined this phone.")

/**
 * FR-OPS-03: every pairing failure is a named, actionable state.
 * These strings are shown in the PC picker while the phone is not connected.
 */
object EnrollmentMessages {
    fun describeFailure(error: Throwable): String = when (error) {
        is EnrollmentDeniedException ->
            "The Windows owner declined this phone. Try again when you are ready."
        is SocketTimeoutException ->
            "No approval arrived from the PC. Check the Windows dialog, then try again."
        is EOFException ->
            "The PC closed the pairing request. Wait 15 seconds, then try again."
        else -> "Could not pair: ${error.message ?: "check Windows and retry"}"
    }
}
