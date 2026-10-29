package com.focuswindow.client.transport

import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.launch
import kotlinx.coroutines.suspendCancellableCoroutine
import kotlinx.coroutines.withContext
import java.io.Closeable
import java.io.DataInputStream
import java.io.DataOutputStream
import java.net.InetSocketAddress
import java.security.MessageDigest
import java.security.SecureRandom
import java.security.cert.X509Certificate
import java.util.concurrent.atomic.AtomicBoolean
import java.util.concurrent.atomic.AtomicReference
import kotlin.coroutines.resumeWithException
import javax.net.ssl.SSLContext
import javax.net.ssl.SSLSocket
import javax.net.ssl.TrustManager
import javax.net.ssl.X509TrustManager

/** Dedicated quarantined setup connection. It cannot be used by normal media/resource APIs. */
internal class TrustedEnrollmentClient private constructor(
    private val socket: SSLSocket, private val signer: AndroidBrokerDeviceSigner,
    private val repository: TrustedEnrollmentRepository, private val signedOffer: ByteArray,
) : Closeable {
    private val offerBytes = TrustedSetupWire.parseSigned(signedOffer).first
    private val input = DataInputStream(socket.inputStream)
    private val output = DataOutputStream(socket.outputStream)
    private val stopped = AtomicBoolean(false)
    private var frozen: ByteArray? = null
    private var checkedHash: ByteArray? = null
    private var approvalIssued = false
    fun offer(): EnrollmentOffer = TrustedEnrollmentProtocol.parseOffer(offerBytes.copyOf())

    suspend fun propose(scopes: List<BrokerScope>): EnrollmentTranscript = withContext(Dispatchers.IO) {
        check(frozen == null && !approvalIssued && !stopped.get())
        val offer = offer(); val host = TrustedEnrollmentProtocol.hash(offer.hostSpki)
        val saved = repository.proposal(host, offer.enrollment)
        val signedProposal = if (saved != null) {
            val (previousOffer, previousProposal) = saved.envelopes()
            require(previousOffer.contentEquals(signedOffer) && TrustedEnrollmentProtocol.parseProposal(
                TrustedSetupWire.parseSigned(previousProposal).first).scopes == scopes) { "Restart trusted setup to change the frozen proposal" }
            previousProposal
        } else {
            val key = signer.prepareEnrollment(host, offer.enrollment)
            val (proposal, signature) = signer.signEnrollmentProposal(host, offer.enrollment, key.publicSpkiSha256,
                offerBytes, ByteArray(32).also { SecureRandom().nextBytes(it) }, scopes.toList())
            TrustedSetupWire.signed(proposal, signature).also {
                repository.saveProposal(host, offer.enrollment, TrustedProposedEnrollment.create(signedOffer, it))
            }
        }
        val proposal = TrustedSetupWire.parseSigned(signedProposal).first
        write(TrustedSetupWire.PROPOSAL, signedProposal)
        val response = read(); require(response.first == TrustedSetupWire.FROZEN)
        val (message, rootSignature) = TrustedSetupWire.parseSigned(response.second)
        val transcript = TrustedEnrollmentProtocol.parseTranscript(message)
        require(TrustedEnrollmentProtocol.offer(transcript.offer).contentEquals(offerBytes) &&
            TrustedEnrollmentProtocol.proposal(transcript.proposal).contentEquals(proposal)) { "The frozen candidate changed this phone proposal" }
        val hash = TrustedEnrollmentProtocol.hash(message)
        require(TrustedEnrollmentProtocol.verify(offer.hostSpki, TrustedEnrollmentProtocol.frozen(hash), rootSignature))
        frozen = response.second.copyOf(); transcript
    }
    fun verifyText(text: String) {
        val signed = frozen ?: error("No frozen transcript")
        val hash = TrustedEnrollmentProtocol.hash(TrustedSetupWire.parseSigned(signed).first)
        require(MessageDigest.isEqual(hash, TrustedEnrollmentProtocol.parseVerificationText(text))) { "PC verification code does not match" }
        checkedHash = hash
    }
    fun verifyQr(text: String) {
        val signed = frozen ?: error("No frozen transcript")
        val hash = TrustedEnrollmentProtocol.hash(TrustedSetupWire.parseSigned(signed).first)
        TrustedEnrollmentProtocol.requireVerificationQr(text, offer().ticket, hash); checkedHash = hash
    }
    suspend fun approve(isForeground: () -> Boolean): TrustedCommittedEnrollment {
        val snapshot = withContext(Dispatchers.Main.immediate) {
            require(isForeground() && !stopped.get() && !approvalIssued && checkedHash != null) { "Foreground consent and PC verification are required" }
            (frozen ?: error("No frozen transcript")).copyOf()
        }
        val prepared = withContext(Dispatchers.IO) { repository.prepare(snapshot) }
        withContext(Dispatchers.Main.immediate) {
            require(isForeground() && !stopped.get() && MessageDigest.isEqual(checkedHash, prepared.binding().hash)) { "Consent is no longer current" }
            approvalIssued = true // Final local approval admission, only after durable Prepared.
        }
        return withContext(Dispatchers.IO) {
            val (message, signature) = signer.signPreparedEnrollmentApproval(prepared)
            write(TrustedSetupWire.PHONE_APPROVAL, TrustedSetupWire.signed(message, signature))
            val response = read(); require(response.first == TrustedSetupWire.RECEIPT) { "Enrollment was not acknowledged as committed" }
            val binding = prepared.binding()
            repository.activate(binding.hostHash, binding.enrollment, response.second)
        }
    }
    suspend fun recover(prepared: TrustedPreparedEnrollment): EnrollmentOutcome = withContext(Dispatchers.IO) {
        val context = prepared.binding(); val offered = offer()
        require(MessageDigest.isEqual(TrustedEnrollmentProtocol.hash(offered.hostSpki), context.hostHash) && offered.namespace == context.namespace)
        val nonce = ByteArray(32).also { SecureRandom().nextBytes(it) }
        val (request, signature) = signer.signEnrollmentStatus(prepared, nonce)
        write(TrustedSetupWire.STATUS_REQUEST, TrustedSetupWire.signed(request, signature))
        val response = read(); require(response.first == TrustedSetupWire.STATUS)
        val (message, hostSignature) = TrustedSetupWire.parseSigned(response.second)
        require(TrustedEnrollmentProtocol.verify(offered.hostSpki, message, hostSignature))
        val status = TrustedEnrollmentProtocol.parseStatus(message)
        require(TrustedEnrollmentProtocol.phoneApproval(status.binding).contentEquals(TrustedEnrollmentProtocol.phoneApproval(context)) &&
            MessageDigest.isEqual(status.requestNonce, nonce)) { "Recovery status changed context or freshness" }
        when (status.outcome) {
            EnrollmentOutcome.COMMITTED -> {
                val receipt = read(); require(receipt.first == TrustedSetupWire.RECEIPT)
                repository.activate(context.hostHash, context.enrollment, receipt.second)
            }
            EnrollmentOutcome.REVOKED -> repository.remove(context.hostHash, context.enrollment)
            else -> Unit // Unknown/Pending never deletes keys, changes identity or activates signing.
        }
        status.outcome
    }
    private fun write(kind: Int, body: ByteArray) = synchronized(output) {
        require(!stopped.get() && body.size < TrustedEnrollmentProtocol.MAX_FRAME)
        output.writeInt(body.size + 1); output.writeByte(kind); output.write(body); output.flush()
    }
    private fun read(): Pair<Int, ByteArray> = synchronized(input) {
        require(!stopped.get()); val size = input.readInt(); require(size in 1..TrustedEnrollmentProtocol.MAX_FRAME)
        val kind = input.readUnsignedByte()
        require(kind in setOf(TrustedSetupWire.OFFER, TrustedSetupWire.FROZEN, TrustedSetupWire.RECEIPT, TrustedSetupWire.STATUS, TrustedSetupWire.CANCEL))
        kind to ByteArray(size - 1).also { input.readFully(it) }
    }
    override fun close() { if (stopped.compareAndSet(false, true)) socket.close() }

    companion object {
        suspend fun connect(host: String, port: Int, signer: AndroidBrokerDeviceSigner, repository: TrustedEnrollmentRepository): TrustedEnrollmentClient = suspendCancellableCoroutine { continuation ->
            val pending = AtomicReference<SSLSocket?>()
            continuation.invokeOnCancellation { pending.getAndSet(null)?.close() }
            CoroutineScope(continuation.context).launch(Dispatchers.IO) {
                try {
                    val client = connectBlocking(host, port, signer, repository) { socket ->
                        pending.set(socket); if (!continuation.isActive) socket.close()
                    }
                    continuation.resume(client) { _, value, _ -> value.close() }
                } catch (failure: Exception) { if (continuation.isActive) continuation.resumeWithException(failure) }
            }
        }
        private fun connectBlocking(host: String, port: Int, signer: AndroidBrokerDeviceSigner, repository: TrustedEnrollmentRepository,
            onSocket: (SSLSocket) -> Unit): TrustedEnrollmentClient {
            require(host.length in 1..253 && host.none { it.isWhitespace() || it.code < 32 } && port in 1..65535)
            val context = SSLContext.getInstance("TLS").apply { init(null, arrayOf<TrustManager>(CandidateCertificate), SecureRandom()) }
            val socket = context.socketFactory.createSocket() as SSLSocket
            try {
                onSocket(socket)
                socket.enabledProtocols = socket.supportedProtocols.filter { it == "TLSv1.3" || it == "TLSv1.2" }.toTypedArray()
                socket.enabledCipherSuites = socket.supportedCipherSuites.filter { it in cipherSuites }.toTypedArray()
                require(socket.enabledProtocols.isNotEmpty() && socket.enabledCipherSuites.isNotEmpty())
                socket.tcpNoDelay = true; socket.soTimeout = 5000; socket.connect(InetSocketAddress(host, port), 4000); socket.startHandshake()
                val input = DataInputStream(socket.inputStream); val size = input.readInt()
                require(size in 1..TrustedEnrollmentProtocol.MAX_FRAME && input.readUnsignedByte() == TrustedSetupWire.OFFER)
                val envelope = ByteArray(size - 1).also { input.readFully(it) }
                val (bytes, signature) = TrustedSetupWire.parseSigned(envelope); val offer = TrustedEnrollmentProtocol.parseOffer(bytes)
                val leaf = socket.session.peerCertificates.single() as X509Certificate
                require(MessageDigest.isEqual(TrustedEnrollmentProtocol.hash(leaf.encoded), offer.tlsLeafHash) &&
                    TrustedEnrollmentProtocol.verify(offer.hostSpki, bytes, signature)) { "Offer does not bind this TLS connection" }
                // The root remains provisional until full-digest OOB verification and foreground consent.
                socket.soTimeout = 300_000
                return TrustedEnrollmentClient(socket, signer, repository, envelope.copyOf())
            } catch (failure: Exception) { socket.close(); throw failure }
        }
        private val cipherSuites = setOf("TLS_AES_128_GCM_SHA256", "TLS_AES_256_GCM_SHA384", "TLS_CHACHA20_POLY1305_SHA256",
            "TLS_ECDHE_ECDSA_WITH_AES_128_GCM_SHA256", "TLS_ECDHE_ECDSA_WITH_AES_256_GCM_SHA384", "TLS_ECDHE_ECDSA_WITH_CHACHA20_POLY1305_SHA256")
        private object CandidateCertificate : X509TrustManager {
            override fun getAcceptedIssuers(): Array<X509Certificate> = emptyArray()
            override fun checkClientTrusted(chain: Array<X509Certificate>?, authType: String?) = throw java.security.cert.CertificateException("Client trust is unsupported")
            override fun checkServerTrusted(chain: Array<X509Certificate>?, authType: String?) {
                require(chain != null && chain.size == 1)
                val certificate = chain.single()
                require(certificate.encoded.size <= 2048 && certificate.basicConstraints < 0 && certificate.subjectX500Principal == certificate.issuerX500Principal &&
                    certificate.sigAlgOID == "1.2.840.10045.4.3.2" && certificate.keyUsage?.getOrNull(0) == true &&
                    certificate.extendedKeyUsage?.contains("1.3.6.1.5.5.7.3.1") == true &&
                    certificate.notAfter.time - certificate.notBefore.time in 1..1_800_000)
                TrustedEnrollmentProtocol.validateSpki(certificate.publicKey.encoded); certificate.verify(certificate.publicKey)
            }
        }
    }
}
