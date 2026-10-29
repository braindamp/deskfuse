package com.focuswindow.client.transport

import android.os.Build
import android.security.keystore.KeyGenParameterSpec
import android.security.keystore.KeyInfo
import android.security.keystore.KeyProperties
import java.security.KeyFactory
import java.security.KeyPairGenerator
import java.security.KeyStore
import java.security.spec.ECGenParameterSpec
import java.util.UUID

/** Android Keystore implementation. Missing/invalidated keys never fall back to software or auto-rotate on signing. */
class AndroidBrokerDeviceSigner {
    private val signer = BrokerDeviceSigner(AndroidBrokerSigningKeys())

    fun prepareEnrollment(hostIdentitySha256: ByteArray, enrollmentId: UUID): BrokerSigningIdentity =
        signer.prepareEnrollment(hostIdentitySha256, enrollmentId)

    internal fun signEnrollmentProposal(host: ByteArray, enrollment: UUID, keyHash: ByteArray, offer: ByteArray,
        nonce: ByteArray, scopes: List<BrokerScope>): Pair<ByteArray, ByteArray> =
        signer.signEnrollmentProposal(host, enrollment, keyHash, offer, nonce, scopes)

    internal fun signPreparedEnrollmentApproval(prepared: TrustedPreparedEnrollment): Pair<ByteArray, ByteArray> =
        signer.signPreparedEnrollmentApproval(prepared)
    internal fun signEnrollmentStatus(prepared: TrustedPreparedEnrollment, nonce: ByteArray): Pair<ByteArray, ByteArray> =
        signer.signEnrollmentStatus(prepared, nonce)

    internal fun signCommittedProof(active: TrustedCommittedEnrollment, transcript: ByteArray, hostSignature: ByteArray,
        approval: BrokerProofApproval): ByteArray = signer.signCommittedProof(active, transcript, hostSignature, approval)

    fun signExisting(hostIdentitySha256: ByteArray, enrollmentId: UUID, approvedSpkiSha256: ByteArray,
        transcript: ByteArray, approval: BrokerProofApproval): ByteArray =
        signer.signExisting(hostIdentitySha256, enrollmentId, approvedSpkiSha256, transcript, approval)
}

private class AndroidBrokerSigningKeys : BrokerSigningKeyStore {
    private fun store(): KeyStore = KeyStore.getInstance("AndroidKeyStore").apply { load(null) }

    override fun find(alias: String): BrokerSigningKey? {
        val store = store()
        if (!store.containsAlias(alias)) return null
        val entry = store.getEntry(alias, null) as? KeyStore.PrivateKeyEntry
            ?: error("Unexpected Android broker key entry")
        val info = KeyFactory.getInstance(entry.privateKey.algorithm, "AndroidKeyStore")
            .getKeySpec(entry.privateKey, KeyInfo::class.java)
        require(info.purposes and KeyProperties.PURPOSE_SIGN != 0 && info.keySize == 256)
        val protection = if (Build.VERSION.SDK_INT >= 31) {
            when (info.securityLevel) {
                KeyProperties.SECURITY_LEVEL_SOFTWARE -> BrokerKeyProtection.SOFTWARE
                KeyProperties.SECURITY_LEVEL_TRUSTED_ENVIRONMENT -> BrokerKeyProtection.TRUSTED_ENVIRONMENT
                KeyProperties.SECURITY_LEVEL_STRONGBOX -> BrokerKeyProtection.STRONGBOX
                else -> BrokerKeyProtection.UNKNOWN
            }
        } else {
            @Suppress("DEPRECATION")
            if (info.isInsideSecureHardware) BrokerKeyProtection.TRUSTED_ENVIRONMENT else BrokerKeyProtection.SOFTWARE
        }
        return BrokerSigningKey(entry.privateKey, entry.certificate.publicKey.encoded, protection)
    }

    override fun create(alias: String): BrokerSigningKey {
        check(!store().containsAlias(alias)) { "Refusing to replace an existing broker key" }
        KeyPairGenerator.getInstance(KeyProperties.KEY_ALGORITHM_EC, "AndroidKeyStore").apply {
            initialize(KeyGenParameterSpec.Builder(alias, KeyProperties.PURPOSE_SIGN)
                .setAlgorithmParameterSpec(ECGenParameterSpec("secp256r1"))
                .setDigests(KeyProperties.DIGEST_SHA256)
                .setUserAuthenticationRequired(false)
                .build())
        }.generateKeyPair()
        return find(alias) ?: error("Android broker key was not retained")
    }
}
