package com.focuswindow.client

import android.content.Context
import android.util.Base64
import com.focuswindow.client.transport.PairingPayload
import com.focuswindow.client.transport.PairingRepository
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test
import org.junit.runner.RunWith
import org.robolectric.RobolectricTestRunner
import org.robolectric.RuntimeEnvironment
import org.robolectric.annotation.Config
import java.io.File
import java.security.KeyStoreException

/**
 * B07 / ISSUE-003.
 *
 * A keystore failure must degrade to session-only pairing: the current process
 * may still connect, but the reusable pairing secret must never be written to
 * ordinary SharedPreferences and must never survive an app restart.
 */
@RunWith(RobolectricTestRunner::class)
@Config(sdk = [34])
class PairingRepositoryKeystoreFailureTest {

    private val context: Context get() = RuntimeEnvironment.getApplication()

    private fun failingRepository(): PairingRepository =
        PairingRepository(context) { throw KeyStoreException("simulated keystore failure") }

    private fun payload() = PairingPayload(
        host = "192.168.2.107",
        port = 47810,
        hostPublicKeyB64 = "test-host-key",
        pairingSecret = ByteArray(32) { it.toByte() },
        hostName = "DeskFuse test PC",
        viaRelay = false,
    )

    @Test
    fun keystore_failure_disables_persistent_storage() {
        assertFalse(failingRepository().persistentStorageAvailable)
    }

    @Test
    fun keystore_failure_never_writes_a_plaintext_credential() {
        val secret = payload()
        failingRepository().remember(secret)

        val legacy = context.getSharedPreferences("focuswindow.pairing", Context.MODE_PRIVATE)
        assertTrue("legacy plaintext pairing store must stay empty", legacy.all.isEmpty())

        val secretB64 = Base64.encodeToString(secret.pairingSecret, Base64.NO_WRAP)
        val legacyFile = File(context.applicationInfo.dataDir, "shared_prefs/focuswindow.pairing.xml")
        if (legacyFile.exists()) {
            assertFalse("no plaintext secret may reach disk", legacyFile.readText().contains(secretB64))
        }
        val encryptedFile = File(context.applicationInfo.dataDir, "shared_prefs/focuswindow.pairing.enc.xml")
        assertFalse("no encrypted store may be created when the keystore fails", encryptedFile.exists())
    }

    @Test
    fun keystore_failure_keeps_the_pairing_in_memory_for_this_process_only() {
        val repository = failingRepository()
        val payload = payload()

        repository.remember(payload)
        assertEquals(payload, repository.last())

        assertNull(
            "a restarted repository must not recover a pairing it could not protect",
            failingRepository().last(),
        )
    }

    @Test
    fun legacy_plaintext_secret_is_purged_even_when_the_keystore_fails() {
        val legacy = context.getSharedPreferences("focuswindow.pairing", Context.MODE_PRIVATE)
        legacy.edit()
            .putString("host", "192.168.2.107")
            .putString("salt", "legacy-plaintext-secret")
            .commit()
        assertTrue(legacy.all.isNotEmpty())

        failingRepository()

        assertTrue("deprecated plaintext credentials must be erased", legacy.all.isEmpty())
    }
}
