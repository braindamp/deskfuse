package com.focuswindow.client.transport

import java.nio.ByteBuffer
import java.nio.ByteOrder
import java.nio.charset.CodingErrorAction
import java.security.MessageDigest
import java.util.UUID

internal enum class AudioOperation(val wire: Int, val broker: BrokerOperation) {
    ACQUIRE_SOURCE(1, BrokerOperation.ACQUIRE), ATTACH_DELIVERY(2, BrokerOperation.ACQUIRE),
    RENEW(3, BrokerOperation.RENEW), SUPERSEDE_DELIVERY(4, BrokerOperation.SUPERSEDE),
}

/** Frozen descriptor. Its Windows endpoint/association is vouched for by the root challenge. */
internal class AudioSourceApproval(bytes: ByteArray) {
    private val canonical = bytes.copyOf()
    private val digest = TrustedEnrollmentProtocol.hash(canonical)
    private val input = AudioReader(canonical, "Source")
    private val hostBytes = input.hash(); val host get() = hostBytes.copyOf()
    val namespace = input.id(); val enrollment = input.id()
    private val peerBytes = input.hash(); val peer get() = peerBytes.copyOf()
    private val subjectBytes = input.sid(); val subject get() = subjectBytes.copyOf()
    val worker = input.id(); private val logonBytes = input.sid(true); val logon get() = logonBytes.copyOf()
    val authentication = input.u64(); val session = input.u32(); val process = input.u32(); val created = input.nonzero()
    val grant = input.id(); val generation = input.nonzero(); val deviceKey = input.id(); val backend = input.byte()
    private val profile = input.hash()
    val durationMs: UInt; val graceMs: UInt; private val wireValues: List<UInt>
    val wire get() = wireValues.toList()
    init {
        require(backend in 1..2 && session != 0u && process != 0u)
        require(input.u32() == 48000u && input.u16() == 2 && input.u16() == 16 && input.u16() == 240 && input.byte() in 0..1)
        durationMs = input.u32(); graceMs = input.u32(); require(durationMs > 0u && graceMs <= durationMs)
        wireValues = input.wire(); input.end()
    }
    fun digest() = digest.copyOf()
    fun requireActive(active: TrustedCommittedEnrollment) {
        active.requireCurrent(); val transcript = active.transcript(); val binding = TrustedEnrollmentProtocol.binding(transcript)
        require(MessageDigest.isEqual(host, binding.hostHash) && namespace == binding.namespace && enrollment == binding.enrollment)
        require(MessageDigest.isEqual(peer, transcript.offer.peer) && subject.contentEquals(transcript.offer.subject))
        require(BrokerScope.SPEAKER in transcript.proposal.scopes)
    }
    fun requireProofFacts(transcript: ByteArray) {
        val reader = AudioReader.proof(transcript)
        reader.bytes(32) // Broker nonce.
        require(reader.bytes(32).contentEquals(host) && reader.id() == namespace && reader.id() == enrollment)
        require(reader.bytes(32).contentEquals(peer)); reader.bytes(32); reader.byte(); require(reader.byte() == BrokerScope.SPEAKER.wire)
        require(reader.id() == worker && reader.sid().contentEquals(subject) && reader.sid(true).contentEquals(logon))
        require(reader.u64() == authentication && reader.u32() == session && reader.u32() == process && reader.u64() == created)
        reader.nonzero(); require(reader.u64() <= Long.MAX_VALUE.toULong()); reader.end()
    }
}

