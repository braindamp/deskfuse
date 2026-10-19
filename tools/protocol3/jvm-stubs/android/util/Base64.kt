package android.util

/** JVM-only stand-in to compile the UNMODIFIED Android SecureChannel.kt under kotlinc. */
object Base64 {
    const val NO_WRAP = 2
    const val URL_SAFE = 8
    fun decode(value: String, flags: Int): ByteArray {
        require(flags and URL_SAFE != 0)
        return java.util.Base64.getUrlDecoder().decode(value)
    }
}
