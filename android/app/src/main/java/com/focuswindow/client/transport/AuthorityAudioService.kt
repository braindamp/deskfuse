package com.focuswindow.client.transport

import android.app.Notification
import android.app.NotificationChannel
import android.app.NotificationManager
import android.app.PendingIntent
import android.app.Service
import android.content.Context
import android.content.Intent
import android.net.Uri
import android.os.Handler
import android.os.IBinder
import android.os.Looper
import com.focuswindow.client.BuildConfig
import java.util.UUID
import java.util.concurrent.Executors

/** Gated first delivery. A run owns its authority, route, socket and renderer until rundown. */
class AuthorityAudioService : Service() {
    private val main = Handler(Looper.getMainLooper())
    private var active: Delivery? = null
    private var lastStart = 0

    override fun onBind(intent: Intent?): IBinder? = null

    override fun onStartCommand(intent: Intent?, flags: Int, startId: Int): Int {
        lastStart = startId
        val generation = intent?.getStringExtra("generation")
        when {
            !BuildConfig.DEBUG || intent == null || generation == null -> if (active == null) stopSelf(startId)
            intent.action == STOP -> {
                active?.takeIf { it.generation == generation }?.let {
                    it.run.stop()
                    it.report(AuthorityAudioState.STOPPING)
                    notify(it, "Stopping")
                }
                if (active == null) stopSelf(startId)
            }
            intent.action == START -> {
                val authority = IAuthorityAudioEnrollment.Stub.asInterface(intent.extras?.getBinder("authority"))
                if (active != null) {
                    if (active?.generation != generation) runCatching {
                        authority?.status(generation, AuthorityAudioState.FAILED.ordinal)
                    }
                    return START_NOT_STICKY
                }
                try {
                    require(UUID.fromString(generation).toString() == generation)
                    val target = AudioCellularTarget(checkNotNull(intent.getStringExtra("host")), intent.getIntExtra("port", 0))
                    val delivery = Delivery(generation, checkNotNull(authority), target, intent.getBooleanExtra("cellular", false))
                    startForeground(NOTIFICATION, notification(delivery, "Connecting"))
                    active = delivery
                    worker.execute { delivery.execute() }
                } catch (_: Exception) {
                    active = null
                    runCatching { authority?.status(generation, AuthorityAudioState.FAILED.ordinal) }
                    stopSelf(startId)
                }
            }
            active == null -> stopSelf(startId)
        }
        return START_NOT_STICKY
    }

    override fun onDestroy() {
        active?.run?.stop()
        active = null
        super.onDestroy()
    }

    private fun notify(delivery: Delivery, text: String) {
        getSystemService(NotificationManager::class.java)?.notify(NOTIFICATION, notification(delivery, text))
    }

    private fun notification(delivery: Delivery, text: String): Notification {
        getSystemService(NotificationManager::class.java)?.createNotificationChannel(
            NotificationChannel(CHANNEL, "Trusted PC speaker", NotificationManager.IMPORTANCE_LOW),
        )
        val stop = PendingIntent.getService(this, 0, stopIntent(this, delivery.generation),
            PendingIntent.FLAG_IMMUTABLE or PendingIntent.FLAG_UPDATE_CURRENT)
        return Notification.Builder(this, CHANNEL)
            .setSmallIcon(android.R.drawable.ic_media_play)
            .setContentTitle("DeskFuse trusted speaker")
            .setContentText(text)
            .setOngoing(true)
            .addAction(Notification.Action.Builder(null, "Stop", stop).build())
            .build()
    }

    private inner class Delivery(
        val generation: String,
        private val authority: IAuthorityAudioEnrollment,
        private val target: AudioCellularTarget,
        private val cellular: Boolean,
    ) {
        val run = AuthorityAudioRun()
        private val died = IBinder.DeathRecipient { run.stop() }
        private val token = object : IAuthorityAudioRun.Stub() {
            override fun stop() { run.stop() }
        }

        fun report(state: AuthorityAudioState) {
            runCatching { authority.status(generation, state.ordinal) }
        }

        fun execute() {
            val binder = authority.asBinder()
            var linked = false
            var terminal = AuthorityAudioState.FAILED
            try {
                run.requireLive()
                binder.linkToDeath(died, 0)
                linked = true
                val evidence = authority.open(generation, token)
                val current = { authority.checkCurrent(generation); Unit }
                val prepared = TrustedPreparedEnrollment.parseStoreRecord(checkNotNull(evidence.getByteArray("prepared")))
                    .completed(current)
                val enrollment = TrustedCommittedEnrollment.validate(prepared,
                    checkNotNull(evidence.getByteArray("receipt")), current)
                require(BrokerScope.SPEAKER in enrollment.transcript().proposal.scopes)
                val audio = AuthorityPhoneAudio(this@AuthorityAudioService, enrollment, BrokerDeviceSigner(AndroidBrokerSigningKeys()))
                run.attach(audio::stop)
                val routes = AuthorityAudioRoutes(this@AuthorityAudioService)
                val ready = Runnable {
                    main.post {
                        if (active === this && !run.isStopped) {
                            report(AuthorityAudioState.PLAYING)
                            notify(this, "Playing")
                        }
                    }
                }
                fun play(address: String, handle: Long, requireRoute: () -> Unit,
                    beforeAcquire: (ByteArray) -> Unit, deliveryAccepted: () -> Unit) {
                    // Bounded development profile. Production tuning requires device qualification.
                    audio.run(address, handle, setupMs = 15_000, rpcMs = 3_000, idleMs = 5_000, clockMarginPpm = 500,
                        bufferBursts = 2, sharing = 0, usage = 0, reserveFrames = 1440,
                        requireRoute = requireRoute, beforeAcquire = beforeAcquire, deliveryAccepted = deliveryAccepted,
                        ready = ready)
                }
                if (cellular) routes.cellular(target, run, ::play)
                else routes.lan(target, run) { address, handle, requireRoute ->
                    play(address, handle, requireRoute, {}, {})
                }
                terminal = AuthorityAudioState.IDLE
            } catch (_: Exception) {
                if (run.isStopped) terminal = AuthorityAudioState.IDLE
            } finally {
                if (linked) runCatching { binder.unlinkToDeath(died, 0) }
                main.post {
                    if (active === this) {
                        active = null
                        report(terminal)
                        stopForeground(STOP_FOREGROUND_REMOVE)
                        stopSelf(lastStart)
                    } else report(terminal)
                }
            }
        }
    }

    internal companion object {
        const val START = "com.focuswindow.client.authority.START_AUDIO"
        private const val STOP = "com.focuswindow.client.authority.STOP_AUDIO"
        private const val CHANNEL = "trusted_pc_speaker"
        private const val NOTIFICATION = 4214
        private val worker = Executors.newSingleThreadExecutor()

        fun stopIntent(context: Context, generation: String) =
            Intent(context, AuthorityAudioService::class.java).setAction(STOP)
                .setData(Uri.fromParts("deskfuse-audio", generation, null)).putExtra("generation", generation)
    }
}
