package az.neoprint.neoprint

import android.app.Activity
import android.content.Intent
import android.content.IntentSender
import com.google.mlkit.vision.documentscanner.GmsDocumentScannerOptions
import com.google.mlkit.vision.documentscanner.GmsDocumentScanning
import com.google.mlkit.vision.documentscanner.GmsDocumentScanningResult
import io.flutter.plugin.common.BinaryMessenger
import io.flutter.plugin.common.MethodChannel
import java.io.File
import java.util.concurrent.ExecutorService

/**
 * The document scanner (plan section 2.11): Google ML Kit's Document Scanner
 * finds the page edges, straightens the perspective and takes several pages
 * in one go. It runs inside Google Play services - nothing is sent anywhere,
 * and the app needs no camera permission for it.
 *
 * The scanner's own crop and rotate are enough here (base mode, no filters):
 * the app applies the plan's filters, brightness and real size afterwards.
 * Each page comes back as a JPEG in the app's cache; the app deletes it once
 * the scan is saved or abandoned.
 *
 * The scanner also keeps a copy of its own of every page, in a folder under
 * the same cache. A scanned ID is not left lying there: those copies are
 * removed as soon as the pages are handed over, when the scan is closed
 * without saving, and before the next scan starts (T-196).
 */
class DocumentScanner(
    private val activity: Activity,
    private val worker: ExecutorService,
) {
    private var pending: MethodChannel.Result? = null

    fun register(messenger: BinaryMessenger) {
        MethodChannel(messenger, CHANNEL).setMethodCallHandler { call, result ->
            when (call.method) {
                // Google Play services decides when the scan starts; there is
                // nothing cheap to ask before that.
                "isAvailable" -> result.success(true)
                "scan" -> start(call.argument<Int>("pageLimit") ?: DEFAULT_PAGE_LIMIT, result)
                else -> result.notImplemented()
            }
        }
    }

    private fun start(pageLimit: Int, result: MethodChannel.Result) {
        if (pending != null) {
            result.error("BUSY", "A scan is already open.", null)
            return
        }

        val options = GmsDocumentScannerOptions.Builder()
            .setGalleryImportAllowed(false)
            .setPageLimit(pageLimit.coerceIn(1, MAX_PAGE_LIMIT))
            .setResultFormats(GmsDocumentScannerOptions.RESULT_FORMAT_JPEG)
            .setScannerMode(GmsDocumentScannerOptions.SCANNER_MODE_BASE)
            .build()

        pending = result

        // Whatever an earlier scan left behind, had the app been closed mid-way.
        worker.execute { purgeScannerCopies() }

        GmsDocumentScanning.getClient(options)
            .getStartScanIntent(activity)
            .addOnSuccessListener { sender ->
                try {
                    activity.startIntentSenderForResult(sender, REQUEST_CODE, null, 0, 0, 0)
                } catch (error: IntentSender.SendIntentException) {
                    fail("FAILED", error.message)
                }
            }
            .addOnFailureListener { error ->
                // Play services missing, too old, or the scanner module not
                // downloaded yet.
                fail("UNAVAILABLE", error.message)
            }
    }

    /** True when the result was the scanner's. */
    fun onActivityResult(requestCode: Int, resultCode: Int, data: Intent?): Boolean {
        if (requestCode != REQUEST_CODE) {
            return false
        }

        val result = pending ?: return true
        pending = null

        if (resultCode != Activity.RESULT_OK) {
            // Closed without saving: no pages, not an error.
            worker.execute { purgeScannerCopies() }
            result.success(emptyList<String>())
            return true
        }

        val pages = GmsDocumentScanningResult.fromActivityResultIntent(data)?.pages.orEmpty()

        // Copying a dozen full-size pages takes a moment: off the main thread.
        worker.execute {
            try {
                val paths = pages.mapIndexed { index, page ->
                    val output = File(activity.cacheDir, "scan-${System.nanoTime()}-$index.jpg")
                    activity.contentResolver.openInputStream(page.imageUri).use { input ->
                        requireNotNull(input) { "The scanned page could not be read." }
                        output.outputStream().use { input.copyTo(it) }
                    }
                    output.absolutePath
                }
                activity.runOnUiThread { result.success(paths) }
            } catch (error: Exception) {
                activity.runOnUiThread { result.error("FAILED", error.message, null) }
            } finally {
                purgeScannerCopies()
            }
        }

        return true
    }

    /** The scanner's own copies of the pages; the app works from its own. */
    private fun purgeScannerCopies() {
        File(activity.cacheDir, SCANNER_CACHE).listFiles()?.forEach { it.delete() }
    }

    private fun fail(code: String, message: String?) {
        val result = pending ?: return
        pending = null
        result.error(code, message, null)
    }

    private companion object {
        const val CHANNEL = "az.neoprint/document_scanner"
        const val REQUEST_CODE = 0x5CA7
        const val DEFAULT_PAGE_LIMIT = 10
        const val MAX_PAGE_LIMIT = 20

        // Where Google Play services' scanner writes its result pages.
        const val SCANNER_CACHE = "mlkit_docscan_ui_client"
    }
}
