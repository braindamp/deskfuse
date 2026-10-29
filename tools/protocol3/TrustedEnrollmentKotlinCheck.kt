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
            override fun find(alias: String) = values[alias]
            override fun create(alias: String): BrokerSigningKey {
                check(!values.containsKey(alias))
                val pair = KeyPairGenerator.getInstance("EC").apply { initialize(ECGenParameterSpec("secp256r1")) }.generateKeyPair()
                return BrokerSigningKey(pair.private, pair.public.encoded, BrokerKeyProtection.SOFTWARE).also { values[alias] = it }
            }
        }
        val signer = BrokerDeviceSigner(keys); val host = TrustedEnrollmentProtocol.hash(offer.hostSpki)
        val identity = signer.prepareEnrollment(host, offer.enrollment)
        val (proposal, signature) = signer.signEnrollmentProposal(host, offer.enrollment, identity.publicSpkiSha256,
            offerBytes, ByteArray(32), listOf(BrokerScope.SPEAKER))
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
        val proofContext = BrokerProofApproval(offer.peer, ByteArray(32), BrokerOperation.ACQUIRE, BrokerScope.SPEAKER,
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
        val foreign = BrokerProofApproval(offer.peer, ByteArray(32), BrokerOperation.ACQUIRE, BrokerScope.SPEAKER,
            offer.ui, 1, ByteArray(32), offer.namespace, offer.enrollment, offer.subject)
        check(runCatching { signer.signExisting(host, offer.enrollment, identity.publicSpkiSha256, challenge, foreign) }.isFailure)
        val v1 = BrokerProofApproval(offer.peer, ByteArray(32), BrokerOperation.ACQUIRE, BrokerScope.SPEAKER, offer.ui, 1)
        check(runCatching { BrokerProofTranscript.requireApproved(challenge, v1) }.isFailure)
        repository.remove(host, offer.enrollment)
        check(runCatching { active.requireCurrent() }.isFailure)
        check(runCatching { signer.signCommittedProof(active, challenge, hostChallengeSignature, proofContext) }.isFailure)
        println("TRUSTED_ENROLLMENT_KOTLIN_PASS")
    }
}
