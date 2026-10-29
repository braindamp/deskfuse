package com.focuswindow.client.transport

import java.nio.ByteBuffer
import java.security.MessageDigest
import java.util.UUID

internal interface TrustedEnrollmentPersistence {
    fun read(host: ByteArray, enrollment: UUID, suffix: String): ByteArray?
    fun writeOnce(host: ByteArray, enrollment: UUID, suffix: String, bytes: ByteArray)
    fun hasRevocation(host: ByteArray, enrollment: UUID): Boolean = read(host, enrollment, "revoked") != null
    fun requireOwnerCurrent() {}
    fun preparedRecords(): List<ByteArray> = emptyList()
}

internal class TrustedProposedEnrollment private constructor(private val saved: ByteArray) {
    fun envelopes(): Pair<ByteArray, ByteArray> {
        val input = ByteBuffer.wrap(saved)
        fun blob(): ByteArray { require(input.remaining() >= 2); val count = input.short.toInt() and 65535
            require(count <= TrustedEnrollmentProtocol.MAX_TRANSCRIPT + 75 && input.remaining() >= count)
            return ByteArray(count).also { input.get(it) } }
        val offer = blob(); val proposal = blob(); require(!input.hasRemaining()); return offer to proposal
    }
    fun bytes(): ByteArray = saved.copyOf()
    companion object {
        fun create(signedOffer: ByteArray, signedProposal: ByteArray): TrustedProposedEnrollment {
            val bytes = ByteBuffer.allocate(4 + signedOffer.size + signedProposal.size).putShort(signedOffer.size.toShort())
                .put(signedOffer).putShort(signedProposal.size.toShort()).put(signedProposal).array()
            return parse(bytes)
        }
        fun parse(bytes: ByteArray): TrustedProposedEnrollment {
            require(bytes.size <= TrustedEnrollmentProtocol.MAX_FRAME)
            val value = TrustedProposedEnrollment(bytes.copyOf()); val (offer, proposal) = value.envelopes()
            val (offerBytes, rootSignature) = TrustedSetupWire.parseSigned(offer); val parsedOffer = TrustedEnrollmentProtocol.parseOffer(offerBytes)
            val (proposalBytes, phoneSignature) = TrustedSetupWire.parseSigned(proposal); val parsedProposal = TrustedEnrollmentProtocol.parseProposal(proposalBytes)
            require(MessageDigest.isEqual(parsedProposal.offerHash, TrustedEnrollmentProtocol.hash(offerBytes)) &&
                parsedOffer.scopes.containsAll(parsedProposal.scopes) && TrustedEnrollmentProtocol.verify(parsedOffer.hostSpki, offerBytes, rootSignature) &&
                TrustedEnrollmentProtocol.verify(parsedProposal.phoneSpki, proposalBytes, phoneSignature)) { "Invalid saved proposal" }
            return value // Still provisional: this record confers no consent, root trust or runtime signing.
        }
    }
}

internal class TrustedPreparedEnrollment private constructor(private val encoded: ByteArray, private val current: (() -> Unit)? = null) {
    fun requireCurrent() = (current ?: error("Prepared enrollment has not completed persistence"))()
    internal fun completed(requireCurrent: () -> Unit): TrustedPreparedEnrollment = TrustedPreparedEnrollment(encoded.copyOf(), requireCurrent)
    fun frozen(): Pair<ByteArray, ByteArray> = TrustedSetupWire.parseSigned(encoded.copyOf())
    fun transcript(): EnrollmentTranscript = TrustedEnrollmentProtocol.parseTranscript(frozen().first)
    fun binding(): EnrollmentBinding = TrustedEnrollmentProtocol.binding(transcript())
    fun bytes(): ByteArray = encoded.copyOf()
    companion object {
        fun parseStoreRecord(bytes: ByteArray): TrustedPreparedEnrollment {
            val snapshot = bytes.copyOf(); val (message, signature) = TrustedSetupWire.parseSigned(snapshot)
            val transcript = TrustedEnrollmentProtocol.parseTranscript(message)
            require(TrustedEnrollmentProtocol.verify(transcript.offer.hostSpki,
                TrustedEnrollmentProtocol.frozen(TrustedEnrollmentProtocol.hash(message)), signature)) { "Invalid saved frozen enrollment" }
            return TrustedPreparedEnrollment(snapshot)
        }
    }
}

