package com.focuswindow.client.transport

import java.nio.ByteBuffer
import java.nio.ByteOrder
import java.security.MessageDigest
import java.security.PrivateKey
import java.security.Signature
import java.util.UUID

enum class BrokerOperation(val wire: Int) { ACQUIRE(1), RENEW(2), SUPERSEDE(3), RELEASE(4) }
enum class BrokerScope(val wire: Int) {
    SPEAKER(1), DISPLAY(2), INPUT(3), APPLICATION_CAPTURE(4), MICROPHONE(5), CAMERA(6), FILES(7), CLIPBOARD(8),
}
enum class BrokerKeyProtection { SOFTWARE, TRUSTED_ENVIRONMENT, STRONGBOX, UNKNOWN }

/** Exact phone-side approval context. Values must come from the current authorized task. */
class BrokerProofApproval(
    peerIdentity: ByteArray, resourceIdentity: ByteArray,
    val operation: BrokerOperation, val scope: BrokerScope,
    val registration: UUID, val expectedOwnerGeneration: Long,
    hostIdentitySha256: ByteArray? = null, val authorityNamespace: UUID? = null,
    val enrollmentId: UUID? = null, subjectUserSid: ByteArray? = null,
) {
    internal val peer = peerIdentity.copyOf().also { require(it.size == 32) }
    internal val resource = resourceIdentity.copyOf().also { require(it.size == 32) }
    internal val host = hostIdentitySha256?.copyOf()
    internal val subject = subjectUserSid?.copyOf()
    init {
        val values = listOf(host, authorityNamespace, enrollmentId, subject)
        require(values.all { it == null } || values.all { it != null })
        if (host != null) {
            require(host.size == 32 && authorityNamespace != UUID(0, 0) && enrollmentId != UUID(0, 0))
            TrustedEnrollmentProtocol.validateSid(subject!!)
        }
    }
}

/** The broker's clock value is opaque to Android; only the broker decides expiry. */
internal object BrokerProofTranscript {
    private val domainV1 = "DeskFuse.BrokerProof.v1\u0000".toByteArray(Charsets.US_ASCII)
    private val domainV2 = "DeskFuse.BrokerProof.v2\u0000".toByteArray(Charsets.US_ASCII)

    fun requireApproved(bytes: ByteArray, approval: BrokerProofApproval) {
        val domain = if (approval.host == null) domainV1 else domainV2
        require(bytes.size in (if (approval.host == null) 188..308 else 252..384)) { "Invalid broker proof size" }
        val input = ByteBuffer.wrap(bytes).order(ByteOrder.BIG_ENDIAN)
        fun field(size: Int) = ByteArray(size).also { require(input.remaining() >= size); input.get(it) }
        require(field(domain.size).contentEquals(domain)) { "Unsupported broker proof domain" }
        field(32) // Unpredictable broker nonce; broker enforces one-use freshness.
        if (approval.host != null) {
            require(MessageDigest.isEqual(field(32), approval.host)) { "Broker host changed" }
            require(UUID(input.long, input.long) == approval.authorityNamespace) { "Broker namespace changed" }
            require(UUID(input.long, input.long) == approval.enrollmentId) { "Broker enrollment changed" }
        }
        require(MessageDigest.isEqual(field(32), approval.peer)) { "Broker proof peer changed" }
        require(MessageDigest.isEqual(field(32), approval.resource)) { "Broker proof resource changed" }
        require((input.get().toInt() and 255) == approval.operation.wire) { "Unapproved broker operation" }
        require((input.get().toInt() and 255) == approval.scope.wire) { "Unapproved broker scope" }
        require(UUID(input.long, input.long) == approval.registration) { "Broker registration changed" }
        fun sid(logon: Boolean) {
            val size = input.get().toInt() and 255
            require(size in 8..68)
            val value = field(size)
            val subAuthorities = value[1].toInt() and 255
            require(value[0].toInt() == 1 && subAuthorities <= 15 && size == 8 + 4 * subAuthorities)
            if (logon) {
                require(size == 20 && subAuthorities == 3 &&
                    value.copyOfRange(2, 8).contentEquals(byteArrayOf(0, 0, 0, 0, 0, 5)) &&
                    ByteBuffer.wrap(value, 8, 4).order(ByteOrder.LITTLE_ENDIAN).int == 5)
            }
            if (!logon && approval.subject != null) require(MessageDigest.isEqual(value, approval.subject)) { "Enrolled Windows subject changed" }
        }
        sid(false); sid(true)
        require(input.remaining() == 40) { "Invalid broker proof layout" }
        input.long // Windows logon authentication ID.
        input.int // Windows session ID.
        require(input.int != 0) { "Invalid broker worker PID" }
        require(input.long != 0L) { "Invalid broker worker incarnation" }
        require(input.long == approval.expectedOwnerGeneration) { "Broker owner generation changed" }
        require(input.long >= 0 && !input.hasRemaining()) { "Invalid broker expiry" }
    }
}

