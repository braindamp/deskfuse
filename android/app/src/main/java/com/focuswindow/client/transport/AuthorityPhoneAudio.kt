package com.focuswindow.client.transport

import android.content.Context
import android.os.SystemClock
import android.util.Log
import androidx.annotation.Keep
import java.io.ByteArrayOutputStream
import java.io.DataOutputStream
import java.io.File
import java.net.SocketTimeoutException
import java.nio.ByteBuffer
import java.util.UUID
import java.util.concurrent.CompletableFuture
import java.util.concurrent.ScheduledThreadPoolExecutor
import java.util.concurrent.TimeUnit

@Keep
private object AuthorityPhoneBridge {
    init { System.loadLibrary("deskfuse_audio_jni") }
    external fun open(library: String, address: String, deadline: Long, idleMs: Int, network: Long): Long
    external fun call(handle: Long, operation: Int, input: ByteArray, deadline: Long, ready: Runnable?): ByteArray
    external fun stop(handle: Long)
    external fun close(handle: Long)
}

private enum class PhoneControl(val wire: Int) {
    INFO(1), PREPARE(2), ISSUE(3), PROOF(4), SIGNED_STOP(9), APPLIED_RESULT(11),
    OBSERVATION(128), INSTALL(129), PLAY(130),
    RENEW_ISSUE(131), RENEW_PROOF(132), RENEW_RESULT(133), RENEW_INSTALL(134),
}

