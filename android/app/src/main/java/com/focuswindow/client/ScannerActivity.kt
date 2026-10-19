package com.focuswindow.client

import android.Manifest
import android.content.Context
import android.content.Intent
import android.content.pm.PackageManager
import android.os.Bundle
import androidx.activity.ComponentActivity
import androidx.activity.result.contract.ActivityResultContract
import androidx.activity.result.contract.ActivityResultContracts
import androidx.camera.core.CameraSelector
import androidx.camera.core.ImageAnalysis
import androidx.camera.core.ImageProxy
import androidx.camera.core.Preview
import androidx.camera.lifecycle.ProcessCameraProvider
import androidx.camera.view.PreviewView
import androidx.core.content.ContextCompat
import com.google.mlkit.vision.barcode.BarcodeScanner
import com.google.mlkit.vision.barcode.BarcodeScanning
import com.google.mlkit.vision.common.InputImage
import java.util.concurrent.Executors
import java.util.concurrent.atomic.AtomicBoolean

/**
 * FR-TRUST-02 — pairing is a single QR scan.
 *
 * Deliberate constraints:
 *   * barcode decoding runs fully on-device (bundled model)
 *   * frames are analysed and immediately closed — never written to disk
 *   * the camera is released the instant a valid payload is found
 *   * this screen uses no network permission at all
 */
class ScannerActivity : ComponentActivity() {

    private val analysisExecutor = Executors.newSingleThreadExecutor()
    private val handled = AtomicBoolean(false)
    private lateinit var previewView: PreviewView

    private val permission = registerForActivityResult(
        ActivityResultContracts.RequestPermission(),
    ) { granted ->
        if (granted) bindCamera() else finishWith(null, "Camera permission is required to scan.")
    }

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        previewView = PreviewView(this)
        setContentView(previewView)

        if (ContextCompat.checkSelfPermission(this, Manifest.permission.CAMERA)
            == PackageManager.PERMISSION_GRANTED
        ) {
            bindCamera()
        } else {
            permission.launch(Manifest.permission.CAMERA)
        }
    }

    private fun bindCamera() {
        val providerFuture = ProcessCameraProvider.getInstance(this)
        providerFuture.addListener({
            val provider = providerFuture.get()
            val preview = Preview.Builder().build().also {
                it.surfaceProvider = previewView.surfaceProvider
            }

            val scanner = BarcodeScanning.getClient()
            val analysis = ImageAnalysis.Builder()
                .setBackpressureStrategy(ImageAnalysis.STRATEGY_KEEP_ONLY_LATEST)
                .build()
                .also { it.setAnalyzer(analysisExecutor) { proxy -> analyse(proxy, scanner) } }

            runCatching {
                provider.unbindAll()
                provider.bindToLifecycle(this, CameraSelector.DEFAULT_BACK_CAMERA, preview, analysis)
            }.onFailure {
                finishWith(null, "This device's camera could not be opened.")
            }
        }, ContextCompat.getMainExecutor(this))
    }

    private fun analyse(proxy: ImageProxy, scanner: BarcodeScanner) {
        val media = proxy.image
        if (media == null || handled.get()) {
            proxy.close()
            return
        }
        val image = InputImage.fromMediaImage(media, proxy.imageInfo.rotationDegrees)
        scanner.process(image)
            .addOnSuccessListener { codes ->
                val raw = codes.firstNotNullOfOrNull { it.rawValue }
                if (raw != null &&
                    raw.startsWith("focuswindow://") &&
                    handled.compareAndSet(false, true)
                ) {
                    finishWith(raw, null)
                }
            }
            .addOnCompleteListener { proxy.close() }
    }

    private fun finishWith(payload: String?, error: String?) {
        setResult(
            if (payload != null) RESULT_OK else RESULT_CANCELED,
            Intent().putExtra(EXTRA_PAYLOAD, payload).putExtra(EXTRA_ERROR, error),
        )
        finish()
    }

    override fun onDestroy() {
        analysisExecutor.shutdown()
        super.onDestroy()
    }

    /**
     * A proper [ActivityResultContract] so the caller registers ONCE during
     * creation. Registering a launcher lazily at click time throws on modern
     * AndroidX, which is a crash this app does not get to have.
     */
    class Contract : ActivityResultContract<Unit, ScanResult>() {
        override fun createIntent(context: Context, input: Unit): Intent =
            Intent(context, ScannerActivity::class.java)

        override fun parseResult(resultCode: Int, intent: Intent?): ScanResult =
            ScanResult(
                payload = intent?.getStringExtra(EXTRA_PAYLOAD),
                error = intent?.getStringExtra(EXTRA_ERROR)
                    ?: if (resultCode != RESULT_OK) "Scan cancelled." else null,
            )
    }

    data class ScanResult(val payload: String?, val error: String?)

    companion object {
        const val EXTRA_PAYLOAD = "payload"
        const val EXTRA_ERROR = "error"
    }
}
