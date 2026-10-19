package com.focuswindow.client.ui

import androidx.compose.animation.AnimatedVisibility
import androidx.compose.animation.fadeIn
import androidx.compose.animation.fadeOut
import androidx.compose.foundation.background
import androidx.compose.foundation.border
import androidx.compose.foundation.clickable
import androidx.compose.foundation.horizontalScroll
import androidx.compose.foundation.interaction.MutableInteractionSource
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.navigationBarsPadding
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.statusBarsPadding
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material3.Surface
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.foundation.rememberScrollState
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.text.font.FontFamily
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import com.focuswindow.client.profile.PhoneProfile
import com.focuswindow.client.protocol.Modifiers
import com.focuswindow.client.session.SessionAdvisory
import com.focuswindow.client.session.SessionState

/**
 * The session overlay.
 *
 * FR-MOB-04 — one-handed reach: every primary control lives in the bottom
 * third, and the document area is never obstructed by persistent chrome.
 * FR-DPI-08 / FR-ENC-04 — the density and codec readout is one tap away and is
 * included verbatim in a diagnostics report.
 */
@Composable
fun SessionChrome(
    state: SessionState,
    advisories: List<SessionAdvisory>,
    modifierMask: Int,
    onToggleModifier: (Int) -> Unit,
    onKeyShortcut: (Int) -> Unit,
    onToggleDiagnostics: () -> Unit,
    diagnosticsVisible: Boolean,
    onDisconnect: () -> Unit,
    onSendClipboard: () -> Unit,
    onReceiveClipboard: () -> Unit,
    onShowKeyboard: () -> Unit,
    onSelectWindow: () -> Unit,
    onSendFile: () -> Unit,
    pcAudioStatus: String,
    onTogglePcAudio: () -> Unit,
    onAdvisoryAction: (SessionAdvisory) -> Unit,
) {
    Box(Modifier.fillMaxSize()) {

        // ── Top: tier + density truth, always visible while streaming ────────
        if (state is SessionState.Streaming) {
            TopStatusBar(
                state = state,
                onToggleDiagnostics = onToggleDiagnostics,
                diagnosticsVisible = diagnosticsVisible,
                modifier = Modifier.align(Alignment.TopCenter),
            )
        }

        // ── Advisories: never silent degradation ─────────────────────────────
        Column(
            modifier = Modifier
                .align(Alignment.TopCenter)
                .statusBarsPadding()
                .padding(top = 44.dp, start = 12.dp, end = 12.dp),
            verticalArrangement = Arrangement.spacedBy(6.dp),
        ) {
            advisories.forEach { advisory ->
                AdvisoryChip(advisory, onClick = { onAdvisoryAction(advisory) })
            }
        }

        AnimatedVisibility(
            visible = diagnosticsVisible && state is SessionState.Streaming,
            enter = fadeIn(),
            exit = fadeOut(),
            modifier = Modifier.align(Alignment.TopEnd),
        ) {
            (state as? SessionState.Streaming)?.let { DiagnosticsPanel(it) }
        }

        // ── Bottom: modifier cluster within thumb reach ──────────────────────
        if (state is SessionState.Streaming) {
            ModifierBar(
                mask = modifierMask,
                onToggle = onToggleModifier,
                onShortcut = onKeyShortcut,
                onDisconnect = onDisconnect,
                onSendClipboard = onSendClipboard,
                onReceiveClipboard = onReceiveClipboard,
                onShowKeyboard = onShowKeyboard,
                onSelectWindow = onSelectWindow,
                onSendFile = onSendFile,
                pcAudioStatus = pcAudioStatus,
                onTogglePcAudio = onTogglePcAudio,
                modifier = Modifier.align(Alignment.BottomCenter),
            )
        }
    }
}

@Composable
private fun TopStatusBar(
    state: SessionState.Streaming,
    onToggleDiagnostics: () -> Unit,
    diagnosticsVisible: Boolean,
    modifier: Modifier = Modifier,
) {
    Row(
        modifier = modifier
            .statusBarsPadding()
            .fillMaxWidth()
            .padding(horizontal = 12.dp, vertical = 6.dp),
        verticalAlignment = Alignment.CenterVertically,
        horizontalArrangement = Arrangement.spacedBy(8.dp),
    ) {
        // The single most important pixel in the app: is this native or not?
        // Three honest states. "1:1" is about PIXELS; "NATIVE" additionally means the host proved the
        // phone's exact density. 1:1 at the wrong density (Windows only offers 250%/300%, never
        // 262.5%) is sharp but not native, and must not be labelled "SCALED".
        StatusPill(
            text = when {
                state.oneToOne && state.record.native -> "1:1 NATIVE"
                state.oneToOne -> "1:1 · OFF-DENSITY"
                else -> "SCALED"
            },
            tone = if (state.oneToOne && state.record.native) Tone.Good else Tone.Warn,
        )
        StatusPill(text = "${state.record.dpi} DPI", tone = Tone.Neutral)
        StatusPill(
            text = "${"%.0f".format(state.tokenDp)} dp",
            tone = if (state.readable) Tone.Neutral else Tone.Warn,
        )
        Spacer(Modifier.weight(1f))
        StatusPill(
            text = "${state.record.codec}/${state.record.pixelFormat}",
            tone = Tone.Neutral,
        )
        StatusPill(
            text = if (diagnosticsVisible) "HIDE" else "STATS",
            tone = Tone.Neutral,
            onClick = onToggleDiagnostics,
        )
    }
}

