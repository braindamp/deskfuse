package com.focuswindow.client.transport

import java.nio.ByteBuffer
import java.security.SecureRandom
import java.util.concurrent.atomic.AtomicBoolean

internal class AudioRequestId(bytes: ByteArray) {
    private val value = bytes.copyOf()
    val sequence: ULong
    init {
        require(value.size == 32)
        sequence = ByteBuffer.wrap(value).long.toULong()
        require(sequence != 0uL && value.drop(8).any { it != 0.toByte() })
    }
    fun bytes() = value.copyOf()
    companion object {
        fun fresh(sequence: ULong): ByteArray {
            require(sequence != 0uL)
            val bytes = ByteArray(32)
            do { SecureRandom().nextBytes(bytes) } while (bytes.drop(8).all { it == 0.toByte() })
            ByteBuffer.wrap(bytes).putLong(sequence.toLong()); return bytes
        }
    }
}

internal class AudioFrozenProof(bytes: ByteArray) {
    private val encoded = bytes.copyOf()
    private val coreBytes: ByteArray
    private val bootBytes: ByteArray
    val source: java.util.UUID
    val sourceGeneration: ULong
    private val digest: ByteArray
    val operation: AudioOperation
    private val request: ByteArray
    val revision: ULong
    val until: ULong
    init {
        require(encoded.size <= 1024)
        val r = AudioReader(encoded, "Proof")
        val length = r.u16(); require(length in 252..384); coreBytes = r.bytes(length)
        bootBytes = r.hash(); source = r.id(); sourceGeneration = r.nonzero(); digest = r.hash()
        val kind = r.byte(); operation = AudioOperation.entries.single { it.wire == kind }
        request = r.bytes(32); AudioRequestId(request); revision = r.u64(); until = r.nonzero(); r.end()
        // Core parsing/identity approval is performed by AudioProofApproval.
        val expiry = ByteBuffer.wrap(coreBytes, coreBytes.size - 8, 8).long
        require(expiry >= 0 && expiry.toULong() <= until / 10000uL)
    }
    fun bytes() = encoded.copyOf()
    fun core() = coreBytes.copyOf()
    fun boot() = bootBytes.copyOf()
    fun sourceDigest() = digest.copyOf()
    fun requestId() = request.copyOf()
}

/** Exactly one original send anchor and one real signing completion per request.
 * The global signer's evictable generic cache is not operation custody. */
