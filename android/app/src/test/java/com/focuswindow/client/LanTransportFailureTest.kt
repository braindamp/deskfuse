package com.focuswindow.client

import com.focuswindow.client.protocol.ServerMessage
import com.focuswindow.client.transport.LanTransport
import kotlinx.coroutines.async
import kotlinx.coroutines.CoroutineStart
import kotlinx.coroutines.flow.first
import kotlinx.coroutines.runBlocking
import org.junit.Assert.*
import org.junit.Test

class LanTransportFailureTest {
    @Test
    fun fatal_source_error_stops_the_reader_and_requests_manual_retry() = runBlocking {
        val transport = LanTransport(this)
        val failure = ServerMessage.Stage(
            stage = "source-geometry",
            detail = "The captured app was minimized.",
            remediation = "Restore it and reconnect.",
            fatal = true,
        )

        assertFalse(transport.dispatchControl(failure))
        val event = transport.events.first() as LanTransport.TransportEvent.Failed
        assertTrue(event.terminal)
        assertEquals(failure.stage, event.stage)
        assertEquals(failure.detail, event.detail)
        assertEquals(failure.remediation, event.remediation)
    }

    @Test
    fun preparation_status_keeps_the_reader_running_and_reaches_control() = runBlocking {
        val transport = LanTransport(this)
        val received = async(start = CoroutineStart.UNDISPATCHED) { transport.control.first() }
        val stage = ServerMessage.Stage("Preparing your display", "Setting up the workspace.")

        assertTrue(transport.dispatchControl(stage))
        assertEquals(stage, received.await())
    }
}