private enum class Tone { Good, Warn, Bad, Neutral }

@Composable
private fun StatusPill(text: String, tone: Tone, onClick: (() -> Unit)? = null) {
    val (bg, fg) = when (tone) {
        Tone.Good -> Color(0x33D6FB6D) to Color(0xFFD6FB6D)
        Tone.Warn -> Color(0x33F2C56F) to Color(0xFFF2C56F)
        Tone.Bad -> Color(0x33FF9484) to Color(0xFFFF9484)
        Tone.Neutral -> Color(0x40000000) to Color(0xFFD8DAD4)
    }
    Surface(
        color = bg,
        shape = RoundedCornerShape(6.dp),
        modifier = Modifier.then(
            if (onClick != null) Modifier.clickableNoRipple(onClick) else Modifier,
        ),
    ) {
        Text(
            text = text,
            color = fg,
            fontSize = 9.sp,
            fontFamily = FontFamily.Monospace,
            fontWeight = FontWeight.Medium,
            modifier = Modifier.padding(horizontal = 6.dp, vertical = 3.dp),
        )
    }
}

@Composable
private fun AdvisoryChip(advisory: SessionAdvisory, onClick: () -> Unit) {
    val accent = when (advisory.severity) {
        SessionAdvisory.Severity.INFO -> Color(0xFFA3C8FF)
        SessionAdvisory.Severity.WARN -> Color(0xFFF2C56F)
        SessionAdvisory.Severity.CRITICAL -> Color(0xFFFF9484)
    }
    Surface(
        color = Color(0xE6101412),
        shape = RoundedCornerShape(10.dp),
        modifier = Modifier
            .fillMaxWidth()
            .border(1.dp, accent.copy(alpha = 0.35f), RoundedCornerShape(10.dp))
            .clickableNoRipple(onClick),
    ) {
        Row(
            modifier = Modifier.padding(10.dp),
            verticalAlignment = Alignment.Top,
            horizontalArrangement = Arrangement.spacedBy(8.dp),
        ) {
            Box(
                Modifier
                    .padding(top = 4.dp)
                    .size(6.dp)
                    .background(accent, CircleShape),
            )
            Text(
                text = advisory.message,
                color = Color(0xFFD8DAD4),
                fontSize = 11.sp,
                lineHeight = 15.sp,
                modifier = Modifier.weight(1f),
            )
            advisory.action?.let {
                Text(
                    text = it,
                    color = accent,
                    fontSize = 10.sp,
                    fontWeight = FontWeight.Medium,
                    fontFamily = FontFamily.Monospace,
                )
            }
        }
    }
}

/**
 * FR-DPI-08 — the proof that the promise is being kept, in numbers the user
 * can read out to support.
 */
@Composable
private fun DiagnosticsPanel(state: SessionState.Streaming) {
    val rows = buildList {
        add("target" to "${state.record.widthPx}×${state.record.heightPx} px")
        add("panel" to "${state.profile.physicalW}×${state.profile.physicalH} px")
        add("density" to "${state.record.dpi} DPI · expect ${state.profile.expectedDpi}")
        add("canvas" to "${state.record.canvasDpW}×${state.record.canvasDpH} dp")
        add("scale" to "${"%.2f".format(state.presentation.scale)}× (${state.presentation.mode})")
        add("token" to "${"%.1f".format(state.tokenDp)} dp")
        add("codec" to "${state.record.codec} / ${state.record.pixelFormat}")
        add("refresh" to "${state.record.refreshHz} Hz")
        add("method" to state.record.densityMethod.name)
        add("tier" to state.record.tierLabel(state.profile))
        add("state" to state.record.stateModel.name)
        state.stats?.let {
            add("decoder" to "${it.codecName}${if (it.hardwareAccelerated) " (hw)" else " (sw)"}")
            add("frames" to "${it.framesDecoded} ok · ${it.framesDropped} drop · ${it.framesStale} stale")
            add("decode" to "${"%.1f".format(it.decodeLatencyMs)} ms")
        }
        add("glass" to "${"%.0f".format(state.glassToGlassMs)} ms p50")
        add("source" to if (state.invariantClean) "unchanged ✓" else "DRIFT: ${state.invariantDrift}")
    }

    Surface(
        color = Color(0xF20A0D0B),
        shape = RoundedCornerShape(12.dp),
        modifier = Modifier
            .statusBarsPadding()
            .padding(12.dp)
            .width(230.dp)
            .border(1.dp, Color(0x1AFFFFFF), RoundedCornerShape(12.dp)),
    ) {
        Column(Modifier.padding(12.dp), verticalArrangement = Arrangement.spacedBy(3.dp)) {
            Text(
                "DIAGNOSTICS",
                color = Color(0xFFD6FB6D),
                fontSize = 8.sp,
                letterSpacing = 1.6.sp,
                fontFamily = FontFamily.Monospace,
            )
            Spacer(Modifier.height(4.dp))
            rows.forEach { (k, v) ->
                Row(Modifier.fillMaxWidth(), horizontalArrangement = Arrangement.SpaceBetween) {
                    Text(k, color = Color(0xFF7F857B), fontSize = 9.sp, fontFamily = FontFamily.Monospace)
                    Text(
                        v,
                        color = Color(0xFFE8EAE3),
                        fontSize = 9.sp,
                        fontFamily = FontFamily.Monospace,
                    )
                }
            }
        }
    }
}

