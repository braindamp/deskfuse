package com.focuswindow.client

import android.content.ClipData
import android.content.ClipboardManager
import android.content.Context
import android.Manifest
import android.content.pm.PackageManager
import android.content.Intent
import android.net.Uri
import android.provider.OpenableColumns
import android.widget.Toast
import androidx.activity.result.contract.ActivityResultContracts
import android.os.Build
import android.os.Bundle
import android.view.KeyEvent
import android.view.MotionEvent
import android.view.WindowManager
import android.view.inputmethod.InputMethodManager
import androidx.activity.ComponentActivity
import androidx.activity.compose.setContent
import androidx.activity.enableEdgeToEdge
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.heightIn
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.ui.unit.dp
import androidx.compose.runtime.collectAsState
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableIntStateOf
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Modifier
import androidx.compose.ui.viewinterop.AndroidView
import androidx.core.content.ContextCompat
import androidx.core.view.ViewCompat
import androidx.core.view.WindowInsetsCompat
import androidx.lifecycle.lifecycleScope
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.launch
import com.focuswindow.client.input.InputMapper
import com.focuswindow.client.media.PcAudioOutput
import com.focuswindow.client.media.PhoneMicrophoneCapture
import com.focuswindow.client.profile.PhoneProfile
import com.focuswindow.client.profile.ProfileProvider
import com.focuswindow.client.protocol.InputEvent
import com.focuswindow.client.protocol.Modifiers
import com.focuswindow.client.render.PhoneSurfaceView
import com.focuswindow.client.session.SessionController
import com.focuswindow.client.session.SpeakerController
import com.focuswindow.client.session.SpeakerService
import com.focuswindow.client.session.SessionState
import com.focuswindow.client.transport.PairingPayload
import com.focuswindow.client.transport.PairingRepository
import com.focuswindow.client.transport.CameraFreeDiscovery
import com.focuswindow.client.transport.CameraFreeEnrollment
import com.focuswindow.client.transport.DiscoveredPc
import com.focuswindow.client.transport.EnrollmentMessages
import com.focuswindow.client.ui.SessionChrome
import com.focuswindow.client.ui.StateScreen

/**
 * The single activity.
 *
 * Its job is narrow and deliberate: own the [PhoneProfile] lifecycle, keep the
 * screen awake for the length of a session, and forward input. Everything else
 * lives in [SessionController].
 *
 * Note the configChanges list in the manifest: we handle rotation, fold, font
 * scale and density changes ourselves rather than being recreated, because a
 * recreation would tear down the decoder and the socket for what is really
 * just "the profile changed" (FR-DPI-09, FR-MOB-01/02).
 */
class MainActivity : ComponentActivity() {

    private lateinit var profiles: ProfileProvider
    private lateinit var pairings: PairingRepository
    private lateinit var controller: SessionController
    private lateinit var surfaceView: PhoneSurfaceView
    private lateinit var input: InputMapper

    private var profile: PhoneProfile? = null
    private var insets: WindowInsetsCompat? = null
    // A gesture begun on the app's own controls must never click the PC behind them.
    private var remoteTouchActive = false
    private var fileDestination: String? = null
    private var pendingRemoteFile: String? = null
    // External Sharesheet entry. Source URI stays owned by Android's content provider.
    // A single explicit user action sends it to the CURRENT authenticated PC.
    private var sharedDocumentUri by mutableStateOf<Uri?>(null)
    @Volatile private var appDialogVisible = false
    private var discoveredPcs by mutableStateOf<List<DiscoveredPc>>(emptyList())
    private var pcPickerVisible by mutableStateOf(false)
    private var pcDiscoveryBusy by mutableStateOf(false)
    private var pcPairingBusy by mutableStateOf(false)
    private var pcPairingCode by mutableStateOf<String?>(null)
    private var pcPairingError by mutableStateOf<String?>(null)
    private var manualPc by mutableStateOf("")

    private val savePcFile = registerForActivityResult(ActivityResultContracts.CreateDocument(
        "application/octet-stream")) { uri ->
        val remote = pendingRemoteFile
        pendingRemoteFile = null
        if (remote != null && uri != null) {
            controller.downloadFile(remote,
                open = { contentResolver.openOutputStream(uri, "w") },
                deleteIncomplete = { contentResolver.delete(uri, null, null); Unit },
            )
        }
    }

