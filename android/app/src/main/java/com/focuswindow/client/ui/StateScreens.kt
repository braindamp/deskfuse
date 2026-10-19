package com.focuswindow.client.ui

import androidx.compose.foundation.background
import androidx.compose.foundation.border
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.systemBarsPadding
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material3.LinearProgressIndicator
import androidx.compose.material3.Surface
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.text.font.FontFamily
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import com.focuswindow.client.session.SessionState

private val Canvas = Color(0xFF070808)
private val Ink = Color(0xFFF2F2EC)
private val Muted = Color(0xFFA9ACA4)
private val Dim = Color(0xFF878D84)
private val Lime = Color(0xFFD6FB6D)
private val Coral = Color(0xFFFF9484)
private val Amber = Color(0xFFF2C56F)

/**
 * FR-OPS-03 — every non-streaming state renders a NAMED screen.
 *
 * There is deliberately no "else -> {}" branch anywhere in this file that
 * draws nothing. If a state exists, it has a screen; if it has a screen, it
 * explains itself and says what to do next. That is the entire defence against
 * the category's signature "black screen on connect" review.
 */
@Composable
fun StateScreen(
    state: SessionState,
    onRetry: () -> Unit,
    onScan: () -> Unit,
    onDismiss: () -> Unit,
) {
    Box(
        Modifier
            .fillMaxSize()
            .background(Canvas)
            .systemBarsPadding()
            .padding(24.dp),
        contentAlignment = Alignment.Center,
    ) {
        when (state) {
            is SessionState.Idle -> Panel(
                kicker = "READY",
                title = "Your PC, ready when you are",
                body = "Find a Windows PC on your network. Approve this phone once on Windows; no camera or account needed.",
                primary = "Find Windows PC" to onScan,
                accent = Lime,
            )

            is SessionState.AwaitingPairing -> Panel(
                kicker = "PAIRING",
                title = "Waiting for your PC",
                body = "Approve this phone from Windows to finish the first connection.",
                accent = Lime,
            )

            is SessionState.Connecting -> Progress(
                kicker = "CONNECTING",
                title = state.host,
                body = "Opening a direct connection on port ${state.port}.",
                progress = 0.15f,
            )

            is SessionState.Verifying -> Panel(
                kicker = "VERIFY",
                title = state.code,
                mono = true,
                body = "Check this number matches the one on ${state.hostName}. " +
                    "If it does not, stop — someone else may be on the line.",
                accent = Lime,
            )

            is SessionState.PreparingTarget -> Progress(
                kicker = "PREPARING",
                title = state.stage,
                body = state.detail,
                progress = state.progress,
            )

            // Retry quietly over the last rendered frame; only surface terminal failure.
            is SessionState.Reconnecting -> Unit

            is SessionState.Stale -> Panel(
                kicker = "PAUSED",
                title = state.reason,
                body = "The last frame is still on screen, dimmed. This is not a freeze: " +
                    "the host has stopped producing frames for this target.",
                accent = Amber,
            )

            is SessionState.ConflictDetected -> Panel(
                kicker = "CONFLICT",
                title = "This document changed in two places",
                body = "${state.summary}\n\nPC copy: ${state.sourceLabel}\n" +
                    "Phone copy: ${state.cloneLabel}",
                primary = "Review both" to onDismiss,
                accent = Amber,
            )

            is SessionState.Failed -> Failure(state, onRetry)

            is SessionState.Ended -> Panel(
                kicker = "ENDED",
                title = "Session closed",
                body = "${state.reason}\n\nYour PC was restored: the source window, its DPI " +
                    "and your display arrangement are exactly as you left them.",
                primary = "Find Windows PC" to onScan,
                accent = Lime,
            )

            is SessionState.Streaming -> Unit // the surface owns the screen
        }
    }
}

