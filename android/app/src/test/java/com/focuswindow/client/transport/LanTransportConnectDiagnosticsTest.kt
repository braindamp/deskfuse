package com.focuswindow.client.transport

import java.net.ConnectException
import java.net.NoRouteToHostException
import java.net.SocketTimeoutException
import java.net.UnknownHostException
import kotlinx.coroutines.runBlocking
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test

class LanTransportConnectDiagnosticsTest {
    private fun pairing() = PairingPayload(
        host = "192.168.2.107",
        port = 47810,
        hostPublicKeyB64 = "unused",
        pairingSecret = ByteArray(32),
        hostName = "DeskFuse",
        viaRelay = false,
    )

    @Test
    fun temporarily_unreachable_pc_is_retryable_and_has_neutral_copy() = runBlocking {
        val connection = LanTransport(this)
        val cases = listOf(
            ConnectException("Connection refused"),
            SocketTimeoutException("timed out"),
            NoRouteToHostException("No route"),
            UnknownHostException("no host"),
        )
        for (exception in cases) {
            val result = connection.diagnose(exception, pairing())
            assertTrue(result.retryable)
            assertFalse(result.terminal)
            assertFalse(result.detail.contains("refused", ignoreCase = true))
            assertFalse(result.detail.contains("192.168", ignoreCase = true))
            assertEquals("The PC isn't reachable right now.", result.detail)
        }
    }

    @Test
    fun verification_problems_do_not_get_retried_as_network_errors() = runBlocking {
        val connection = LanTransport(this)
        val failure = connection.diagnose(
            IllegalArgumentException("Host key mismatch"), pairing())
        assertFalse(failure.retryable)
        assertEquals("verify", failure.stage)
    }
}