internal class AudioPendingRequest internal constructor(
    val approval: AudioProofApproval, val firstSendNanos: Long, boot: ByteArray?, val minimumRevision: ULong,
    private val requireCurrent: () -> Unit, private val beginSigning: () -> Unit, private val completed: () -> Unit,
) {
    private val boot = boot?.copyOf()
    private val signingGate = Any()
    private var proof: ByteArray? = null
    private var rootSignature: ByteArray? = null
    private var signature: ByteArray? = null
    private var attempted = false
    private val signing = AtomicBoolean(false)
    private val verifying = AtomicBoolean(false)
    @Volatile private var retired = false
    @Volatile private var verified = false
    private var acceptedReceipt: ByteArray? = null
    private var acceptedWindow: AudioGrantWindow? = null
    val sequence = AudioRequestId(approval.nonce()).sequence
    fun expectedBoot() = boot?.copyOf()
    fun proofBytes(): ByteArray { require(verified); return proof!!.copyOf() }
    fun isSigning() = signing.get() || verifying.get()
    fun beginVerification() { check(verifying.compareAndSet(false, true)) { "Receipt verification is in progress" } }
    fun endVerification() { verifying.set(false) }
    fun markSigning() { check(signing.compareAndSet(false, true)) }
    fun isRetired() = retired
    fun retire() { retired = true }
    fun hasVerifiedProof() = verified
    fun setChallenge(bytes: ByteArray, hostSignature: ByteArray) = synchronized(signingGate) {
        requireCurrent(); require(!retired)
        val snapshot = bytes.copyOf(); val root = hostSignature.copyOf()
        val parsed = AudioFrozenProof(snapshot)
        require(parsed.requestId().contentEquals(approval.nonce()) && (boot == null || boot.contentEquals(parsed.boot())))
        if (proof != null) require(proof!!.contentEquals(snapshot) && rootSignature!!.contentEquals(root))
        else { require(!attempted); proof = snapshot; rootSignature = root }
    }
    internal fun signWith(validateCached: () -> Unit = {}, action: (ByteArray, ByteArray, AudioProofApproval) -> ByteArray): ByteArray = synchronized(signingGate) {
        requireCurrent(); require(!retired)
        signature?.let {
            beginSigning()
            try { validateCached(); requireCurrent(); require(!retired); return@synchronized it.copyOf() }
            finally { signing.set(false); completed() }
        }
        check(!attempted) { "Audio signing is unconfirmed; do not generate a replacement signature" }
        val bytes = proof?.copyOf() ?: error("No audio challenge")
        val root = rootSignature!!.copyOf(); attempted = true; beginSigning()
        try {
            val result = action(bytes, root, approval)
            require(result.size in 8..72)
            signature = result.copyOf(); verified = true
            requireCurrent(); require(!retired); result.copyOf()
        } finally { signing.set(false); completed() }
    }
    fun accept(active: TrustedCommittedEnrollment, receipt: ByteArray, now: Long, margin: Int): AudioGrantWindow = synchronized(this) {
        require(!retired && verified)
        acceptedWindow?.let {
            require(acceptedReceipt!!.contentEquals(receipt)); active.requireCurrent(); it.requireCurrent(now); return@synchronized it
        }
        AudioGrantWindow.accept(active, this, receipt, now, margin).also {
            acceptedReceipt = receipt.copyOf(); acceptedWindow = it
        }
    }
}

/** Control-only state; N6 connects it to the independent Android audio owner.
 * Losing this object is cold recovery, never adoption of a saved runtime grant. */
