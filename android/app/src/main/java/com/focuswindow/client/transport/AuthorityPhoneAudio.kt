package com.focuswindow.client.transport

import android.content.Context
import android.os.SystemClock
import androidx.annotation.Keep
import java.io.ByteArrayOutputStream
import java.io.DataOutputStream
import java.io.File
import java.net.SocketTimeoutException
import java.nio.ByteBuffer
import java.util.UUID
import java.util.concurrent.CompletableFuture

@Keep
private object AuthorityPhoneBridge {
    init { System.loadLibrary("deskfuse_audio_jni") }
    external fun open(library: String, address: String, deadline: Long, idleMs: Int): Long
    external fun call(handle: Long, operation: Int, input: ByteArray, deadline: Long): ByteArray
    external fun stop(handle: Long)
    external fun close(handle: Long)
}

private enum class PhoneControl(val wire: Int) {
    INFO(1), PREPARE(2), ISSUE(3), PROOF(4), SIGNED_STOP(9), APPLIED_RESULT(11),
    OBSERVATION(128), INSTALL(129), PLAY(130),
}

/** One gated first-delivery session. Run on the audio lifecycle thread. Only Stop
 * may enter from another thread, and it fences native admission synchronously. */
internal class AuthorityPhoneAudio(
    context: Context,
    private val enrollment: TrustedCommittedEnrollment,
    private val signer: BrokerDeviceSigner,
) {
    private val library = File(context.applicationInfo.nativeLibraryDir, "libdeskfuse_quiche_lab.so").absolutePath
    private val renderer = File(context.applicationInfo.nativeLibraryDir, "libdeskfuse_android_audio.so").absolutePath
    private val lifetime = Any()
    private var handle = 0L
    private var started = false
    private var stopped = false
    private var stopTarget: StopTarget? = null
    private var stopWork: CompletableFuture<Void>? = null

    private class StopTarget(val owner: AudioPhoneRequests, val boot: ByteArray,
        val observation: AudioStopObservation, val timeoutMs: Int)

    fun stop() = synchronized(lifetime) {
        stopped = true
        if (handle != 0L) AuthorityPhoneBridge.stop(handle)
        startStopWork()
    }

    // Called under lifetime. The real signing and pipe outcome remain owned
    // independently of an in-flight positive proof or renderer start.
    private fun startStopWork() {
        val target = stopTarget ?: return
        val native = handle
        if (!stopped || native == 0L || stopWork != null) return
        stopWork = CompletableFuture.runAsync {
            runCatching {
                val attempt = target.owner.beginStop(enrollment, target.boot).prepare(target.observation)
                signer.signAudioSourceStop(enrollment, attempt)
                AuthorityPhoneBridge.call(native, PhoneControl.SIGNED_STOP.wire, attempt.envelope(),
                    Math.addExact(SystemClock.elapsedRealtimeNanos(), target.timeoutMs * 1_000_000L))
            }
        }
    }

    fun run(address: String, setupMs: Int, rpcMs: Int, idleMs: Int, clockMarginPpm: Int,
        bufferBursts: Int, sharing: Int, usage: Int, reserveFrames: Int) {
        require(setupMs > 0 && rpcMs in 1..120000 && idleMs > 0 && clockMarginPpm in 0..999999)
        enrollment.requireCurrent()
        val setupDeadline = Math.addExact(SystemClock.elapsedRealtimeNanos(), setupMs * 1_000_000L)
        val native = synchronized(lifetime) {
            check(!started && !stopped)
            started = true
            AuthorityPhoneBridge.open(library, address, setupDeadline, idleMs).also {
                check(it != 0L) { "Could not open authority audio connection" }
                handle = it
            }
        }
        var requests: AudioPhoneRequests? = null
        try {
            var cutoff = setupDeadline
            fun deadline() = minOf(cutoff, Math.addExact(SystemClock.elapsedRealtimeNanos(), rpcMs * 1_000_000L))
            fun call(kind: PhoneControl, bytes: ByteArray = byteArrayOf()) =
                AuthorityPhoneBridge.call(native, kind.wire, bytes, deadline())

            val sourceBytes = call(PhoneControl.INFO)
            val observation = call(PhoneControl.OBSERVATION)
            check(observation.size == 112)
            val connection = observation.copyOfRange(0, 16)
            val connectionId = ByteBuffer.wrap(connection)
            val stopObservation = AudioStopObservation(UUID(connectionId.long, connectionId.long),
                observation.copyOfRange(16, 48), observation.copyOfRange(48, 80), observation.copyOfRange(80, 112))
            val source = AudioSourceApproval(sourceBytes)
            source.requireActive(enrollment)
            val owner = AudioPhoneRequests(source, SystemClock::elapsedRealtimeNanos)
            requests = owner
            fun prove(approval: AudioProofApproval, target: ByteArray): Pair<AudioGrantWindow, ByteArray> {
                val pending = owner.begin(approval)
                val issue = ByteBuffer.allocate(53).put(approval.operation.wire.toByte())
                    .put(approval.nonce()).putInt(rpcMs).put(target).array()
                val (challenge, rootSignature) = TrustedSetupWire.parseSigned(call(PhoneControl.ISSUE, issue))
                pending.setChallenge(challenge, rootSignature)
                val signature = signer.signAudioProof(enrollment, pending)
                owner.confirmProof(pending)
                val frozen = AudioFrozenProof(challenge)
                synchronized(lifetime) {
                    stopTarget = StopTarget(owner, frozen.boot(), stopObservation, rpcMs)
                    startStopWork()
                }
                val proof = frozen.brokerNonce() + byteArrayOf(signature.size.toByte()) + signature
                val receipt = try { call(PhoneControl.PROOF, proof) } catch (_: SocketTimeoutException) {
                    call(PhoneControl.APPLIED_RESULT, pending.appliedResultRequest())
                }
                return owner.accept(enrollment, pending, receipt, clockMarginPpm) to receipt
            }
            val (parent, _) = prove(AudioProofApproval.acquire(source, owner.nextRequestId()), ByteArray(16))
            cutoff = minOf(cutoff, parent.deadlineNanos)
            val binding = call(PhoneControl.INFO, byteArrayOf(1))
            val request = owner.nextRequestId()
            val delivery = call(PhoneControl.PREPARE, ByteBuffer.allocate(56).putLong(1).put(request).put(connection).array())
            val approval = AudioProofApproval.delivery(source, binding, delivery, AudioOperation.ATTACH_DELIVERY,
                observation.copyOfRange(16, 48), observation.copyOfRange(48, 80), observation.copyOfRange(80, 112), request)
            val (window, receipt) = prove(approval, connection)
            window.requireCurrent(SystemClock.elapsedRealtimeNanos())
            enrollment.requireCurrent()
            val install = ByteArrayOutputStream()
            DataOutputStream(install).use { out ->
                out.write(window.boot())
                out.writeLong(window.revision.toLong())
                out.writeLong(minOf(parent.deadlineNanos, window.deadlineNanos))
                out.write(observation)
                for (bytes in listOf(sourceBytes, delivery, receipt)) {
                    out.writeShort(bytes.size)
                    out.write(bytes)
                }
            }
            call(PhoneControl.INSTALL, install.toByteArray())
            val config = ByteBuffer.allocate(16).putInt(bufferBursts).putInt(sharing).putInt(usage).putInt(reserveFrames).array()
            call(PhoneControl.PLAY, config + renderer.toByteArray(Charsets.UTF_8))
        } catch (error: Exception) {
            if (!synchronized(lifetime) { stopped }) throw error
        } finally {
            val pendingStop = synchronized(lifetime) {
                stopWork.also { if (it == null) handle = 0 }
            }
            pendingStop?.join()
            requests?.close()
            synchronized(lifetime) {
                handle = 0
                AuthorityPhoneBridge.close(native)
            }
        }
    }
}
