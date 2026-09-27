package az.neoprint.neoprint

import android.graphics.Bitmap
import android.graphics.ImageDecoder
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

    override fun configureFlutterEngine(flutterEngine: FlutterEngine) {
        super.configureFlutterEngine(flutterEngine)

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

    override fun onDestroy() {
        worker.shutdown()
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
        const val JPEG_QUALITY = 90
    }
}
