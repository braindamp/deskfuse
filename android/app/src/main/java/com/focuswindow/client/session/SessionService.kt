package com.focuswindow.client.session

import android.app.Notification
import android.app.NotificationChannel
import android.app.NotificationManager
import android.app.Service
import android.content.Context
import android.content.Intent
import android.os.Build
import android.os.IBinder
import android.os.PowerManager
import androidx.core.app.NotificationCompat
import com.focuswindow.client.MainActivity
import com.focuswindow.client.R

/**
 * FR-REF-05 — a session must survive the user glancing at a notification, the
 * screen turning off, or a Wi-Fi handover, and then reattach to the SAME
 * document rather than opening a second copy.
 *
 * FR-REF-03/04 — the wake lock is PARTIAL only. We keep the CPU alive to hold
 * the socket and the decoder, but we never force the display on from here: the
 * activity owns the screen policy so the user's own timeout still applies.
 */
class SessionService : Service() {

    private var wakeLock: PowerManager.WakeLock? = null

    override fun onBind(intent: Intent?): IBinder? = null

    override fun onStartCommand(intent: Intent?, flags: Int, startId: Int): Int {
        when (intent?.action) {
            ACTION_START -> start(
                hostName = intent.getStringExtra(EXTRA_HOST) ?: "PC",
                tier = intent.getStringExtra(EXTRA_TIER) ?: "",
            )
            ACTION_STOP -> stopSelf()
        }
        // The session is explicitly user-scoped: if the system kills us, we do
        // NOT silently resurrect and reconnect to someone's PC.
        return START_NOT_STICKY
    }

    private fun start(hostName: String, tier: String) {
        createChannel()
        startForeground(NOTIFICATION_ID, buildNotification(hostName, tier))

        val power = getSystemService(Context.POWER_SERVICE) as PowerManager
        wakeLock = power.newWakeLock(PowerManager.PARTIAL_WAKE_LOCK, WAKE_TAG).apply {
            setReferenceCounted(false)
            acquire(MAX_SESSION_MS)
        }
    }

    /**
     * The notification is honest about what is happening: which PC, which tier,
     * and a one-tap end. No ambiguity about whether a stream is live.
     */
    private fun buildNotification(hostName: String, tier: String): Notification {
        val open = android.app.PendingIntent.getActivity(
            this,
            0,
            Intent(this, MainActivity::class.java),
            android.app.PendingIntent.FLAG_IMMUTABLE,
        )
        val stop = android.app.PendingIntent.getService(
            this,
            1,
            Intent(this, SessionService::class.java).setAction(ACTION_STOP),
            android.app.PendingIntent.FLAG_IMMUTABLE,
        )

        return NotificationCompat.Builder(this, CHANNEL_ID)
            .setContentTitle("Connected to $hostName")
            .setContentText(tier.ifBlank { "Secure Windows workspace session" })
            .setSmallIcon(R.drawable.ic_session)
            .setOngoing(true)
            .setCategory(NotificationCompat.CATEGORY_SERVICE)
            .setPriority(NotificationCompat.PRIORITY_LOW)
            .setContentIntent(open)
            .addAction(0, "End session", stop)
            .build()
    }

    private fun createChannel() {
        if (Build.VERSION.SDK_INT < Build.VERSION_CODES.O) return
        val manager = getSystemService(NotificationManager::class.java)
        if (manager.getNotificationChannel(CHANNEL_ID) != null) return
        manager.createNotificationChannel(
            NotificationChannel(
                CHANNEL_ID,
                "Active session",
                NotificationManager.IMPORTANCE_LOW,
            ).apply {
                description = "Shown while your PC is streaming to this device."
                setShowBadge(false)
            },
        )
    }

    override fun onDestroy() {
        wakeLock?.let { if (it.isHeld) it.release() }
        wakeLock = null
        super.onDestroy()
    }

    companion object {
        const val ACTION_START = "com.focuswindow.client.SESSION_START"
        const val ACTION_STOP = "com.focuswindow.client.SESSION_STOP"
        const val EXTRA_HOST = "host"
        const val EXTRA_TIER = "tier"

        private const val CHANNEL_ID = "session"
        private const val NOTIFICATION_ID = 1001
        private const val WAKE_TAG = "focuswindow:session"

        /** Hard ceiling so a forgotten session cannot hold the CPU forever. */
        private const val MAX_SESSION_MS = 8L * 60 * 60 * 1000

        fun start(context: Context, hostName: String, tier: String) {
            context.startForegroundService(
                Intent(context, SessionService::class.java)
                    .setAction(ACTION_START)
                    .putExtra(EXTRA_HOST, hostName)
                    .putExtra(EXTRA_TIER, tier),
            )
        }

        fun stop(context: Context) {
            context.startService(
                Intent(context, SessionService::class.java).setAction(ACTION_STOP),
            )
        }
    }
}
