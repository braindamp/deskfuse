package com.focuswindow.client.transport

import java.io.ByteArrayOutputStream
import java.io.DataOutputStream
import java.security.SecureRandom
import java.util.UUID
import java.util.concurrent.atomic.AtomicBoolean

internal enum class AudioSourceStopState(val wire: Int) { UNKNOWN(0), DENIED(1), RUNDOWN_COMPLETE(2), QUARANTINED(3) }

/** Facts supplied by the native TLS owner, never a peer's authenticated boolean. */
internal class AudioStopObservation(val connection: UUID, native: ByteArray, phone: ByteArray, nonce: ByteArray) {
    private val encoded = StopWriter().apply { id(connection); hash(native); hash(phone); hash(nonce) }.bytes()
    internal fun bytes() = encoded.copyOf()
}
private class StopWriter(kind: String? = null) {
    private val output = ByteArrayOutputStream(512)
    private val data = DataOutputStream(output)
    init { if (kind != null) data.write("DeskFuse.Audio.$kind.v1\u0000".toByteArray(Charsets.US_ASCII)) }
    fun bytes(value: ByteArray) { data.write(value) }
    fun byte(value: Int) { data.writeByte(value) }
    fun hash(value: ByteArray) { require(value.size == 32 && value.any { it != 0.toByte() }); data.write(value) }
    fun id(value: UUID) { require(value != UUID(0, 0)); data.writeLong(value.mostSignificantBits); data.writeLong(value.leastSignificantBits) }
    fun u64(value: ULong) { data.writeLong(value.toLong()) }
    fun bytes(): ByteArray = output.toByteArray().also { require(it.size <= 1024) }
}
internal object AudioSourceStopCodec {
    fun target(source: AudioSourceApproval, boot: ByteArray): ByteArray = StopWriter().apply {
        hash(source.host); id(source.namespace); id(source.enrollment); byte(BrokerScope.SPEAKER.wire); hash(boot)
        id(source.worker); id(source.grant); require(source.generation != 0uL); u64(source.generation); hash(source.digest()); id(source.deviceKey)
    }.bytes()
    private fun readTarget(r: AudioReader): ByteArray = StopWriter().apply {
        hash(r.hash()); id(r.id()); id(r.id()); require(r.byte() == BrokerScope.SPEAKER.wire); byte(BrokerScope.SPEAKER.wire)
        hash(r.hash()); id(r.id()); id(r.id()); u64(r.nonzero()); hash(r.hash()); id(r.id())
    }.bytes()
    fun canonicalStop(bytes: ByteArray): ByteArray {
        require(bytes.size <= 1024); val r = AudioReader(bytes, "SourceStop")
        val w = StopWriter("SourceStop"); w.bytes(readTarget(r)); w.hash(r.hash()); w.id(r.id())
        repeat(3) { w.hash(r.hash()) }; r.end(); return w.bytes()
    }
    fun status(active: TrustedCommittedEnrollment, target: ByteArray, query: ByteArray, signed: ByteArray): AudioSourceStopState {
        active.requireCurrent(); val envelope = TrustedSetupWire.parseSigned(signed.copyOf()); require(envelope.first.size <= 1024)
        require(TrustedEnrollmentProtocol.verify(active.transcript().offer.hostSpki, envelope.first, envelope.second))
        val r = AudioReader(envelope.first, "SourceStopStatus")
        require(r.hash().contentEquals(query) && readTarget(r).contentEquals(target))
        val revision = r.u64(); val kind = r.byte(); r.end()
        val state = AudioSourceStopState.entries.single { it.wire == kind }
        require((revision == 0uL) == (state == AudioSourceStopState.UNKNOWN)); active.requireCurrent(); return state
    }
}

