package com.focuswindow.client.ui

import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.heightIn
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.Checkbox
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.DisposableEffect
import androidx.compose.runtime.collectAsState
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.runtime.setValue
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.unit.dp
import androidx.lifecycle.Lifecycle
import androidx.lifecycle.LifecycleEventObserver
import com.focuswindow.client.BuildConfig
import com.focuswindow.client.transport.AndroidBrokerDeviceSigner
import com.focuswindow.client.transport.AndroidTrustedEnrollmentStore
import com.focuswindow.client.transport.AuthorityAudioControl
import com.focuswindow.client.transport.AuthorityAudioState
import com.focuswindow.client.transport.BrokerScope
import com.focuswindow.client.transport.EnrollmentOutcome
import com.focuswindow.client.transport.TrustedEnrollmentClient
import com.focuswindow.client.transport.TrustedCommittedEnrollment
import com.focuswindow.client.transport.TrustedEnrollmentProtocol
import com.focuswindow.client.transport.TrustedEnrollmentRepository
import com.focuswindow.client.transport.TrustedPreparedEnrollment
import com.focuswindow.client.transport.TrustedSetupDiscovery
import com.focuswindow.client.transport.TrustedSpeakerSelection
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.Job
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext
import java.util.concurrent.atomic.AtomicReference

