package com.focuswindow.client.transport

import android.app.Service
import android.content.ComponentName
import android.content.Intent
import android.os.Bundle
import android.os.IBinder
import android.os.Looper
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test
import org.junit.runner.RunWith
import org.robolectric.Robolectric
import org.robolectric.RobolectricTestRunner
import org.robolectric.RuntimeEnvironment
import org.robolectric.Shadows.shadowOf
import org.robolectric.annotation.Config
import java.util.UUID
import java.util.concurrent.CountDownLatch
import java.util.concurrent.TimeUnit
import java.util.concurrent.atomic.AtomicReference

@RunWith(RobolectricTestRunner::class)
@Config(sdk = [34])
class AuthorityAudioServiceTest {
    @Test fun revocation_during_enrollment_setup_fences_the_run() {
        exercise(Trigger.REVOKE, AuthorityAudioState.IDLE)
    }

    @Test fun authority_process_death_during_setup_fences_the_run() {
        exercise(Trigger.DEATH, AuthorityAudioState.IDLE)
    }

    @Test fun explicit_stop_during_setup_fences_the_run() {
        exercise(Trigger.STOP, AuthorityAudioState.IDLE)
    }

    @Test fun stale_stop_cannot_cancel_another_generation() {
        exercise(Trigger.STALE_STOP, AuthorityAudioState.FAILED)
    }

    private enum class Trigger { REVOKE, DEATH, STOP, STALE_STOP }

    private fun exercise(trigger: Trigger, expected: AuthorityAudioState) {
        val context = RuntimeEnvironment.getApplication()
        val component = ComponentName(context, AuthorityAudioService::class.java)
        @Suppress("DEPRECATION")
        val manifest = context.packageManager.getServiceInfo(component, 0)
        assertFalse(manifest.exported)
        assertEquals(context.packageName + ":audio", manifest.processName)
        val controller = Robolectric.buildService(AuthorityAudioService::class.java).create()
        val service = controller.get()
        val generation = UUID.randomUUID().toString()
        val opened = CountDownLatch(1)
        val release = CountDownLatch(1)
        val token = AtomicReference<IAuthorityAudioRun>()
        val death = AtomicReference<IBinder.DeathRecipient>()
        val terminal = AtomicReference<AuthorityAudioState>()
        val authority = object : IAuthorityAudioEnrollment.Stub() {
            override fun linkToDeath(recipient: IBinder.DeathRecipient, flags: Int) {
                death.set(recipient)
            }
            override fun open(generation: String, run: IAuthorityAudioRun): Bundle {
                token.set(run)
                opened.countDown()
                check(release.await(5, TimeUnit.SECONDS))
                return Bundle() // No valid enrollment, so this test cannot reach network or native audio.
            }
            override fun checkCurrent(generation: String) = Unit
            override fun status(generation: String, state: Int) {
                val value = AuthorityAudioState.entries[state]
                if (value == AuthorityAudioState.IDLE || value == AuthorityAudioState.FAILED) terminal.set(value)
            }
        }
        try {
            val intent = Intent(context, AuthorityAudioService::class.java).setAction(AuthorityAudioService.START)
                .putExtra("generation", generation).putExtra("host", "192.0.2.3").putExtra("port", 44002)
                .putExtras(Bundle().apply { putBinder("authority", authority) })
            assertEquals(Service.START_NOT_STICKY, service.onStartCommand(intent, 0, 1))
            assertTrue(opened.await(5, TimeUnit.SECONDS))
            when (trigger) {
                Trigger.REVOKE -> token.get().stop()
                Trigger.DEATH -> death.get().binderDied()
                Trigger.STOP -> service.onStartCommand(AuthorityAudioService.stopIntent(context, generation), 0, 2)
                Trigger.STALE_STOP -> service.onStartCommand(
                    AuthorityAudioService.stopIntent(context, UUID.randomUUID().toString()), 0, 2)
            }
            release.countDown()
            val deadline = System.nanoTime() + TimeUnit.SECONDS.toNanos(5)
            while (terminal.get() == null && System.nanoTime() < deadline) {
                shadowOf(Looper.getMainLooper()).idle()
                if (terminal.get() == null) Thread.sleep(10)
            }
            assertEquals(expected, terminal.get())
        } finally {
            release.countDown()
            controller.destroy()
        }
    }
}