/** A local, irreversible negative intent; construction does not sign peer requests. */
internal class AudioSourceStopIntent private constructor(internal val source: AudioSourceApproval, boot: ByteArray) {
    private val gate = Any()
    private val target = AudioSourceStopCodec.target(source, boot.copyOf())
    private val intent = fresh()
    private var attempt: AudioStopAttempt? = null
    private var query: ByteArray? = null
    private val verifying = AtomicBoolean(false)
    internal fun transcript(observation: AudioStopObservation): ByteArray =
        StopWriter("SourceStop").apply { bytes(target); hash(intent); bytes(observation.bytes()) }.bytes()
    fun prepare(observation: AudioStopObservation): AudioStopAttempt = synchronized(gate) {
        val bytes = transcript(observation)
        attempt?.let {
            if (it.transcript().contentEquals(bytes)) return@synchronized it
            check(!it.isSigning()) { "Actual Stop signing has not completed" }; it.retire()
        }
        AudioStopAttempt.create(this, observation).also { attempt = it }
    }
    fun beginStatusQuery(): ByteArray = synchronized(gate) { (query ?: fresh().also { query = it }).copyOf() }
    fun statusRequest(): ByteArray = beginStatusQuery() + target.copyOf()
    fun acceptStatus(active: TrustedCommittedEnrollment, signed: ByteArray): AudioSourceStopState {
        check(verifying.compareAndSet(false, true)) { "Stop status verification is in progress" }
        try {
            val nonce = synchronized(gate) { query?.copyOf() ?: error("No pending Stop status query") }
            source.requireActive(active); val state = AudioSourceStopCodec.status(active, target, nonce, signed)
            synchronized(gate) { require(query!!.contentEquals(nonce)); query = null }; return state
        } finally { verifying.set(false) }
    }
    companion object {
        private fun fresh(): ByteArray { val b = ByteArray(32); do { SecureRandom().nextBytes(b) } while (b.all { it == 0.toByte() }); return b }
        fun forSource(active: TrustedCommittedEnrollment, source: AudioSourceApproval, boot: ByteArray): AudioSourceStopIntent {
            source.requireActive(active); return AudioSourceStopIntent(source, boot)
        }
        fun forSelectedCleanup(active: TrustedCommittedEnrollment, locator: AudioSourceApproval, boot: ByteArray,
            selectedDeviceKey: UUID): AudioSourceStopIntent {
            require(locator.deviceKey == selectedDeviceKey); return forSource(active, locator, boot)
        }
    }
}

/** One real key operation per exact connection attempt; failure is latched. */
internal class AudioStopAttempt private constructor(internal val intent: AudioSourceStopIntent, bytes: ByteArray) {
    private val gate = Any()
    private val bytes = bytes.copyOf()
    private var attempted = false
    private var signature: ByteArray? = null
    private val signing = AtomicBoolean(false)
    @Volatile private var retired = false
    fun transcript() = bytes.copyOf()
    fun isSigning() = signing.get()
    fun retire() { retired = true }
    internal fun signWith(validate: () -> Unit, action: (ByteArray) -> ByteArray): ByteArray = synchronized(gate) {
        require(!retired); check(signing.compareAndSet(false, true))
        try {
            validate(); signature?.let { require(!retired); return@synchronized it.copyOf() }
            check(!attempted) { "Stop signing is unconfirmed" }; attempted = true
            val result = action(bytes.copyOf()); require(result.size in 8..72)
            signature = result.copyOf(); validate(); require(!retired); result.copyOf()
        } finally { signing.set(false) }
    }
    fun envelope(): ByteArray = synchronized(gate) {
        require(!retired); val exact = signature ?: error("No completed Stop signature")
        byteArrayOf((bytes.size ushr 8).toByte(), bytes.size.toByte()) + bytes + byteArrayOf(exact.size.toByte()) + exact
    }
    companion object {
        internal fun create(intent: AudioSourceStopIntent, observation: AudioStopObservation): AudioStopAttempt =
            AudioStopAttempt(intent, intent.transcript(observation))
    }
}