/** Actual TLS hashes must come from the native connection/key owner at N3 composition. */
internal class AudioProofApproval private constructor(val source: AudioSourceApproval, val operation: AudioOperation,
    resource: ByteArray, val generation: ULong, requestNonce: ByteArray) {
    private val resource = resource.copyOf()
    private val requestNonce = requestNonce.copyOf()
    init { AudioRequestId(this.requestNonce) }
    fun nonce() = requestNonce.copyOf()
    fun resource() = resource.copyOf()
    fun brokerApproval() = BrokerProofApproval(source.peer, resource, operation.broker, BrokerScope.SPEAKER, source.worker,
        generation.toLong(), source.host, source.namespace, source.enrollment, source.subject)
    fun requireApproved(active: TrustedCommittedEnrollment, transcript: ByteArray): AudioFrozenProof {
        val proof = AudioFrozenProof(transcript)
        source.requireActive(active); source.requireProofFacts(proof.core())
        BrokerProofTranscript.requireApproved(proof.core(), brokerApproval())
        require(proof.source == source.grant && proof.sourceGeneration == source.generation && proof.sourceDigest().contentEquals(source.digest()) &&
            proof.operation == operation && proof.requestId().contentEquals(requestNonce)) { "Audio proof changed the frozen request" }
        return proof
    }
    companion object {
        fun acquire(source: AudioSourceApproval, freshRequestNonce: ByteArray) =
            AudioProofApproval(source, AudioOperation.ACQUIRE_SOURCE, source.digest(), source.generation, freshRequestNonce)
        fun delivery(source: AudioSourceApproval, bindingBytes: ByteArray, deliveryBytes: ByteArray, operation: AudioOperation,
            nativeTlsHash: ByteArray, phoneTlsHash: ByteArray, freshRequestNonce: ByteArray, currentDeliveryDigest: ByteArray? = null): AudioProofApproval {
            require(operation != AudioOperation.ACQUIRE_SOURCE)
            val binding = bindingBytes.copyOf(); val delivery = deliveryBytes.copyOf()
            val b = AudioReader(binding, "Binding")
            require(b.hash().contentEquals(source.digest()) && b.id() == source.deviceKey); b.text(1024); b.id()
            require(b.u32() > 0u); b.nonzero(); b.nonzero(); val pcm = b.u64()
            require((source.backend == 1) == (pcm != 0uL)); b.end()
            val d = AudioReader(delivery, "Delivery")
            require(d.id() == source.grant && d.hash().contentEquals(source.digest()) && d.nonzero() == source.generation)
            require(d.hash().contentEquals(TrustedEnrollmentProtocol.hash(binding)) && d.id() == source.worker)
            val generation = d.nonzero(); require(d.wire() == source.wire)
            require(d.hash().contentEquals(nativeTlsHash.copyOf()) && d.hash().contentEquals(phoneTlsHash.copyOf())) { "Actual TLS identity changed" }
            val descriptorRequest = d.hash(); d.hash()
            require(d.text(64) == "df-audio-authority-2" && d.byte() == 1); d.end()
            val digest = TrustedEnrollmentProtocol.hash(delivery)
            if (operation == AudioOperation.RENEW) require(currentDeliveryDigest != null && digest.contentEquals(currentDeliveryDigest))
            else require(descriptorRequest.contentEquals(freshRequestNonce))
            return AudioProofApproval(source, operation, digest, generation, freshRequestNonce)
        }
    }
}

