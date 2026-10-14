package az.neoprint.neoprint

import android.content.ActivityNotFoundException
import android.content.Intent
import android.graphics.Bitmap
import android.graphics.ImageDecoder
import android.net.Uri
import android.os.Build
import io.flutter.embedding.android.FlutterActivity
import io.flutter.embedding.engine.FlutterEngine
import io.flutter.plugin.common.MethodChannel
import java.io.File
import java.util.concurrent.Executors

/**
 * The Flutter host, plus one platform channel: HEIC/HEIF photos are turned
 * into JPEG here before upload (plan section 2.2 - the backend does not take
 * HEIC; the phone converts it with its own API).
 *
 * ImageDecoder reads HEIF from Android 9 (API 28) and applies the photo's
 * EXIF orientation. Older phones get UNSUPPORTED, which the app explains.
 */
class MainActivity : FlutterActivity() {
    private val worker = Executors.newSingleThreadExecutor()
    private val scanner = DocumentScanner(this, worker)
    private val secureStore = SecureStore(this)
    private val biometrics = Biometrics(this)

    override fun configureFlutterEngine(flutterEngine: FlutterEngine) {
        super.configureFlutterEngine(flutterEngine)
        registerBrowser(flutterEngine)
        registerShare(flutterEngine)
        scanner.register(flutterEngine.dartExecutor.binaryMessenger)
        secureStore.register(flutterEngine.dartExecutor.binaryMessenger)
        biometrics.register(flutterEngine.dartExecutor.binaryMessenger)

        MethodChannel(flutterEngine.dartExecutor.binaryMessenger, HEIC_CHANNEL)
            .setMethodCallHandler { call, result ->
                if (call.method != "toJpeg") {
                    result.notImplemented()
                    return@setMethodCallHandler
                }

                val path = call.argument<String>("path")

                if (path.isNullOrEmpty()) {
                    result.error("BAD_ARGUMENT", "path is required", null)
                    return@setMethodCallHandler
                }

                // Decoding a 12-megapixel photo takes a moment: off the main
                // thread, answered back on it.
                worker.execute {
                    try {
                        val output = toJpeg(File(path))
                        runOnUiThread { result.success(output.absolutePath) }
                    } catch (error: UnsupportedOperationException) {
                        runOnUiThread { result.error("UNSUPPORTED", error.message, null) }
                    } catch (error: Exception) {
                        runOnUiThread { result.error("FAILED", error.message, null) }
                    }
                }
            }
    }

    /**
     * A second channel: a link opened in the phone's browser - the
     * "Məlumatlarımı yüklə" archive, which is too large to pass through the
     * app (docs/DECISIONS.md T-147). Only http and https.
     */
    private fun registerBrowser(flutterEngine: FlutterEngine) {
        MethodChannel(flutterEngine.dartExecutor.binaryMessenger, BROWSER_CHANNEL)
            .setMethodCallHandler { call, result ->
                val url = call.argument<String>("url")
                val uri = url?.let(Uri::parse)

                if (call.method != "open" || uri == null || uri.scheme !in setOf("http", "https")) {
                    result.error("BAD_ARGUMENT", "an http(s) url is required", null)
                    return@setMethodCallHandler
                }

                try {
                    startActivity(Intent(Intent.ACTION_VIEW, uri))
                    result.success(true)
                } catch (error: ActivityNotFoundException) {
                    result.success(false)
                }
            }
    }

    /**
     * A third channel: text handed to the system share sheet (ACTION_SEND) -
     * the referral code and link (plan sections 2.0, 8.2: our own channel, no
     * plugin). Answers false when nothing on the phone can take it.
     */
    private fun registerShare(flutterEngine: FlutterEngine) {
        MethodChannel(flutterEngine.dartExecutor.binaryMessenger, SHARE_CHANNEL)
            .setMethodCallHandler { call, result ->
                val text = call.argument<String>("text")

                if (call.method != "text" || text.isNullOrBlank()) {
                    result.error("BAD_ARGUMENT", "text is required", null)
                    return@setMethodCallHandler
                }

                val send = Intent(Intent.ACTION_SEND).apply {
                    type = "text/plain"
                    putExtra(Intent.EXTRA_TEXT, text)
                    call.argument<String>("subject")?.let { putExtra(Intent.EXTRA_SUBJECT, it) }
                }

                try {
                    startActivity(Intent.createChooser(send, null))
                    result.success(true)
                } catch (error: ActivityNotFoundException) {
                    result.success(false)
                }
            }
    }

    // The scanner is started with startIntentSenderForResult, whose result
    // still arrives here on a FlutterActivity.
    @Deprecated("Deprecated in Java")
    override fun onActivityResult(requestCode: Int, resultCode: Int, data: Intent?) {
        if (!scanner.onActivityResult(requestCode, resultCode, data)) {
            super.onActivityResult(requestCode, resultCode, data)
        }
    }

    override fun onDestroy() {
        worker.shutdown()
        secureStore.close()
        super.onDestroy()
    }

    private fun toJpeg(input: File): File {
        if (Build.VERSION.SDK_INT < Build.VERSION_CODES.P) {
            throw UnsupportedOperationException("HEIF decoding needs Android 9")
        }

        val bitmap = ImageDecoder.decodeBitmap(ImageDecoder.createSource(input)) { decoder, _, _ ->
            // A software bitmap, so it can be compressed.
            decoder.allocator = ImageDecoder.ALLOCATOR_SOFTWARE
        }

        try {
            // In the app's cache: private to the app, and cleared by the system.
            val output = File(cacheDir, "heic-${System.nanoTime()}.jpg")

            output.outputStream().use { stream ->
                if (!bitmap.compress(Bitmap.CompressFormat.JPEG, JPEG_QUALITY, stream)) {
                    throw IllegalStateException("The JPEG could not be written")
                }
            }

            return output
        } finally {
            bitmap.recycle()
        }
    }

    private companion object {
        const val HEIC_CHANNEL = "az.neoprint/heic"
        const val BROWSER_CHANNEL = "az.neoprint/browser"
        const val SHARE_CHANNEL = "az.neoprint/share"
        const val JPEG_QUALITY = 90
    }
}
