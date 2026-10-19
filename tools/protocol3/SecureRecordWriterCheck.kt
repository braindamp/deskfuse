package com.focuswindow.client.transport

import java.io.ByteArrayInputStream
import java.io.ByteArrayOutputStream
import java.io.DataInputStream
import java.io.DataOutputStream
import java.nio.ByteBuffer
import java.nio.file.Files
import java.nio.file.Path
import java.util.HexFormat
import java.util.Properties
import java.util.concurrent.CountDownLatch
import kotlin.concurrent.thread
import javax.crypto.Cipher
import javax.crypto.spec.GCMParameterSpec
import javax.crypto.spec.SecretKeySpec

/** Exercises the exact Android record writer with concurrent UI-like sends. */
object SecureRecordWriterCheck {
    @JvmStatic fun main(args: Array<String>) {
        val f = Properties().apply { Files.newBufferedReader(Path.of(args.single())).use { load(it) } }
        fun bytes(key: String) = HexFormat.of().parseHex(f.getProperty(key))
        val output = ByteArrayOutputStream()
        val channel = SecureChannel.fromSharedSecret(bytes("rawSharedSecretHex"), bytes("pairingSecretHex"))
        val writer = SecureRecordWriter(DataOutputStream(output), channel)
        // Exact fixture bytes, not another implementation of the crypto framing.
        writer.control(bytes("clientPlain0Hex"))
        check(output.toByteArray().contentEquals(bytes("clientControlWire0Hex"))) { "first encrypted record differs from C# fixture" }
        writer.pong()

        val threads = 8
        val each = 64
        val ready = CountDownLatch(threads)
        val start = CountDownLatch(1)
        val workers = (0 until threads).map { id -> thread(start = true) {
            ready.countDown(); start.await()
            repeat(each) { n -> writer.control("sender=$id;seq=$n".toByteArray()) }
        } }
        ready.await(); start.countDown(); workers.forEach { it.join() }

        val input = DataInputStream(ByteArrayInputStream(output.toByteArray()))
        var received = 0L
        val key = SecretKeySpec(bytes("c2sKeyHex"), "AES")
        val seen = mutableSetOf<String>()
        while (input.available() > 0) {
            val kind = input.readUnsignedByte()
            if (kind == 4) continue
            check(kind == 1) { "unexpected wire kind $kind" }
            val size = input.readInt()
            check(size in 25..(1 shl 20))
            val packet = ByteArray(size).also(input::readFully)
            val counter = ByteBuffer.wrap(packet, 0, 8).long
            check(counter == received) { "out-of-order encrypted record: $counter instead of $received" }
            val nonce = ByteArray(12).also { ByteBuffer.wrap(it, 4, 8).putLong(counter) }
            val cipher = Cipher.getInstance("AES/GCM/NoPadding").apply {
                init(Cipher.DECRYPT_MODE, key, GCMParameterSpec(128, nonce))
            }
            val decoded = String(cipher.doFinal(packet, 8, size - 8))
            if (received > 0) check(seen.add(decoded)) { "duplicate send: $decoded" }
            received++
        }
        check(received == 1L + threads * each) { "lost messages: $received" }
        check(seen.size == threads * each)
        // A rejected oversize record cannot consume a nonce or write a partial record.
        val before = output.size()
        check(runCatching { writer.control(ByteArray(1 shl 20)) }.isFailure)
        check(output.size() == before)
        println("KOTLIN_SECURE_WRITER_PASS: $received authenticated, ordered records from $threads parallel senders; pong; size guard")
    }
}