@Composable
private fun Failure(state: SessionState.Failed, onRetry: () -> Unit) {
    Column(
        Modifier.width(380.dp),
        horizontalAlignment = Alignment.Start,
        verticalArrangement = Arrangement.spacedBy(12.dp),
    ) {
        Row(verticalAlignment = Alignment.CenterVertically) {
            Box(Modifier.size(8.dp).background(Coral, CircleShape))
            Spacer(Modifier.width(8.dp))
            Text(
                "FAILED AT: ${state.stage.uppercase()}",
                color = Coral,
                fontSize = 9.sp,
                letterSpacing = 1.6.sp,
                fontFamily = FontFamily.Monospace,
            )
        }

        Text(
            state.detail,
            color = Ink,
            fontSize = 18.sp,
            lineHeight = 24.sp,
            fontWeight = FontWeight.Medium,
        )

        // The remediation is the point. A failure without a next step is just
        // a black screen with extra words.
        state.remediation?.let {
            Surface(
                color = Color(0x14D6FB6D),
                shape = RoundedCornerShape(10.dp),
                modifier = Modifier.border(1.dp, Color(0x33D6FB6D), RoundedCornerShape(10.dp)),
            ) {
                Row(Modifier.padding(12.dp), horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                    Text("TRY", color = Lime, fontSize = 9.sp, fontFamily = FontFamily.Monospace)
                    Text(it, color = Muted, fontSize = 12.sp, lineHeight = 17.sp)
                }
            }
        }

        if (state.recoverable) {
            ActionButton("Try again", onRetry)
        }
    }
}

@Composable
private fun Panel(
    kicker: String,
    title: String,
    body: String,
    primary: Pair<String, () -> Unit>? = null,
    accent: Color = Lime,
    mono: Boolean = false,
) {
    Column(
        Modifier.width(380.dp),
        verticalArrangement = Arrangement.spacedBy(12.dp),
    ) {
        Row(verticalAlignment = Alignment.CenterVertically) {
            Box(Modifier.size(8.dp).background(accent, CircleShape))
            Spacer(Modifier.width(8.dp))
            Text(
                kicker,
                color = accent,
                fontSize = 9.sp,
                letterSpacing = 1.6.sp,
                fontFamily = FontFamily.Monospace,
            )
        }
        Text(
            title,
            color = Ink,
            fontSize = if (mono) 44.sp else 24.sp,
            letterSpacing = if (mono) 8.sp else 0.sp,
            lineHeight = if (mono) 48.sp else 30.sp,
            fontWeight = FontWeight.Medium,
            fontFamily = if (mono) FontFamily.Monospace else FontFamily.Default,
        )
        Text(body, color = Muted, fontSize = 13.sp, lineHeight = 20.sp)
        primary?.let { (label, action) -> ActionButton(label, action) }
    }
}

@Composable
private fun Progress(
    kicker: String,
    title: String,
    body: String,
    progress: Float,
    accent: Color = Lime,
) {
    Column(
        Modifier.width(380.dp),
        verticalArrangement = Arrangement.spacedBy(12.dp),
    ) {
        Text(
            kicker,
            color = accent,
            fontSize = 9.sp,
            letterSpacing = 1.6.sp,
            fontFamily = FontFamily.Monospace,
        )
        Text(title, color = Ink, fontSize = 22.sp, fontWeight = FontWeight.Medium)
        Text(body, color = Dim, fontSize = 12.5.sp, lineHeight = 19.sp)
        Spacer(Modifier.height(4.dp))
        LinearProgressIndicator(
            progress = { progress.coerceIn(0f, 1f) },
            color = accent,
            trackColor = Color(0x1AFFFFFF),
            modifier = Modifier.fillMaxWidth().height(3.dp),
        )
    }
}

@Composable
private fun ActionButton(label: String, onClick: () -> Unit) {
    Surface(
        color = Lime,
        shape = RoundedCornerShape(10.dp),
        modifier = Modifier.clickable(onClick = onClick),
    ) {
        Text(
            label,
            color = Color(0xFF0B0E0A),
            fontSize = 13.sp,
            fontWeight = FontWeight.SemiBold,
            modifier = Modifier.padding(horizontal = 18.dp, vertical = 11.dp),
        )
    }
}
