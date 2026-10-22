package com.focuswindow.client

import com.focuswindow.client.protocol.ServerMessage
import com.focuswindow.client.transport.LanTransport
import java.net.ServerSocket
import java.net.Socket
import java.util.concurrent.atomic.AtomicBoolean
import kotlinx.coroutines.runBlocking
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test
import org.robolectric.util.ReflectionHelpers

/**
 * A Wi-Fi reader can receive a terminal stage AFTER cellular has established
 * a fresh authenticated socket. The obsolete reader cannot tear the new one
 * down (or drop the phone speaker a second time).
 */
class LanTransportGenerationTest {
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
