package com.focuswindow.client.transport

import java.util.concurrent.TimeUnit
import org.junit.Assert.assertEquals
import org.junit.Assert.assertTrue
import org.junit.Test

class AuthorityAudioRunTest {
    @Test fun stop_during_acquisition_wakes_the_worker_and_fences_a_late_native_owner() {
        val run = AuthorityAudioRun()
        run.stop()
        assertTrue(run.wake.await(0, TimeUnit.MILLISECONDS))
        var fences = 0
        run.attach { fences++ }
        assertEquals(1, fences)
        assertTrue(runCatching { run.requireLive() }.isFailure)
    }

    @Test fun stop_or_authority_loss_fences_playback_once_and_cannot_affect_a_later_run() {
        val first = AuthorityAudioRun()
        val second = AuthorityAudioRun()
        var firstFences = 0
        var secondFences = 0
        first.attach { firstFences++ }
        second.attach { secondFences++ }
        first.stop()
        first.stop()
        assertEquals(1, firstFences)
        assertEquals(0, secondFences)
        second.requireLive()
    }

    @Test fun late_route_loss_cannot_stop_a_released_native_candidate() {
        val candidate = AuthorityAudioRun()
        var fences = 0
        candidate.attach { fences++ }
        candidate.detachFence()
        candidate.stop()
        assertEquals(0, fences)
        assertTrue(candidate.isStopped)
    }
}
