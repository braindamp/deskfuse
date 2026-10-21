package com.focuswindow.client.media

/** Drain a packet completely; its tail must not depend on a future packet. */
internal fun writePcmPacket(size: Int, write: (offset: Int, count: Int) -> Int): Boolean {
    var offset = 0
    while (offset < size) {
        val remaining = size - offset
        val written = write(offset, remaining)
        if (written <= 0 || written > remaining) return false
        offset += written
    }
    return true
}
