package com.focuswindow.client.transport

import org.junit.Assert.*
import org.junit.Test
import java.util.concurrent.CountDownLatch
import java.util.concurrent.TimeUnit
import java.util.concurrent.atomic.AtomicReference

class AudioCellularCustodyTest {
    private class Fixture {
        var now = 0L
        val owner = AudioCellularCustody(100, 200) { now }
        val request = ByteArray(32) { 1 }
        fun ready(): AudioCellularCustody.Work {
            owner.registrationFinished(true); assertTrue(owner.available(11))
            owner.observation(11, true); return owner.beginWork(11)
        }
    }
    private fun reject(action: () -> Unit) { assertTrue(runCatching(action).isFailure) }

    @Test fun callback_and_registration_completion_are_owned_even_when_cancelled_during_requestNetwork() {
        val f = Fixture(); assertTrue(f.owner.available(11)); f.owner.observation(11, true)
        val work = f.owner.beginWork(11)
        f.owner.cancelPreparation()
        assertNull(f.owner.beginRelease())
        f.owner.workCompleted(work)
        assertNull(f.owner.beginRelease()) // actual requestNetwork call has not returned
        f.owner.registrationFinished(true)
        assertEquals(true, f.owner.beginRelease()); assertNull(f.owner.beginRelease())
        f.owner.releaseCompleted(true)
        assertEquals(AudioCellularCustody.Phase.RELEASED, f.owner.phase())
        assertFalse(f.owner.available(11)); assertFalse(f.owner.available(22))
    }
    @Test fun acquisition_timeout_and_exact_warm_boundary_do_not_reanchor() {
        val f = Fixture(); f.owner.registrationFinished(true)
        f.now = 100_000_000; assertEquals(AudioCellularCustody.Phase.RETIRING, f.owner.phase())
        assertFalse(f.owner.available(11)); assertEquals(true, f.owner.beginRelease())
        val w = Fixture(); val work = w.ready(); val until = w.owner.deadline()
        w.now = 199_999_999; assertTrue(w.owner.available(11)); assertEquals(until, w.owner.deadline())
        w.now++; reject { w.owner.pin(work, w.request) }
        assertEquals(AudioCellularCustody.Phase.RETIRING, w.owner.phase()); assertNull(w.owner.beginRelease())
        w.owner.workCompleted(work); assertEquals(true, w.owner.beginRelease())
    }
    @Test fun pin_removes_warm_timer_and_reconciliation_retains_exact_request_and_lease() {
        val f = Fixture(); val work = f.ready(); val original = f.request.copyOf()
        val pin = f.owner.pin(work, f.request); f.request.fill(2)
        assertArrayEquals(original, pin.requestId()); assertNull(f.owner.deadline())
        f.now = 900_000_000; assertEquals(AudioCellularCustody.Phase.PINNED, f.owner.phase())
        f.owner.cancelPreparation(); f.owner.lost(11); f.owner.workCompleted(work)
        assertEquals(AudioCellularCustody.Phase.RECONCILING, f.owner.phase()); assertNull(f.owner.beginRelease())
        f.owner.promote(pin) // confirmed commit can transfer custody even after transport loss
        assertEquals(AudioCellularCustody.Phase.ACTIVE, f.owner.phase()); assertNull(f.owner.beginRelease())
        f.owner.cancelPreparation(); assertEquals(AudioCellularCustody.Phase.ACTIVE, f.owner.phase())
        f.owner.retire(); assertEquals(true, f.owner.beginRelease())
    }
    @Test fun one_actual_dependent_job_and_exact_pin_identity() {
        val f = Fixture(); val work = f.ready(); reject { f.owner.beginWork(11) }
        val pin = f.owner.pin(work, f.request)
        assertSame(pin, f.owner.pin(work, f.request.copyOf()))
        reject { f.owner.pin(work, ByteArray(32) { 3 }) }
        reject { f.owner.promote(AudioCellularCustody.Pin(f.request)) }
        f.owner.workCompleted(work); reject { f.owner.requireWork(work, 11) }
        reject { f.owner.beginWork(11) }; assertNull(f.owner.beginRelease())
    }
    @Test fun new_network_callback_never_rebinds_a_candidate_or_active_socket() {
        for (committed in listOf(false, true)) {
            val f = Fixture(); val work = f.ready(); val pin = f.owner.pin(work, f.request)
            if (committed) f.owner.promote(pin)
            assertFalse(f.owner.available(22)); reject { f.owner.requireWork(work, 11) }
            f.owner.observation(11, true); reject { f.owner.requireWork(work, 11) }
            reject { f.owner.beginWork(22) }; f.owner.lost(22)
            assertEquals(if (committed) AudioCellularCustody.Phase.ACTIVE else AudioCellularCustody.Phase.RECONCILING, f.owner.phase())
            assertNull(f.owner.beginRelease())
            f.owner.retire(); f.owner.workCompleted(work); assertEquals(true, f.owner.beginRelease())
        }
    }
    @Test fun stale_or_unusable_observations_never_open_setup_or_clear_terminal_denial() {
        val f = Fixture(); f.owner.registrationFinished(true); f.owner.available(11)
        reject { f.owner.beginWork(11) }; f.owner.observation(22, true); reject { f.owner.beginWork(11) }
        f.owner.observation(11, true); val work = f.owner.beginWork(11)
        f.owner.retire(); f.owner.observation(11, true); reject { f.owner.requireWork(work, 11) }
        reject { f.owner.pin(work, f.request) }; f.owner.workCompleted(work)
        assertEquals(true, f.owner.beginRelease()); f.owner.releaseCompleted(false)
        assertEquals(AudioCellularCustody.Phase.QUARANTINED, f.owner.phase()); assertNull(f.owner.beginRelease())
        assertFalse(f.owner.available(11))
    }
    @Test fun unavailable_before_request_call_returns_does_not_unregister_an_automatically_released_request() {
        val f = Fixture(); f.owner.unavailable(); assertNull(f.owner.beginRelease())
        f.owner.registrationFinished(true); assertEquals(false, f.owner.beginRelease())
        f.owner.releaseCompleted(true); assertEquals(AudioCellularCustody.Phase.RELEASED, f.owner.phase())
    }
    @Test fun registration_failure_and_clock_regression_fail_closed_without_losing_actual_work() {
        val failed = Fixture(); failed.owner.registrationFinished(false)
        assertEquals(false, failed.owner.beginRelease()); failed.owner.releaseCompleted(true)
        val f = Fixture(); f.now = 10; val work = f.ready(); f.now = 9
        reject { f.owner.requireWork(work, 11) }; assertNull(f.owner.beginRelease())
        f.owner.workCompleted(work); assertEquals(true, f.owner.beginRelease())
    }
    @Test fun policy_or_network_denial_cannot_be_cleared_by_a_late_positive_snapshot() {
        val f = Fixture(); val work = f.ready(); val pin = f.owner.pin(work, f.request)
        f.owner.observation(11, false); f.owner.observation(11, true)
        reject { f.owner.requireWork(work, 11) }
        f.owner.promote(pin) // Retain confirmed target custody; still no permission to use its failed socket.
        f.owner.observation(11, true); reject { f.owner.requireWork(work, 11) }
        assertNull(f.owner.beginRelease()); f.owner.retire(); f.owner.workCompleted(work)
        assertEquals(true, f.owner.beginRelease())
    }
    @Test fun retired_work_blocks_release_until_the_actual_blocked_thread_returns() {
        val f = Fixture(); val work = f.ready()
        val entered = CountDownLatch(1); val finish = CountDownLatch(1); val completed = CountDownLatch(1)
        val failure = AtomicReference<Throwable?>()
        val thread = Thread {
            try {
                entered.countDown()
                var returned = false
                while (!returned) {
                    try { check(finish.await(5, TimeUnit.SECONDS)); returned = true }
                    catch (_: InterruptedException) { /* Simulated real JNI/DNS work still owns resources. */ }
                }
            }
            catch (error: Throwable) { failure.set(error) }
            finally { f.owner.workCompleted(work); completed.countDown() }
        }
        thread.start()
        try {
            assertTrue(entered.await(5, TimeUnit.SECONDS)); f.owner.retire(); thread.interrupt()
            assertNull(f.owner.beginRelease()); assertEquals(1L, completed.count)
        } finally { finish.countDown(); thread.join(5000) }
        assertFalse(thread.isAlive); assertTrue(completed.await(1, TimeUnit.SECONDS))
        assertEquals(true, f.owner.beginRelease())
        assertNull(failure.get())
    }
    @Test fun policy_requires_explicit_mobile_consent_route_and_background_permission() {
        val p = AudioCellularAccess(true, false, false, true, AudioCellularAccess.Route.DIRECT_ALLOWED)
        assertTrue(p.admitted()); assertFalse(p.copy(mobileDataConsent = false).admitted())
        assertFalse(p.copy(backgroundDataAllowed = false).admitted())
        assertFalse(p.copy(route = AudioCellularAccess.Route.UNCONFIRMED).admitted())
        assertFalse(p.copy(route = AudioCellularAccess.Route.VPN_REQUIRED).admitted())
    }
}