internal class TrustedCommittedEnrollment private constructor(
    private val prepared: TrustedPreparedEnrollment, private val encodedReceipt: ByteArray, private val current: () -> Unit,
) {
    fun requireCurrent() = current()
    fun transcript(): EnrollmentTranscript = prepared.transcript()
    fun receipt(): EnrollmentReceipt = TrustedEnrollmentProtocol.parseReceipt(TrustedSetupWire.parseSigned(encodedReceipt).first)
    companion object {
        fun validate(prepared: TrustedPreparedEnrollment, receipt: ByteArray, requireCurrent: () -> Unit): TrustedCommittedEnrollment {
            prepared.requireCurrent()
            val bytes = receipt.copyOf(); val (message, signature) = TrustedSetupWire.parseSigned(bytes)
            val actual = TrustedEnrollmentProtocol.parseReceipt(message); val transcript = prepared.transcript()
            require(TrustedEnrollmentProtocol.verify(transcript.offer.hostSpki, message, signature)) { "Invalid committed receipt signature" }
            require(TrustedEnrollmentProtocol.phoneApproval(actual.binding).contentEquals(
                TrustedEnrollmentProtocol.phoneApproval(prepared.binding()))) { "Receipt changed enrollment context" }
            require(MessageDigest.isEqual(actual.peer, transcript.offer.peer) && MessageDigest.isEqual(actual.subject, transcript.offer.subject) &&
                actual.scopes == transcript.proposal.scopes && MessageDigest.isEqual(actual.phoneKeyHash, TrustedEnrollmentProtocol.hash(transcript.proposal.phoneSpki)))
            return TrustedCommittedEnrollment(prepared, bytes, requireCurrent)
        }
    }
}

/** One writer owns all state transitions. No runtime signer is activated by a proposed/prepared record. */
internal class TrustedEnrollmentRepository(private val persistence: TrustedEnrollmentPersistence) {
    private val preparedCache = mutableMapOf<String, TrustedPreparedEnrollment>()
    private val activeCache = mutableMapOf<String, TrustedCommittedEnrollment>()

    fun recoverable(host: ByteArray): List<TrustedPreparedEnrollment> = synchronized(writerGate) {
        require(host.size == 32); persistence.requireOwnerCurrent()
        val result = mutableListOf<TrustedPreparedEnrollment>()
        for (bytes in persistence.preparedRecords()) {
            val candidate = TrustedPreparedEnrollment.parseStoreRecord(bytes); val binding = candidate.binding()
            if (!MessageDigest.isEqual(binding.hostHash, host)) continue
            val key = localKey(host, binding.enrollment)
            if (denied.contains(key)) continue
            if (persistence.hasRevocation(host, binding.enrollment)) { denied.add(key); continue }
            result.add(prepared(host, binding.enrollment)) // Complete through its exact bound filename.
        }
        result
    }

    private fun requireCurrent(key: String) {
        persistence.requireOwnerCurrent()
        require(!denied.contains(key)) { "Enrollment was locally removed or quarantined" }
    }
    private fun requireAvailable(host: ByteArray, enrollment: UUID): String {
        val key = localKey(host, enrollment)
        requireCurrent(key)
        try {
            if (persistence.hasRevocation(host, enrollment)) {
                denied.add(key)
                error("Enrollment was locally removed")
            }
        } catch (failure: Exception) { denied.add(key); throw failure }
        return key
    }

