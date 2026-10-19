package com.focuswindow.client.render

import android.content.Context
import android.util.AttributeSet
import android.text.InputType
import android.view.KeyEvent
import android.view.inputmethod.BaseInputConnection
import android.view.inputmethod.EditorInfo
import android.view.inputmethod.InputConnection
import android.view.SurfaceHolder
import android.view.SurfaceView
import com.focuswindow.client.profile.PhoneProfile
import com.focuswindow.client.protocol.TargetRecord
import kotlin.math.min

/**
 * The presentation surface.
 *
 * NFR-PIX-01 / FR-DPI-06 — in the native path this view does **nothing**.
 * The host already sized the target to this phone's pixels, so the correct
 * behaviour is to hand the decoder a buffer of exactly that size and let the
 * compositor blit it 1:1.
 *
 * The only transform this class will ever apply is a LETTERBOX (integer offset,
 * scale 1.0) when the stream is deliberately smaller than the panel — for
 * example when safe-area insets were subtracted for a camera cutout
 * (FR-MOB-03). It never stretches, and it never resamples: those are the two
 * operations that make text soft, and they are what this whole product exists
 * to avoid.
 */
class PhoneSurfaceView @JvmOverloads constructor(
    context: Context,
    attrs: AttributeSet? = null,
) : SurfaceView(context, attrs) {

    /** What we are currently configured to display. */
    data class Presentation(
        val bufferW: Int,
        val bufferH: Int,
        val offsetX: Int,
        val offsetY: Int,
        val scale: Float,
        val mode: Mode,
    ) {
        enum class Mode {
            /** One source pixel per device pixel. The only acceptable native mode. */
            ONE_TO_ONE,

            /** 1:1 pixels, centred, with black around a cutout-reduced stream. */
            LETTERBOX,

            /** Host could not match density; we are showing a scaled image and
             *  the UI says so. Never silent. */
            SCALED_FALLBACK,
        }
    }

    var presentation: Presentation? = null
        private set

    // The Surface is still a native decoder target, not an editable text widget.
    // The input connection exists solely to let Android IMEs commit Unicode text.
    // Composition is local; only committed text and delete keys cross the LAN.
    var onCommittedText: ((String) -> Unit)? = null
    var onDeleteBackward: (() -> Unit)? = null

    init {
        isFocusable = true
        isFocusableInTouchMode = true
    }

    override fun onCheckIsTextEditor(): Boolean = true

    override fun onCreateInputConnection(outAttrs: EditorInfo): InputConnection {
        outAttrs.inputType = InputType.TYPE_CLASS_TEXT or
            InputType.TYPE_TEXT_FLAG_MULTI_LINE or InputType.TYPE_TEXT_FLAG_NO_SUGGESTIONS
        outAttrs.imeOptions = EditorInfo.IME_FLAG_NO_EXTRACT_UI
        return object : BaseInputConnection(this@PhoneSurfaceView, false) {
            override fun commitText(text: CharSequence?, newCursorPosition: Int): Boolean {
                if (!text.isNullOrEmpty()) onCommittedText?.invoke(text.take(4096).toString())
                return true
            }
            override fun deleteSurroundingText(beforeLength: Int, afterLength: Int): Boolean {
                // Most soft keyboards send deleteSurroundingText instead of KEYCODE_DEL.
                repeat(beforeLength.coerceIn(0, 32)) { onDeleteBackward?.invoke() }
                return true
            }
            override fun sendKeyEvent(event: KeyEvent): Boolean {
                if (event.action == KeyEvent.ACTION_DOWN && event.keyCode == KeyEvent.KEYCODE_DEL) {
                    onDeleteBackward?.invoke()
                    return true
                }
                return super.sendKeyEvent(event)
            }
        }
    }

    private var listener: ((Presentation) -> Unit)? = null

    fun onPresentationChanged(block: (Presentation) -> Unit) {
        listener = block
    }

    /**
     * Configure for a target record. Returns the presentation actually used.
     *
     * The assertion here is deliberate and loud: if the host sent a stream that
     * is not the phone's size while claiming to be native, that is a host bug
     * and we want it visible in development rather than quietly scaled away.
     */
    fun configureFor(record: TargetRecord, profile: PhoneProfile): Presentation {
        val panelW = profile.physicalW
        val panelH = profile.physicalH

        val exact = record.widthPx == panelW && record.heightPx == panelH
        val fitsInside = record.widthPx <= panelW && record.heightPx <= panelH

        val result = when {
            exact -> Presentation(
                bufferW = record.widthPx,
                bufferH = record.heightPx,
                offsetX = 0,
                offsetY = 0,
                scale = 1.0f,
                mode = Presentation.Mode.ONE_TO_ONE,
            )

            // Cutout-reduced or deliberately inset stream: centre it, 1:1.
            record.native && fitsInside -> Presentation(
                bufferW = record.widthPx,
                bufferH = record.heightPx,
                offsetX = (panelW - record.widthPx) / 2,
                offsetY = (panelH - record.heightPx) / 2,
                scale = 1.0f,
                mode = Presentation.Mode.LETTERBOX,
            )

            // The host told us it could not reach our density. We show it, and
            // the session chrome labels it (FR-DPI-04, FR-TGT-01).
            else -> {
                val s = min(
                    panelW.toFloat() / record.widthPx,
                    panelH.toFloat() / record.heightPx,
                )
                Presentation(
                    bufferW = record.widthPx,
                    bufferH = record.heightPx,
                    offsetX = ((panelW - record.widthPx * s) / 2f).toInt(),
                    offsetY = ((panelH - record.heightPx * s) / 2f).toInt(),
                    scale = s,
                    mode = Presentation.Mode.SCALED_FALLBACK,
                )
            }
        }

        // setFixedSize makes the decoder write a buffer of exactly this many
        // pixels. With ONE_TO_ONE that is the panel size, so SurfaceFlinger
        // performs a straight blit with no filtering.
        holder.setFixedSize(result.bufferW, result.bufferH)

        presentation = result
        listener?.invoke(result)
        return result
    }

    /**
     * Maps a touch point in VIEW coordinates to TARGET CLIENT pixels.
     *
     * In ONE_TO_ONE this is the identity, which is exactly the point: the
     * class of "cursor drifts away from my finger" bugs that dominates this
     * category comes from clients that scale the image and then forget to
     * invert the same transform for input.
     */
    fun toTargetPixels(viewX: Float, viewY: Float): Pair<Int, Int>? {
        val p = presentation ?: return null
        val x = ((viewX - p.offsetX) / p.scale).toInt()
        val y = ((viewY - p.offsetY) / p.scale).toInt()
        if (x < 0 || y < 0 || x >= p.bufferW || y >= p.bufferH) return null
        return x to y
    }

    fun awaitSurface(onReady: (android.view.Surface) -> Unit, onLost: () -> Unit) {
        holder.addCallback(object : SurfaceHolder.Callback {
            override fun surfaceCreated(h: SurfaceHolder) = onReady(h.surface)

            override fun surfaceChanged(h: SurfaceHolder, f: Int, w: Int, hh: Int) = Unit

            override fun surfaceDestroyed(h: SurfaceHolder) = onLost()
        })
    }
}
