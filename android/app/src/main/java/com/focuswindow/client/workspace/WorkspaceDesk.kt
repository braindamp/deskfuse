package com.focuswindow.client.workspace

import androidx.compose.foundation.background
import androidx.compose.foundation.clickable
import androidx.compose.foundation.gestures.detectDragGestures
import androidx.compose.foundation.gestures.detectTransformGestures
import androidx.compose.foundation.horizontalScroll
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.offset
import androidx.compose.foundation.layout.navigationBarsPadding
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.statusBarsPadding
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material3.Button
import androidx.compose.material3.DropdownMenu
import androidx.compose.material3.DropdownMenuItem
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.Surface
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableFloatStateOf
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.TransformOrigin
import androidx.compose.ui.graphics.graphicsLayer
import androidx.compose.ui.input.pointer.pointerInput
import androidx.compose.ui.platform.LocalDensity
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.IntOffset
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import kotlin.math.roundToInt

/**
 * Phone-side composition over the existing authenticated window list.
 * No previews or individual window capture: Focus only activates Windows HWND.
 */
@Composable
fun WorkspaceDesk(
    snapshot: DeskSnapshot,
    onRefresh: () -> Unit,
    onClose: () -> Unit,
    onFocus: (String) -> Unit,
    onMove: (String, Float, Float) -> Unit,
    onMoveToTopic: (String, String) -> Unit,
    onAddTopic: (String) -> Boolean,
    onRemoveTopic: (String) -> Unit,
) {
    val density = LocalDensity.current.density
    var zoom by remember { mutableFloatStateOf(1f) }
    var panX by remember { mutableFloatStateOf(0f) }
    var panY by remember { mutableFloatStateOf(0f) }
    var selectedTopic by remember { mutableStateOf<String?>(null) }
    var enteringTopic by remember { mutableStateOf(false) }
    var newTopic by remember { mutableStateOf("") }
    val selected = selectedTopic?.takeIf { id -> snapshot.topics.any { it.id == id } }
    val visible = snapshot.cards.filter { selected == null || it.topicId == selected }
    val lime = Color(0xFFD6FB6D)

    Box(Modifier.fillMaxSize().background(Color(0xFF0B100E))) {
        Box(
            Modifier.fillMaxSize().pointerInput(Unit) {
                detectTransformGestures { _, pan, change, _ ->
                    zoom = (zoom * change).coerceIn(0.55f, 2.5f)
                    panX += pan.x
                    panY += pan.y
                }
            },
        ) {
            if (visible.isEmpty()) {
                Text(
                    if (snapshot.cards.isEmpty()) "No windows yet. Try Refresh." else "No cards in this topic.",
                    color = Color(0xFF9CAAA0),
                    modifier = Modifier.align(Alignment.Center).padding(18.dp),
                )
            }
            visible.forEach { card ->
                var showTopics by remember(card.sourceId) { mutableStateOf(false) }
                Surface(
                    shape = RoundedCornerShape(14.dp),
                    color = Color(0xFF202A25),
                    modifier = Modifier
                        .offset {
                            IntOffset((card.x * density * zoom + panX).roundToInt(),
                                (card.y * density * zoom + panY).roundToInt())
                        }
                        .graphicsLayer {
                            scaleX = zoom
                            scaleY = zoom
                            transformOrigin = TransformOrigin(0f, 0f)
                        }
                        .size(164.dp, 130.dp)
                        .pointerInput(card.sourceId, zoom) {
                            detectDragGestures { change, amount ->
                                change.consume()
                                onMove(card.sourceId, amount.x / density / zoom, amount.y / density / zoom)
                            }
                        },
                ) {
                    Column(Modifier.padding(10.dp), verticalArrangement = Arrangement.spacedBy(4.dp)) {
                        Text(card.appLabel.uppercase(), fontSize = 10.sp, color = lime,
                            maxLines = 1, overflow = TextOverflow.Ellipsis)
                        Text(card.title, color = Color.White, fontSize = 12.sp,
                            maxLines = 2, overflow = TextOverflow.Ellipsis,
                            modifier = Modifier.weight(1f))
                        Row(verticalAlignment = Alignment.CenterVertically) {
                            Text("Focus PC", color = Color.White, fontSize = 11.sp,
                                fontWeight = FontWeight.Bold,
                                modifier = Modifier.clickable { onFocus(card.sourceId) }.padding(4.dp))
                            TextButton(onClick = { showTopics = true }) { Text("Group", fontSize = 10.sp) }
                            DropdownMenu(expanded = showTopics, onDismissRequest = { showTopics = false }) {
                                snapshot.topics.forEach { topic ->
                                    DropdownMenuItem(
                                        text = { Text(topic.name) },
                                        onClick = {
                                            showTopics = false
                                            onMoveToTopic(card.sourceId, topic.id)
                                        },
                                    )
                                }
                            }
                        }
                    }
                }
            }
        }

        Column(Modifier.align(Alignment.TopCenter).fillMaxWidth()
            .background(Color(0xF20D1511)).statusBarsPadding().padding(bottom = 8.dp)) {
            Row(Modifier.fillMaxWidth().padding(horizontal = 12.dp),
                horizontalArrangement = Arrangement.SpaceBetween,
                verticalAlignment = Alignment.CenterVertically) {
                Text("DESKFUSE / DESK", color = lime, fontWeight = FontWeight.Bold, fontSize = 14.sp)
                Row {
                    TextButton(onClick = onRefresh) { Text("Refresh") }
                    TextButton(onClick = onClose) { Text("Close") }
                }
            }
            Row(Modifier.fillMaxWidth().horizontalScroll(rememberScrollState()).padding(horizontal = 8.dp)) {
                TextButton(onClick = { selectedTopic = null }) {
                    Text("All", color = if (selected == null) lime else Color.LightGray)
                }
                snapshot.topics.forEach { topic ->
                    TextButton(onClick = { selectedTopic = topic.id }) {
                        Text(topic.name, color = if (selected == topic.id) lime else Color.LightGray)
                    }
                }
                TextButton(onClick = { enteringTopic = !enteringTopic }) { Text("+ Topic") }
            }
            if (enteringTopic) {
                Row(Modifier.padding(horizontal = 12.dp), verticalAlignment = Alignment.CenterVertically) {
                    OutlinedTextField(newTopic, onValueChange = { newTopic = it.take(32) },
                        label = { Text("Topic name") }, singleLine = true,
                        modifier = Modifier.weight(1f))
                    Button(onClick = {
                        if (onAddTopic(newTopic)) {
                            enteringTopic = false
                            newTopic = ""
                        }
                    }) { Text("Add") }
                }
            }
            if (selected != null && selected != DeskWorkspace.INBOX) {
                TextButton(onClick = { onRemoveTopic(selected); selectedTopic = null }) {
                    Text("Delete selected topic (cards return to Inbox)", fontSize = 11.sp)
                }
            }
        }

        Column(Modifier.align(Alignment.BottomCenter).fillMaxWidth()
            .background(Color(0xF20D1511)).navigationBarsPadding()
            .padding(horizontal = 14.dp, vertical = 10.dp)) {
            Text("Drag cards to arrange · Pan/pinch empty space to explore",
                color = Color(0xFFE1E9DF), fontSize = 11.sp)
            Text("Activation only: Focus PC switches the Windows foreground window. " +
                "Video remains the current desktop feed; no per-window preview/stream yet.",
                color = Color(0xFF9AAFA2), fontSize = 10.sp, lineHeight = 13.sp)
        }
    }
}