/** One gated audio run. Renewal control uses the live native owner's mailbox.
 * Stop fences native admission synchronously and signs independently. */
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
                    Math.addExact(SystemClock.elapsedRealtimeNanos(), target.timeoutMs * 1_000_000L), null)
            }
        }
    }

    fun run(address: String, network: Long, setupMs: Int, rpcMs: Int, idleMs: Int, clockMarginPpm: Int,
        bufferBursts: Int, sharing: Int, usage: Int, reserveFrames: Int, requireRoute: () -> Unit,
        beforeAcquire: (ByteArray) -> Unit, deliveryAccepted: () -> Unit, ready: Runnable) {
        require(setupMs > 0 && rpcMs in 1..120000 && idleMs > 0 && clockMarginPpm in 0..999999)
        enrollment.requireCurrent()
        val setupDeadline = Math.addExact(SystemClock.elapsedRealtimeNanos(), setupMs * 1_000_000L)
        synchronized(lifetime) {
            check(!started && !stopped)
            started = true
        }
        requireRoute()
        val native = AuthorityPhoneBridge.open(library, address, setupDeadline, idleMs, network)
        check(native != 0L) { "Could not open authority audio connection" }
        var requests: AudioPhoneRequests? = null
        var renewals: ScheduledThreadPoolExecutor? = null
        try {
            synchronized(lifetime) {
                if (stopped) return
                handle = native
            }
            var cutoff = setupDeadline
            fun call(kind: PhoneControl, bytes: ByteArray = byteArrayOf(), onReady: Runnable? = null,
                expiresAt: Long = cutoff): ByteArray {
                requireRoute()
                enrollment.requireCurrent()
                val deadline = minOf(expiresAt, Math.addExact(SystemClock.elapsedRealtimeNanos(), rpcMs * 1_000_000L))
                return AuthorityPhoneBridge.call(native, kind.wire, bytes, deadline, onReady)
            }

            val sourceBytes = call(PhoneControl.INFO)
            val observation = call(PhoneControl.OBSERVATION)
            check(observation.size == 112)
            val connection = observation.copyOfRange(0, 16)
            val connectionId = ByteBuffer.wrap(connection)
            val nativeHash = observation.copyOfRange(16, 48)
            val phoneHash = observation.copyOfRange(48, 80)
            val nativeNonce = observation.copyOfRange(80, 112)
            val stopObservation = AudioStopObservation(UUID(connectionId.long, connectionId.long), nativeHash, phoneHash, nativeNonce)
            val source = AudioSourceApproval(sourceBytes)
            source.requireActive(enrollment)
            val owner = AudioPhoneRequests(source, SystemClock::elapsedRealtimeNanos)
            requests = owner
            fun prove(approval: AudioProofApproval, target: ByteArray, expiresAt: Long = cutoff): Pair<AudioGrantWindow, ByteArray> {
                val renewal = approval.operation == AudioOperation.RENEW
                val pending = owner.begin(approval)
                val issue = ByteBuffer.allocate(53).put(approval.operation.wire.toByte())
                    .put(approval.nonce()).putInt(rpcMs).put(target).array()
                val (challenge, rootSignature) = TrustedSetupWire.parseSigned(call(
                    if (renewal) PhoneControl.RENEW_ISSUE else PhoneControl.ISSUE, issue, expiresAt = expiresAt,
                ))
                pending.setChallenge(challenge, rootSignature)
                val signature = signer.signAudioProof(enrollment, pending)
                owner.confirmProof(pending)
                val frozen = AudioFrozenProof(challenge)
                synchronized(lifetime) {
                    stopTarget = StopTarget(owner, frozen.boot(), stopObservation, rpcMs)
                    startStopWork()
                }
                val proof = frozen.brokerNonce() + byteArrayOf(signature.size.toByte()) + signature
                if (approval.operation == AudioOperation.ACQUIRE_SOURCE) beforeAcquire(approval.nonce())
                val receipt = try {
                    call(if (renewal) PhoneControl.RENEW_PROOF else PhoneControl.PROOF, proof, expiresAt = expiresAt)
                } catch (_: SocketTimeoutException) {
                    call(if (renewal) PhoneControl.RENEW_RESULT else PhoneControl.APPLIED_RESULT,
                        pending.appliedResultRequest(), expiresAt = expiresAt)
                }
                return owner.accept(enrollment, pending, receipt, clockMarginPpm) to receipt
            }
            val (parent, _) = prove(AudioProofApproval.acquire(source, owner.nextRequestId()), ByteArray(16))
            cutoff = minOf(cutoff, parent.deadlineNanos)
            val binding = call(PhoneControl.INFO, byteArrayOf(1))
            val request = owner.nextRequestId()
            val delivery = call(PhoneControl.PREPARE, ByteBuffer.allocate(56).putLong(1).put(request).put(connection).array())
            val approval = AudioProofApproval.delivery(source, binding, delivery, AudioOperation.ATTACH_DELIVERY,
                nativeHash, phoneHash, nativeNonce, request)
            val (window, receipt) = prove(approval, connection)
            window.requireCurrent(SystemClock.elapsedRealtimeNanos())
            enrollment.requireCurrent()
            deliveryAccepted()
            fun installBytes(grant: AudioGrantWindow, exactReceipt: ByteArray, deadline: Long): ByteArray {
                val install = ByteArrayOutputStream()
                DataOutputStream(install).use { out ->
                    out.write(grant.boot())
                    out.writeLong(grant.revision.toLong())
                    out.writeLong(deadline)
                    out.write(observation)
                    for (bytes in listOf(sourceBytes, delivery, exactReceipt)) {
                        out.writeShort(bytes.size)
                        out.write(bytes)
                    }
                }
                return install.toByteArray()
            }
            val initialDeadline = minOf(parent.deadlineNanos, window.deadlineNanos)
            call(PhoneControl.INSTALL, installBytes(window, receipt, initialDeadline))
            val executor = ScheduledThreadPoolExecutor(1) { action -> Thread(action, "audio-renewal") }.apply {
                setExecuteExistingDelayedTasksAfterShutdownPolicy(false)
                prestartCoreThread()
            }
            renewals = executor
            val deliveryDigest = TrustedEnrollmentProtocol.hash(delivery)
            fun scheduleRenewal(localDeadline: Long) {
                val remaining = localDeadline - SystemClock.elapsedRealtimeNanos()
                if (remaining <= 0 || executor.isShutdown) return
                executor.schedule({
                    try {
                        requireRoute()
                        val renewal = AudioProofApproval.delivery(source, binding, delivery, AudioOperation.RENEW,
                            nativeHash, phoneHash, nativeNonce, owner.nextRequestId(), deliveryDigest)
                        val (grant, exactReceipt) = prove(renewal, ByteArray(16), localDeadline)
                        check(grant.deadlineNanos > localDeadline)
                        call(PhoneControl.RENEW_INSTALL, installBytes(grant, exactReceipt, grant.deadlineNanos),
                            expiresAt = localDeadline)
                        scheduleRenewal(grant.deadlineNanos)
                    } catch (_: Exception) {
                        if (!synchronized(lifetime) { stopped })
                            Log.w("DeskFuseAudio", "Speaker renewal unavailable; existing local cutoff applies")
                    }
                }, remaining / 2, TimeUnit.NANOSECONDS)
            }
            val config = ByteBuffer.allocate(16).putInt(bufferBursts).putInt(sharing).putInt(usage).putInt(reserveFrames).array()
            call(PhoneControl.PLAY, config + renderer.toByteArray(Charsets.UTF_8), Runnable {
                ready.run()
                scheduleRenewal(initialDeadline)
            })
        } catch (error: Exception) {
            if (!synchronized(lifetime) { stopped }) {
                stop()
                throw error
            }
        } finally {
            renewals?.let { executor ->
                executor.shutdown()
                var interrupted = false
                while (!executor.isTerminated) {
                    try { executor.awaitTermination(Long.MAX_VALUE, TimeUnit.NANOSECONDS) }
                    catch (_: InterruptedException) { interrupted = true }
                }
                if (interrupted) Thread.currentThread().interrupt()
            }
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