/** Metadata offered to the protected enrollment ceremony; this does not approve enrollment. */
class BrokerSigningIdentity(publicSpki: ByteArray, val protection: BrokerKeyProtection) {
    private val spki = publicSpki.copyOf()
    val publicSpki: ByteArray get() = spki.copyOf()
    val publicSpkiSha256: ByteArray get() = MessageDigest.getInstance("SHA-256").digest(spki)
}

internal class BrokerSigningKey(val privateKey: PrivateKey, val spki: ByteArray, val protection: BrokerKeyProtection)
internal interface BrokerSigningKeyStore {
    fun find(alias: String): BrokerSigningKey?
    fun create(alias: String): BrokerSigningKey
}

/** Separate ECDSA identity; transport ECDH keys and pairing secrets are never signing keys. */
internal class BrokerDeviceSigner(private val keys: BrokerSigningKeyStore) {
    private val proofSignatures = linkedMapOf<String, ByteArray>()
    fun prepareEnrollment(hostIdentitySha256: ByteArray, enrollmentId: UUID): BrokerSigningIdentity = synchronized(gate) {
        val alias = alias(hostIdentitySha256, enrollmentId)
        val key = keys.find(alias) ?: keys.create(alias)
        validatePublic(key.spki)
        BrokerSigningIdentity(key.spki, key.protection)
    }

    /** Proposal possession proof only; this neither confirms enrollment nor activates runtime signing. */
    fun signEnrollmentProposal(hostIdentitySha256: ByteArray, enrollmentId: UUID, approvedSpkiSha256: ByteArray,
        offerBytes: ByteArray, phoneNonce: ByteArray, selectedScopes: List<BrokerScope>): Pair<ByteArray, ByteArray> = synchronized(gate) {
        val offerSnapshot = offerBytes.copyOf()
        val offer = TrustedEnrollmentProtocol.parseOffer(offerSnapshot)
        require(offer.enrollment == enrollmentId && MessageDigest.isEqual(TrustedEnrollmentProtocol.hash(offer.hostSpki), hostIdentitySha256))
        require(offer.scopes.containsAll(selectedScopes))
        val key = existingKey(hostIdentitySha256, enrollmentId, approvedSpkiSha256)
        val proposal = TrustedEnrollmentProtocol.proposal(EnrollmentProposal(TrustedEnrollmentProtocol.hash(offerSnapshot),
            key.spki.copyOf(), phoneNonce.copyOf(), selectedScopes.toList()))
        proposal to sign(key, proposal)
    }

    fun signPreparedEnrollmentApproval(prepared: TrustedPreparedEnrollment): Pair<ByteArray, ByteArray> = synchronized(gate) {
        prepared.requireCurrent()
        val transcript = prepared.transcript(); val binding = prepared.binding()
        val key = existingKey(binding.hostHash, binding.enrollment, TrustedEnrollmentProtocol.hash(transcript.proposal.phoneSpki))
        require(key.spki.contentEquals(transcript.proposal.phoneSpki))
        val approval = TrustedEnrollmentProtocol.phoneApproval(binding)
        approval to sign(key, approval)
    }
    fun signEnrollmentStatus(prepared: TrustedPreparedEnrollment, nonce: ByteArray): Pair<ByteArray, ByteArray> = synchronized(gate) {
        val transcript = prepared.transcript(); val binding = prepared.binding()
        val key = existingKey(binding.hostHash, binding.enrollment, TrustedEnrollmentProtocol.hash(transcript.proposal.phoneSpki))
        val request = TrustedEnrollmentProtocol.statusRequest(EnrollmentStatusRequest(binding, nonce.copyOf(), key.spki.copyOf()))
        request to sign(key, request)
    }

