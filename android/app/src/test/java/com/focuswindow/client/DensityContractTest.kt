package com.focuswindow.client

import com.focuswindow.client.profile.PhoneProfile
import com.focuswindow.client.protocol.TargetRecord
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test

/**
 * The density contract is the product. These tests are the executable form of
 * the P0 exit criteria, so a regression here fails the build rather than
 * shipping a soft screenshot.
 */
class DensityContractTest {

    private fun flagship() = PhoneProfile(
        deviceId = "test",
        deviceLabel = "Test Flagship",
        physicalW = 1440,
        physicalH = 3120,
        dpr = 3.5f,
        safeTop = 0,
        safeBottom = 0,
        safeLeft = 0,
        safeRight = 0,
        fontScale = 1f,
        maxRefreshHz = 120,
        diagonalMm = 170,
        orientation = PhoneProfile.Orientation.PORTRAIT,
        formFactor = PhoneProfile.FormFactor.PHONE,
    )

    /** FR-DPI-01 — 1440 / 3.5 = 411 dp. This single number is the whole thesis. */
    @Test
    fun `canvas is the phone's logical size, not its pixel size`() {
        val p = flagship()
        assertEquals(411, p.canvasDpW)
        assertEquals(891, p.canvasDpH)
    }

    /** FR-DPI-03 — the request we send to the display path. */
    @Test
    fun `requested scale factor is dpr as a percentage, clamped to the valid range`() {
        assertEquals(350, flagship().requestedScaleFactor)
        assertEquals(500, flagship().copy(dpr = 6.0f).requestedScaleFactor)
        assertEquals(100, flagship().copy(dpr = 0.5f).requestedScaleFactor)
    }

    /** FR-DPI-04 — the assertion the host must satisfy before streaming. */
    @Test
    fun `expected dpi is 96 times dpr`() {
        assertEquals(336, flagship().expectedDpi)
        assertEquals(288, flagship().copy(dpr = 3.0f).expectedDpi)
    }

    /**
     * FR-DPI-09 — rotation must move the safe-area insets by PHYSICAL edge.
     * Getting this wrong is how content ends up under a camera cutout.
     */
    @Test
    fun `rotation swaps axes and remaps insets by physical edge`() {
        val portrait = flagship().copy(safeTop = 120, safeBottom = 60, safeLeft = 0, safeRight = 0)
        val landscape = portrait.withOrientation(PhoneProfile.Orientation.LANDSCAPE)

        assertEquals(3120, landscape.physicalW)
        assertEquals(1440, landscape.physicalH)
        // The notch was at the top; in landscape it is on an edge, not the top.
        assertEquals(0, landscape.safeTop)
        assertEquals(120, landscape.safeRight)
        assertEquals(60, landscape.safeLeft)
    }

    /** FR-MOB-03 — insets reduce the canvas; they never scale the content. */
    @Test
    fun `safe area reduces the canvas rather than scaling it`() {
        val p = flagship().copy(safeTop = 140, safeBottom = 70)
        assertEquals(1440, p.surfaceW)
        assertEquals(3120 - 210, p.surfaceH)
        assertEquals(411, p.canvasDpW)
        assertEquals(831, p.canvasDpH)
    }

    /**
     * FR-DPI-12 — the readability prediction. A native render at 350% puts a
     * 14 dp token at 14 dp. The same pixels at 100% would put it at 4 dp,
     * which is the failure this whole architecture exists to prevent.
     */
    @Test
    fun `token size proves native beats a pixel-resized desktop`() {
        val p = flagship()

        val native = record(dpi = 336, native = true)
        assertEquals(14f, native.predictedTokenDp(p), 0.1f)
        assertTrue(native.predictedTokenDp(p) >= PhoneProfile.READABLE_TOKEN_DP)

        // The "pixel resize at 96 DPI" trap: same pixel count, no density.
        val pixelResized = record(dpi = 96, native = true)
        assertEquals(4f, pixelResized.predictedTokenDp(p), 0.1f)
        assertFalse(pixelResized.predictedTokenDp(p) >= PhoneProfile.READABLE_TOKEN_DP)
    }

    /**
     * The host cannot always reach the phone's exact density (Windows has 250% and 300%, not 262.5%).
     * With 1:1 pixels the token size must follow the DPI the host really used, not claim 14 dp.
     */
    @Test
    fun `token size follows the real dpi when pixels are 1 to 1 but density is off`() {
        val p = flagship()
        val offDensity = record(dpi = 240, native = false) // 250%: 14 * 2.5 / 3.5 = 10 dp
        assertTrue(offDensity.isOneToOne(p))
        assertEquals(10f, offDensity.predictedTokenDp(p), 0.1f)
        // Pixels reduced and upscaled by the phone: the layout is phone-sized, only softer.
        val upscaled = record(dpi = 240, native = false).copy(widthPx = 1028, heightPx = 2228)
        assertFalse(upscaled.isOneToOne(p))
        assertEquals(14f, upscaled.predictedTokenDp(p), 0.1f)
    }
    /** NFR-PIX-01 — one source pixel per device pixel, or it is not native. */
    @Test
    fun `one to one requires the stream to equal the surface exactly`() {
        val p = flagship()
        assertTrue(record(dpi = 336, native = true).isOneToOne(p))
        assertFalse(
            record(dpi = 336, native = true).copy(widthPx = 1080, heightPx = 2400).isOneToOne(p),
        )
    }

    /** FR-TGT-01 — a non-native tier must never be labelled as native. */
    @Test
    fun `tier label never claims native for a window capture`() {
        val windowTier = record(dpi = 96, native = false)
            .copy(occupant = TargetRecord.Occupant.WINDOW)
        assertTrue(windowTier.tierLabel().contains("not native"))
    }

    @Test
    fun `profile validation rejects an unusable canvas`() {
        val tiny = flagship().copy(physicalW = 400, dpr = 3.5f)
        assertTrue(tiny.validate().any { it.contains("canvas too narrow") })
        assertTrue(flagship().validate().isEmpty())
    }

    private fun record(dpi: Int, native: Boolean) = TargetRecord(
        version = 1,
        occupant = TargetRecord.Occupant.CLONE,
        widthPx = 1440,
        heightPx = 3120,
        dpi = dpi,
        canvasDpW = 411,
        canvasDpH = 891,
        native = native,
        densityMethod = TargetRecord.DensityMethod.IDD_SCALE,
        codec = "hevc",
        pixelFormat = "YUV444_10",
        refreshHz = 60,
        stateModel = TargetRecord.StateModel.SHARED_VIEW,
        appLabel = "Word",
    )
}