/** Explicit trusted setup, independent of legacy pairing secrets and normal session transport. */
@Composable
internal fun TrustedEnrollmentDialog(lifecycle: Lifecycle, initialHost: String, onPaired: () -> Unit, onDismiss: () -> Unit) {
    val context = LocalContext.current.applicationContext
    val repository = remember { TrustedEnrollmentRepository(AndroidTrustedEnrollmentStore(context)) }
    val signer = remember { AndroidBrokerDeviceSigner() }
    val connection = remember { AtomicReference<TrustedEnrollmentClient?>() }
    val scope = rememberCoroutineScope()
    var job by remember { mutableStateOf<Job?>(null) }
    var busy by remember { mutableStateOf(false) }
    var step by remember { mutableStateOf(0) }
    val host = initialHost
    var offered by remember { mutableStateOf<List<BrokerScope>>(emptyList()) }
    var selected by remember { mutableStateOf<Set<BrokerScope>>(emptySet()) }
    var recoverable by remember { mutableStateOf<List<TrustedPreparedEnrollment>>(emptyList()) }
    var message by remember { mutableStateOf<String?>(null) }
    var committed by remember { mutableStateOf<TrustedCommittedEnrollment?>(null) }
    var audioHost by remember { mutableStateOf("") }
    var audioPort by remember { mutableStateOf("") }
    val audioState by AuthorityAudioControl.state.collectAsState()

    fun work(action: suspend () -> Unit) {
        if (busy) return
        busy = true; message = null
        job = scope.launch {
            try { action() }
            catch (cancelled: CancellationException) { throw cancelled }
            catch (failure: Exception) {
                if (BuildConfig.DEBUG) {
                    // Diagnostic types and call sites only: never record enrollment material.
                    val causes = generateSequence<Throwable>(failure) { it.cause }.take(8)
                    android.util.Log.e("TrustedSetup", causes.joinToString("\nCaused by: ") {
                        it.javaClass.name + "\n" + it.stackTrace.take(12).joinToString("\n")
                    })
                }
                message = "Couldn’t pair. Open pairing in DeskFuse on your PC, then try again."
            }
            finally { busy = false }
        }
    }
    fun connect(address: String) = work {
        val setupPort = TrustedSetupDiscovery.locate(address)?.port
            ?: error("Open pairing on your PC first")
        require(lifecycle.currentState.isAtLeast(Lifecycle.State.RESUMED))
        require(address.isNotBlank() && address.length <= 253 && address.none { it.isWhitespace() || it.isISOControl() } && setupPort in 1..65535)
        connection.getAndSet(null)?.close()
        val client = TrustedEnrollmentClient.connect(address, setupPort, signer, repository)
        connection.set(client)
        offered = client.offer().scopes; selected = offered.toSet()
        recoverable = withContext(Dispatchers.IO) { repository.recoverable(TrustedEnrollmentProtocol.hash(client.offer().hostSpki)) }
        if (offered == listOf(BrokerScope.SPEAKER) && recoverable.isEmpty()) {
            client.propose(offered)
            step = 2
        } else step = 1
    }
    LaunchedEffect(initialHost) { connect(initialHost) }
    DisposableEffect(lifecycle) {
        val observer = LifecycleEventObserver { _, event ->
            if (event == Lifecycle.Event.ON_STOP) {
                connection.getAndSet(null)?.close(); job?.cancel()
                if (step != 4) { step = 0; message = "Pairing paused. Try again when you’re ready." }
            }
        }
        lifecycle.addObserver(observer)
        onDispose { lifecycle.removeObserver(observer); connection.getAndSet(null)?.close(); job?.cancel() }
    }
    AlertDialog(
        onDismissRequest = onDismiss,
        title = { Text("Pair with PC") },
        text = {
            Column(Modifier.heightIn(max = 480.dp).verticalScroll(rememberScrollState())) {
                when (step) {
                    0 -> {
                        if (busy) Text("Connecting…")
                        else TextButton(onClick = { connect(host) }) { Text("Try again") }

                    }
                    1 -> {
                        Text("Choose what this PC can use.")
                        for (permission in offered) Row {
                            Checkbox(permission in selected, { checked -> selected = if (checked) selected + permission else selected - permission }, enabled = !busy)
                            Text(permissionName(permission))
                        }
                        TextButton(onClick = { work {
                            connection.get()!!.propose(offered.filter { it in selected }); step = 2
                        } }, enabled = !busy && selected.isNotEmpty()) { Text("Continue") }
                        for (saved in recoverable) TextButton(onClick = { work {
                            when (connection.get()!!.recover(saved)) {
                                EnrollmentOutcome.COMMITTED -> {
                                    val binding = saved.binding()
                                    committed = withContext(Dispatchers.IO) { repository.committed(binding.hostHash, binding.enrollment) }
                                    audioHost = host
                                    committed?.let { TrustedSpeakerSelection.save(context, it, host) }
                                    step = 4; message = "Paired."
                                    if (BuildConfig.TRUSTED_SPEAKER_FIRST_DELIVERY && committed?.transcript()?.proposal?.scopes?.contains(BrokerScope.SPEAKER) == true) onPaired()
                                }
                                EnrollmentOutcome.REVOKED -> message = "That pairing was removed. Pair again on your PC."
                                else -> message = "Pairing is not finished. Try again."
                            }
                        } }, enabled = !busy) { Text("Resume pairing") }
                    }
                    2 -> {
                        for (permission in offered.filter { it in selected }) Text(permissionName(permission))
                        Text("Check that Windows setup shows the same number:")
                        Text(connection.get()?.comparisonNumber().orEmpty(), style = androidx.compose.material3.MaterialTheme.typography.headlineLarge)
                        Text("Only pair if the numbers on both devices match.")
                        TextButton(onClick = { work {
                            require(lifecycle.currentState.isAtLeast(Lifecycle.State.RESUMED))
                            connection.get()!!.confirmNumbersMatch()
                            message = "Waiting for confirmation in Windows setup…"
                            committed = connection.get()!!.approve { lifecycle.currentState.isAtLeast(Lifecycle.State.RESUMED) }
                            audioHost = host
                            committed?.let { TrustedSpeakerSelection.save(context, it, host) }
                            step = 4; message = "Paired."
                            if (BuildConfig.TRUSTED_SPEAKER_FIRST_DELIVERY && committed?.transcript()?.proposal?.scopes?.contains(BrokerScope.SPEAKER) == true) onPaired()
                        } }, enabled = !busy) { Text("Numbers match — Pair") }

                    }
                    4 -> { Text("Paired.") }
                }
                if (BuildConfig.TRUSTED_SPEAKER_FIRST_DELIVERY) when (audioState) {
                    AuthorityAudioState.STARTING, AuthorityAudioState.PLAYING -> {
                        Text(if (audioState == AuthorityAudioState.PLAYING) "Speaker is playing." else "Connecting…")
                        if (BuildConfig.TRUSTED_SPEAKER_HANDOVER &&
                            audioState == AuthorityAudioState.PLAYING) {
                            OutlinedTextField(audioHost, { audioHost = it }, label = { Text("Approved alternate endpoint") }, singleLine = true)
                            OutlinedTextField(audioPort, { audioPort = it }, label = { Text("Alternate listener port") }, singleLine = true)
                            TextButton(onClick = {
                                runCatching {
                                    AuthorityAudioControl.prepareAlternate(context, audioHost.trim().removeSurrounding("[", "]"),
                                        checkNotNull(audioPort.toIntOrNull()))
                                }.onFailure { message = "Could not prepare the alternate route." }
                            }, enabled = audioHost.isNotBlank() && audioPort.toIntOrNull() in 1..65535) {
                                Text("Prepare alternate route and allow metered data")
                            }
                        }
                        TextButton(onClick = { AuthorityAudioControl.stop(context) }) { Text("Stop speaker") }
                    }
                    AuthorityAudioState.STOPPING -> Text("Stopping…")
                    AuthorityAudioState.FAILED -> Text("Couldn’t start the speaker. Try again.")
                    AuthorityAudioState.IDLE -> Unit
                }
                message?.let { Text(it) }
                if (busy && message == null) Text("Working…")
            }
        },
        confirmButton = {},
        dismissButton = { TextButton(onClick = onDismiss) { Text("Close") } },
    )
}

private fun permissionName(scope: BrokerScope): String = when (scope) {
    BrokerScope.SPEAKER -> "Windows audio playback"
    BrokerScope.DISPLAY -> "Windows displays"
    BrokerScope.INPUT -> "Keyboard and pointer input"
    BrokerScope.APPLICATION_CAPTURE -> "Application presentation"
    BrokerScope.MICROPHONE -> "Phone microphone"
    BrokerScope.CAMERA -> "Phone camera"
    BrokerScope.FILES -> "Files"
    BrokerScope.CLIPBOARD -> "Clipboard"
}