/**
 * FR-IN-03 — Ctrl/Alt/Esc/Tab and arrows reachable in one thumb.
 *
 * This is the pattern the one well-received new entrant in the category got
 * right, and it is missing almost everywhere else.
 */
@Composable
private fun ModifierBar(
    mask: Int,
    onToggle: (Int) -> Unit,
    onShortcut: (Int) -> Unit,
    onDisconnect: () -> Unit,
    onSendClipboard: () -> Unit,
    onReceiveClipboard: () -> Unit,
    onShowKeyboard: () -> Unit,
    onSelectWindow: () -> Unit,
    onSendFile: () -> Unit,
    pcAudioStatus: String,
    onTogglePcAudio: () -> Unit,
    modifier: Modifier = Modifier,
) {
    Column(modifier = modifier, verticalArrangement = Arrangement.spacedBy(4.dp)) {
    Row(
        modifier = Modifier.fillMaxWidth().horizontalScroll(rememberScrollState()).padding(horizontal = 10.dp),
        horizontalArrangement = Arrangement.spacedBy(6.dp),
    ) {
        ModKey("Send clip → PC", false) { onSendClipboard() }
        ModKey("PC clip → phone", false) { onReceiveClipboard() }
        ModKey("Keyboard", false) { onShowKeyboard() }
        ModKey("Apps", false) { onSelectWindow() }
        ModKey("PC files", false) { onSendFile() }
        ModKey("PC sound: $pcAudioStatus", pcAudioStatus == "On") { onTogglePcAudio() }
    }
    Row(
        modifier = Modifier
            .navigationBarsPadding()
            .fillMaxWidth()
            .padding(horizontal = 10.dp, vertical = 8.dp),
        horizontalArrangement = Arrangement.spacedBy(6.dp),
        verticalAlignment = Alignment.CenterVertically,
    ) {
        ModKey("Ctrl", mask and Modifiers.CTRL != 0) { onToggle(Modifiers.CTRL) }
        ModKey("Alt", mask and Modifiers.ALT != 0) { onToggle(Modifiers.ALT) }
        ModKey("Shift", mask and Modifiers.SHIFT != 0) { onToggle(Modifiers.SHIFT) }
        ModKey("Win", mask and Modifiers.META != 0) { onToggle(Modifiers.META) }
        ModKey("Esc", false) { onShortcut(android.view.KeyEvent.KEYCODE_ESCAPE) }
        ModKey("Tab", false) { onShortcut(android.view.KeyEvent.KEYCODE_TAB) }
        Spacer(Modifier.weight(1f))
        ModKey("End", false, tone = Tone.Bad) { onDisconnect() }
    }
    }
}

@Composable
private fun ModKey(
    label: String,
    active: Boolean,
    tone: Tone = Tone.Neutral,
    onClick: () -> Unit,
) {
    val bg = when {
        active -> Color(0xFFD6FB6D)
        tone == Tone.Bad -> Color(0x33FF9484)
        else -> Color(0xCC161B18)
    }
    val fg = when {
        active -> Color(0xFF0B0E0A)
        tone == Tone.Bad -> Color(0xFFFF9484)
        else -> Color(0xFFD8DAD4)
    }
    Surface(
        color = bg,
        shape = RoundedCornerShape(8.dp),
        modifier = Modifier
            .height(36.dp)
            .border(1.dp, Color(0x1AFFFFFF), RoundedCornerShape(8.dp))
            .clickableNoRipple(onClick),
    ) {
        Box(Modifier.padding(horizontal = 12.dp), contentAlignment = Alignment.Center) {
            Text(
                label,
                color = fg,
                fontSize = 11.sp,
                fontWeight = if (active) FontWeight.SemiBold else FontWeight.Normal,
                fontFamily = FontFamily.Monospace,
            )
        }
    }
}

/**
 * Chrome taps must never feel like app chrome stealing from the remote
 * surface, so we drop the ripple and keep the hit target explicit.
 */
@Composable
private fun Modifier.clickableNoRipple(onClick: () -> Unit): Modifier {
    val interaction = remember { MutableInteractionSource() }
    return this.clickable(interactionSource = interaction, indication = null, onClick = onClick)
}

/** Used by the readability advisory copy (FR-DPI-12). */
val PhoneProfile.readabilityNote: String
    get() = "A ${PhoneProfile.REFERENCE_TOKEN_DP.toInt()} dp token renders at " +
        "${(PhoneProfile.REFERENCE_TOKEN_DP * dpr).toInt()} physical pixels on this screen."