/** Root-authenticated remaining validity is anchored to the original BOOTTIME request send. */
internal class AudioGrantWindow private constructor(boot: ByteArray, val revision: ULong, val authorityUntil: ULong,
    private val sentNanos: Long, val deadlineNanos: Long) {
    private val boot = boot.copyOf()
    private var terminal = false
    private var lastNanos = sentNanos
    fun boot() = boot.copyOf()
    @Synchronized fun requireCurrent(nowElapsedRealtimeNanos: Long) {
        if (terminal || nowElapsedRealtimeNanos < lastNanos || nowElapsedRealtimeNanos >= deadlineNanos) {
            terminal = true; error("Audio grant is terminal/expired")
        }
        lastNanos = nowElapsedRealtimeNanos
    }
    companion object {
        internal fun accept(active: TrustedCommittedEnrollment, pending: AudioPendingRequest, signedReceipt: ByteArray,
            nowElapsedRealtimeNanos: Long, qualifiedRateReductionPpm: Int): AudioGrantWindow {
            val approval = pending.approval; val proofTranscript = pending.proofBytes()
            val frozen = approval.requireApproved(active, proofTranscript)
            val expectedBoot = pending.expectedBoot(); val minimumExclusiveRevision = pending.minimumRevision
            val requestSentElapsedRealtimeNanos = pending.firstSendNanos
            require(qualifiedRateReductionPpm in 0..999999 && requestSentElapsedRealtimeNanos >= 0)
            val envelope = signedReceipt.copyOf(); require(envelope.size in 12..4096)
            val length = ((envelope[0].toInt() and 255) shl 8) or (envelope[1].toInt() and 255)
            require(length in 1..2048 && envelope.size >= length + 3)
            val count = envelope[length + 2].toInt() and 255; require(count in 8..72 && envelope.size == length + 3 + count)
            val message = envelope.copyOfRange(2, 2 + length); val signature = envelope.copyOfRange(length + 3, envelope.size)
            require(TrustedEnrollmentProtocol.verify(active.transcript().offer.hostSpki, message, signature)) { "Untrusted audio grant receipt" }
            val source = approval.source; val r = AudioReader(message, "GrantReceipt")
            require(r.byte() == approval.operation.wire && r.hash().contentEquals(source.host) && r.id() == source.namespace && r.id() == source.enrollment)
            val boot = r.hash()
            if (expectedBoot != null) require(boot.contentEquals(expectedBoot.copyOf()))
            else require(approval.operation == AudioOperation.ACQUIRE_SOURCE)
            require(r.id() == source.worker && r.id() == source.grant && r.nonzero() == source.generation && r.hash().contentEquals(source.digest()))
            val delivery = r.bytes(32); val deliveryGeneration = r.u64()
            if (approval.operation == AudioOperation.ACQUIRE_SOURCE) require(delivery.all { it == 0.toByte() } && deliveryGeneration == 0uL)
            else require(delivery.contentEquals(approval.resource()) && deliveryGeneration == approval.generation)
            val revision = r.nonzero(); val until = r.nonzero()
            require(frozen.revision != ULong.MAX_VALUE && revision == frozen.revision + 1uL && revision > minimumExclusiveRevision && until == frozen.until)
            require(boot.contentEquals(frozen.boot()))
            require(r.hash().contentEquals(TrustedEnrollmentProtocol.hash(proofTranscript)) && r.hash().contentEquals(approval.nonce()))
            val remaining = r.nonzero(); r.end()
            require(remaining <= until && remaining <= source.durationMs.toULong() * 10000uL)
            val nanos = (remaining * 100uL).toLong() // uint32-ms policy keeps this within positive Long.
            val factor = 1000000L - qualifiedRateReductionPpm
            val conservative = (nanos / 1000000L) * factor + (nanos % 1000000L) * factor / 1000000L
            val deadline = Math.addExact(requestSentElapsedRealtimeNanos, conservative)
            return AudioGrantWindow(boot, revision, until, requestSentElapsedRealtimeNanos, deadline).also {
                active.requireCurrent(); it.requireCurrent(nowElapsedRealtimeNanos)
            }
        }
    }
}

internal class AudioReader private constructor(bytes: ByteArray, domain: ByteArray) {
    private val input = ByteBuffer.wrap(bytes.copyOf()).order(ByteOrder.BIG_ENDIAN)
    constructor(bytes: ByteArray, kind: String) : this(bytes, "DeskFuse.Audio.$kind.v1\u0000".toByteArray(Charsets.US_ASCII))
    init { require(bytes.size <= 2048 && bytes(domain.size).contentEquals(domain)) }
    fun bytes(count: Int): ByteArray { require(count >= 0 && input.remaining() >= count); return ByteArray(count).also { input.get(it) } }
    fun byte(): Int = bytes(1)[0].toInt() and 255
    fun u16(): Int = (byte() shl 8) or byte()
    fun u32(): UInt { require(input.remaining() >= 4); return input.int.toUInt() }
    fun u64(): ULong { require(input.remaining() >= 8); return input.long.toULong() }
    fun nonzero() = u64().also { require(it != 0uL) }
    fun id(): UUID { require(input.remaining() >= 16); return UUID(input.long, input.long).also { require(it != UUID(0, 0)) } }
    fun hash() = bytes(32).also { require(it.any { value -> value != 0.toByte() }) }
    fun sid(logon: Boolean = false): ByteArray = bytes(byte()).also { TrustedEnrollmentProtocol.validateSid(it, logon) }
    fun wire() = listOf(u32(), u32(), u32()).also { require(it.all { value -> value != 0u }) }
    fun text(maximum: Int): String {
        val length = u16(); require(length in 1..maximum)
        return Charsets.UTF_8.newDecoder().onMalformedInput(CodingErrorAction.REPORT).onUnmappableCharacter(CodingErrorAction.REPORT)
            .decode(ByteBuffer.wrap(bytes(length))).toString().also { require(it.none(Char::isISOControl)) }
    }
    fun end() = require(!input.hasRemaining())
    companion object { fun proof(bytes: ByteArray) = AudioReader(bytes, "DeskFuse.BrokerProof.v2\u0000".toByteArray(Charsets.US_ASCII)) }
}
