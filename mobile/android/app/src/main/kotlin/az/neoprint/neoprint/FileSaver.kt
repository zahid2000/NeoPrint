package az.neoprint.neoprint

import android.app.Activity
import android.content.ActivityNotFoundException
import android.content.Intent
import io.flutter.plugin.common.BinaryMessenger
import io.flutter.plugin.common.MethodChannel
import java.util.concurrent.ExecutorService

/**
 * "Cihaza yüklə" (T-429): a file of the account saved on the phone where the
 * user chooses - the system's own "save as" (ACTION_CREATE_DOCUMENT), so the
 * app needs no storage permission and no library (plan section 2.0: our own
 * channel). Downloads, Google Drive or any folder the picker offers.
 */
class FileSaver(
    private val activity: Activity,
    private val worker: ExecutorService,
) {
    private var pending: MethodChannel.Result? = null
    private var bytes: ByteArray? = null

    fun register(messenger: BinaryMessenger) {
        MethodChannel(messenger, CHANNEL).setMethodCallHandler { call, result ->
            if (call.method != "save") {
                result.notImplemented()
                return@setMethodCallHandler
            }

            val name = call.argument<String>("name")
            val content = call.argument<ByteArray>("bytes")
            if (name.isNullOrBlank() || content == null) {
                result.error("BAD_ARGUMENT", "name and bytes are required", null)
                return@setMethodCallHandler
            }
            if (pending != null) {
                result.error("BUSY", "A save is already open.", null)
                return@setMethodCallHandler
            }

            val intent = Intent(Intent.ACTION_CREATE_DOCUMENT).apply {
                addCategory(Intent.CATEGORY_OPENABLE)
                type = call.argument<String>("mime") ?: "application/octet-stream"
                putExtra(Intent.EXTRA_TITLE, name)
            }

            pending = result
            bytes = content
            try {
                activity.startActivityForResult(intent, REQUEST_CODE)
            } catch (error: ActivityNotFoundException) {
                pending = null
                bytes = null
                result.error("UNAVAILABLE", error.message, null)
            }
        }
    }

    /** True when the result was the save dialog's. */
    fun onActivityResult(requestCode: Int, resultCode: Int, data: Intent?): Boolean {
        if (requestCode != REQUEST_CODE) {
            return false
        }

        val result = pending ?: return true
        val content = bytes
        pending = null
        bytes = null

        val uri = data?.data
        if (resultCode != Activity.RESULT_OK || uri == null || content == null) {
            // Closed without saving: nothing, not an error.
            result.success(false)
            return true
        }

        worker.execute {
            try {
                activity.contentResolver.openOutputStream(uri, "wt").use { stream ->
                    requireNotNull(stream) { "The place chosen cannot be written." }
                    stream.write(content)
                }
                activity.runOnUiThread { result.success(true) }
            } catch (error: Exception) {
                activity.runOnUiThread { result.error("FAILED", error.message, null) }
            }
        }
        return true
    }

    private companion object {
        const val CHANNEL = "az.neoprint/file_saver"
        const val REQUEST_CODE = 0x5A7E
    }
}
