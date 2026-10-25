package com.focuswindow.client.media

import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.CoroutineStart
import kotlinx.coroutines.ExperimentalCoroutinesApi
import kotlinx.coroutines.async
import kotlinx.coroutines.test.StandardTestDispatcher
import kotlinx.coroutines.test.runCurrent
import kotlinx.coroutines.test.runTest
import org.junit.Assert.*
import org.junit.Test

@OptIn(ExperimentalCoroutinesApi::class)
class NativeOwnerCoordinatorTest {
    @Test fun replacement_joins_queued_old_native_entry_before_resetting_stop() = runTest {
        val trace = mutableListOf<String>()
        var stopped = false
        val dispatcher = StandardTestDispatcher(testScheduler)
        val owners = NativeOwnerCoordinator(CoroutineScope(backgroundScope.coroutineContext),
            { trace += "prepare"; stopped = false; true },
            { trace += "stop"; stopped = true }, dispatcher)
        val first = owners.claim(owners.register())
        owners.start(first, "library", { trace += "old-run:$stopped"; 0 }, { trace += "old-completion" })
        val nextClient = owners.register()
        val replacing = async(start = CoroutineStart.UNDISPATCHED) { owners.claim(nextClient) }
        assertFalse(replacing.isCompleted)
        assertEquals(listOf("prepare", "stop"), trace)
        runCurrent()
        val second = replacing.await()
        assertTrue(trace.contains("old-run:true"))
        assertFalse(trace.contains("old-completion"))
        owners.start(second, "library", { trace += "new-run:$stopped"; 0 }, {})
        owners.retire(first) // a retired controller cannot stop its successor
        runCurrent()
        assertTrue(trace.contains("new-run:false"))
        assertEquals(1, trace.count { it == "stop" })
        owners.retire(second)
    }

    @Test fun stale_controller_cannot_claim_or_start_over_a_successor() = runTest {
        val owners = NativeOwnerCoordinator(backgroundScope, { true }, {}, StandardTestDispatcher(testScheduler))
        val oldClient = owners.register()
        val old = owners.claim(oldClient)
        val current = owners.claim(owners.register())
        assertFalse(owners.isCurrent(old))
        assertTrue(owners.isCurrent(current))
        try { owners.claim(oldClient); fail("old controller reclaimed owner") } catch (_: IllegalStateException) { }
        try { owners.start(old, "library", { fail("old native entry"); 0 }, {}); fail("old start accepted") }
        catch (_: IllegalStateException) { }
        owners.retire(old)
        assertTrue(owners.isCurrent(current))
        owners.retire(current)
    }
}
