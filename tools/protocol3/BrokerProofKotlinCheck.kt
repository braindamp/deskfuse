package com.focuswindow.client.transport

import java.security.KeyFactory
import java.security.KeyPairGenerator
import java.security.Signature
import java.security.spec.ECGenParameterSpec
import java.security.spec.X509EncodedKeySpec
import java.util.HexFormat
import java.util.UUID

/** JVM harness of the exact shipping proof parser/signer; Android Keystore itself needs device qualification. */
object BrokerProofKotlinCheck {
    private val hex = HexFormat.of()
    private val host = ByteArray(32) { 0x33 }
    private val enrollment = UUID.fromString("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee")
    private val registration = UUID.fromString("00112233-4455-6677-8899-aabbccddeeff")
    private const val vector = "4465736b467573652e42726f6b657250726f6f662e763100000102030405060708090a0b0c0d0e0f101112131415161718191a1b1c1d1e1f11111111111111111111111111111111111111111111111111111111111111112222222222222222222222222222222222222222222222222222222222222222010100112233445566778899aabbccddeeff0c010100000000000515000000140103000000000005050000001600000021000000000000000000002a000000010000006500000000000000c800000000000000010000000000002af8"

    private class Keys : BrokerSigningKeyStore {
        val values = mutableMapOf<String, BrokerSigningKey>()
        var creates = 0
        override fun find(alias: String): BrokerSigningKey? = values[alias]
        override fun create(alias: String): BrokerSigningKey {
            check(!values.containsKey(alias))
            creates++
            val key = KeyPairGenerator.getInstance("EC").apply { initialize(ECGenParameterSpec("secp256r1")) }.generateKeyPair()
            return BrokerSigningKey(key.private, key.public.encoded, BrokerKeyProtection.SOFTWARE).also { values[alias] = it }
        }
    }

    private fun approval() = BrokerProofApproval(ByteArray(32) { 0x11 }, ByteArray(32) { 0x22 },
        BrokerOperation.ACQUIRE, BrokerScope.SPEAKER, registration, 1)
    private inline fun reject(block: () -> Unit) = check(runCatching(block).isFailure) { "Unapproved proof was accepted" }

    @JvmStatic
    fun main(args: Array<String>) {
        val keys = Keys()
        val signer = BrokerDeviceSigner(keys)
        val identity = signer.prepareEnrollment(host, enrollment)
        if (args.firstOrNull() == "bridge") {
            println(hex.formatHex(identity.publicSpki))
            val challenge = hex.parseHex(readln())
            println(hex.formatHex(signer.signExisting(host, enrollment, identity.publicSpkiSha256, challenge, approval())))
            return
        }
        val bytes = hex.parseHex(vector)
        BrokerProofTranscript.requireApproved(bytes, approval())
        val signature = signer.signExisting(host, enrollment, identity.publicSpkiSha256, bytes, approval())
        val public = KeyFactory.getInstance("EC").generatePublic(X509EncodedKeySpec(identity.publicSpki))
        check(Signature.getInstance("SHA256withECDSA").run { initVerify(public); update(bytes); verify(signature) })
        check(keys.creates == 1)
        signer.prepareEnrollment(host, enrollment)
        check(keys.creates == 1)
        reject { signer.signExisting(ByteArray(32), enrollment, identity.publicSpkiSha256, bytes, approval()) }
        reject { signer.signExisting(host, enrollment, ByteArray(32), bytes, approval()) }
        check(keys.creates == 1)
        reject { BrokerProofTranscript.requireApproved(bytes + 0, approval()) }
        reject { BrokerProofTranscript.requireApproved(bytes.copyOf(bytes.size - 1), approval()) }
        for (index in intArrayOf(0, 56, 88, 119, 120, 121, 122, 138, 139, bytes.size - 9)) {
            val changed = bytes.copyOf().also { it[index] = (it[index].toInt() xor 0xff).toByte() }
            reject { signer.signExisting(host, enrollment, identity.publicSpkiSha256, changed, approval()) }
        }
        keys.values.clear()
        reject { signer.signExisting(host, enrollment, identity.publicSpkiSha256, bytes, approval()) }
        check(keys.creates == 1) // Runtime loss never silently creates a replacement identity.
        println("BROKER_PROOF_KOTLIN_PASS: canonical context, actual JCA signing, independent key, no runtime rotation")
    }
}
