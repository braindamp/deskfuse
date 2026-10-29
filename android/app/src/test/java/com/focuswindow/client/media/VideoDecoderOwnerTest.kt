package com.focuswindow.client.media

import android.graphics.SurfaceTexture
import android.media.MediaCodecInfo
import android.media.MediaCodecList
import android.media.MediaFormat
import android.os.Handler
import android.os.HandlerThread
import android.os.Looper
import android.view.Surface
import com.focuswindow.client.protocol.FrameHeader
import com.focuswindow.client.transport.WireFrame
import java.util.concurrent.ConcurrentLinkedQueue
import java.util.concurrent.CountDownLatch
import java.util.concurrent.TimeUnit
import kotlinx.coroutines.runBlocking
import kotlinx.coroutines.launch
import kotlinx.coroutines.CoroutineStart
import org.junit.Assert.*
import org.junit.Test
import org.junit.runner.RunWith
import org.robolectric.RobolectricTestRunner
import org.robolectric.Shadows.shadowOf
import org.robolectric.annotation.Config
import org.robolectric.annotation.LooperMode
import org.robolectric.shadows.MediaCodecInfoBuilder
import org.robolectric.shadows.ShadowMediaCodec
import org.robolectric.shadows.ShadowMediaCodecList
import org.robolectric.util.ReflectionHelpers

