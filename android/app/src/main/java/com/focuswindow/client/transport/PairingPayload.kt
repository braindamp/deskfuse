package com.focuswindow.client.transport

import android.net.Uri
import android.util.Base64

/**
 * FR-TRUST-02 — pairing is a single QR scan.
 *
 * No typed codes, no email, no account. The payload is deliberately small so
 * the QR stays low-density and scans instantly from a laptop screen:
 *
 *   focuswindow://pair?h=192.168.1.24&p=47810&k=<b64 host pubkey>&s=<b64 salt>&n=Desk-PC
 *
 * FR-TRUST-03 — the host address is a LAN address by default. A relay host is
 * only ever used when the user explicitly opts in, and the client shows it.
 */
data class PairingPayload(
    val host: String,
    val port: Int,
    val hostPublicKeyB64: String,
    val pairingSecret: ByteArray,
    val hostName: String,
    val viaRelay: Boolean,
) {
    val isLoopback: Boolean get() = host == "127.0.0.1" || host == "localhost"

    /**
     * A conservative private-range check. We surface this in the UI so the user
     * can see at a glance that nothing is leaving their network.
     */
    val isPrivateAddress: Boolean
        get() = host.startsWith("10.") ||
            host.startsWith("192.168.") ||
            host.startsWith("169.254.") ||
            Regex("""^172\.(1[6-9]|2\d|3[01])\.""").containsMatchIn(host) ||
            isLoopback

    override fun equals(other: Any?): Boolean {
        if (this === other) return true
        if (other !is PairingPayload) return false
        return host == other.host &&
            port == other.port &&
            hostPublicKeyB64 == other.hostPublicKeyB64 &&
            pairingSecret.contentEquals(other.pairingSecret) &&
            hostName == other.hostName &&
            viaRelay == other.viaRelay
    }

    override fun hashCode(): Int {
        var result = host.hashCode()
        result = 31 * result + port
        result = 31 * result + hostPublicKeyB64.hashCode()
        result = 31 * result + pairingSecret.contentHashCode()
        result = 31 * result + hostName.hashCode()
        result = 31 * result + viaRelay.hashCode()
        return result
    }

    companion object {
        const val SCHEME = "focuswindow"
        const val HOST_PAIR = "pair"

        sealed interface Result {
            data class Ok(val payload: PairingPayload) : Result
            data class Invalid(val reason: String) : Result
        }

        /**
         * Parsing never throws: a mis-scanned QR is a normal, nameable state
         * (FR-OPS-03), not a crash.
         */
        fun parse(raw: String): Result {
            val uri = runCatching { Uri.parse(raw.trim()) }.getOrNull()
                ?: return Result.Invalid("That is not a pairing code.")

            if (!uri.scheme.equals(SCHEME, ignoreCase = true)) {
                return Result.Invalid("This QR is not a Focus Window pairing code.")
            }
            if (!uri.host.equals(HOST_PAIR, ignoreCase = true)) {
                return Result.Invalid("Unsupported pairing link.")
            }

            val host = uri.getQueryParameter("h")
                ?: return Result.Invalid("Pairing code is missing the host address.")
            val port = uri.getQueryParameter("p")?.toIntOrNull()
                ?: return Result.Invalid("Pairing code is missing a valid port.")
            if (port !in 1..65535) return Result.Invalid("Pairing code has an invalid port.")

            val key = uri.getQueryParameter("k")
                ?: return Result.Invalid("Pairing code is missing the host key.")
            val saltB64 = uri.getQueryParameter("s")
                ?: return Result.Invalid("Pairing code is missing its salt.")

            val salt = runCatching {
                Base64.decode(saltB64, Base64.NO_WRAP or Base64.URL_SAFE)
            }.getOrNull() ?: return Result.Invalid("Pairing code is malformed.")

            if (salt.size < 16) return Result.Invalid("Pairing code is too weak; regenerate it.")

            return Result.Ok(
                PairingPayload(
                    host = host,
                    port = port,
                    hostPublicKeyB64 = key,
                    pairingSecret = salt,
                    hostName = uri.getQueryParameter("n") ?: host,
                    viaRelay = uri.getBooleanQueryParameter("relay", false),
                ),
            )
        }
    }
}
