package com.focuswindow.client.transport

import java.io.ByteArrayOutputStream
import java.nio.ByteBuffer
import java.nio.ByteOrder
import java.security.KeyFactory
import java.security.MessageDigest
import java.security.Signature
import java.security.interfaces.ECPublicKey
import java.security.spec.X509EncodedKeySpec
import java.util.Base64
import java.util.UUID

internal enum class EnrollmentAction(val wire: Int) { NEW(1), REPLACE(2) }
internal enum class EnrollmentOutcome(val wire: Int) { COMMITTED(1), PENDING(2), ABORTED(3), UNKNOWN(4), REVOKED(5) }
internal data class EnrollmentOffer(
    val hostSpki: ByteArray, val namespace: UUID, val ticket: UUID, val enrollment: UUID,
    val peer: ByteArray, val action: EnrollmentAction, val oldEnrollment: UUID, val bootNonce: ByteArray,
    val hostNonce: ByteArray, val worker: UUID, val tlsLeafHash: ByteArray, val subject: ByteArray,
    val logonSid: ByteArray, val authenticationId: Long, val session: Int, val approver: ByteArray,
    val ui: UUID, val uiPid: Int, val uiCreated: Long, val scopes: List<BrokerScope>, val expiry: Long,
)
internal data class EnrollmentProposal(val offerHash: ByteArray, val phoneSpki: ByteArray, val phoneNonce: ByteArray, val scopes: List<BrokerScope>)
internal data class EnrollmentTranscript(val offer: EnrollmentOffer, val proposal: EnrollmentProposal, val freezeNonce: ByteArray)
internal data class EnrollmentBinding(val hostHash: ByteArray, val namespace: UUID, val ticket: UUID, val enrollment: UUID, val hash: ByteArray)
internal data class EnrollmentReceipt(val binding: EnrollmentBinding, val peer: ByteArray, val subject: ByteArray,
    val scopes: List<BrokerScope>, val phoneKeyHash: ByteArray, val recordHash: ByteArray)
internal data class EnrollmentStatus(val binding: EnrollmentBinding, val requestNonce: ByteArray, val outcome: EnrollmentOutcome)
internal data class EnrollmentStatusRequest(val binding: EnrollmentBinding, val requestNonce: ByteArray, val phoneSpki: ByteArray)

/** Setup-only, canonical binary protocol. Parsing or possession of these messages never grants authority. */
internal object TrustedEnrollmentProtocol {
    const val MAX_FRAME = 8192
    const val MAX_TRANSCRIPT = 2048
    private const val ALPHABET = "0123456789ABCDEFGHJKMNPQRSTVWXYZ"
    private val zero = UUID(0, 0)
    fun hash(bytes: ByteArray): ByteArray = MessageDigest.getInstance("SHA-256").digest(bytes)
    private fun domain(kind: String) = "DeskFuse.Enrollment.$kind.v1\u0000".toByteArray(Charsets.US_ASCII)

