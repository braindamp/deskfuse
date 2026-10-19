package com.focuswindow.client.transport

import android.util.Base64
import java.io.DataInputStream
import java.io.DataOutputStream
import java.security.KeyFactory
import java.security.KeyPair
import java.security.KeyPairGenerator
import java.security.MessageDigest
import java.security.PublicKey
import java.security.SecureRandom
import java.security.spec.ECGenParameterSpec
import java.security.spec.X509EncodedKeySpec
import javax.crypto.Cipher
import javax.crypto.KeyAgreement
import javax.crypto.Mac
import javax.crypto.spec.GCMParameterSpec
import javax.crypto.spec.SecretKeySpec

/**
 * FR-TRUST-02 — QR + ECDH pairing, end-to-end encrypted.
 *
 * There is no account, no password typed by hand, and no key material that
 * leaves the two devices (FR-TRUST-01). The QR carries the host's ephemeral
 * public key and a short verification nonce; we complete ECDH, derive separate
 * send/receive keys with HKDF, and authenticate the handshake with a short
 * numeric code the user can compare on both screens.
 *
 * P-256 is used rather than X25519 because XDH requires API 33; this keeps the
 * minimum supported device wide without weakening the exchange.
 */
class SecureChannel private constructor(
    private val sendKey: ByteArray,
    private val recvKey: ByteArray,
    val verificationCode: String,
) {

    private val sendCipher = Cipher.getInstance(TRANSFORM)
    private val recvCipher = Cipher.getInstance(TRANSFORM)
    private val sendKeySpec = SecretKeySpec(sendKey, "AES")
    private val recvKeySpec = SecretKeySpec(recvKey, "AES")

    private var sendCounter: Long = 0
    private var recvCounter: Long = -1

    /** Nonce = 4 zero bytes || counter. Never reused: the counter is monotonic. */
    private fun nonce(counter: Long): ByteArray =
        ByteArray(NONCE_BYTES).also { n ->
            for (i in 0 until 8) {
                n[NONCE_BYTES - 1 - i] = ((counter shr (8 * i)) and 0xFF).toByte()
            }
        }

    @Synchronized
    fun seal(plaintext: ByteArray): ByteArray {
        check(sendCounter >= 0 && sendCounter != Long.MAX_VALUE) { "outbound nonce sequence exhausted" }
        val counter = sendCounter++
        sendCipher.init(Cipher.ENCRYPT_MODE, sendKeySpec, GCMParameterSpec(TAG_BITS, nonce(counter)))
        val body = sendCipher.doFinal(plaintext)
        // Counter travels in the clear; it is not secret, only unique.
        return ByteArray(8) { i -> ((counter shr (8 * (7 - i))) and 0xFF).toByte() } + body
    }

    @Synchronized
    fun open(packet: ByteArray): ByteArray {
        require(packet.size > 8 + TAG_BITS / 8) { "ciphertext too short" }
        var counter = 0L
        for (i in 0 until 8) counter = (counter shl 8) or (packet[i].toLong() and 0xFF)
        // Replay / reorder protection: the control channel is ordered.
        require(counter > recvCounter) { "replayed or reordered frame: $counter <= $recvCounter" }
        // Never commit an unauthenticated counter: a forged high counter must not
        // lock out later genuine frames (C# advances only after GCM succeeds).
        recvCipher.init(Cipher.DECRYPT_MODE, recvKeySpec, GCMParameterSpec(TAG_BITS, nonce(counter)))
        val plain = recvCipher.doFinal(packet, 8, packet.size - 8)
        recvCounter = counter
        return plain
    }

    companion object {
        private const val TRANSFORM = "AES/GCM/NoPadding"
        private const val TAG_BITS = 128
        private const val NONCE_BYTES = 12
        private const val KEY_BYTES = 32

        /**
         * Performs the client half of the handshake over an already-connected
         * stream. Returns a channel, or throws — we never fall back to plaintext.
         */
        fun handshake(
            input: DataInputStream,
            output: DataOutputStream,
            hostPublicKeyB64: String,
            pairingSecret: ByteArray,
        ): SecureChannel {
            val ephemeral = generateKeyPair()

            val ourPublic = ephemeral.public.encoded
            output.writeInt(ourPublic.size)
            output.write(ourPublic)
            output.flush()

            val hostPublic = decodePublicKey(hostPublicKeyB64)

            // Confirm the host presented the same key over the wire as in the QR.
            val wireLen = input.readInt()
            require(wireLen in 1..4096) { "bad host key length" }
            val wireKey = ByteArray(wireLen).also { input.readFully(it) }
            require(wireKey.contentEquals(hostPublic.encoded)) {
                "host key mismatch — possible interception; refusing to continue"
            }

            val shared = KeyAgreement.getInstance("ECDH").run {
                init(ephemeral.private)
                doPhase(hostPublic, true)
                generateSecret()
            }

            return fromSharedSecret(shared, pairingSecret)
        }

        /** Pure protocol-v3 key schedule, also exercised by independent fixed ECDH vectors. */
        internal fun fromSharedSecret(shared: ByteArray, pairingSecret: ByteArray): SecureChannel {
            val prk = hkdfExtract(pairingSecret, shared)
            val c2s = hkdfExpand(prk, "focuswindow:c2s:v3", KEY_BYTES)
            val s2c = hkdfExpand(prk, "focuswindow:s2c:v3", KEY_BYTES)
            val confirm = hkdfExpand(prk, "focuswindow:confirm:v3", 4)
            val code = (bytesToInt(confirm) % 1_000_000).toString().padStart(6, '0')
            return SecureChannel(c2s, s2c, code)
        }

        private fun generateKeyPair(): KeyPair =
            KeyPairGenerator.getInstance("EC").apply {
                initialize(ECGenParameterSpec("secp256r1"), SecureRandom())
            }.generateKeyPair()

        private fun decodePublicKey(b64: String): PublicKey {
            val bytes = Base64.decode(b64, Base64.NO_WRAP or Base64.URL_SAFE)
            return KeyFactory.getInstance("EC").generatePublic(X509EncodedKeySpec(bytes))
        }

        private fun hkdfExtract(salt: ByteArray, ikm: ByteArray): ByteArray {
            val mac = Mac.getInstance("HmacSHA256")
            mac.init(SecretKeySpec(if (salt.isEmpty()) ByteArray(32) else salt, "HmacSHA256"))
            return mac.doFinal(ikm)
        }

        private fun hkdfExpand(prk: ByteArray, info: String, length: Int): ByteArray {
            val mac = Mac.getInstance("HmacSHA256")
            mac.init(SecretKeySpec(prk, "HmacSHA256"))
            val out = ByteArray(length)
            var t = ByteArray(0)
            var pos = 0
            var counter = 1
            while (pos < length) {
                mac.reset()
                mac.update(t)
                mac.update(info.toByteArray())
                mac.update(counter.toByte())
                t = mac.doFinal()
                val n = minOf(t.size, length - pos)
                System.arraycopy(t, 0, out, pos, n)
                pos += n
                counter++
            }
            return out
        }

        private fun bytesToInt(b: ByteArray): Int {
            var v = 0
            for (byte in b) v = (v shl 8) or (byte.toInt() and 0xFF)
            return v and 0x7FFFFFFF
        }

        fun fingerprint(key: ByteArray): String =
            MessageDigest.getInstance("SHA-256").digest(key)
                .take(4)
                .joinToString("") { "%02X".format(it) }
    }
}
