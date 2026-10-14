package az.neoprint.neoprint

import android.app.Activity
import android.hardware.biometrics.BiometricManager
import android.hardware.biometrics.BiometricPrompt
import android.os.Build
import android.os.CancellationSignal
import io.flutter.plugin.common.BinaryMessenger
import io.flutter.plugin.common.MethodChannel
import java.util.concurrent.atomic.AtomicBoolean

/**
 * Unlocking the app with a fingerprint or a face (T-270), over our own channel
 * and the system's BiometricPrompt - no library (plan section 2.0, the owner's
 * decision of 2026-10-14).
 *
 * It only answers "the owner of this phone is here": nothing is unsealed with
 * it. The PIN stays the way in, and a phone without a fingerprint or face
 * enrolled is told so ("available" is false) and is never offered one.
 *
 * From Android 10 (API 29), where BiometricManager says whether anything is
 * enrolled; older phones use the PIN alone.
 */
class Biometrics(private val activity: Activity) {
    fun register(messenger: BinaryMessenger) {
        MethodChannel(messenger, CHANNEL).setMethodCallHandler { call, result ->
            when (call.method) {
                "available" -> result.success(available())
                "authenticate" -> authenticate(
                    call.argument<String>("title").orEmpty(),
                    call.argument<String>("cancel").orEmpty(),
                    result,
                )
                else -> result.notImplemented()
            }
        }
    }

    private fun available(): Boolean {
        if (Build.VERSION.SDK_INT < Build.VERSION_CODES.Q) {
            return false
        }

        val manager = activity.getSystemService(BiometricManager::class.java) ?: return false

        val answer = if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.R) {
            manager.canAuthenticate(BiometricManager.Authenticators.BIOMETRIC_WEAK)
        } else {
            @Suppress("DEPRECATION")
            manager.canAuthenticate()
        }

        return answer == BiometricManager.BIOMETRIC_SUCCESS
    }

    private fun authenticate(title: String, cancel: String, result: MethodChannel.Result) {
        if (!available() || title.isEmpty() || cancel.isEmpty()) {
            result.success(false)
            return
        }

        // The prompt can end in more than one way at once (an error after a
        // cancel); the app hears one answer.
        val answered = AtomicBoolean(false)
        val answer = { ok: Boolean -> if (answered.compareAndSet(false, true)) result.success(ok) }

        val builder = BiometricPrompt.Builder(activity)
            .setTitle(title)
            .setNegativeButton(cancel, activity.mainExecutor) { _, _ -> answer(false) }

        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.R) {
            builder.setAllowedAuthenticators(BiometricManager.Authenticators.BIOMETRIC_WEAK)
        }

        try {
            builder.build().authenticate(
                CancellationSignal(),
                activity.mainExecutor,
                object : BiometricPrompt.AuthenticationCallback() {
                    override fun onAuthenticationSucceeded(result: BiometricPrompt.AuthenticationResult?) {
                        answer(true)
                    }

                    override fun onAuthenticationError(errorCode: Int, errString: CharSequence?) {
                        // Cancelled, too many tries, the sensor busy: the PIN is asked instead.
                        answer(false)
                    }

                    // onAuthenticationFailed: one finger not recognised - the
                    // prompt stays and lets the user try again.
                },
            )
        } catch (error: Exception) {
            answer(false)
        }
    }

    private companion object {
        const val CHANNEL = "az.neoprint/biometrics"
    }
}
