package com.focuswindow.client.transport

import android.content.Context
import android.content.Intent
import android.os.Binder
import android.os.Bundle
import android.os.Handler
import android.os.IBinder
import android.os.Looper
import android.os.Process
import com.focuswindow.client.BuildConfig
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.asStateFlow
import java.util.UUID

internal enum class AuthorityAudioState { IDLE, STARTING, PLAYING, STOPPING, FAILED }

/** App-scoped control. The capability carries signed evidence for one run, never a signer. */
internal object AuthorityAudioControl {
    private val gate = Any()
    private val main = Handler(Looper.getMainLooper())
    private val status = MutableStateFlow(AuthorityAudioState.IDLE)
    val state = status.asStateFlow()
    private var active: Grant? = null

    fun start(context: Context, enrollment: TrustedCommittedEnrollment, host: String, port: Int, cellular: Boolean) {
        check(BuildConfig.DEBUG)
        require(BrokerScope.SPEAKER in enrollment.transcript().proposal.scopes)
        AudioCellularTarget(host, port)
        val grant = synchronized(gate) {
            check(active == null)
            Grant(enrollment).also { active = it; status.value = AuthorityAudioState.STARTING }
        }
        try {
            val intent = Intent(context, AuthorityAudioService::class.java).setAction(AuthorityAudioService.START)
                .putExtra("generation", grant.generation).putExtra("host", host)
                .putExtra("port", port).putExtra("cellular", cellular)
                .putExtras(Bundle().apply { putBinder("authority", grant) })
            context.startForegroundService(intent)
            main.postDelayed({
                synchronized(gate) {
                    if (active === grant && !grant.opened) grant.finish(AuthorityAudioState.FAILED)
                }
            }, 20_000)
        } catch (failure: Exception) {
            grant.finish(AuthorityAudioState.FAILED)
            throw failure
        }
    }

    fun stop(context: Context) {
        val generation = synchronized(gate) {
            val grant = active ?: return
            status.value = AuthorityAudioState.STOPPING
            grant.generation
        }
        context.startService(AuthorityAudioService.stopIntent(context, generation))
    }

    private class Grant(private val enrollment: TrustedCommittedEnrollment) : IAuthorityAudioEnrollment.Stub() {
        val generation = UUID.randomUUID().toString()
        var opened = false
            private set
        private var watch: AutoCloseable? = null
        private var peer: IBinder? = null
        private val died = IBinder.DeathRecipient { finish(AuthorityAudioState.FAILED) }

        override fun open(generation: String, run: IAuthorityAudioRun): Bundle = synchronized(gate) {
            checkCurrent(generation)
            check(!opened && status.value != AuthorityAudioState.STOPPING)
            val binder = run.asBinder()
            binder.linkToDeath(died, 0)
            peer = binder
            try {
                watch = TrustedEnrollmentRepository.watchAudio(enrollment) {
                    // Synchronous local fence in :audio. It does not join the worker.
                    try { run.stop() }
                    catch (_: Exception) { finish(AuthorityAudioState.FAILED) }
                }
                val (prepared, receipt) = enrollment.evidence()
                opened = true
                Bundle().apply { putByteArray("prepared", prepared); putByteArray("receipt", receipt) }
            } catch (failure: Exception) {
                finish(AuthorityAudioState.FAILED)
                throw failure
            }
        }

        override fun checkCurrent(generation: String) = synchronized(gate) {
            check(Binder.getCallingUid() == Process.myUid())
            check(active === this && generation == this.generation)
            enrollment.requireCurrent()
        }

        override fun status(generation: String, state: Int) = synchronized(gate) {
            check(Binder.getCallingUid() == Process.myUid())
            if (active !== this || generation != this.generation) return@synchronized
            when (AuthorityAudioState.entries.getOrNull(state)) {
                AuthorityAudioState.IDLE -> finish(AuthorityAudioState.IDLE)
                AuthorityAudioState.FAILED -> finish(AuthorityAudioState.FAILED)
                AuthorityAudioState.PLAYING -> if (status.value == AuthorityAudioState.STARTING) {
                    status.value = AuthorityAudioState.PLAYING
                } else Unit
                AuthorityAudioState.STOPPING -> status.value = AuthorityAudioState.STOPPING
                else -> Unit
            }
        }

        fun finish(state: AuthorityAudioState): Unit = synchronized(gate) {
            if (active !== this) return@synchronized
            watch?.close()
            watch = null
            peer?.let { runCatching { it.unlinkToDeath(died, 0) } }
            peer = null
            active = null
            status.value = state
        }
    }
}
