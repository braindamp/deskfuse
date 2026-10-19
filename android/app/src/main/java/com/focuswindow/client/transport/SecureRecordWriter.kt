package com.focuswindow.client.transport

import java.io.DataOutputStream

/**
 * The protocol-v3 sender. Counter allocation, AES-GCM sealing and socket writes
 * MUST share one lock. Encrypting before taking the write lock can reorder
 * counters when the UI and decoder send concurrently; the host then rejects
 * a legitimate packet as a replay and disconnects.
 *
 * A record is [kind:1][ciphertext length:4 big endian][counter:8][GCM body].
 * Pong is the only outbound unencrypted record and has no length/payload.
 */
internal class SecureRecordWriter(
    private val output: DataOutputStream,
    private val channel: SecureChannel,
) {
    @Synchronized
    fun control(plaintext: ByteArray) {
        // The wire limit includes the 8-byte counter and 16-byte GCM tag.
        require(plaintext.isNotEmpty() && plaintext.size.toLong() + 24 <= MAX_CONTROL_BYTES) {
            "control message exceeds protocol-v3 limit"
        }
        val sealed = channel.seal(plaintext)
        output.writeByte(KIND_CONTROL)
        output.writeInt(sealed.size)
        output.write(sealed)
        output.flush()
    }

    /** A short encrypted mono PCM block after the host authorizes mic capture.
     *  Shares the same lock/counter as all other client-to-host records.
     */
    @Synchronized
    fun microphone(streamId: Int, pcm16le: ByteArray) {
        require(streamId > 0 && pcm16le.size in 2..640 && pcm16le.size % 2 == 0)
        val plain = ByteArray(4 + pcm16le.size)
        java.nio.ByteBuffer.wrap(plain).order(java.nio.ByteOrder.BIG_ENDIAN)
            .putInt(streamId).put(pcm16le)
        val sealed = channel.seal(plain)
        output.writeByte(KIND_PHONE_MIC)
        output.writeInt(sealed.size)
        output.write(sealed)
        output.flush()
    }

    @Synchronized
    fun pong() {
        output.writeByte(KIND_PONG)
        output.flush()
    }

    private companion object {
        const val KIND_CONTROL = 1
        const val KIND_PONG = 4
        const val KIND_PHONE_MIC = 6
        const val MAX_CONTROL_BYTES = 1 shl 20
    }
}
