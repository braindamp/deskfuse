package com.focuswindow.client.transport

import android.util.Base64
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.withContext
import org.json.JSONObject
import java.io.DataInputStream
import java.io.DataOutputStream
import java.net.InetSocketAddress
import java.net.Socket
import java.nio.ByteBuffer
import java.security.KeyFactory
import java.security.KeyPairGenerator
import java.security.MessageDigest
import java.security.SecureRandom
import java.security.spec.ECGenParameterSpec
import java.security.spec.X509EncodedKeySpec
import javax.crypto.Cipher
import javax.crypto.KeyAgreement
import javax.crypto.Mac
import javax.crypto.spec.GCMParameterSpec
import javax.crypto.spec.SecretKeySpec

/**
 * Independent enrollment channel. Never hands out a reusable session secret until
 * Windows has shown the matching verification code and the PC owner clicked Allow.
 * Existing protocol-v3 encrypted streaming stays byte-for-byte unchanged.
 */
object CameraFreeEnrollment {
    suspend fun enroll(
        pc: DiscoveredPc,
        deviceId: String,
        deviceLabel: String,
        onCode: (String) -> Unit,
    ): PairingPayload = withContext(Dispatchers.IO) {
        require(deviceId.length in 8..128 && deviceLabel.length in 1..100)
        val keyPair = KeyPairGenerator.getInstance("EC").apply {
            initialize(ECGenParameterSpec("secp256r1"), SecureRandom())
        }.generateKeyPair()
        val publicKey = keyPair.public.encoded
        val clientNonce = ByteArray(16).also { SecureRandom().nextBytes(it) }

        Socket().use { socket ->
            socket.tcpNoDelay = true
            socket.soTimeout = 90_000
            socket.connect(InetSocketAddress(pc.address, pc.enrollmentPort), 4000)
            val input = DataInputStream(socket.getInputStream())
            val output = DataOutputStream(socket.getOutputStream())

            write(output, JSONObject()
                .put("v", 1)
                .put("deviceId", deviceId)
                .put("deviceLabel", deviceLabel)
                .put("publicKey", b64(publicKey))
                .put("clientNonce", b64(clientNonce)))

            val challenge = read(input)
            require(challenge.getInt("v") == 1) { "Unsupported enrollment version" }
            val hostPublic = Base64.decode(challenge.getString("publicKey"), Base64.DEFAULT)
            val serverNonce = Base64.decode(challenge.getString("serverNonce"), Base64.DEFAULT)
            require(hostPublic.size in 65..512 && serverNonce.size == 16) {
                "Invalid PC enrollment challenge"
            }
            val publicHost = KeyFactory.getInstance("EC")
                .generatePublic(X509EncodedKeySpec(hostPublic))
            val rawShared = KeyAgreement.getInstance("ECDH").run {
                init(keyPair.private)
                doPhase(publicHost, true)
                generateSecret()
            }
            // Windows returns the full P-256 x coordinate, including leading zeros.
            val shared = if (rawShared.size < 32) ByteArray(32 - rawShared.size) + rawShared
                         else rawShared
            require(shared.size == 32) { "Unexpected ECDH secret length" }

            val transcript = MessageDigest.getInstance("SHA-256").digest(
                "DeskFuse-enroll-v1".toByteArray(Charsets.US_ASCII) +
                    clientNonce + serverNonce + publicKey + hostPublic
            )
            val prk = hmac(transcript, shared)
            val sas = expand(prk, "deskfuse:enroll:sas:v1", 4)
            val code = ((ByteBuffer.wrap(sas).int and 0x7fffffff) % 1_000_000)
                .toString().padStart(6, '0')
            val aesKey = expand(prk, "deskfuse:enroll:credential:v1", 32)
            prk.fill(0)
            shared.fill(0)

            // This number must be shown WHILE the Windows confirmation is pending.
            withContext(Dispatchers.Main) { onCode(code) }
            try {
                val decision = read(input)
                if (decision.optString("status") != "approved") {
                    throw EnrollmentDeniedException()
                }
                val nonce = Base64.decode(decision.getString("nonce"), Base64.DEFAULT)
                val sealed = Base64.decode(decision.getString("ciphertext"), Base64.DEFAULT)
                require(nonce.size == 12 && sealed.size > 16) { "Invalid approval envelope" }
                val cipher = Cipher.getInstance("AES/GCM/NoPadding")
                cipher.init(Cipher.DECRYPT_MODE, SecretKeySpec(aesKey, "AES"),
                    GCMParameterSpec(128, nonce))
                cipher.updateAAD(transcript)
                val approved = JSONObject(String(cipher.doFinal(sealed), Charsets.UTF_8))
                val secret = Base64.decode(approved.getString("secret"), Base64.DEFAULT)
                val port = approved.getInt("port")
                require(secret.size == 32 && port == pc.sessionPort) {
                    "PC enrollment parameters changed unexpectedly"
                }
                PairingPayload(
                    host = pc.address,
                    port = port,
                    hostPublicKeyB64 = Base64.encodeToString(hostPublic,
                        Base64.URL_SAFE or Base64.NO_WRAP),
                    pairingSecret = secret,
                    hostName = approved.getString("hostName").take(100),
                    viaRelay = false,
                )
            } finally {
                aesKey.fill(0)
            }
        }
    }

    private fun write(output: DataOutputStream, objectValue: JSONObject) {
        val bytes = objectValue.toString().toByteArray(Charsets.UTF_8)
        require(bytes.size in 2..4096) { "Enrollment request too large" }
        output.writeInt(bytes.size)
        output.write(bytes)
        output.flush()
    }

    private fun read(input: DataInputStream): JSONObject {
        val size = input.readInt()
        require(size in 2..4096) { "Enrollment response too large" }
        return JSONObject(ByteArray(size).also { input.readFully(it) }
            .toString(Charsets.UTF_8))
    }

    private fun b64(bytes: ByteArray): String =
        Base64.encodeToString(bytes, Base64.NO_WRAP)

    private fun hmac(key: ByteArray, bytes: ByteArray): ByteArray =
        Mac.getInstance("HmacSHA256").run {
            init(SecretKeySpec(key, "HmacSHA256"))
            doFinal(bytes)
        }

    private fun expand(prk: ByteArray, label: String, length: Int): ByteArray {
        val out = ByteArray(length)
        var previous = ByteArray(0)
        var pos = 0
        var counter = 1
        while (pos < length) {
            previous = hmac(prk, previous + label.toByteArray(Charsets.US_ASCII) +
                counter.toByte())
            val n = minOf(previous.size, length - pos)
            previous.copyInto(out, pos, 0, n)
            pos += n
            counter++
        }
        return out
    }
}
