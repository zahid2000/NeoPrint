package az.neoprint.neoprint

import android.content.Context
import android.content.SharedPreferences
import android.os.Handler
import android.os.Looper
import android.security.keystore.KeyGenParameterSpec
import android.security.keystore.KeyProperties
import android.util.Base64
import io.flutter.plugin.common.BinaryMessenger
import io.flutter.plugin.common.MethodChannel
import java.security.GeneralSecurityException
import java.security.KeyStore
import java.util.concurrent.Executors
import javax.crypto.Cipher
import javax.crypto.KeyGenerator
import javax.crypto.SecretKey
import javax.crypto.spec.GCMParameterSpec

/**
 * The app's secure storage (plan sections 2.0 and 2.8, our own channel): the
 * refresh token, the per-install id and the chosen language.
 *
 * Each value is sealed with AES-256-GCM under a key that is made in the
 * Android Keystore and never leaves it; only the sealed bytes go into a
 * private preferences file. The value's name is bound in as associated data,
 * so one sealed value cannot be passed off as another.
 *
 * A value that no longer opens - the key is gone after a restore to another
 * phone, or the file was changed - is dropped and read as absent: the app
 * then asks the user to sign in, which is the right answer. Nothing stored
 * here is ever logged or put into an error message.
 */
class SecureStore(private val context: Context) {
    // Its own thread: a token is not kept waiting behind a photo conversion,
    // and a write is always finished before the read that follows it.
    private val worker = Executors.newSingleThreadExecutor()
    private val main = Handler(Looper.getMainLooper())

    private val preferences: SharedPreferences by lazy {
        context.applicationContext.getSharedPreferences(FILE, Context.MODE_PRIVATE)
    }

    fun register(messenger: BinaryMessenger) {
        MethodChannel(messenger, CHANNEL).setMethodCallHandler { call, result ->
            val name = call.argument<String>("key")
            val value = call.argument<String>("value")

            val action: (() -> Any?)? = when {
                call.method == "clear" -> { -> clear() }
                name.isNullOrEmpty() -> null
                call.method == "read" -> { -> read(name) }
                call.method == "write" && value != null -> { -> write(name, value) }
                call.method == "delete" -> { -> delete(name) }
                else -> null
            }

            if (action == null) {
                if (call.method in METHODS) {
                    result.error("BAD_ARGUMENT", "key and value are required", null)
                } else {
                    result.notImplemented()
                }
                return@setMethodCallHandler
            }

            worker.execute {
                try {
                    val answer = action()
                    main.post { result.success(answer) }
                } catch (error: Exception) {
                    // The kind of failure only: never the value or its name.
                    main.post { result.error("FAILED", error.javaClass.simpleName, null) }
                }
            }
        }
    }

    fun close() {
        worker.shutdown()
    }

    private fun read(name: String): String? {
        val sealed = preferences.getString(name, null) ?: return null

        return try {
            val bytes = Base64.decode(sealed, Base64.NO_WRAP)
            require(bytes.size > NONCE_BYTES) { "The sealed value is too short." }

            val cipher = Cipher.getInstance(TRANSFORMATION)
            cipher.init(
                Cipher.DECRYPT_MODE,
                key(),
                GCMParameterSpec(TAG_BITS, bytes, 0, NONCE_BYTES),
            )
            cipher.updateAAD(name.toByteArray(Charsets.UTF_8))

            String(cipher.doFinal(bytes, NONCE_BYTES, bytes.size - NONCE_BYTES), Charsets.UTF_8)
        } catch (error: GeneralSecurityException) {
            drop(name)
        } catch (error: IllegalArgumentException) {
            drop(name)
        }
    }

    private fun write(name: String, value: String): Any? {
        val cipher = Cipher.getInstance(TRANSFORMATION)
        // The Keystore picks a fresh random nonce for every value.
        cipher.init(Cipher.ENCRYPT_MODE, key())
        cipher.updateAAD(name.toByteArray(Charsets.UTF_8))

        check(cipher.iv.size == NONCE_BYTES) { "Unexpected nonce length." }
        val sealed = cipher.iv + cipher.doFinal(value.toByteArray(Charsets.UTF_8))

        // commit, not apply: a token that was just rotated must be on disk
        // before the old one stops working.
        check(
            preferences.edit().putString(name, Base64.encodeToString(sealed, Base64.NO_WRAP)).commit()
        ) { "The value could not be saved." }

        return null
    }

    private fun delete(name: String): Any? {
        preferences.edit().remove(name).commit()
        return null
    }

    private fun clear(): Any? {
        preferences.edit().clear().commit()
        return null
    }

    /** A value that cannot be opened is of no use to anyone. */
    private fun drop(name: String): String? {
        preferences.edit().remove(name).commit()
        return null
    }

    private fun key(): SecretKey {
        val store = KeyStore.getInstance(KEYSTORE).apply { load(null) }

        (store.getKey(ALIAS, null) as? SecretKey)?.let { return it }

        val generator = KeyGenerator.getInstance(KeyProperties.KEY_ALGORITHM_AES, KEYSTORE)
        generator.init(
            KeyGenParameterSpec.Builder(
                ALIAS,
                KeyProperties.PURPOSE_ENCRYPT or KeyProperties.PURPOSE_DECRYPT,
            )
                .setBlockModes(KeyProperties.BLOCK_MODE_GCM)
                .setEncryptionPaddings(KeyProperties.ENCRYPTION_PADDING_NONE)
                .setKeySize(KEY_BITS)
                .build()
        )

        return generator.generateKey()
    }

    private companion object {
        const val CHANNEL = "az.neoprint/secure_storage"
        const val FILE = "neoprint_secure_storage"
        const val KEYSTORE = "AndroidKeyStore"
        const val ALIAS = "az.neoprint.secure_storage.v1"
        const val TRANSFORMATION = "AES/GCM/NoPadding"
        const val KEY_BITS = 256
        const val NONCE_BYTES = 12
        const val TAG_BITS = 128
        val METHODS = setOf("read", "write", "delete", "clear")
    }
}