    private val phoneMicrophonePermission =
        registerForActivityResult(ActivityResultContracts.RequestPermission()) { granted ->
            if (granted && controller.state.value is SessionState.Streaming) {
                controller.togglePhoneMicrophone()
            } else if (!granted) {
                Toast.makeText(this, "Android microphone permission is required to send voice to Windows",
                    Toast.LENGTH_LONG).show()
            }
        }

    private val filePicker = registerForActivityResult(ActivityResultContracts.OpenDocument()) { uri ->
        if (uri != null) {
            var name = "android-document"
            var size = -1L
            contentResolver.query(uri, arrayOf(OpenableColumns.DISPLAY_NAME, OpenableColumns.SIZE),
                null, null, null)?.use { cursor ->
                if (cursor.moveToFirst()) {
                    val nameCol = cursor.getColumnIndex(OpenableColumns.DISPLAY_NAME)
                    val sizeCol = cursor.getColumnIndex(OpenableColumns.SIZE)
                    if (nameCol >= 0 && !cursor.isNull(nameCol)) name = cursor.getString(nameCol)
                    if (sizeCol >= 0 && !cursor.isNull(sizeCol)) size = cursor.getLong(sizeCol)
                }
            }
            val selected = uri
            controller.uploadFile(name, size,
                open = { contentResolver.openInputStream(selected) },
                destination = fileDestination,
            )
        }
    }

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        enableEdgeToEdge()

        profiles = ProfileProvider(applicationContext)
        pairings = PairingRepository(applicationContext)
        controller = SessionController(
            lifecycleScope,
            clientVersion = BuildConfig.VERSION_NAME,
            audioPreferences = pairings,
        )

        surfaceView = PhoneSurfaceView(this).apply {
            // The decoder writes here directly; see NFR-PIX-01.
            keepScreenOn = true
        }
        controller.attachSurface(surfaceView)
        controller.attachPcAudioOutput(PcAudioOutput(applicationContext))
        controller.attachPhoneMicrophone(PhoneMicrophoneCapture(applicationContext))
        input = InputMapper(surfaceView)
        surfaceView.onCommittedText = { controller.sendInput(InputEvent.Text(it)) }
        surfaceView.onDeleteBackward = { sendSyntheticKey(KeyEvent.KEYCODE_DEL) }
        lifecycleScope.launch {
            controller.fileStatus.collect { message ->
                Toast.makeText(this@MainActivity, message, Toast.LENGTH_LONG).show()
            }
        }
        lifecycleScope.launch {
            controller.clipboardFromPc.collect { text ->
                (getSystemService(Context.CLIPBOARD_SERVICE) as ClipboardManager)
                    .setPrimaryClip(ClipData.newPlainText("DeskFuse PC", text))
            }
        }

        handleExternalShare(intent)

