package com.focuswindow.client.transport

import java.security.KeyPairGenerator
import java.security.spec.ECGenParameterSpec
import java.util.HexFormat

/** Cross-language exercise of shipping parsers, encoding and typed signing, not a separate implementation. */
object TrustedEnrollmentKotlinCheck {
    @JvmStatic fun main(args: Array<String>) {
        val hex = HexFormat.of()
        val offerBytes = hex.parseHex(readln())
        val offer = TrustedEnrollmentProtocol.parseOffer(offerBytes)
        check(offerBytes.contentEquals(TrustedEnrollmentProtocol.offer(offer)))
        val keys = object : BrokerSigningKeyStore {
            val values = mutableMapOf<String, BrokerSigningKey>()
            @Volatile var beforeFind: (() -> Unit)? = null
            override fun find(alias: String): BrokerSigningKey? { beforeFind?.invoke(); return values[alias] }
            override fun create(alias: String): BrokerSigningKey {
                check(!values.containsKey(alias))
                val pair = KeyPairGenerator.getInstance("EC").apply { initialize(ECGenParameterSpec("secp256r1")) }.generateKeyPair()
                return BrokerSigningKey(pair.private, pair.public.encoded, BrokerKeyProtection.SOFTWARE).also { values[alias] = it }
            }
        }
        val signer = BrokerDeviceSigner(keys); val host = TrustedEnrollmentProtocol.hash(offer.hostSpki)
        val identity = signer.prepareEnrollment(host, offer.enrollment)
        val (proposal, signature) = signer.signEnrollmentProposal(host, offer.enrollment, identity.publicSpkiSha256,
            offerBytes, ByteArray(32), listOf(BrokerScope.SPEAKER, BrokerScope.DISPLAY))
        println(hex.formatHex(proposal)); println(hex.formatHex(signature))
        val transcript = hex.parseHex(readln()); val parsed = TrustedEnrollmentProtocol.parseTranscript(transcript)
        check(transcript.contentEquals(TrustedEnrollmentProtocol.transcript(parsed)))
        val approval = hex.parseHex(readln()); check(approval.contentEquals(TrustedEnrollmentProtocol.phoneApproval(TrustedEnrollmentProtocol.parsePhoneApproval(approval))))
        val receipt = hex.parseHex(readln()); check(receipt.contentEquals(TrustedEnrollmentProtocol.receipt(TrustedEnrollmentProtocol.parseReceipt(receipt))))
        val status = hex.parseHex(readln()); check(status.contentEquals(TrustedEnrollmentProtocol.status(TrustedEnrollmentProtocol.parseStatus(status))))
        val binding = TrustedEnrollmentProtocol.binding(parsed)
        check(readln() == TrustedEnrollmentProtocol.verificationText(binding.hash))
        TrustedEnrollmentProtocol.requireVerificationQr(readln(), offer.ticket, binding.hash)
        check(TrustedEnrollmentProtocol.parseVerificationText(TrustedEnrollmentProtocol.verificationText(binding.hash)).contentEquals(binding.hash))
        check(TrustedEnrollmentProtocol.verificationText(ByteArray(32) { 255.toByte() }) == "Z".repeat(51) + "G")
        check(runCatching { TrustedEnrollmentProtocol.parseVerificationText("Z".repeat(52)) }.isFailure)
        check(runCatching { TrustedEnrollmentProtocol.parseOffer(offerBytes + 0) }.isFailure)
        check(runCatching { signer.signEnrollmentProposal(ByteArray(32), offer.enrollment, identity.publicSpkiSha256,
            offerBytes, ByteArray(32), listOf(BrokerScope.SPEAKER)) }.isFailure)
        val challenge = hex.parseHex(readln())
        val proofContext = BrokerProofApproval(offer.peer, ByteArray(32), BrokerOperation.ACQUIRE, BrokerScope.DISPLAY,
            offer.ui, 1, host, offer.namespace, offer.enrollment, offer.subject)
        val signedFrozen = hex.parseHex(readln()); val signedReceipt = hex.parseHex(readln()); val hostChallengeSignature = hex.parseHex(readln())
        val records = object : TrustedEnrollmentPersistence {
            val values = mutableMapOf("prepared" to signedFrozen.copyOf(), "active" to signedReceipt.copyOf())
            val completed = mutableListOf<String>()
            var reads = 0
            override fun read(host: ByteArray, enrollment: java.util.UUID, suffix: String): ByteArray? {
                reads++; return values[suffix]?.copyOf()
            }
            override fun writeOnce(host: ByteArray, enrollment: java.util.UUID, suffix: String, bytes: ByteArray) {
                values[suffix]?.let { check(it.contentEquals(bytes)) }
                completed.add(suffix); values[suffix] = bytes.copyOf()
            }
        }
        val repository = TrustedEnrollmentRepository(records)
        check(runCatching { signer.signPreparedEnrollmentApproval(TrustedPreparedEnrollment.parseStoreRecord(signedFrozen)) }.isFailure)
        check(runCatching { signer.signExisting(host, offer.enrollment, identity.publicSpkiSha256, challenge, proofContext) }.isFailure)
        val active = repository.committed(host, offer.enrollment)
        check(records.completed == listOf("prepared", "active")) // Existing readable records still complete in order.
        val readCount = records.reads
        repeat(3) { active.requireCurrent() }
        check(records.reads == readCount) // Proof-current checks cannot touch storage.
        val proof = signer.signCommittedProof(active, challenge, hostChallengeSignature, proofContext)
        check(proof.contentEquals(signer.signCommittedProof(active, challenge, hostChallengeSignature, proofContext)))
        check(runCatching { signer.signCommittedProof(active, challenge, ByteArray(8), proofContext) }.isFailure)
        println(hex.formatHex(proof))
        val foreign = BrokerProofApproval(offer.peer, ByteArray(32), BrokerOperation.ACQUIRE, BrokerScope.DISPLAY,
            offer.ui, 1, ByteArray(32), offer.namespace, offer.enrollment, offer.subject)
        check(runCatching { signer.signExisting(host, offer.enrollment, identity.publicSpkiSha256, challenge, foreign) }.isFailure)
        val v1 = BrokerProofApproval(offer.peer, ByteArray(32), BrokerOperation.ACQUIRE, BrokerScope.DISPLAY, offer.ui, 1)
        check(runCatching { BrokerProofTranscript.requireApproved(challenge, v1) }.isFailure)
        // N2 uses shipping typed descriptor/proof/receipt code, including local BOOTTIME cutoffs.
        val audioSourceBytes = hex.parseHex(readln()); val audioNonce = hex.parseHex(readln())
        val audioChallenge = hex.parseHex(readln()); val audioHostSignature = hex.parseHex(readln())
        val audioSource = AudioSourceApproval(audioSourceBytes)
        check(audioSource.mode == AudioDeliveryMode.GAME)
        for (mode in listOf(0, 2, 3, 255)) check(runCatching { AudioDeliveryMode.requireSupported(mode) }.isFailure)
        val audioApproval = AudioProofApproval.acquire(audioSource, audioNonce)
        check(runCatching { signer.signCommittedProof(active, audioChallenge, audioHostSignature, audioApproval.brokerApproval()) }.isFailure)
        var audioNow = 1000000000L
        val audioOwner = AudioPhoneRequests(audioSource) { audioNow }
        val sourcePending = audioOwner.begin(audioApproval)
        sourcePending.setChallenge(audioChallenge, audioHostSignature)
        val sourceSignature = signer.signAudioProof(active, sourcePending)
        check(sourceSignature.contentEquals(signer.signAudioProof(active, sourcePending)))
        val savedKeys = keys.values.toMap(); keys.values.clear()
        check(runCatching { signer.signAudioProof(active, sourcePending) }.isFailure)
        keys.values.putAll(savedKeys)
        check(sourceSignature.contentEquals(signer.signAudioProof(active, sourcePending)))
        audioOwner.confirmProof(sourcePending)
        println(hex.formatHex(sourceSignature))
        val sourceReceipt = hex.parseHex(readln())
        audioNow = 1100000000L
        val sourceWindow = audioOwner.accept(active, sourcePending, sourceReceipt, 1000)
        check(sourceWindow === audioOwner.accept(active, sourcePending, sourceReceipt, 1000))
        check(sourceWindow.deadlineNanos == 1499500000L)
        check(runCatching { AudioSourceApproval(audioSourceBytes + 0) }.isFailure)
        println("AUDIO_SOURCE_PASS")
        val audioBinding = hex.parseHex(readln()); val deliveryBytes = hex.parseHex(readln())
        val deliveryChallenge = hex.parseHex(readln()); val deliveryHostSignature = hex.parseHex(readln())
        val deliveryRequest = AudioFrozenProof(deliveryChallenge).requestId()
        val deliveryApproval = AudioProofApproval.delivery(audioSource, audioBinding, deliveryBytes, AudioOperation.ATTACH_DELIVERY,
            ByteArray(32) { 4 }, ByteArray(32) { 5 }, deliveryRequest)
        check(runCatching { AudioProofApproval.delivery(audioSource, audioBinding, deliveryBytes, AudioOperation.ATTACH_DELIVERY,
            ByteArray(32) { 8 }, ByteArray(32) { 5 }, deliveryRequest) }.isFailure)
        val deliveryPending = audioOwner.begin(deliveryApproval)
        deliveryPending.setChallenge(deliveryChallenge, deliveryHostSignature)
        println(hex.formatHex(signer.signAudioProof(active, deliveryPending)))
        audioOwner.confirmProof(deliveryPending)
        val deliveryReceipt = hex.parseHex(readln())
        val window = audioOwner.accept(active, deliveryPending, deliveryReceipt, 1000)
        check(window.revision == 3uL)
        check(window === audioOwner.accept(active, deliveryPending, deliveryReceipt, 1000))
        val tampered = deliveryReceipt.copyOf().also { it[it.lastIndex] = (it.last().toInt() xor 1).toByte() }
        check(runCatching { audioOwner.accept(active, deliveryPending, tampered, 1000) }.isFailure)
        check(runCatching { audioOwner.accept(active, sourcePending, sourceReceipt, 1000) }.isFailure)
        check(records.reads == readCount)
        println("AUDIO_DELIVERY_PASS")
        val queryOne = audioOwner.beginResync(); check(queryOne.contentEquals(audioOwner.beginResync()))
        println(hex.formatHex(queryOne))
        check(runCatching { audioOwner.resync(active, hex.parseHex(readln())) }.isFailure) // Valid root, wrong query, MAX floor.
        val statusOne = hex.parseHex(readln())
        check(runCatching { audioOwner.resync(active, statusOne.copyOf().also { it[it.lastIndex] = (it.last().toInt() xor 1).toByte() }) }.isFailure)
        audioOwner.resync(active, statusOne)
        check(AudioRequestId(audioOwner.nextRequestId()).sequence == 3uL)
        fun renewal(id: ByteArray) = AudioProofApproval.delivery(audioSource, audioBinding, deliveryBytes, AudioOperation.RENEW,
            ByteArray(32) { 4 }, ByteArray(32) { 5 }, id, TrustedEnrollmentProtocol.hash(deliveryBytes))
        val renewalApproval = renewal(audioOwner.nextRequestId())
        val renewalPending = audioOwner.begin(renewalApproval)
        println(hex.formatHex(audioOwner.beginResync()))
        val foreignStatus = hex.parseHex(readln())
        check(runCatching { audioOwner.resync(active, foreignStatus) }.isFailure)
        audioOwner.resync(active, hex.parseHex(readln())) // Floor 2: proposal not yet reserved.
        check(audioOwner.nextRequestId().contentEquals(renewalApproval.nonce()))
        check(renewalPending === audioOwner.begin(renewalApproval) && renewalPending.firstSendNanos == 1100000000L)
        println(hex.formatHex(renewalApproval.nonce()))
        println(hex.formatHex(audioOwner.beginResync()))
        audioOwner.resync(active, hex.parseHex(readln())) // Floor 3, retiring same identity: burned request is stale.
        check(renewalPending.isRetired() && AudioRequestId(audioOwner.nextRequestId()).sequence == 4uL)
        audioNow += 50000000L
        val successor = audioOwner.begin(renewal(audioOwner.nextRequestId()))
        check(successor.firstSendNanos == audioNow && successor.firstSendNanos > renewalPending.firstSendNanos)
        audioOwner.retirePending()
        check(AudioRequestId(audioOwner.nextRequestId()).sequence == 4uL) // Unreserved proposal never burns the local floor.
        check(AudioRequestId(AudioRequestId.fresh(1uL shl 63)).sequence == 1uL shl 63)
        check(AudioRequestId(AudioRequestId.fresh(ULong.MAX_VALUE)).sequence == ULong.MAX_VALUE)
        val cold = AudioPhoneRequests(audioSource) { audioNow }
        check(runCatching { cold.accept(active, sourcePending, sourceReceipt, 1000) }.isFailure)
        check(runCatching { cold.begin(renewal(cold.nextRequestId())) }.isFailure)
        // Real pending machinery, fault-injected synchronous key operation: an
        // uncertain signature is never retried, and cancellation cannot release
        // the operation slot before that actual call completes.
        val failureOwner = AudioPhoneRequests(audioSource) { audioNow }
        val failureApproval = AudioProofApproval.acquire(audioSource, audioNonce)
        val failurePending = failureOwner.begin(failureApproval)
        failurePending.setChallenge(audioChallenge, audioHostSignature)
        var attempts = 0
        repeat(2) { check(runCatching { failurePending.signWith { _, _, _ -> attempts++; throw java.io.IOException("Injected key failure") } }.isFailure) }
        check(attempts == 1)
        val blockedOwner = AudioPhoneRequests(audioSource) { audioNow }
        val blockedApproval = AudioProofApproval.acquire(audioSource, audioNonce)
        val blocked = blockedOwner.begin(blockedApproval); blocked.setChallenge(audioChallenge, audioHostSignature)
        val entered = java.util.concurrent.CountDownLatch(1); val release = java.util.concurrent.CountDownLatch(1)
        val task = Thread { runCatching { blocked.signWith { _, _, _ -> entered.countDown(); check(release.await(5, java.util.concurrent.TimeUnit.SECONDS)); ByteArray(8) } } }
        task.start(); check(entered.await(5, java.util.concurrent.TimeUnit.SECONDS)); blockedOwner.retirePending()
        check(blocked.isSigning() && runCatching { blockedOwner.begin(AudioProofApproval.acquire(audioSource, audioNonce)) }.isFailure)
        release.countDown(); task.join(5000); check(!task.isAlive && !blocked.isSigning())
        check(blockedOwner.begin(AudioProofApproval.acquire(audioSource, blockedOwner.nextRequestId())) !== blocked)
        // A committed Supersede can have an empty remote pending slot and a
        // lost receipt. Its exact phone operation must survive signed resync.
        val replacementBytes = hex.parseHex(readln())
        val replacementChallenge = hex.parseHex(readln()); val replacementRoot = hex.parseHex(readln())
        val replacementId = AudioFrozenProof(replacementChallenge).requestId()
        val replacementApproval = AudioProofApproval.delivery(audioSource, audioBinding, replacementBytes,
            AudioOperation.SUPERSEDE_DELIVERY, ByteArray(32) { 10 }, ByteArray(32) { 11 }, replacementId)
        val replacement = audioOwner.begin(replacementApproval)
        replacement.setChallenge(replacementChallenge, replacementRoot)
        val replacementSignature = signer.signAudioProof(active, replacement); audioOwner.confirmProof(replacement)
        println(hex.formatHex(replacementSignature)); println(hex.formatHex(audioOwner.beginResync()))
        audioOwner.resync(active, hex.parseHex(readln()))
        check(!replacement.isRetired() && audioOwner.begin(replacementApproval) === replacement)
        check(audioOwner.nextRequestId().contentEquals(replacementId) && replacement.firstSendNanos == 1150000000L)
        println(hex.formatHex(replacement.appliedResultRequest()))
        val replacementReceipt = hex.parseHex(readln()); audioNow = 1200000000L
        val replacementWindow = audioOwner.accept(active, replacement, replacementReceipt, 1000)
        check(replacementWindow.deadlineNanos == 1549600000L)
        audioNow = 1250000000L
        check(replacementWindow === audioOwner.accept(active, replacement, replacementReceipt, 1000))
        check(runCatching { replacement.appliedResultRequest() }.isFailure) // No longer a pending recovery.
        println("AUDIO_HANDOVER_RESULT_PASS")
        val stopIntent = audioOwner.beginStop(active, AudioFrozenProof(replacementChallenge).boot())
        check(stopIntent === audioOwner.beginStop(active, AudioFrozenProof(replacementChallenge).boot()))
        check(runCatching { audioOwner.nextRequestId() }.isFailure)
        val stopObservation = AudioStopObservation(java.util.UUID.fromString("99998888-7777-6666-5555-444433332222"),
            ByteArray(32) { 10 }, ByteArray(32) { 11 }, ByteArray(32) { 13 })
        val stopAttempt = stopIntent.prepare(stopObservation)
        val stopSignature = signer.signAudioSourceStop(active, stopAttempt)
        check(stopSignature.contentEquals(signer.signAudioSourceStop(active, stopAttempt)))
        keys.values.clear(); check(runCatching { signer.signAudioSourceStop(active, stopAttempt) }.isFailure)
        keys.values.putAll(savedKeys); check(stopSignature.contentEquals(signer.signAudioSourceStop(active, stopAttempt)))
        check(stopAttempt === stopIntent.prepare(stopObservation))
        check(stopAttempt.transcript().contentEquals(AudioSourceStopCodec.canonicalStop(stopAttempt.transcript())))
        println(hex.formatHex(stopAttempt.envelope())); println(hex.formatHex(stopIntent.statusRequest()))
        val stopStatus = hex.parseHex(readln())
        check(runCatching { stopIntent.acceptStatus(active, stopStatus.copyOf().also { it[it.lastIndex] = (it.last().toInt() xor 1).toByte() }) }.isFailure)
        check(stopIntent.acceptStatus(active, stopStatus) == AudioSourceStopState.DENIED)
        check(runCatching { stopIntent.acceptStatus(active, stopStatus) }.isFailure)
        println("AUDIO_SOURCE_STOP_PASS")
        val positiveOwner = AudioPhoneRequests(audioSource) { 1150000000L }
        val positive = positiveOwner.begin(AudioProofApproval.acquire(audioSource, audioNonce))
        positive.setChallenge(audioChallenge, audioHostSignature)
        val positiveEntered = java.util.concurrent.CountDownLatch(1); val positiveRelease = java.util.concurrent.CountDownLatch(1)
        val blockFirstLookup = java.util.concurrent.atomic.AtomicBoolean(true)
        keys.beforeFind = { if (blockFirstLookup.compareAndSet(true, false)) { positiveEntered.countDown(); check(positiveRelease.await(5, java.util.concurrent.TimeUnit.SECONDS)) } }
        val positiveWork = Thread { runCatching { signer.signAudioProof(active, positive) } }
        val negativeDone = java.util.concurrent.CountDownLatch(1)
        val negativeSignature = java.util.concurrent.atomic.AtomicReference<ByteArray?>()
        val freshStop = stopIntent.prepare(AudioStopObservation(java.util.UUID.randomUUID(), ByteArray(32) { 10 }, ByteArray(32) { 11 }, ByteArray(32) { 14 }))
        val negativeWork = Thread { try { negativeSignature.set(signer.signAudioSourceStop(active, freshStop)) } finally { negativeDone.countDown() } }
        positiveWork.start()
        try {
            check(positiveEntered.await(5, java.util.concurrent.TimeUnit.SECONDS)); negativeWork.start()
            check(negativeDone.await(3, java.util.concurrent.TimeUnit.SECONDS) && negativeSignature.get() != null)
        } finally {
            positiveRelease.countDown(); keys.beforeFind = null; positiveWork.join(5000); negativeWork.join(5000)
        }
        check(!positiveWork.isAlive && !negativeWork.isAlive)
        audioNow = window.deadlineNanos
        check(runCatching { audioOwner.accept(active, deliveryPending, deliveryReceipt, 1000) }.isFailure)
        check(runCatching { audioOwner.nextRequestId() }.isFailure)
        println("AUDIO_STATUS_PASS")
        val denialOwner = AudioPhoneRequests(audioSource) { 1100000000L }
        val denialPending = denialOwner.begin(AudioProofApproval.acquire(audioSource, audioNonce))
        denialPending.setChallenge(audioChallenge, audioHostSignature)
        signer.signAudioProof(active, denialPending)
        repository.remove(host, offer.enrollment)
        check(runCatching { signer.signAudioSourceStop(active, freshStop) }.isFailure)
        check(runCatching { signer.signAudioProof(active, denialPending) }.isFailure)
        check(runCatching { active.requireCurrent() }.isFailure)
        check(runCatching { signer.signCommittedProof(active, challenge, hostChallengeSignature, proofContext) }.isFailure)
        println("TRUSTED_ENROLLMENT_KOTLIN_PASS")
    }
}