internal class AudioPhoneRequests(private val source: AudioSourceApproval, private val clock: () -> Long) {
    private val gate = Any()
    private var issuedThrough = 0uL
    private var pending: AudioPendingRequest? = null
    private var lastAccepted: AudioPendingRequest? = null
    private var window: AudioGrantWindow? = null
    private var boot: ByteArray? = null
    private var highestRevision = 0uL
    private var terminal = false
    private var lastNow = 0L
    private var query: ByteArray? = null
    private fun now(): Long {
        val now = clock()
        if (terminal || now < lastNow || now < 0) { terminal = true; error("Phone audio owner is terminal") }
        lastNow = now
        try { window?.requireCurrent(now) } catch (error: Exception) { terminal = true; throw error }
        return now
    }
    fun nextRequestId(): ByteArray = synchronized(gate) {
        now(); pending?.let { return@synchronized it.approval.nonce() }
        check(issuedThrough != ULong.MAX_VALUE); AudioRequestId.fresh(issuedThrough + 1uL)
    }
    fun begin(approval: AudioProofApproval): AudioPendingRequest = synchronized(gate) {
        val sent = now()
        require(approval.source.digest().contentEquals(source.digest()))
        pending?.let { require(it.approval === approval); return@synchronized it }
        require(issuedThrough != ULong.MAX_VALUE && AudioRequestId(approval.nonce()).sequence == issuedThrough + 1uL)
        require((approval.operation == AudioOperation.ACQUIRE_SOURCE) == (window == null))
        lateinit var value: AudioPendingRequest
        value = AudioPendingRequest(approval, sent, boot, highestRevision,
            { synchronized(gate) { now(); require(pending === value && !value.isRetired()) } },
            { synchronized(gate) { now(); require(pending === value && !value.isRetired()); value.markSigning() } },
            { synchronized(gate) { if (pending === value && value.isRetired() && !value.isSigning()) pending = null } })
        pending = value; value
    }
    fun confirmProof(value: AudioPendingRequest) = synchronized(gate) {
        now(); require(pending === value && value.hasVerifiedProof() && !value.isRetired())
        val frozen = AudioFrozenProof(value.proofBytes())
        require(boot == null || boot!!.contentEquals(frozen.boot()))
        boot = frozen.boot(); issuedThrough = maxOf(issuedThrough, value.sequence)
    }
    fun accept(active: TrustedCommittedEnrollment, value: AudioPendingRequest, receipt: ByteArray, margin: Int): AudioGrantWindow {
        val sampled = synchronized(gate) {
            now().also { require(pending === value || lastAccepted === value); value.beginVerification() }
        }
        try {
            val result = value.accept(active, receipt, sampled, margin)
            return synchronized(gate) {
                now(); require(pending === value || lastAccepted === value)
                if (lastAccepted === value) { require(window === result); return@synchronized result }
                require(result.revision > highestRevision)
                highestRevision = result.revision; boot = result.boot(); issuedThrough = maxOf(issuedThrough, value.sequence)
                window = result; lastAccepted = value; pending = null; result
            }
        } finally {
            synchronized(gate) {
                value.endVerification()
                if (pending === value && value.isRetired() && !value.isSigning()) pending = null
            }
        }
    }
    fun retirePending() = synchronized(gate) {
        pending?.let { it.retire(); if (!it.isSigning()) pending = null }
    }
    fun close() = synchronized(gate) { terminal = true; retirePending() }
    fun beginResync(): ByteArray = synchronized(gate) {
        now(); query?.let { return@synchronized it.copyOf() }; ByteArray(32).also { SecureRandom().nextBytes(it); query = it.copyOf() }
    }
    fun resync(active: TrustedCommittedEnrollment, signed: ByteArray) {
        val nonce = synchronized(gate) { now(); query?.copyOf() ?: error("No status query") }
        val envelope = TrustedSetupWire.parseSigned(signed); require(envelope.first.size <= 1024)
        active.requireCurrent(); source.requireActive(active)
        require(TrustedEnrollmentProtocol.verify(active.transcript().offer.hostSpki, envelope.first, envelope.second))
        val r = AudioReader(envelope.first, "RequestStatus")
        require(r.hash().contentEquals(nonce) && r.hash().contentEquals(source.host) && r.id() == source.namespace && r.id() == source.enrollment)
        val observedBoot = r.hash(); require(r.id() == source.worker && r.id() == source.grant && r.nonzero() == source.generation && r.hash().contentEquals(source.digest()))
        val phase = r.byte(); require(phase in 0..3)
        val binding = r.bytes(32); val generation = r.u64(); val delivery = r.bytes(32); val revision = r.u64(); val until = r.u64(); val floor = r.u64()
        val activeId = r.bytes(32); val stage = r.byte(); val expiry = r.u64(); val kind = r.byte(); r.end()
        require(stage in 0..6 && (generation == 0uL) == delivery.all { it == 0.toByte() })
        if (stage == 0) require(activeId.all { it == 0.toByte() } && expiry == 0uL && kind == 0)
        else require(AudioRequestId(activeId).sequence <= floor && expiry > 0uL && kind in 1..4)
        require(phase != 0 || (revision == 0uL && until == 0uL && binding.all { it == 0.toByte() } && generation == 0uL))
        require(phase !in 1..2 || (revision > 0uL && until > 0uL))
        require(phase != 1 || binding.all { it == 0.toByte() })
        require(generation == 0uL || binding.any { it != 0.toByte() })
        require(phase != 2 || binding.any { it != 0.toByte() })
        synchronized(gate) {
            now(); require(query!!.contentEquals(nonce) && (boot == null || boot!!.contentEquals(observedBoot)))
            query = null
            if (phase == 3) { close(); error("Broker source is terminal") }
            require(floor >= issuedThrough); boot = observedBoot; issuedThrough = floor
            pending?.let {
                // An unreserved proposal is retried exactly; an active identity
                // is not abandoned just because its issuance response was lost.
                if (it.sequence <= floor && (stage == 6 || !activeId.contentEquals(it.approval.nonce()))) {
                    it.retire(); if (!it.isSigning()) pending = null
                }
            }
        }
    }
}