/** Executes the shipping owner against a fake Android codec; no device/GPU is used. */
@RunWith(RobolectricTestRunner::class)
@Config(sdk = [35])
@LooperMode(LooperMode.Mode.PAUSED)
class VideoDecoderOwnerTest {
    @Test fun owner_serializes_reconnect_input_pressure_and_stop_without_an_extra_frame_fifo() = runBlocking {
        val name = "test.deskfuse.decoder"
        val configuredOn = ConcurrentLinkedQueue<String>()
        val processed = ConcurrentLinkedQueue<Int>()
        val failures = ConcurrentLinkedQueue<String>()
        val failNext = java.util.concurrent.atomic.AtomicBoolean(false)
        val caps = MediaCodecInfoBuilder.CodecCapabilitiesBuilder.newBuilder()
            .setMediaFormat(MediaFormat.createVideoFormat(VideoDecoder.MIME_H264, 64, 64))
            .setProfileLevels(arrayOf(MediaCodecInfo.CodecProfileLevel().apply {
                profile = MediaCodecInfo.CodecProfileLevel.AVCProfileBaseline
                level = MediaCodecInfo.CodecProfileLevel.AVCLevel31
            }))
            .setIsEncoder(false).setColorFormats(intArrayOf(MediaCodecInfo.CodecCapabilities.COLOR_FormatYUV420Flexible))
            .build()
        // Capability construction reads global codec settings and can cache an empty list.
        ShadowMediaCodecList.reset()
        ShadowMediaCodecList.addCodec(MediaCodecInfoBuilder.newBuilder().setName(name)
            .setIsEncoder(false).setIsHardwareAccelerated(true).setCapabilities(caps).build())
        val advertised = MediaCodecList(MediaCodecList.REGULAR_CODECS).codecInfos
        assertTrue("fake codec not advertised: ${advertised.map { it.name }}", advertised.any { it.name == name })
        val videoCaps = advertised.first { it.name == name }.getCapabilitiesForType(VideoDecoder.MIME_H264).videoCapabilities
        assertNotNull("fake codec has no video capabilities", videoCaps)
        assertTrue("fake codec size range: ${videoCaps.supportedWidths} × ${videoCaps.supportedHeights}", videoCaps.isSizeSupported(64, 64))
        ShadowMediaCodec.addDecoder(name, ShadowMediaCodec.CodecConfig(4096, 4096,
            object : ShadowMediaCodec.CodecConfig.Codec {
                override fun onConfigured(format: MediaFormat, surface: Surface?, crypto: android.media.MediaCrypto?, flags: Int) {
                    configuredOn.add(Thread.currentThread().name)
                }
                override fun process(input: java.nio.ByteBuffer, output: java.nio.ByteBuffer) {
                    if (failNext.getAndSet(false)) throw IllegalStateException("injected queue failure")
                    processed.add(input.get(input.position()).toInt())
                    output.put(input)
                }
            }))
        val texture = SurfaceTexture(0)
        val surface = Surface(texture)
        val decoder = VideoDecoder({}, { _, detail, _ -> failures.add(detail) }, {})
        val handler = ReflectionHelpers.getField<Handler>(decoder, "handler")
        val owner = ReflectionHelpers.getField<HandlerThread>(decoder, "thread")
        fun barrier(action: () -> Unit = {}) {
            val done = CountDownLatch(1)
            val failure = java.util.concurrent.atomic.AtomicReference<Throwable?>()
            assertTrue(handler.post { try { action() } catch (error: Throwable) { failure.set(error) } finally { done.countDown() } })
            assertTrue("owner command did not finish", done.await(5, TimeUnit.SECONDS))
            failure.get()?.let { throw it }
            shadowOf(Looper.getMainLooper()).idle()
            assertTrue(failures.toString(), failures.isEmpty())
        }
        fun frame(generation: Long, pts: Long, key: Boolean, value: Byte) = WireFrame(
            FrameHeader(key, 7, pts, 4), byteArrayOf(value, 0, 0, 1), connectionGeneration = generation)
        try {
            decoder.start(surface, VideoDecoder.MIME_H264, 64, 64, 7, connectionGeneration = 1)
            barrier()
            decoder.submit(frame(1, 1, true, 11))
            barrier()
            assertEquals(listOf(11), processed.toList())
            assertEquals(listOf("fw-decoder"), configuredOn.toList())

            decoder.start(surface, VideoDecoder.MIME_H264, 64, 64, 7, connectionGeneration = 2)
            barrier()
            decoder.submit(frame(1, 2, true, 99)) // same target version, retired connection
            decoder.submit(frame(2, 3, false, 98)) // new incarnation still needs IDR
            decoder.submit(frame(2, 4, true, 22))
            barrier()
            assertEquals(listOf(11, 22), processed.toList())

            var indices: List<Int> = emptyList()
            barrier {
                val queue = ReflectionHelpers.getField<ArrayDeque<Int>>(decoder, "inputQueue")
                indices = queue.toList()
                queue.clear()
            }
            decoder.submit(frame(2, 5, false, 97))
            barrier { ReflectionHelpers.getField<ArrayDeque<Int>>(decoder, "inputQueue").addAll(indices) }
            decoder.submit(frame(2, 6, false, 96))
            decoder.submit(frame(2, 7, true, 33))
            barrier()
            assertEquals(listOf(11, 22, 33), processed.toList())

            failNext.set(true)
            decoder.submit(frame(2, 8, false, 94))
            barrier() // dispatch the fake codec's asynchronous fresh-input callbacks after recovery
            decoder.submit(frame(2, 9, false, 93))
            decoder.submit(frame(2, 10, true, 44))
            barrier()
            assertEquals(listOf(11, 22, 33, 44), processed.toList())

            val blocked = CountDownLatch(1)
            val release = CountDownLatch(1)
            handler.post { blocked.countDown(); release.await(5, TimeUnit.SECONDS) }
            assertTrue(blocked.await(5, TimeUnit.SECONDS))
            val cancelled = launch(start = CoroutineStart.UNDISPATCHED) { decoder.submit(frame(2, 11, false, 92)) }
            cancelled.cancel()
            release.countDown()
            cancelled.join()
            decoder.submit(frame(2, 12, false, 91))
            decoder.submit(frame(2, 13, true, 55))
            barrier()
            assertEquals(listOf(11, 22, 33, 44, 55), processed.toList())

            decoder.stop()
            decoder.submit(frame(2, 14, true, 95))
            barrier { assertNull(ReflectionHelpers.getField<Any?>(decoder, "codec")) }
            assertEquals(listOf(11, 22, 33, 44, 55), processed.toList())
        } finally {
            decoder.close()
            owner.join(5000)
            assertFalse("decoder thread must retire", owner.isAlive)
            surface.release()
            texture.release()
            ShadowMediaCodec.clearCodecs()
            ShadowMediaCodecList.reset()
        }
    }
}
