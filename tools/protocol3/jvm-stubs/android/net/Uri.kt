package android.net

import java.net.URLDecoder

/** Test-only JVM surrogate for the android.net.Uri methods used by PairingPayload.kt. */
class Uri private constructor(private val delegate: java.net.URI) {
    val scheme: String? get() = delegate.scheme
    val host: String? get() = delegate.host
    fun getQueryParameter(name: String): String? = delegate.rawQuery
        ?.split('&')
        ?.mapNotNull {
            val pair = it.split('=', limit = 2)
            if (pair.size != 2) null else URLDecoder.decode(pair[0], "UTF-8") to URLDecoder.decode(pair[1], "UTF-8")
        }?.firstOrNull { it.first == name }?.second

    fun getBooleanQueryParameter(name: String, defaultValue: Boolean): Boolean =
        getQueryParameter(name)?.equals("true", ignoreCase = true) ?: defaultValue

    companion object {
        fun parse(raw: String): Uri = Uri(java.net.URI(raw))
    }
}
