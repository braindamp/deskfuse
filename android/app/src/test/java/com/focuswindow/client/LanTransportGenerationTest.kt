package com.focuswindow.client

import com.focuswindow.client.protocol.ServerMessage
import com.focuswindow.client.transport.LanTransport
import java.net.ServerSocket
import java.net.Socket
import java.util.concurrent.atomic.AtomicBoolean
import kotlinx.coroutines.runBlocking
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Assert.assertEquals
import kotlinx.coroutines.CoroutineStart
import kotlinx.coroutines.async
import kotlinx.coroutines.flow.first
import org.junit.Test
import org.robolectric.util.ReflectionHelpers

/**
 * A Wi-Fi reader can receive a terminal stage AFTER cellular has established
 * a fresh authenticated socket. The obsolete reader cannot tear the new one
 * down (or drop the phone speaker a second time).
 */
class LanTransportGenerationTest {
    @Test
    fun queued_envelopes_keep_their_generation_even_when_target_versions_repeat() = runBlocking {
        val transport = LanTransport(this)
        ReflectionHelpers.setField(transport, "socketGeneration", 44L)
        ReflectionHelpers.getField<AtomicBoolean>(transport, "running").set(true)
        val waiting = async(start = CoroutineStart.UNDISPATCHED) { transport.control.first() }
        transport.dispatchControl(ServerMessage.Stage("capture", "ready"), 44L)
        val old = waiting.await()
        assertEquals(44L, old.connectionGeneration)
        assertTrue(transport.isCurrentGeneration(old.connectionGeneration))
        ReflectionHelpers.setField(transport, "socketGeneration", 45L)
        assertFalse(transport.isCurrentGeneration(old.connectionGeneration))
        assertFalse(transport.isLatestGeneration(LanTransport.TransportEvent.Ready(44L).generation))
        assertTrue(transport.isCurrentGeneration(45L))
        transport.disconnectNow()
    }

    @Test
    fun stale_control_from_retired_socket_cannot_close_current_session() = runBlocking {
        ServerSocket(0).use { listener ->
            val client = Socket("127.0.0.1", listener.localPort)
            val server = listener.accept()
            try {
                val transport = LanTransport(this)
                ReflectionHelpers.setField(transport, "socket", client)
                ReflectionHelpers.setField(transport, "socketGeneration", 44L)
                ReflectionHelpers.getField<AtomicBoolean>(transport, "running").set(true)

                val lateFailure = ServerMessage.Stage(
                    stage = "speakers",
                    detail = "old reader disconnected",
                    fatal = true,
                )
                assertFalse(transport.dispatchControl(lateFailure, generation = 43L))
                assertTrue(transport.isRunning)
                assertFalse(client.isClosed)

                transport.disconnectNow()
                assertFalse(transport.isRunning)
                assertTrue(client.isClosed)
            } finally {
                client.close()
                server.close()
            }
        }
    }
}