        setContent {
            val state by controller.state.collectAsState()
            val advisories by controller.advisories.collectAsState()
            val pcAudioStatus by controller.pcAudioStatus.collectAsState()
            val speakerState by SpeakerService.state.collectAsState()
            val speakerStatus = when (val current = speakerState) {
                is SpeakerController.State.Playing -> "On"
                is SpeakerController.State.Waiting -> "Starting"
                is SpeakerController.State.Connecting -> "Starting"
                is SpeakerController.State.Failed -> "Unavailable"
                SpeakerController.State.Idle -> "Off"
            }
            val phoneMicStatus by controller.phoneMicStatus.collectAsState()
            var modifierMask by remember { mutableIntStateOf(Modifiers.NONE) }
            var diagnostics by remember { mutableStateOf(false) }
            var showWindows by remember { mutableStateOf(false) }
            var showPcFiles by remember { mutableStateOf(false) }
            var typedPcPath by remember { mutableStateOf("") }
            val windows by controller.windows.collectAsState()
            val pcFiles by controller.files.collectAsState()

            Box(Modifier.fillMaxSize()) {
                // The remote surface sits underneath everything.
                AndroidView(
                    factory = { surfaceView },
                    modifier = Modifier.fillMaxSize(),
                )

                // FR-OPS-03 — a named screen for every non-streaming state.
                StateScreen(
                    state = state,
                    onRetry = { retry() },
                    onScan = { beginPairing() },
                    onDismiss = { },
                    onUseAsSpeaker = {
                        // The phone becomes a speaker for this PC: the host
                        // creates a Windows playback endpoint for it and the
                        // PC's sound follows to this phone.
                        profile?.let { current ->
                            SpeakerService.start(this@MainActivity, current)
                        }
                    },
                )

                if (pcPickerVisible) {
                    AlertDialog(
                        onDismissRequest = {
                            if (!pcPairingBusy) pcPickerVisible = false
                        },
                        title = { Text(if (pcPairingCode != null) "Approve this phone on Windows"
                            else "Your Windows PCs") },
                        text = {
                            Column {
                                if (pcPairingCode != null) {
                                    Text("Confirm this code matches the Windows dialog:")
                                    Text(pcPairingCode!!, modifier = Modifier.padding(12.dp))
                                    Text("Waiting for approval on your PC...")
                                } else {
                                    if (pcDiscoveryBusy) Text("Finding PCs on your local network...")
                                    else if (discoveredPcs.isEmpty()) Text(
                                        "No PCs found automatically. Make sure DeskFuse is running on Windows, or enter its address."
                                    )
                                    for (pc in discoveredPcs) {
                                        TextButton(
                                            onClick = { enrollWith(pc) },
                                            enabled = !pcPairingBusy,
                                        ) { Text("${pc.name}  (${pc.address})") }
                                    }
                                    if (pcPairingBusy) Text("Waiting for the Windows owner...")
                                    if (!pcPairingBusy) {
                                        OutlinedTextField(
                                            value = manualPc,
                                            onValueChange = { manualPc = it },
                                            label = { Text("PC IP address or hostname") },
                                            singleLine = true,
                                        )
                                        TextButton(
                                            onClick = {
                                                runCatching { DiscoveredPc.manual(manualPc) }
                                                    .onSuccess(::enrollWith)
                                                    .onFailure { pcPairingError = "Enter a valid PC address." }
                                            },
                                            enabled = manualPc.isNotBlank(),
                                        ) { Text("Connect by address") }
                                    }
                                }
                                pcPairingError?.let { Text(it) }
                            }
                        },
                        confirmButton = {
                            if (!pcPairingBusy) TextButton(onClick = { findPcs() }) {
                                Text("Search again")
                            }
                        },
                        dismissButton = {
                            TextButton(onClick = {
                                if (!pcPairingBusy) pcPickerVisible = false
                            }, enabled = !pcPairingBusy) { Text("Close") }
                        },
                    )
                }

                sharedDocumentUri?.let { sharedUri ->
                    AlertDialog(
                        onDismissRequest = { sharedDocumentUri = null; appDialogVisible = false },
                        title = { Text("Send to DeskFuse PC") },
                        text = {
                            Text(if (state is SessionState.Streaming)
                                "Send this Android document over the encrypted session to the connected PC? " +
                                    "It will be saved to the selected PC upload folder."
                            else
                                "Connect to an approved Windows PC first. Your selected document remains on your phone.")
                        },
                        confirmButton = {
                            if (state is SessionState.Streaming) {
                                TextButton(onClick = {
                                    uploadSelectedDocument(sharedUri, fileDestination, openOnPc = false)
                                    sharedDocumentUri = null
                                    appDialogVisible = false
                                }) { Text("Send file") }
                                TextButton(onClick = {
                                    uploadSelectedDocument(sharedUri, null, openOnPc = true)
                                    sharedDocumentUri = null
                                    appDialogVisible = false
                                }) { Text("Open on PC") }
                            }
                        },
                        dismissButton = {
                            TextButton(onClick = { sharedDocumentUri = null; appDialogVisible = false }) {
                                Text("Cancel")
                            }
                        },
                    )
                }

                if (showWindows) {
                    AlertDialog(
                        onDismissRequest = { showWindows = false; appDialogVisible = false },
                        title = { Text("Windows applications") },
                        text = {
                            androidx.compose.foundation.lazy.LazyColumn {
                                items(windows.size) { i ->
                                    val win = windows[i]
                                    Text(
                                        "${win.appLabel} · ${win.title}",
                                        modifier = Modifier.clickable {
                                            controller.activateWindow(win.id)
                                            showWindows = false
                                            appDialogVisible = false
                                        }.padding(9.dp),
                                    )
                                }
                            }
                        },
                        confirmButton = {
                            TextButton(onClick = { showWindows = false; appDialogVisible = false }) { Text("Close") }
                        },
                    )
                }

                if (showPcFiles) {
                    AlertDialog(
                        onDismissRequest = { showPcFiles = false; appDialogVisible = false },
                        title = { Text("Windows files") },
                        text = {
                            Column {
                                Text(pcFiles.path.ifBlank { "This PC · available drives" })
                                Row(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                                    TextButton(onClick = { controller.browsePc(pcFiles.parent) }) {
                                        Text("Up")
                                    }
                                    TextButton(onClick = { controller.browsePc() }) { Text("Drives") }
                                }
                                OutlinedTextField(
                                    value = typedPcPath,
                                    onValueChange = { typedPcPath = it },
                                    label = { Text("Windows folder or UNC path") },
                                    singleLine = true,
                                )
                                TextButton(onClick = { controller.browsePc(typedPcPath) }) { Text("Go") }
                                LazyColumn(modifier = Modifier.heightIn(max = 360.dp)) {
                                    items(pcFiles.entries.size) { index ->
                                        val entry = pcFiles.entries[index]
                                        Text(
                                            (if (entry.directory) "📁 " else "📄 ") + entry.name,
                                            modifier = Modifier.clickable {
                                                if (entry.directory) controller.browsePc(entry.path)
                                                else {
                                                    pendingRemoteFile = entry.path
                                                    savePcFile.launch(entry.name)
                                                }
                                            }.padding(9.dp),
                                        )
                                    }
                                }
                                Row {
                                    if (pcFiles.offset > 0) TextButton(onClick = {
                                        controller.browsePc(pcFiles.path, (pcFiles.offset - 200).coerceAtLeast(0))
                                    }) { Text("Previous") }
                                    if (pcFiles.truncated) TextButton(onClick = {
                                        controller.browsePc(pcFiles.path, pcFiles.nextOffset)
                                    }) { Text("Next 200") }
                                }
                                TextButton(onClick = { controller.cancelDownload() }) { Text("Cancel download") }
                            }
                        },
                        confirmButton = {
                            TextButton(onClick = {
                                fileDestination = pcFiles.path.takeIf { it.isNotBlank() }
                                filePicker.launch(arrayOf("*/*"))
                            }) { Text("Upload here") }
                        },
                        dismissButton = {
                            TextButton(onClick = { showPcFiles = false; appDialogVisible = false }) {
                                Text("Close")
                            }
                        },
                    )
                }

                SessionChrome(
                    state = state,
                    advisories = advisories,
                    modifierMask = modifierMask,
                    onToggleModifier = {
                        input.toggleModifier(it)
                        modifierMask = input.stickyModifiers
                    },
                    onKeyShortcut = { keyCode -> sendSyntheticKey(keyCode) },
                    onToggleDiagnostics = { diagnostics = !diagnostics },
                    diagnosticsVisible = diagnostics,
                    onDisconnect = { controller.stop() },
                    onSendClipboard = { sendPhoneClipboard() },
                    onReceiveClipboard = { controller.requestHostClipboard() },
                    onShowKeyboard = { showPhoneKeyboard() },
                    onSelectWindow = { controller.requestWindows(); showWindows = true; appDialogVisible = true },
                    onSendFile = { controller.browsePc(); showPcFiles = true; appDialogVisible = true },
                    pcAudioStatus = pcAudioStatus,
                    onTogglePcAudio = { controller.togglePcAudio() },
                    speakerStatus = speakerStatus,
                    onToggleSpeaker = { togglePhoneSpeaker() },
                    phoneMicStatus = phoneMicStatus,
                    onTogglePhoneMic = {
                        if (phoneMicStatus == "Unavailable") {
                            controller.togglePhoneMicrophone()
                            if (controller.phoneMicStatus.value == "Unavailable") {
                                Toast.makeText(this@MainActivity, "Windows has no approved phone microphone destination",
                                    Toast.LENGTH_SHORT).show()
                            }
                        } else if (phoneMicStatus in setOf("On", "Starting") ||
                            ContextCompat.checkSelfPermission(this@MainActivity, Manifest.permission.RECORD_AUDIO)
                            == PackageManager.PERMISSION_GRANTED) {
                            controller.togglePhoneMicrophone()
                        } else {
                            phoneMicrophonePermission.launch(Manifest.permission.RECORD_AUDIO)
                        }
                    },
                    onAdvisoryAction = { },
                )
            }
        }

