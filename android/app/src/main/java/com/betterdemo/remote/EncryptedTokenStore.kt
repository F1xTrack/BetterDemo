package com.betterdemo.remote

import android.content.Context
import android.security.keystore.KeyGenParameterSpec
import android.security.keystore.KeyProperties
import android.util.Base64
import java.security.GeneralSecurityException
import java.security.KeyStore
import javax.crypto.Cipher
import javax.crypto.KeyGenerator
import javax.crypto.SecretKey
import javax.crypto.spec.GCMParameterSpec

class StoredRemoteSession(
    val host: String,
    val port: Int,
    val fingerprint: String,
    val token: String,
    @Volatile var nextSequence: Long,
) {
    override fun toString(): String = "StoredRemoteSession(host=$host, port=$port, token=<redacted>)"
}

data class StoredPendingCommand(
    val sequence: Long,
    val command: String,
    val idempotencyKey: String,
    val payloadJson: String,
)

class EncryptedTokenStore(context: Context) {
    private val preferences = context.getSharedPreferences("betterdemo.remote", Context.MODE_PRIVATE)

    @Synchronized
    fun save(host: String, port: Int, fingerprint: String, token: String, nextSequence: Long = 1L) {
        require(token.isNotBlank())
        require(nextSequence > 0)
        val cipher = Cipher.getInstance(TRANSFORMATION)
        cipher.init(Cipher.ENCRYPT_MODE, getOrCreateKey())
        val encrypted = cipher.doFinal(token.toByteArray(Charsets.UTF_8))
        preferences.edit()
            .putString(KEY_HOST, host)
            .putInt(KEY_PORT, port)
            .putString(KEY_FINGERPRINT, fingerprint)
            .putString(KEY_TOKEN_CIPHERTEXT, Base64.encodeToString(encrypted, Base64.NO_WRAP))
            .putString(KEY_TOKEN_IV, Base64.encodeToString(cipher.iv, Base64.NO_WRAP))
            .putLong(KEY_NEXT_SEQUENCE, nextSequence)
            .apply()
    }

    @Synchronized
    fun load(): StoredRemoteSession? {
        val host = preferences.getString(KEY_HOST, null) ?: return null
        val port = preferences.getInt(KEY_PORT, 0)
        val fingerprint = preferences.getString(KEY_FINGERPRINT, null) ?: return null
        val encryptedText = preferences.getString(KEY_TOKEN_CIPHERTEXT, null) ?: return null
        val ivText = preferences.getString(KEY_TOKEN_IV, null) ?: return null
        if (port !in 1..65535) {
            clear()
            return null
        }

        return try {
            val cipher = Cipher.getInstance(TRANSFORMATION)
            cipher.init(
                Cipher.DECRYPT_MODE,
                getOrCreateKey(),
                GCMParameterSpec(128, Base64.decode(ivText, Base64.NO_WRAP)),
            )
            val token = cipher.doFinal(Base64.decode(encryptedText, Base64.NO_WRAP)).toString(Charsets.UTF_8)
            if (token.isBlank()) {
                clear()
                null
            } else {
                StoredRemoteSession(
                    host,
                    port,
                    fingerprint,
                    token,
                    preferences.getLong(KEY_NEXT_SEQUENCE, 1L).coerceAtLeast(1L),
                )
            }
        } catch (_: GeneralSecurityException) {
            clear()
            null
        } catch (_: IllegalArgumentException) {
            clear()
            null
        }
    }

    @Synchronized
    fun saveNextSequence(sequence: Long) {
        require(sequence > 0)
        preferences.edit().putLong(KEY_NEXT_SEQUENCE, sequence).apply()
    }

    @Synchronized
    fun savePending(command: StoredPendingCommand) {
        preferences.edit()
            .putLong(KEY_PENDING_SEQUENCE, command.sequence)
            .putString(KEY_PENDING_COMMAND, command.command)
            .putString(KEY_PENDING_IDEMPOTENCY, command.idempotencyKey)
            .putString(KEY_PENDING_PAYLOAD, command.payloadJson)
            .commit()
    }

    @Synchronized
    fun loadPending(): StoredPendingCommand? {
        val command = preferences.getString(KEY_PENDING_COMMAND, null) ?: return null
        val idempotencyKey = preferences.getString(KEY_PENDING_IDEMPOTENCY, null) ?: return null
        val payload = preferences.getString(KEY_PENDING_PAYLOAD, null) ?: return null
        val sequence = preferences.getLong(KEY_PENDING_SEQUENCE, 0L)
        if (sequence <= 0) return null
        return StoredPendingCommand(sequence, command, idempotencyKey, payload)
    }

    @Synchronized
    fun clearPending() {
        preferences.edit()
            .remove(KEY_PENDING_SEQUENCE)
            .remove(KEY_PENDING_COMMAND)
            .remove(KEY_PENDING_IDEMPOTENCY)
            .remove(KEY_PENDING_PAYLOAD)
            .apply()
    }

    @Synchronized
    fun clear() {
        preferences.edit().clear().commit()
        val keyStore = KeyStore.getInstance(ANDROID_KEYSTORE).apply { load(null) }
        if (keyStore.containsAlias(KEY_ALIAS)) keyStore.deleteEntry(KEY_ALIAS)
    }

    private fun getOrCreateKey(): SecretKey {
        val keyStore = KeyStore.getInstance(ANDROID_KEYSTORE).apply { load(null) }
        (keyStore.getKey(KEY_ALIAS, null) as? SecretKey)?.let { return it }

        val generator = KeyGenerator.getInstance(KeyProperties.KEY_ALGORITHM_AES, ANDROID_KEYSTORE)
        generator.init(
            KeyGenParameterSpec.Builder(
                KEY_ALIAS,
                KeyProperties.PURPOSE_ENCRYPT or KeyProperties.PURPOSE_DECRYPT,
            )
                .setBlockModes(KeyProperties.BLOCK_MODE_GCM)
                .setEncryptionPaddings(KeyProperties.ENCRYPTION_PADDING_NONE)
                .setRandomizedEncryptionRequired(true)
                .build(),
        )
        return generator.generateKey()
    }

    private companion object {
        const val ANDROID_KEYSTORE = "AndroidKeyStore"
        const val KEY_ALIAS = "betterdemo.remote.token.v1"
        const val TRANSFORMATION = "AES/GCM/NoPadding"
        const val KEY_HOST = "host"
        const val KEY_PORT = "port"
        const val KEY_FINGERPRINT = "fingerprint"
        const val KEY_TOKEN_CIPHERTEXT = "token_ciphertext"
        const val KEY_TOKEN_IV = "token_iv"
        const val KEY_NEXT_SEQUENCE = "next_sequence"
        const val KEY_PENDING_SEQUENCE = "pending_sequence"
        const val KEY_PENDING_COMMAND = "pending_command"
        const val KEY_PENDING_IDEMPOTENCY = "pending_idempotency"
        const val KEY_PENDING_PAYLOAD = "pending_payload"
    }
}