    fun prepare(frozen: ByteArray): TrustedPreparedEnrollment = synchronized(writerGate) {
        val candidate = TrustedPreparedEnrollment.parseStoreRecord(frozen); val binding = candidate.binding()
        val key = requireAvailable(binding.hostHash, binding.enrollment)
        try {
            persistence.writeOnce(binding.hostHash, binding.enrollment, "prepared", candidate.bytes())
            requireCurrent(key)
            candidate.completed { requireCurrent(key) }.also { preparedCache[key] = it }
        } catch (failure: Exception) { denied.add(key); throw failure }
    }
    fun proposal(host: ByteArray, enrollment: UUID): TrustedProposedEnrollment? = synchronized(writerGate) {
        requireAvailable(host, enrollment)
        require(persistence.read(host, enrollment, "prepared") == null && persistence.read(host, enrollment, "active") == null) {
            "This enrollment is already prepared; recover its status instead of creating another key/proposal"
        }
        val bytes = persistence.read(host, enrollment, "proposed") ?: return@synchronized null
        val value = TrustedProposedEnrollment.parse(bytes)
        requireProposalBinding(value, host, enrollment)
        persistence.writeOnce(host, enrollment, "proposed", value.bytes()) // Complete exact recovered proposal before use.
        requireCurrent(localKey(host, enrollment)); value
    }
    fun saveProposal(host: ByteArray, enrollment: UUID, value: TrustedProposedEnrollment) = synchronized(writerGate) {
        val key = requireAvailable(host, enrollment)
        requireProposalBinding(value, host, enrollment)
        try {
            persistence.writeOnce(host, enrollment, "proposed", value.bytes())
            requireCurrent(key)
        } catch (failure: Exception) { denied.add(key); throw failure }
    }
    fun prepared(host: ByteArray, enrollment: UUID): TrustedPreparedEnrollment = synchronized(writerGate) {
        val key = requireAvailable(host, enrollment)
        preparedCache[key]?.let { return@synchronized it.also { it.requireCurrent() } }
        try {
            val value = TrustedPreparedEnrollment.parseStoreRecord(persistence.read(host, enrollment, "prepared") ?: error("Missing prepared enrollment"))
            require(MessageDigest.isEqual(value.binding().hostHash, host) && value.binding().enrollment == enrollment)
            persistence.writeOnce(host, enrollment, "prepared", value.bytes())
            requireCurrent(key)
            value.completed { requireCurrent(key) }.also { preparedCache[key] = it }
        } catch (failure: Exception) { denied.add(key); throw failure }
    }
    fun activate(host: ByteArray, enrollment: UUID, signedReceipt: ByteArray): TrustedCommittedEnrollment = synchronized(writerGate) {
        val key = requireAvailable(host, enrollment); val prepared = prepared(host, enrollment)
        try {
            val active = TrustedCommittedEnrollment.validate(prepared, signedReceipt) { requireCurrent(key) }
            persistence.writeOnce(host, enrollment, "active", signedReceipt.copyOf())
            requireCurrent(key); active.also { activeCache[key] = it }
        } catch (failure: Exception) { denied.add(key); throw failure }
    }
    fun committed(host: ByteArray, enrollment: UUID): TrustedCommittedEnrollment = synchronized(writerGate) {
        val key = requireAvailable(host, enrollment)
        activeCache[key]?.let { return@synchronized it.also { it.requireCurrent() } }
        try {
            val prepared = prepared(host, enrollment)
            val bytes = persistence.read(host, enrollment, "active") ?: error("Enrollment is not committed")
            val active = TrustedCommittedEnrollment.validate(prepared, bytes) { requireCurrent(key) }
            persistence.writeOnce(host, enrollment, "active", bytes)
            requireCurrent(key); active.also { activeCache[key] = it }
        } catch (failure: Exception) { denied.add(key); throw failure }
    }
    fun remove(host: ByteArray, enrollment: UUID) {
        denied.add(localKey(host, enrollment)) // Immediate memory fence cannot wait behind storage.
        synchronized(writerGate) { persistence.writeOnce(host, enrollment, "revoked", byteArrayOf(1)) }
        // Keep keys and records for exact status/revocation recovery.
    }
    private fun requireProposalBinding(value: TrustedProposedEnrollment, host: ByteArray, enrollment: UUID) {
        val offer = TrustedEnrollmentProtocol.parseOffer(TrustedSetupWire.parseSigned(value.envelopes().first).first)
        require(offer.enrollment == enrollment && MessageDigest.isEqual(TrustedEnrollmentProtocol.hash(offer.hostSpki), host))
    }
    private companion object {
        val writerGate = Any()
        val denied = java.util.concurrent.ConcurrentHashMap.newKeySet<String>()
        fun localKey(host: ByteArray, enrollment: UUID): String {
            require(host.size == 32 && enrollment != UUID(0, 0))
            return host.joinToString("") { "%02x".format(it.toInt() and 255) } + enrollment.toString()
        }
    }
}

internal object TrustedSetupWire {
    const val OFFER = 32
    const val PROPOSAL = 33
    const val FROZEN = 34
    const val PHONE_APPROVAL = 35
    const val RECEIPT = 36
    const val STATUS_REQUEST = 37
    const val STATUS = 38
    const val CANCEL = 6
    fun signed(message: ByteArray, signature: ByteArray): ByteArray {
        require(message.size <= TrustedEnrollmentProtocol.MAX_TRANSCRIPT && signature.size in 8..72)
        return ByteBuffer.allocate(message.size + signature.size + 3).putShort(message.size.toShort()).put(message)
            .put(signature.size.toByte()).put(signature).array()
    }
    fun parseSigned(bytes: ByteArray): Pair<ByteArray, ByteArray> {
        require(bytes.size in 3..(TrustedEnrollmentProtocol.MAX_TRANSCRIPT + 75))
        val input = ByteBuffer.wrap(bytes); val count = input.short.toInt() and 65535
        require(count <= TrustedEnrollmentProtocol.MAX_TRANSCRIPT && input.remaining() >= count + 1)
        val message = ByteArray(count).also { input.get(it) }; val signatureSize = input.get().toInt() and 255
        require(signatureSize in 8..72 && input.remaining() == signatureSize)
        return message to ByteArray(signatureSize).also { input.get(it) }
    }
}
