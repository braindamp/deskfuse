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
import com.focuswindow.client.transport.TrustedSetupLocation
import com.focuswindow.client.transport.TrustedSpeakerSelection
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.Job
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext
import java.util.concurrent.atomic.AtomicReference

/** Explicit trusted setup, independent of legacy pairing secrets and normal session transport. */
@Composable
internal fun TrustedEnrollmentDialog(lifecycle: Lifecycle, onDismiss: () -> Unit) {
    val context = LocalContext.current.applicationContext
    val repository = remember { TrustedEnrollmentRepository(AndroidTrustedEnrollmentStore(context)) }
    val signer = remember { AndroidBrokerDeviceSigner() }
    val connection = remember { AtomicReference<TrustedEnrollmentClient?>() }
    val scope = rememberCoroutineScope()
    var job by remember { mutableStateOf<Job?>(null) }
    var busy by remember { mutableStateOf(false) }
    var step by remember { mutableStateOf(0) }
    var host by remember { mutableStateOf("") }
    var port by remember { mutableStateOf("") }
    var code by remember { mutableStateOf("") }
    var offered by remember { mutableStateOf<List<BrokerScope>>(emptyList()) }
    var selected by remember { mutableStateOf<Set<BrokerScope>>(emptySet()) }
    var found by remember { mutableStateOf<List<TrustedSetupLocation>>(emptyList()) }
    var recoverable by remember { mutableStateOf<List<TrustedPreparedEnrollment>>(emptyList()) }
    var message by remember { mutableStateOf<String?>(null) }
    var committed by remember { mutableStateOf<TrustedCommittedEnrollment?>(null) }
    var audioHost by remember { mutableStateOf("") }
    var audioPort by remember { mutableStateOf("") }
    var cellular by remember { mutableStateOf(false) }
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
                message = "Setup did not finish. Saved pairing details are kept; reopen trusted setup to recover them."
            }
            finally { busy = false }
        }
    }
    fun connect(address: String, setupPort: Int) = work {
        require(lifecycle.currentState.isAtLeast(Lifecycle.State.RESUMED))
        require(address.isNotBlank() && address.length <= 253 && address.none { it.isWhitespace() || it.isISOControl() } && setupPort in 1..65535)
        connection.getAndSet(null)?.close()
        val client = TrustedEnrollmentClient.connect(address, setupPort, signer, repository)
        connection.set(client)
        offered = client.offer().scopes; selected = offered.toSet()
        recoverable = withContext(Dispatchers.IO) { repository.recoverable(TrustedEnrollmentProtocol.hash(client.offer().hostSpki)) }
        step = 1
    }
    DisposableEffect(lifecycle) {
        val observer = LifecycleEventObserver { _, event ->
            if (event == Lifecycle.Event.ON_STOP) {
                connection.getAndSet(null)?.close(); job?.cancel()
                if (step != 4) { step = 0; message = "Setup paused. Reopen Windows trusted setup and reconnect; saved pairing details are kept." }
            }
        }
        lifecycle.addObserver(observer)
        onDispose { lifecycle.removeObserver(observer); connection.getAndSet(null)?.close(); job?.cancel() }
    }
    AlertDialog(
        onDismissRequest = onDismiss,
        title = { Text("Trusted phone setup") },
        text = {
            Column(Modifier.heightIn(max = 480.dp).verticalScroll(rememberScrollState())) {
                when (step) {
                    0 -> {
                        Text("Open trusted setup on Windows. Choose that PC below, or enter the address and setup port shown there.")
                        for (pc in found) TextButton(onClick = { host = pc.address; port = pc.port.toString(); connect(pc.address, pc.port) }, enabled = !busy) {
                            Text("${pc.name} (${pc.address}:${pc.port})")
                        }
                        OutlinedTextField(host, { host = it }, label = { Text("PC name or address") }, enabled = !busy, singleLine = true)
                        OutlinedTextField(port, { port = it }, label = { Text("Setup port") }, enabled = !busy, singleLine = true)
                        TextButton(onClick = { work { found = TrustedSetupDiscovery.scan(); if (found.isEmpty()) message = "No setup PCs found. You can connect by address." } }, enabled = !busy) { Text("Find setup PCs") }
                        TextButton(onClick = { connect(host.trim().removeSurrounding("[", "]"), port.toIntOrNull() ?: 0) }, enabled = !busy && host.isNotBlank() && port.toIntOrNull() in 1..65535) { Text("Connect") }
                    }
                    1 -> {
                        Text("Choose permissions, then verify the code displayed in the Windows setup window.")
                        for (permission in offered) Row {
                            Checkbox(permission in selected, { checked -> selected = if (checked) selected + permission else selected - permission }, enabled = !busy)
                            Text(permissionName(permission))
                        }
                        TextButton(onClick = { work {
                            connection.get()!!.propose(offered.filter { it in selected }); step = 2
                        } }, enabled = !busy && selected.isNotEmpty()) { Text("Continue with these permissions") }
                        for (saved in recoverable) TextButton(onClick = { work {
                            when (connection.get()!!.recover(saved)) {
                                EnrollmentOutcome.COMMITTED -> {
                                    val binding = saved.binding()
                                    committed = withContext(Dispatchers.IO) { repository.committed(binding.hostHash, binding.enrollment) }
                                    audioHost = host
                                    committed?.let { TrustedSpeakerSelection.save(context, it, host) }
                                    step = 4; message = "Trusted pairing recovered."
                                }
                                EnrollmentOutcome.REVOKED -> message = "That pairing was revoked. Start a new trusted setup on Windows."
                                else -> message = "Pairing status is unresolved. Saved details are kept; retry trusted setup."
                            }
                        } }, enabled = !busy) { Text("Recover pairing ${saved.binding().enrollment.toString().take(8)}") }
                    }
                    2 -> {
                        Text("Enter all 52 characters shown in the genuine Windows setup window. A PC name or an old pairing code cannot verify this setup.")
                        OutlinedTextField(code, { code = it }, label = { Text("Full verification code") }, enabled = !busy)
                        TextButton(onClick = {
                            try {
                                val client = connection.get()!!
                                if (code.startsWith("DESKFUSE-ENROLL-VERIFY-V1:")) client.verifyQr(code) else client.verifyText(code)
                                step = 3; message = null
                            } catch (_: Exception) { message = "The full code does not match this setup. Check the Windows setup window." }
                        }, enabled = !busy) { Text("Verify code") }
                    }
                    3 -> {
                        Text("Windows setup verified. Allow this phone to use:")
                        for (permission in offered.filter { it in selected }) Text(permissionName(permission))
                        Text("Check the Windows account and approve on both devices.")
                        TextButton(onClick = { work {
                            message = "Waiting for confirmation in Windows setup…"
                            committed = connection.get()!!.approve { lifecycle.currentState.isAtLeast(Lifecycle.State.RESUMED) }
                            audioHost = host
                            committed?.let { TrustedSpeakerSelection.save(context, it, host) }
                            step = 4; message = "Trusted pairing saved."
                        } }, enabled = !busy) { Text("Approve pairing") }
                    }
                    4 -> {
                        Text("Trusted pairing is saved on this phone.")
                        val enrollment = committed
                        if (BuildConfig.TRUSTED_SPEAKER_FIRST_DELIVERY && enrollment != null && BrokerScope.SPEAKER in enrollment.transcript().proposal.scopes &&
                            audioState in setOf(AuthorityAudioState.IDLE, AuthorityAudioState.FAILED)) {
                            Text("Trusted speaker development test")
                            OutlinedTextField(audioHost, { audioHost = it }, label = { Text("Approved audio route") }, singleLine = true)
                            OutlinedTextField(audioPort, { audioPort = it }, label = { Text("Windows audio listener port") }, singleLine = true)
                            Row {
                                Checkbox(cellular, { cellular = it })
                                Text("Use this approved cellular route and allow metered data")
                            }
                            TextButton(onClick = {
                                runCatching {
                                    check(lifecycle.currentState.isAtLeast(Lifecycle.State.RESUMED))
                                    AuthorityAudioControl.start(context, enrollment, audioHost.trim().removeSurrounding("[", "]"),
                                        checkNotNull(audioPort.toIntOrNull()), cellular)
                                }.onFailure { message = "Trusted speaker could not start. Check the audio route and listener port." }
                            }, enabled = audioHost.isNotBlank() && audioPort.toIntOrNull() in 1..65535) { Text("Start trusted speaker") }
                        }
                    }
                }
                if (BuildConfig.TRUSTED_SPEAKER_FIRST_DELIVERY) when (audioState) {
                    AuthorityAudioState.STARTING, AuthorityAudioState.PLAYING -> {
                        Text(if (audioState == AuthorityAudioState.PLAYING) "Trusted speaker is playing." else "Connecting trusted speaker…")
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
                        TextButton(onClick = { AuthorityAudioControl.stop(context) }) { Text("Stop trusted speaker") }
                    }
                    AuthorityAudioState.STOPPING -> Text("Stopping trusted speaker…")
                    AuthorityAudioState.FAILED -> Text("Trusted speaker ended before it could complete the session.")
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
