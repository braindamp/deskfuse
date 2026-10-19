package com.focuswindow.client.input

import android.view.KeyEvent
import android.view.MotionEvent
import com.focuswindow.client.protocol.InputEvent
import com.focuswindow.client.protocol.Modifiers
import com.focuswindow.client.protocol.PointerAction
import com.focuswindow.client.protocol.PointerButton
import com.focuswindow.client.render.PhoneSurfaceView
import kotlin.math.abs
import kotlin.math.roundToInt

/**
 * FR-IN-01 — absolute touch is the DEFAULT, not a buried option.
 *
 * The corpus is unambiguous here: relative/trackpad emulation produces cursor
 * drift and lost double-clicks, and it is the single most common input
 * complaint across the category. Users of one leading client teach each other
 * to switch modes in reviews. We ship the good mode first and keep relative as
 * a labelled choice for the cases that genuinely want it.
 */
class InputMapper(
    private val surface: PhoneSurfaceView,
    var mode: Mode = Mode.ABSOLUTE,
) {
    enum class Mode { ABSOLUTE, RELATIVE }

    /** Sticky modifier state driven by the on-screen bar (FR-IN-03). */
    var stickyModifiers: Int = Modifiers.NONE

    private var lastX = 0f
    private var lastY = 0f
    private var downX = 0f
    private var downY = 0f

    /**
     * True once a gesture has moved past slop. A drag must NOT also be
     * delivered as a click when the finger lifts — that double-delivery is a
     * classic cause of "it selected the wrong thing" reports.
     */
    private var isDragging = false

    /** Touch slop before a press becomes a drag rather than a click. */
    private val slopPx = 12f

    /** Exposed so the chrome can show a drag indicator during a selection. */
    val dragging: Boolean get() = isDragging

    fun onTouch(event: MotionEvent): List<InputEvent> {
        // FR-IN-05 — a stylus carries pressure and tilt; a finger does not.
        val isStylus = event.getToolType(0) == MotionEvent.TOOL_TYPE_STYLUS
        val button = resolveButton(event, isStylus)

        return when (mode) {
            Mode.ABSOLUTE -> absolute(event, button, isStylus)
            Mode.RELATIVE -> relative(event, button)
        }
    }

    private fun absolute(
        event: MotionEvent,
        button: PointerButton,
        isStylus: Boolean,
    ): List<InputEvent> {
        val mapped = surface.toTargetPixels(event.x, event.y)
            ?: return emptyList() // outside the stream: clamp by ignoring (FR-TGT-05)
        val (x, y) = mapped

        val pressure = if (isStylus) event.pressure.coerceIn(0f, 1f) else 0f
        val tiltX: Float
        val tiltY: Float
        if (isStylus) {
            // Android gives tilt + orientation; the host wants per-axis degrees.
            val tilt = Math.toDegrees(event.getAxisValue(MotionEvent.AXIS_TILT).toDouble())
            val orientation = event.orientation.toDouble()
            tiltX = (tilt * kotlin.math.sin(orientation)).toFloat()
            tiltY = (-tilt * kotlin.math.cos(orientation)).toFloat()
        } else {
            tiltX = 0f
            tiltY = 0f
        }

        fun pointer(action: PointerAction) = InputEvent.Pointer(
            x = x,
            y = y,
            action = action,
            button = button,
            pressure = pressure,
            tiltX = tiltX,
            tiltY = tiltY,
            isStylus = isStylus,
        )

        return when (event.actionMasked) {
            MotionEvent.ACTION_DOWN -> {
                downX = event.x
                downY = event.y
                isDragging = false
                listOf(pointer(PointerAction.DOWN))
            }

            MotionEvent.ACTION_MOVE -> {
                // FR-IN-04 — past slop this is a drag, not a click. The host
                // needs the distinction to avoid delivering both.
                if (!isDragging &&
                    (abs(event.x - downX) > slopPx || abs(event.y - downY) > slopPx)
                ) {
                    isDragging = true
                }
                listOf(pointer(PointerAction.MOVE))
            }

            MotionEvent.ACTION_UP, MotionEvent.ACTION_CANCEL -> {
                isDragging = false
                listOf(pointer(PointerAction.UP))
            }

            MotionEvent.ACTION_HOVER_MOVE ->
                listOf(pointer(PointerAction.HOVER))

            else -> emptyList()
        }
    }

    private fun relative(event: MotionEvent, button: PointerButton): List<InputEvent> {
        return when (event.actionMasked) {
            MotionEvent.ACTION_DOWN -> {
                lastX = event.x
                lastY = event.y
                emptyList()
            }

            MotionEvent.ACTION_MOVE -> {
                val dx = ((event.x - lastX) * RELATIVE_GAIN).roundToInt()
                val dy = ((event.y - lastY) * RELATIVE_GAIN).roundToInt()
                lastX = event.x
                lastY = event.y
                if (dx == 0 && dy == 0) emptyList()
                else listOf(InputEvent.Relative(dx, dy, button))
            }

            else -> emptyList()
        }
    }

    /**
     * FR-IN-04 — right click must exist. On touch we use a two-finger tap or a
     * long press; on a stylus, the barrel button; on a real mouse, the actual
     * secondary button.
     */
    private fun resolveButton(event: MotionEvent, isStylus: Boolean): PointerButton = when {
        event.buttonState and MotionEvent.BUTTON_SECONDARY != 0 -> PointerButton.RIGHT
        event.buttonState and MotionEvent.BUTTON_TERTIARY != 0 -> PointerButton.MIDDLE
        isStylus && event.buttonState and MotionEvent.BUTTON_STYLUS_PRIMARY != 0 ->
            PointerButton.RIGHT
        event.pointerCount >= 2 -> PointerButton.RIGHT
        else -> PointerButton.LEFT
    }

    fun onScroll(x: Float, y: Float, dxPx: Float, dyPx: Float): InputEvent? {
        val mapped = surface.toTargetPixels(x, y) ?: return null
        return InputEvent.Scroll(mapped.first, mapped.second, dxPx.roundToInt(), dyPx.roundToInt())
    }

    /**
     * FR-IN-02 — scancode pass-through.
     *
     * We send the hardware scancode AND the Android keyCode so the host can
     * resolve layout correctly: sending characters breaks shortcuts, dead keys
     * and every non-US layout, which is a recurring one-star theme.
     */
    fun onKey(event: KeyEvent): InputEvent? {
        if (event.action != KeyEvent.ACTION_DOWN && event.action != KeyEvent.ACTION_UP) return null

        val modifiers = stickyModifiers or
            (if (event.isCtrlPressed) Modifiers.CTRL else 0) or
            (if (event.isAltPressed) Modifiers.ALT else 0) or
            (if (event.isShiftPressed) Modifiers.SHIFT else 0) or
            (if (event.isMetaPressed) Modifiers.META else 0)

        return InputEvent.Key(
            scancode = event.scanCode,
            keyCode = event.keyCode,
            down = event.action == KeyEvent.ACTION_DOWN,
            modifiers = modifiers,
            repeat = event.repeatCount,
        )
    }

    /** IME composition and non-Latin input need a text channel (FR-IN-02). */
    fun onText(text: String): InputEvent? =
        text.takeIf { it.isNotEmpty() }?.let { InputEvent.Text(it) }

    /** Fired by the on-screen modifier bar; applies to the NEXT key. */
    fun toggleModifier(flag: Int) {
        stickyModifiers = stickyModifiers xor flag
    }

    fun clearModifiers() {
        stickyModifiers = Modifiers.NONE
    }

    private companion object {
        const val RELATIVE_GAIN = 1.6f
    }
}