    fun offer(v: EnrollmentOffer): ByteArray = Writer("Offer").run {
        spki(v.hostSpki); id(v.namespace); id(v.ticket); id(v.enrollment); bytes(v.peer, 32)
        require((v.action == EnrollmentAction.NEW) == (v.oldEnrollment == zero))
        byte(v.action.wire); id(v.oldEnrollment, true); bytes(v.bootNonce, 32); bytes(v.hostNonce, 32)
        id(v.worker); bytes(v.tlsLeafHash, 32); sid(v.subject); sid(v.logonSid, true)
        u64(v.authenticationId); u32(v.session); sid(v.approver); id(v.ui); u32(v.uiPid); u64(v.uiCreated)
        require(v.session != 0 && v.uiPid != 0 && v.uiCreated != 0L)
        scopes(v.scopes); u64(v.expiry); finish()
    }
    fun parseOffer(bytes: ByteArray): EnrollmentOffer = Reader(bytes, "Offer").run {
        val host = bytes(91); val ns = id(); val ticket = id(); val enrollment = id(); val peer = bytes(32); val action = byte()
        val v = EnrollmentOffer(host, ns, ticket, enrollment, peer,
            EnrollmentAction.entries.single { it.wire == action }, id(true), bytes(32), bytes(32), id(), bytes(32),
            sid(), sid(true), u64(), u32(), sid(), id(), u32(), u64(), scopes(), u64())
        end(); require(bytes.contentEquals(offer(v))); v
    }
    fun proposal(v: EnrollmentProposal): ByteArray = Writer("Proposal").run {
        bytes(v.offerHash, 32); spki(v.phoneSpki); bytes(v.phoneNonce, 32); scopes(v.scopes); finish()
    }
    fun parseProposal(bytes: ByteArray): EnrollmentProposal = Reader(bytes, "Proposal").run {
        val v = EnrollmentProposal(bytes(32), bytes(91), bytes(32), scopes())
        end(); require(bytes.contentEquals(proposal(v))); v
    }
    fun transcript(v: EnrollmentTranscript): ByteArray {
        val offer = offer(v.offer); val proposal = proposal(v.proposal)
        require(MessageDigest.isEqual(hash(offer), v.proposal.offerHash) && v.offer.scopes.containsAll(v.proposal.scopes))
        return Writer("Transcript").run { blob(offer); blob(proposal); bytes(v.freezeNonce, 32); finish() }
            .also { require(it.size <= MAX_TRANSCRIPT) }
    }
    fun parseTranscript(bytes: ByteArray): EnrollmentTranscript {
        require(bytes.size <= MAX_TRANSCRIPT)
        return Reader(bytes, "Transcript").run {
            val v = EnrollmentTranscript(parseOffer(blob()), parseProposal(blob()), bytes(32))
            end(); require(bytes.contentEquals(transcript(v))); v
        }
    }
    fun binding(v: EnrollmentTranscript) = EnrollmentBinding(hash(v.offer.hostSpki), v.offer.namespace,
        v.offer.ticket, v.offer.enrollment, hash(transcript(v)))
    fun frozen(hash: ByteArray): ByteArray = Writer("Frozen").run { bytes(hash, 32); finish() }
    fun phoneApproval(v: EnrollmentBinding): ByteArray = Writer("PhoneApproval").run { binding(v); finish() }
    fun parsePhoneApproval(bytes: ByteArray): EnrollmentBinding = Reader(bytes, "PhoneApproval").run { val v = binding(); end(); v }
    fun receipt(v: EnrollmentReceipt): ByteArray = Writer("Committed").run {
        identity(v.binding); bytes(v.peer, 32); sid(v.subject); scopes(v.scopes); bytes(v.phoneKeyHash, 32)
        bytes(v.binding.hash, 32); bytes(v.recordHash, 32); finish()
    }
    fun parseReceipt(bytes: ByteArray): EnrollmentReceipt = Reader(bytes, "Committed").run {
        val host = bytes(32); val ns = id(); val ticket = id(); val enrollment = id()
        val peer = bytes(32); val subject = sid(); val scopes = scopes(); val phone = bytes(32)
        val v = EnrollmentReceipt(EnrollmentBinding(host, ns, ticket, enrollment, bytes(32)), peer, subject, scopes, phone, bytes(32))
        end(); require(bytes.contentEquals(receipt(v))); v
    }
    fun status(v: EnrollmentStatus): ByteArray = Writer("Status").run {
        binding(v.binding); bytes(v.requestNonce, 32); byte(v.outcome.wire); finish()
    }
    fun parseStatus(bytes: ByteArray): EnrollmentStatus = Reader(bytes, "Status").run {
        val binding = binding(); val nonce = bytes(32); val outcome = byte()
        val v = EnrollmentStatus(binding, nonce, EnrollmentOutcome.entries.single { it.wire == outcome })
        end(); require(bytes.contentEquals(status(v))); v
    }
    fun statusRequest(v: EnrollmentStatusRequest): ByteArray = Writer("StatusRequest").run {
        binding(v.binding); bytes(v.requestNonce, 32); spki(v.phoneSpki); finish()
    }
    fun parseStatusRequest(bytes: ByteArray): EnrollmentStatusRequest = Reader(bytes, "StatusRequest").run {
        val v = EnrollmentStatusRequest(binding(), bytes(32), bytes(91)); end(); require(bytes.contentEquals(statusRequest(v))); v
    }
    fun verificationQr(ticket: UUID, hash: ByteArray): String {
        require(ticket != zero && hash.size == 32)
        return "DESKFUSE-ENROLL-VERIFY-V1:${ticket.toString().replace("-", "")}:${Base64.getUrlEncoder().withoutPadding().encodeToString(hash)}"
    }
    fun requireVerificationQr(text: String, ticket: UUID, hash: ByteArray) {
        require(text.length <= 128 && text == verificationQr(ticket, hash)) { "PC verification QR does not match this attempt" }
    }
    fun verificationText(hash: ByteArray): String {
        require(hash.size == 32)
        return buildString(52) {
            repeat(52) { symbol ->
                var value = 0
                repeat(5) { bit -> val p = symbol * 5 + bit
                    value = (value shl 1) or if (p < 256) ((hash[p / 8].toInt() ushr (7 - p % 8)) and 1) else 0 }
                append(ALPHABET[value])
            }
        }
    }
    fun parseVerificationText(text: String): ByteArray {
        require(text.length <= 128); val output = ByteArray(32); var symbol = 0
        for (c in text) {
            if (c == ' ' || c == '-') continue
            val value = ALPHABET.indexOf(if (c in 'a'..'z') (c.code - 32).toChar() else c)
            require(value >= 0 && symbol < 52 && (symbol != 51 || value and 15 == 0))
            repeat(5) { bit -> val p = symbol * 5 + bit
                if (p < 256) output[p / 8] = (output[p / 8].toInt() or (((value ushr (4 - bit)) and 1) shl (7 - p % 8))).toByte() }
            symbol++
        }
        require(symbol == 52); return output
    }
    fun validateSpki(spki: ByteArray) {
        val prefix = byteArrayOf(0x30, 0x59, 0x30, 0x13, 0x06, 0x07, 0x2a, 0x86.toByte(), 0x48, 0xce.toByte(), 0x3d,
            0x02, 0x01, 0x06, 0x08, 0x2a, 0x86.toByte(), 0x48, 0xce.toByte(), 0x3d, 0x03, 0x01, 0x07, 0x03, 0x42, 0x00, 0x04)
        require(spki.size == 91 && spki.copyOfRange(0, prefix.size).contentEquals(prefix))
        val key = KeyFactory.getInstance("EC").generatePublic(X509EncodedKeySpec(spki)) as ECPublicKey
        require(key.params.curve.field.fieldSize == 256 && key.encoded.contentEquals(spki))
    }
    fun validateSid(sid: ByteArray, logon: Boolean = false) {
        require(sid.size in 8..68 && sid[0].toInt() == 1 && (sid[1].toInt() and 255) <= 15 && sid.size == 8 + 4 * (sid[1].toInt() and 255))
        if (logon) require(sid.size == 20 && sid.copyOfRange(2, 8).contentEquals(byteArrayOf(0, 0, 0, 0, 0, 5)) &&
            ByteBuffer.wrap(sid, 8, 4).order(ByteOrder.LITTLE_ENDIAN).int == 5)
    }
    fun verify(spki: ByteArray, bytes: ByteArray, signature: ByteArray): Boolean {
        if (bytes.size > MAX_FRAME || signature.size !in 8..72) return false
        return runCatching {
            validateSpki(spki)
            Signature.getInstance("SHA256withECDSA").run {
                initVerify(KeyFactory.getInstance("EC").generatePublic(X509EncodedKeySpec(spki))); update(bytes); verify(signature)
            }
        }.getOrDefault(false)
    }
    private class Writer(kind: String) {
        private val output = ByteArrayOutputStream(512).apply { write(domain(kind)) }
        fun byte(value: Int) { require(value in 0..255); output.write(value) }
        fun bytes(value: ByteArray, count: Int) { require(value.size == count); output.write(value) }
        fun u32(value: Int) { output.write(ByteBuffer.allocate(4).putInt(value).array()) }
        fun u64(value: Long) { output.write(ByteBuffer.allocate(8).putLong(value).array()) }
        fun id(value: UUID, allowEmpty: Boolean = false) { require(allowEmpty || value != zero); u64(value.mostSignificantBits); u64(value.leastSignificantBits) }
        fun spki(value: ByteArray) { validateSpki(value); bytes(value, 91) }
        fun sid(value: ByteArray, logon: Boolean = false) { validateSid(value, logon); byte(value.size); output.write(value) }
        fun scopes(scopes: List<BrokerScope>) {
            require(scopes.size in 1..8); byte(scopes.size); var previous = 0
            for (scope in scopes) { require(scope.wire > previous); byte(scope.wire); previous = scope.wire }
        }
        fun blob(bytes: ByteArray) { require(bytes.size <= MAX_TRANSCRIPT); output.write(ByteBuffer.allocate(2).putShort(bytes.size.toShort()).array()); output.write(bytes) }
        fun identity(v: EnrollmentBinding) { bytes(v.hostHash, 32); id(v.namespace); id(v.ticket); id(v.enrollment) }
        fun binding(v: EnrollmentBinding) { identity(v); bytes(v.hash, 32) }
        fun finish(): ByteArray = output.toByteArray().also { require(it.size <= MAX_FRAME) }
    }
    private class Reader(bytes: ByteArray, kind: String) {
        private val input = ByteBuffer.wrap(bytes).order(ByteOrder.BIG_ENDIAN)
        init { require(bytes.size <= MAX_FRAME); require(bytes(domain(kind).size).contentEquals(domain(kind))) }
        fun byte(): Int { require(input.hasRemaining()); return input.get().toInt() and 255 }
        fun bytes(count: Int): ByteArray { require(count >= 0 && input.remaining() >= count); return ByteArray(count).also { input.get(it) } }
        fun u32(): Int { require(input.remaining() >= 4); return input.int }
        fun u64(): Long { require(input.remaining() >= 8); return input.long }
        fun id(allowEmpty: Boolean = false): UUID = UUID(u64(), u64()).also { require(allowEmpty || it != zero) }
        fun sid(logon: Boolean = false): ByteArray = bytes(byte()).also { validateSid(it, logon) }
        fun scopes(): List<BrokerScope> {
            val count = byte(); require(count in 1..8); var previous = 0
            return List(count) { val value = byte(); require(value > previous); previous = value; BrokerScope.entries.single { it.wire == value } }
        }
        fun blob(): ByteArray { require(input.remaining() >= 2); val count = input.short.toInt() and 65535; require(count <= MAX_TRANSCRIPT); return bytes(count) }
        fun binding() = EnrollmentBinding(bytes(32), id(), id(), id(), bytes(32))
        fun end() { require(!input.hasRemaining()) }
    }
}
