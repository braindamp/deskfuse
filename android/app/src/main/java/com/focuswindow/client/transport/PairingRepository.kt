package com.focuswindow.client.transport

import android.content.Context
import android.content.SharedPreferences
import android.util.Base64
import androidx.security.crypto.EncryptedSharedPreferences
import androidx.security.crypto.MasterKey

/**
 * Remembers paired hosts so the user scans once, not every session.
 *
 * FR-TRUST-01 — this is the ONLY persistence of connection identity, it is
 * local, and it is encrypted at rest with a hardware-backed key. There is no
 * account, no cloud sync and no device list on a server we run.
 */
class PairingRepository(context: Context) {

    // If Android Keystore fails, never write the QR pairing secret to ordinary
    // SharedPreferences. The current process can still connect, but the pairing
    // is deliberately forgotten after restart until secure storage is restored.
    private val prefs: SharedPreferences? = runCatching {
        val key = MasterKey.Builder(context)
            .setKeyScheme(MasterKey.KeyScheme.AES256_GCM)
            .build()
        EncryptedSharedPreferences.create(
            context,
            FILE,
            key,
            EncryptedSharedPreferences.PrefKeyEncryptionScheme.AES256_SIV,
            EncryptedSharedPreferences.PrefValueEncryptionScheme.AES256_GCM,
        ) as SharedPreferences
    }.getOrNull()

    init {
        // Earlier development builds stored a fallback QR secret as plaintext.
        // Erase that deprecated preference file; never migrate its credentials.
        runCatching {
            context.getSharedPreferences("focuswindow.pairing", Context.MODE_PRIVATE)
                .edit().clear().apply()
        }
    }

    @Volatile private var ephemeralPairing: PairingPayload? = null

    /** True when a paired host can be remembered across an app restart securely. */
    val persistentStorageAvailable: Boolean get() = prefs != null

    fun remember(payload: PairingPayload) {
        ephemeralPairing = payload
        // Never fall back to unencrypted storage. Encryption failures should
        // degrade to session-only use, not leak a reusable pairing credential.
        runCatching {
            prefs?.edit()
                ?.putString(KEY_HOST, payload.host)
                ?.putInt(KEY_PORT, payload.port)
                ?.putString(KEY_PUBKEY, payload.hostPublicKeyB64)
                ?.putString(KEY_SALT, Base64.encodeToString(payload.pairingSecret, Base64.NO_WRAP))
                ?.putString(KEY_NAME, payload.hostName)
                ?.putBoolean(KEY_RELAY, payload.viaRelay)
                ?.apply()
        }
    }

    fun last(): PairingPayload? {
        ephemeralPairing?.let { return it }
        val stored = prefs ?: return null
        return runCatching {
            val host = stored.getString(KEY_HOST, null) ?: return null
            val pubkey = stored.getString(KEY_PUBKEY, null) ?: return null
            val saltB64 = stored.getString(KEY_SALT, null) ?: return null
            val salt = Base64.decode(saltB64, Base64.NO_WRAP)
            PairingPayload(
                host = host,
                port = stored.getInt(KEY_PORT, 0).takeIf { it in 1..65535 } ?: return null,
                hostPublicKeyB64 = pubkey,
                pairingSecret = salt,
                hostName = stored.getString(KEY_NAME, host) ?: host,
                viaRelay = stored.getBoolean(KEY_RELAY, false),
            )
        }.getOrNull()
    }

    fun forget() {
        ephemeralPairing = null
        runCatching { prefs?.edit()?.clear()?.apply() }
    }

    private companion object {
        const val FILE = "focuswindow.pairing.enc"
        const val KEY_HOST = "host"
        const val KEY_PORT = "port"
        const val KEY_PUBKEY = "pubkey"
        const val KEY_SALT = "salt"
        const val KEY_NAME = "name"
        const val KEY_RELAY = "relay"
    }
}
