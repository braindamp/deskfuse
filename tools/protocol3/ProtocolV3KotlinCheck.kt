package com.focuswindow.protocol3

import com.focuswindow.client.transport.PairingPayload
import com.focuswindow.client.transport.SecureChannel
import com.focuswindow.client.transport.SecureRecordWriter
import java.io.DataInputStream
import java.io.DataOutputStream
import java.net.Socket
import java.nio.file.Files
import java.nio.file.Path
import java.security.KeyFactory
import java.security.MessageDigest
import java.security.spec.PKCS8EncodedKeySpec
import java.security.spec.X509EncodedKeySpec
import java.util.Base64
import java.util.Properties
import javax.crypto.KeyAgreement

/** JDK-only executable of the ACTUAL Android crypto class, not a reimplementation. */
object ProtocolV3KotlinCheck {
    private fun bytes(hex: String): ByteArray = java.util.HexFormat.of().parseHex(hex)
    private fun hex(bytes: ByteArray): String = java.util.HexFormat.of().formatHex(bytes)
    private fun fixture(file: String): Properties = Properties().apply {
        Files.newBufferedReader(Path.of(file)).use { load(it) }
    }
    private fun Properties.b(name: String): ByteArray = bytes(getProperty(name) ?: error("missing $name"))
    private fun assertBytes(actual: ByteArray, expected: ByteArray, what: String) {
        check(actual.contentEquals(expected)) { "$what mismatch: ${hex(actual)}" }
    }
    private inline fun reject(what: String, block: () -> Unit) {
        val rejected = runCatching(block).isFailure
        check(rejected) { "$what was incorrectly accepted" }
    }
    private fun checkVectors(f: Properties) {
        check(f.getProperty("protocolVersion") == "3")
        val hostPublic = KeyFactory.getInstance("EC").generatePublic(X509EncodedKeySpec(f.b("hostPublicSpkiHex")))
        val clientPrivate = KeyFactory.getInstance("EC").generatePrivate(PKCS8EncodedKeySpec(f.b("clientPrivatePkcs8Hex")))
        val actualShared = KeyAgreement.getInstance("ECDH").run {
            init(clientPrivate); doPhase(hostPublic, true); generateSecret()
        }
        assertBytes(actualShared, f.b("rawSharedSecretHex"), "P-256 ECDH")
        val fingerprint = hex(MessageDigest.getInstance("SHA-256").digest(f.b("hostPublicSpkiHex")).copyOfRange(0,4))
        check(fingerprint.equals(f.getProperty("hostFingerprint"), ignoreCase = true))
        val uri = f.getProperty("qrUri")
        check(uri.startsWith("focuswindow://pair?h=192.168.1.24&p=47810&k="))
        check(uri.contains("&s="))
        check(uri.contains("&n=Desk%20PC"))
        val parsed = PairingPayload.parse(uri)
        check(parsed is PairingPayload.Companion.Result.Ok) { "Android QR parser did not accept host offer: $parsed" }
        val payload = parsed.payload
        check(payload.host == "192.168.1.24" && payload.port == 47810)
        check(payload.hostName == "Desk PC" && payload.isPrivateAddress && !payload.viaRelay)
        assertBytes(payload.pairingSecret, f.b("pairingSecretHex"), "QR pairing secret")
        assertBytes(Base64.getUrlDecoder().decode(payload.hostPublicKeyB64), f.b("hostPublicSpkiHex"), "QR host identity")
        check(PairingPayload.parse("https://pair/?h=127.0.0.1") is PairingPayload.Companion.Result.Invalid)
        val channel = SecureChannel.fromSharedSecret(actualShared, f.b("pairingSecretHex"))
        check(channel.verificationCode == f.getProperty("confirmationCode"))
        assertBytes(channel.seal(f.b("clientPlain0Hex")), f.b("clientPacket0Hex"), "AES-GCM c2s counter=0")
        assertBytes(channel.seal(f.b("clientPlain1Hex")), f.b("clientPacket1Hex"), "AES-GCM c2s counter=1")
        assertBytes(channel.open(f.b("hostPacket0Hex")), f.b("hostPlain0Hex"), "AES-GCM s2c")
        reject("replayed packet") { channel.open(f.b("hostPacket0Hex")) }
        reject("forged high counter") { channel.open(f.b("forgedHighCounterHostPacket1Hex")) }
        reject("bad authentication tag") { channel.open(f.b("corruptedHostPacket1Hex")) }
        assertBytes(channel.open(f.b("hostPacket1Hex")), f.b("hostPlain1Hex"), "valid after tamper")
        reject("duplicate second packet") { channel.open(f.b("hostPacket1Hex")) }
        val framed = f.b("clientControlWire0Hex")
        check(framed[0].toInt() == 1)
        val wireLength = java.nio.ByteBuffer.wrap(framed, 1, 4).int
        check(wireLength == framed.size - 5 && wireLength == f.b("clientPacket0Hex").size)
        assertBytes(framed.copyOfRange(5,framed.size), f.b("clientPacket0Hex"), "control framing")
        println("KOTLIN_V3_VECTORS_PASS: ECDH, QR fingerprint, HKDF, confirmation, framing, AES-GCM, replay, forged counter")
    }
    private fun bridge(port: Int, hostSpkiHex: String, secretHex: String) {
        Socket("127.0.0.1", port).use { socket ->
            socket.soTimeout = 10000
            val input = DataInputStream(socket.getInputStream())
            val output = DataOutputStream(socket.getOutputStream())
            val hostPublic = Base64.getUrlEncoder().encodeToString(bytes(hostSpkiHex))
            val client = SecureChannel.handshake(input, output, hostPublic, bytes(secretHex))
            val sender = SecureRecordWriter(output, client)
            fun send(plain: String) = sender.control(plain.toByteArray(Charsets.UTF_8))
            send("hello from real Kotlin SecureChannel")
            check(input.readUnsignedByte() == 1)
            val len = input.readInt()
            require(len in 25..4096)
            val sealed = ByteArray(len).also { input.readFully(it) }
            check(String(client.open(sealed), Charsets.UTF_8) == "welcome from CSharp SecureChannel")
            send("confirmed by Kotlin")
        }
        println("KOTLIN_CSHARP_BRIDGE_PASS")
    }
    @JvmStatic fun main(args: Array<String>) {
        when (args.firstOrNull()) {
            "vectors" -> checkVectors(fixture(args[1]))
            "bridge" -> bridge(args[1].toInt(), args[2], args[3])
            else -> error("usage: vectors <fixture.properties> | bridge <port> <hostSpkiHex> <secretHex>")
        }
    }
}
