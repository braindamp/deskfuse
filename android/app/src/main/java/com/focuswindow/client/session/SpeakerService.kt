package com.focuswindow.client.session

import android.app.Notification
import android.app.NotificationChannel
import android.app.NotificationManager
import android.app.Service
import android.content.Context
import android.content.Intent
import android.os.Build
import android.os.IBinder
import androidx.core.app.NotificationCompat
import com.focuswindow.client.R
import com.focuswindow.client.media.PcAudioOutput
import com.focuswindow.client.profile.PhoneProfile
import com.focuswindow.client.transport.PairingPayload
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.Job
import kotlinx.coroutines.cancel
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.launch

/**
 * Keeps the phone speaker session alive while the phone plays the PC's
 * sound.
 *
 * This is sustained media playback, so it is declared as a media playback
 * foreground service. The data sync classification belongs to the workspace
 * session and is not reused here: playing received sound is a different job
 * with a different Android lifecycle.
 */
class SpeakerService : Service() {

    private val scope = CoroutineScope(SupervisorJob() + Dispatchers.Default)
    private var controller: SpeakerController? = null
    private var stateJob: Job? = null

    override fun onBind(intent: Intent?): IBinder? = null

    override fun onStartCommand(intent: Intent?, flags: Int, startId: Int): Int {
        when (intent?.action) {
            ACTION_START -> start(intent)
            ACTION_STOP -> stopSelf()
        }
        // The user ends the session explicitly. A killed service does not
        // silently reconnect to someone's PC.
        return START_NOT_STICKY
    }

    private fun start(intent: Intent) {
        createChannel()
        startForeground(NOTIFICATION_ID, buildNotification("Connecting to the PC"))

        // The phone already holds the enrollment for this PC; the speaker
        // session reuses it and never asks for a second credential.
        val payload = runCatching {
            com.focuswindow.client.transport.PairingRepository(applicationContext).last()
        }.getOrNull()
        // The profile comes from the app when the user starts the session
        // from the interface, and from the app's own stored identity
        // otherwise. Either way it is the same phone.
        val profile = intent.getStringExtra(EXTRA_PROFILE)?.let { encoded ->
            runCatching {
                json.decodeFromString(PhoneProfile.serializer(), encoded)
            }.getOrNull()
        } ?: com.focuswindow.client.profile.ProfileProvider(applicationContext)
            .currentWithoutDisplay()
        if (payload == null) {
            stopSelf()
            return
        }
        // Repeated ACTION_START must never orphan the previous authenticated
        // socket, capture route or StateFlow collector.
        controller?.stop()
        stateJob?.cancel()
        stateJob = null
        controller = null
        running = true
        val output = PcAudioOutput(applicationContext)
        val speaker = SpeakerController(scope, clientVersion(), output)
        controller = speaker
        stateJob = scope.launch {
            speaker.state.collect { current ->
                state.value = current
                notificationManager()?.notify(
                    NOTIFICATION_ID,
                    buildNotification(describe(current)),
                )
            }
        }
        speaker.start(payload, profile)
    }

    private fun clientVersion(): String = runCatching {
        applicationContext.packageManager.getPackageInfo(applicationContext.packageName, 0).versionName
    }.getOrNull() ?: "0.3.0"

    private fun describe(current: SpeakerController.State): String = when (current) {
        is SpeakerController.State.Playing -> current.endpoint.ifEmpty { "Playing" }
        is SpeakerController.State.Waiting -> current.detail
        is SpeakerController.State.Failed -> current.detail
        is SpeakerController.State.Connecting -> "Connecting to ${current.host}"
        SpeakerController.State.Idle -> "Phone speaker"
    }

    private fun notificationManager() =
        getSystemService(Context.NOTIFICATION_SERVICE) as? NotificationManager

    private fun buildNotification(text: String): Notification {
        val open = android.app.PendingIntent.getActivity(
            this,
            0,
            Intent(this, com.focuswindow.client.MainActivity::class.java),
            android.app.PendingIntent.FLAG_IMMUTABLE,
        )
        val stop = android.app.PendingIntent.getService(
            this,
            1,
            Intent(this, SpeakerService::class.java).setAction(ACTION_STOP),
            android.app.PendingIntent.FLAG_IMMUTABLE,
        )
        return NotificationCompat.Builder(this, CHANNEL_ID)
            .setContentTitle("DeskFuse phone speaker")
            .setContentText(text)
            .setSmallIcon(R.drawable.ic_session)
            .setOngoing(true)
            .setCategory(NotificationCompat.CATEGORY_SERVICE)
            .setPriority(NotificationCompat.PRIORITY_LOW)
            .setContentIntent(open)
            .addAction(0, "Stop", stop)
            .build()
    }

    private fun createChannel() {
        if (Build.VERSION.SDK_INT < Build.VERSION_CODES.O) return
        val manager = notificationManager() ?: return
        if (manager.getNotificationChannel(CHANNEL_ID) != null) return
        manager.createNotificationChannel(
            NotificationChannel(
                CHANNEL_ID,
                "Phone speaker",
                NotificationManager.IMPORTANCE_LOW,
            ).apply {
                description = "Shown while the PC is sending sound to this phone."
                setShowBadge(false)
            },
        )
    }

    override fun onDestroy() {
        running = false
        controller?.stop()
        controller = null
        stateJob?.cancel()
        stateJob = null
        scope.cancel()
        // The session is over: the interface must show the speaker as off,
        // otherwise the next tap would only ever try to stop it again.
        state.value = SpeakerController.State.Idle
        super.onDestroy()
    }

    companion object {
        /** One Json instance for the whole service. */
        val json = kotlinx.serialization.json.Json {
            ignoreUnknownKeys = true
            encodeDefaults = true
        }

        const val ACTION_START = "com.focuswindow.client.SPEAKER_START"
        const val ACTION_STOP = "com.focuswindow.client.SPEAKER_STOP"
        const val EXTRA_HOST = "host"
        const val EXTRA_PORT = "port"
        const val EXTRA_KEY = "key"
        const val EXTRA_SALT = "salt"
        const val EXTRA_NAME = "name"
        const val EXTRA_RELAY = "relay"
        const val EXTRA_PROFILE = "profile"

        private const val CHANNEL_ID = "speaker"
        private const val NOTIFICATION_ID = 1002

        /** The state of the speaker session, for the user interface. */
        val state: MutableStateFlow<SpeakerController.State> =
            MutableStateFlow(SpeakerController.State.Idle)

        /** True while the speaker session is actually running. */
        @Volatile
        var running: Boolean = false
            private set

        fun start(context: Context, profile: PhoneProfile) {
            val intent = Intent(context, SpeakerService::class.java)
                .setAction(ACTION_START)
                .putExtra(
                    EXTRA_PROFILE,
                    json.encodeToString(PhoneProfile.serializer(), profile),
                )
            context.startForegroundService(intent)
        }

        fun stop(context: Context) {
            context.startService(
                Intent(context, SpeakerService::class.java).setAction(ACTION_STOP),
            )
        }
    }
}
