package com.focuswindow.client.media

import android.content.Context
import android.util.Base64
import androidx.annotation.Keep
import com.focuswindow.client.protocol.ClientMessage
import com.focuswindow.client.protocol.ServerMessage
import java.io.File
import java.security.KeyPair
import java.security.KeyPairGenerator
import java.security.Signature
import java.security.spec.ECGenParameterSpec
import javax.security.auth.x500.X500Principal
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.SupervisorJob
import org.json.JSONObject

@Keep
internal object NativeAudioBridge {
    val available = runCatching { System.loadLibrary("deskfuse_audio_jni"); true }.getOrDefault(false)
    external fun run(config: String, library: String): Int
    external fun prepare(library: String): Boolean
    external fun stop()
}

private object NativeAudioOwners {
    val coordinator = NativeOwnerCoordinator(
        CoroutineScope(SupervisorJob() + Dispatchers.Default),
        { library -> NativeAudioBridge.prepare(library) }, { NativeAudioBridge.stop() },
    )
}

/** Control bootstrap only. PCM stays in the existing native QUIC/AAudio core. */
class NativeAudioSession(private val context: Context) {
    private var grant: String? = null
    @Volatile private var authorization: String? = null
    private var key: KeyPair? = null
    private val owners = NativeAudioOwners.coordinator
    private val client = owners.register()
    @Volatile private var lease: NativeOwnerCoordinator.Lease? = null
    @Volatile private var startedGeneration = -1L
    @Volatile private var generation = 0L
    val available: Boolean get() = NativeAudioBridge.available &&
        File(context.applicationInfo.nativeLibraryDir, "libdeskfuse_quiche_lab.so").isFile
    fun acceptsVolume(grant: String): Boolean = grant == authorization && isCurrent()
    fun isCurrent(): Boolean = generation == startedGeneration && owners.isCurrent(lease)
    fun stop() {
        generation++; val previous = lease; lease = null; grant = null; authorization = null; key = null
        if (previous != null) owners.requestRetire(previous)
    }
    suspend fun retire() {
        generation++; val previous = lease; lease = null; grant = null; authorization = null; key = null
        if (previous != null) owners.retire(previous)
    }
    suspend fun challenge(message: ServerMessage.NativeAudioChallenge): ClientMessage.NativeAudioCsr {
        retire()
        lease = owners.claim(client)
        require(available && message.version == 2 && message.grant.length == 64 &&
            message.expiresUnixMs >= System.currentTimeMillis()) { "Unsupported/expired native audio grant" }
        val generator = KeyPairGenerator.getInstance("EC")
        generator.initialize(ECGenParameterSpec("secp256r1"))
        val pair = generator.generateKeyPair()
        // PKCS#10: signature proves possession; no requested extensions.
        val request = der(0x30, der(0x02, byteArrayOf(0)) +
            X500Principal("CN=DeskFuse phone").encoded + pair.public.encoded + der(0xa0, byteArrayOf()))
        val signature = Signature.getInstance("SHA256withECDSA")
        signature.initSign(pair.private); signature.update(request)
        val algorithm = byteArrayOf(0x30,0x0a,0x06,0x08,0x2a,0x86.toByte(),0x48,0xce.toByte(),0x3d,0x04,0x03,0x02)
        val csr = der(0x30, request + algorithm + der(0x03, byteArrayOf(0) + signature.sign()))
        grant = message.grant; authorization = message.grant; key = pair
        return ClientMessage.NativeAudioCsr(message.grant, pem("CERTIFICATE REQUEST", csr))
    }
    suspend fun start(message: ServerMessage.NativeAudioGrant, host: String, finished: (Int) -> Unit) {
        require(message.version == 2 && message.grant == grant && message.port in 1..65535 &&
            message.expiresUnixMs > System.currentTimeMillis())
        val pair = key ?: error("No session key")
        val selected = lease ?: error("No current native ownership")
        key = null; grant = null // one-use, bound to this authenticated control connection
        val ownerGeneration = generation
        startedGeneration = ownerGeneration
        owners.start(selected,
            File(context.applicationInfo.nativeLibraryDir, "libdeskfuse_quiche_lab.so").absolutePath,
            run = {
            val directory = File(context.noBackupFilesDir, "native-audio-${java.util.UUID.randomUUID()}")
            check(directory.mkdir())
            try {
                fun write(name: String, value: String): String = File(directory, name).apply { writeText(value) }.absolutePath
                val ca = write("ca.pem", message.caPem)
                val cert = write("phone.pem", message.clientPem)
                val encoded = pair.private.encoded
                val privatePem = pem("PRIVATE KEY", encoded); encoded.fill(0)
                val privateKey = write("phone.key", privatePem)
                val peer = File(directory, "host.der").apply { writeBytes(Base64.decode(message.serverDer, Base64.DEFAULT)) }
                val remaining = ((message.expiresUnixMs - System.currentTimeMillis()) / 1000).coerceIn(1, 1800)
                val config = JSONObject().put("version", 1).put("role", "client")
                    .put("remote", "$host:${message.port}").put("ca", ca).put("cert", cert).put("key", privateKey)
                    .put("peerDer", peer.absolutePath).put("seconds", remaining)
                    .put("library", File(context.applicationInfo.nativeLibraryDir, "libdeskfuse_android_audio.so").absolutePath)
                NativeAudioBridge.run(config.toString(),
                    File(context.applicationInfo.nativeLibraryDir, "libdeskfuse_quiche_lab.so").absolutePath)
            } finally { directory.deleteRecursively() }
        }, finished = { result -> if (generation == ownerGeneration && owners.isCurrent(selected)) finished(result) })
    }
    private fun pem(kind: String, bytes: ByteArray): String = "-----BEGIN $kind-----\n" +
        Base64.encodeToString(bytes, Base64.NO_WRAP).chunked(64).joinToString("\n") + "\n-----END $kind-----\n"
    private fun der(tag: Int, bytes: ByteArray): ByteArray {
        val size = bytes.size
        val length = when {
            size < 128 -> byteArrayOf(size.toByte())
            size < 256 -> byteArrayOf(0x81.toByte(), size.toByte())
            else -> byteArrayOf(0x82.toByte(), (size shr 8).toByte(), size.toByte())
        }
        return byteArrayOf(tag.toByte()) + length + bytes
    }
}
