package com.focuswindow.client

import com.focuswindow.client.transport.EnrollmentDeniedException
import com.focuswindow.client.transport.EnrollmentMessages
import org.junit.Assert.assertTrue
import org.junit.Test
import java.io.EOFException
import java.net.SocketTimeoutException

/** B03 UX: refusal, timeout and rate-limit close are named states, not raw exception text. */
class EnrollmentMessagesTest {

    @Test
    fun denial_is_explained_as_a_refusal() {
        val message = EnrollmentMessages.describeFailure(EnrollmentDeniedException())
        assertTrue(message, message.contains("declined"))
    }

    @Test
    fun closed_request_mentions_the_rate_limit_wait() {
        val message = EnrollmentMessages.describeFailure(EOFException())
        assertTrue(message, message.contains("15 seconds"))
    }

    @Test
    fun timeout_points_at_the_windows_dialog() {
        val message = EnrollmentMessages.describeFailure(SocketTimeoutException())
        assertTrue(message, message.contains("Windows dialog"))
    }

    @Test
    fun unknown_failure_keeps_the_underlying_reason() {
        val message = EnrollmentMessages.describeFailure(IllegalStateException("boom"))
        assertTrue(message, message.contains("boom"))
    }
}