    fun signCommittedProof(active: TrustedCommittedEnrollment, bytes: ByteArray, hostSignature: ByteArray,
        approval: BrokerProofApproval): ByteArray = synchronized(gate) {
        active.requireCurrent()
        val transcript = active.transcript(); val binding = TrustedEnrollmentProtocol.binding(transcript)
        require(approval.host != null && MessageDigest.isEqual(approval.host, binding.hostHash) &&
            approval.authorityNamespace == binding.namespace && approval.enrollmentId == binding.enrollment &&
            MessageDigest.isEqual(approval.subject, transcript.offer.subject) && MessageDigest.isEqual(approval.peer, transcript.offer.peer) &&
            transcript.proposal.scopes.contains(approval.scope)) { "Proof is outside the committed enrollment" }
        val snapshot = bytes.copyOf()
        val challenge = "DeskFuse.BrokerChallenge.v1\u0000".toByteArray(Charsets.US_ASCII) + TrustedEnrollmentProtocol.hash(snapshot)
        require(TrustedEnrollmentProtocol.verify(transcript.offer.hostSpki, challenge, hostSignature.copyOf())) { "Untrusted broker challenge" }
        val signature = signBoundExisting(binding.hostHash, binding.enrollment, TrustedEnrollmentProtocol.hash(transcript.proposal.phoneSpki), snapshot, approval)
        active.requireCurrent() // A local denial during Keystore work wins before returning the proof.
        signature
    }

    fun signExisting(hostIdentitySha256: ByteArray, enrollmentId: UUID, approvedSpkiSha256: ByteArray,
        transcript: ByteArray, approval: BrokerProofApproval): ByteArray = synchronized(gate) {
        require(approval.host == null) { "Authority v2 proofs require committed enrollment and an authenticated broker challenge" }
        signBoundExisting(hostIdentitySha256, enrollmentId, approvedSpkiSha256, transcript, approval)
    }

    private fun signBoundExisting(hostIdentitySha256: ByteArray, enrollmentId: UUID, approvedSpkiSha256: ByteArray,
        transcript: ByteArray, approval: BrokerProofApproval): ByteArray {
        require(approvedSpkiSha256.size == 32)
        // Snapshot all mutable input before verification and signing.
        val expectedKey = approvedSpkiSha256.copyOf()
        val bytes = transcript.copyOf()
        BrokerProofTranscript.requireApproved(bytes, approval)
        if (approval.host != null) require(MessageDigest.isEqual(hostIdentitySha256, approval.host) && enrollmentId == approval.enrollmentId)
        val key = existingKey(hostIdentitySha256, enrollmentId, expectedKey)
        val cacheKey = alias(hostIdentitySha256, enrollmentId) + ":" + TrustedEnrollmentProtocol.hash(bytes).joinToString("") { "%02x".format(it.toInt() and 255) }
        proofSignatures[cacheKey]?.let { return it.copyOf() }
        val signature = sign(key, bytes)
        if (proofSignatures.size >= 64) proofSignatures.remove(proofSignatures.keys.first())
        proofSignatures[cacheKey] = signature.copyOf()
        return signature
    }

    private fun existingKey(host: ByteArray, enrollment: UUID, expectedKey: ByteArray): BrokerSigningKey {
        require(expectedKey.size == 32)
        val key = keys.find(alias(host.copyOf(), enrollment))
            ?: error("Broker signing key is unavailable; a new explicit enrollment is required")
        validatePublic(key.spki)
        require(MessageDigest.isEqual(MessageDigest.getInstance("SHA-256").digest(key.spki), expectedKey)) {
            "Broker signing identity changed; a new explicit enrollment is required"
        }
        return key
    }

    private fun sign(key: BrokerSigningKey, bytes: ByteArray): ByteArray = Signature.getInstance("SHA256withECDSA").run {
        initSign(key.privateKey); update(bytes)
        sign().also { require(it.size in 8..72) { "Unexpected P-256 signature encoding" } }
    }

    private fun alias(host: ByteArray, enrollmentId: UUID): String {
        require(host.size == 32 && enrollmentId != UUID(0, 0))
        val uuid = ByteBuffer.allocate(16).putLong(enrollmentId.mostSignificantBits).putLong(enrollmentId.leastSignificantBits).array()
        val digest = MessageDigest.getInstance("SHA-256").digest(
            "DeskFuse.BrokerSigningKey.v1\u0000".toByteArray(Charsets.US_ASCII) + host + uuid)
        return "deskfuse.broker-sign.v1." + digest.joinToString("") { "%02x".format(it.toInt() and 255) }
    }

    private fun validatePublic(spki: ByteArray) {
        val prefix = byteArrayOf(0x30, 0x59, 0x30, 0x13, 0x06, 0x07, 0x2a, 0x86.toByte(), 0x48, 0xce.toByte(), 0x3d,
            0x02, 0x01, 0x06, 0x08, 0x2a, 0x86.toByte(), 0x48, 0xce.toByte(), 0x3d, 0x03, 0x01, 0x07, 0x03, 0x42, 0x00, 0x04)
        require(spki.size == 91 && spki.copyOfRange(0, prefix.size).contentEquals(prefix)) { "Expected P-256 SPKI" }
    }

    private companion object { val gate = Any() }
}