        ViewCompat.setOnApplyWindowInsetsListener(window.decorView) { _, applied ->
            insets = applied
            refreshProfile()
            applied
        }

        // A pairing deep link may have launched us.
        intent?.data?.toString()?.let(::handlePairingUri)
    }

    override fun onNewIntent(intent: Intent) {
        super.onNewIntent(intent)
        setIntent(intent)
        if (intent.action == Intent.ACTION_SEND) handleExternalShare(intent)
        else intent.data?.toString()?.let(::handlePairingUri)
    }

    /** Sharesheet content URI, not a filesystem path; permission comes from the source app. */
    private fun handleExternalShare(intent: Intent?) {
        if (intent?.action != Intent.ACTION_SEND) return
        val uri = if (Build.VERSION.SDK_INT >= 33)
            intent.getParcelableExtra(Intent.EXTRA_STREAM, Uri::class.java)
        else {
            @Suppress("DEPRECATION")
            val legacy: Uri? = intent.getParcelableExtra(Intent.EXTRA_STREAM)
            legacy
        } ?: intent.clipData?.takeIf { it.itemCount > 0 }?.getItemAt(0)?.uri
        if (uri?.scheme != "content") {
            Toast.makeText(this, "DeskFuse needs a shared document from an Android file app", Toast.LENGTH_LONG).show()
            return
        }
        sharedDocumentUri = uri
        appDialogVisible = true
    }

    private fun uploadSelectedDocument(uri: Uri, destination: String?, openOnPc: Boolean = false) {
        try {
            var name = uri.lastPathSegment?.substringAfterLast('/') ?: "shared-document"
            var size = -1L
            contentResolver.query(uri, arrayOf(OpenableColumns.DISPLAY_NAME, OpenableColumns.SIZE),
                null, null, null)?.use { cursor ->
                if (cursor.moveToFirst()) {
                    val nameCol = cursor.getColumnIndex(OpenableColumns.DISPLAY_NAME)
                    val sizeCol = cursor.getColumnIndex(OpenableColumns.SIZE)
                    if (nameCol >= 0 && !cursor.isNull(nameCol)) name = cursor.getString(nameCol)
                    if (sizeCol >= 0 && !cursor.isNull(sizeCol)) size = cursor.getLong(sizeCol)
                }
            }
            // No path is sent from Android. The host validates a Windows destination
            // and applies the signed-in user's ACLs before writing anything.
            controller.uploadFile(name, size,
                open = { contentResolver.openInputStream(uri) }, destination = destination,
                openOnPc = openOnPc)
        } catch (e: SecurityException) {
            Toast.makeText(this, "Document permission expired: share the file again", Toast.LENGTH_LONG).show()
        } catch (e: RuntimeException) {
            Toast.makeText(this, "Cannot read shared document", Toast.LENGTH_LONG).show()
        }
    }

    /**
     * FR-DPI-09 / FR-MOB-01 / FR-MOB-02 — rotation, fold and font-scale changes
     * re-derive the TARGET. The source window on the PC is never involved.
     */
    override fun onConfigurationChanged(newConfig: android.content.res.Configuration) {
        super.onConfigurationChanged(newConfig)
        refreshProfile()
    }

    private fun refreshProfile() {
        val next = profiles.current(this, insets)
        val previous = profile
        profile = next
        if (previous == null) return
        if (previous.geometryKey() != next.geometryKey()) {
            controller.onProfileChanged(next)
        }
    }

    /**
     * FR-REF-03 — keep-awake with a policy the user can see, rather than a
     * silent wakelock. A regressed keep-awake is a live one-star theme in a
     * leading competitor.
     */
    override fun onResume() {
        super.onResume()
        controller.setPhoneMicrophoneForeground(true)
        window.addFlags(WindowManager.LayoutParams.FLAG_KEEP_SCREEN_ON)
        refreshProfile()
        // A previously approved PC requires no scanning or further confirmation.
        if (controller.state.value is SessionState.Idle && !pcPickerVisible) {
            val existing = pairings.last()
            val current = profile
            if (existing != null && current != null) controller.start(existing, current)
        }
        applyPreferredRefreshRate()
    }

    override fun onPause() {
        controller.setPhoneMicrophoneForeground(false)
        super.onPause()
        // We only release the screen lock; the SESSION keeps running in the
        // foreground service so a glance at a notification does not kill it.
        window.clearFlags(WindowManager.LayoutParams.FLAG_KEEP_SCREEN_ON)
    }

    /**
     * FR-REF-01 — ask the platform for the highest mode the panel offers, so a
     * 120 Hz phone is not quietly presented at 60.
     */
    private fun applyPreferredRefreshRate() {
        val p = profile ?: return
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.R) {
            val best = display?.supportedModes
                ?.filter { it.physicalWidth == p.physicalW || it.physicalHeight == p.physicalH }
                ?.maxByOrNull { it.refreshRate }
            if (best != null) {
                window.attributes = window.attributes.apply { preferredDisplayModeId = best.modeId }
            }
        }
        controller.setRefresh(p.maxRefreshHz)
    }

    // Explicit opt-in transfers only; never monitor the phone clipboard in the background.
    private fun sendPhoneClipboard() {
        val clipboard = getSystemService(Context.CLIPBOARD_SERVICE) as ClipboardManager
        val item = clipboard.primaryClip?.getItemAt(0) ?: return
        val text = item.coerceToText(this)?.toString() ?: return
        controller.sendClipboard(text)
    }

    // ── input ────────────────────────────────────────────────────────────────

    private fun showPhoneKeyboard() {
        surfaceView.requestFocus()
        (getSystemService(Context.INPUT_METHOD_SERVICE) as InputMethodManager)
            .showSoftInput(surfaceView, InputMethodManager.SHOW_IMPLICIT)
    }

    override fun dispatchTouchEvent(event: MotionEvent): Boolean {
        if (controller.state.value !is SessionState.Streaming || appDialogVisible) {
            remoteTouchActive = false
            return super.dispatchTouchEvent(event)
        }
        if (event.actionMasked == MotionEvent.ACTION_DOWN) {
            val density = resources.displayMetrics.density
            val bars = insets?.getInsets(WindowInsetsCompat.Type.systemBars())
            // These bands are occupied by the native app overlay. A click on Ctrl,
            // Clipboard, Keyboard or End must not also inject a Windows pointer.
            val topBand = (bars?.top ?: 0) + (76 * density).toInt()
            val bottomBand = (bars?.bottom ?: 0) + (112 * density).toInt()
            val y = event.y
            remoteTouchActive = y >= topBand && y < window.decorView.height - bottomBand
        }
        if (!remoteTouchActive) return super.dispatchTouchEvent(event)
        val events = input.onTouch(event)
        events.forEach(controller::sendInput)
        if (event.actionMasked == MotionEvent.ACTION_UP ||
            event.actionMasked == MotionEvent.ACTION_CANCEL) remoteTouchActive = false
        // A remote gesture belongs to the remote Surface, not Compose controls.
        return if (events.isNotEmpty()) true else super.dispatchTouchEvent(event)
    }

    /**
     * FR-IN-02 — hard keyboards on foldables, DeX and tablet covers are broken
     * nearly everywhere in this category. We take the raw event, including the
     * scancode, before the IME can swallow it.
     */
    override fun dispatchKeyEvent(event: KeyEvent): Boolean {
        if (controller.state.value is SessionState.Streaming && !appDialogVisible) {
            // Volume and power stay with the system; everything else is the
            // remote session's.
            val systemKey = event.keyCode in setOf(
                KeyEvent.KEYCODE_VOLUME_UP,
                KeyEvent.KEYCODE_VOLUME_DOWN,
                KeyEvent.KEYCODE_POWER,
            )
            if (!systemKey) {
                input.onKey(event)?.let {
                    controller.sendInput(it)
                    if (event.action == KeyEvent.ACTION_UP) input.clearModifiers()
                    return true
                }
            }
        }
        return super.dispatchKeyEvent(event)
    }

    override fun dispatchGenericMotionEvent(event: MotionEvent): Boolean {
        if (controller.state.value is SessionState.Streaming && !appDialogVisible &&
            event.action == MotionEvent.ACTION_SCROLL
        ) {
            val dx = event.getAxisValue(MotionEvent.AXIS_HSCROLL)
            val dy = event.getAxisValue(MotionEvent.AXIS_VSCROLL)
            input.onScroll(event.x, event.y, dx * SCROLL_STEP, dy * SCROLL_STEP)
                ?.let(controller::sendInput)
            return true
        }
        return super.dispatchGenericMotionEvent(event)
    }

    private fun sendSyntheticKey(keyCode: Int) {
        controller.sendInput(
            InputEvent.Key(
                scancode = 0,
                keyCode = keyCode,
                down = true,
                modifiers = input.stickyModifiers,
            ),
        )
        controller.sendInput(
            InputEvent.Key(
                scancode = 0,
                keyCode = keyCode,
                down = false,
                modifiers = input.stickyModifiers,
            ),
        )
        input.clearModifiers()
    }

    // ── pairing ──────────────────────────────────────────────────────────────

    /**
     * The phone becomes a speaker for this PC. The host creates a Windows
     * playback endpoint for it; the PC's sound follows to this phone.
     */
    private fun togglePhoneSpeaker() {
        val current = profile ?: return
        val running = SpeakerService.state.value
        if (running is SpeakerController.State.Playing || running is SpeakerController.State.Waiting) {
            SpeakerService.stop(this)
        } else {
            SpeakerService.start(this, current)
        }
    }

    private fun beginPairing() {
        pcPickerVisible = true
        pcPairingError = null
        findPcs()
    }

    private fun findPcs() {
        if (pcDiscoveryBusy || pcPairingBusy) return
        pcDiscoveryBusy = true
        pcPairingError = null
        lifecycleScope.launch {
            runCatching { CameraFreeDiscovery.scan() }
                .onSuccess { discoveredPcs = it }
                .onFailure {
                    pcPairingError = "Discovery unavailable. Enter the Windows PC address instead."
                }
            pcDiscoveryBusy = false
        }
    }

    private fun enrollWith(pc: DiscoveredPc) {
        if (pcPairingBusy) return
        val p = profile ?: profiles.current(this, insets).also { profile = it }
        pcPairingBusy = true
        pcPairingCode = null
        pcPairingError = null
        lifecycleScope.launch {
            try {
                val pairing = CameraFreeEnrollment.enroll(
                    pc, p.deviceId, p.deviceLabel,
                    onCode = { pcPairingCode = it },
                )
                pairings.remember(pairing)
                pcPickerVisible = false
                controller.start(pairing, p)
            } catch (ex: CancellationException) {
                throw ex
            } catch (ex: Exception) {
                pcPairingError = EnrollmentMessages.describeFailure(ex)
            } finally {
                pcPairingBusy = false
                pcPairingCode = null
            }
        }
    }

    private fun handlePairingUri(raw: String) {
        when (val result = PairingPayload.parse(raw)) {
            is PairingPayload.Companion.Result.Ok -> {
                val p = profile ?: profiles.current(this, insets).also { profile = it }
                pairings.remember(result.payload)
                controller.start(result.payload, p)
            }

            is PairingPayload.Companion.Result.Invalid -> {
                // Even a bad scan is a named state, not a toast that vanishes.
                controller.stop(result.reason)
            }
        }
    }

    private fun retry() {
        val p = profile ?: return
        val last = pairings.last()
        if (last != null) controller.start(last, p) else beginPairing()
    }

    override fun onDestroy() {
        controller.stop("activity destroyed")
        super.onDestroy()
    }

    private companion object {
        const val SCROLL_STEP = 40f
    }
}


