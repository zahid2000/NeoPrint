package az.neoprint.neoprint

import android.app.Activity
import android.content.ActivityNotFoundException
import android.content.Intent
import android.net.Uri
import android.provider.OpenableColumns
import io.flutter.plugin.common.BinaryMessenger
import io.flutter.plugin.common.MethodChannel
import java.io.File
import java.util.concurrent.ExecutorService

/**
 * "Buluddan əlavə et" (T-399): a cloud app's own file chooser - Google
 * Drive, OneDrive, Dropbox - opened directly, instead of the system picker
 * that starts on the phone's files. Each of these apps answers
 * ACTION_GET_CONTENT itself, with the user's own account, so the app needs
 * no sign-in, key or library of its own (plan section 2.0: our own channel).
 *
 * Only the apps listed here can be opened. The file chosen is copied into
 * the app's cache - one at a time, the previous copy removed - and uploaded
 * from there like any picked file.
 */
class CloudPicker(
    private val activity: Activity,
    private val worker: ExecutorService,
) {
    private var pending: MethodChannel.Result? = null

    fun register(messenger: BinaryMessenger) {
        MethodChannel(messenger, CHANNEL).setMethodCallHandler { call, result ->
            when (call.method) {
                "installed" -> result.success(APPS.filter(::canPick))
                "pick" -> start(call.argument<String>("app"), result)
                else -> result.notImplemented()
            }
        }
    }

    private fun chooser(app: String) = Intent(Intent.ACTION_GET_CONTENT).apply {
        type = "*/*"
        addCategory(Intent.CATEGORY_OPENABLE)
        setPackage(app)
    }

    private fun canPick(app: String) =
        chooser(app).resolveActivity(activity.packageManager) != null

    private fun start(app: String?, result: MethodChannel.Result) {
        if (app == null || app !in APPS) {
            result.error("BAD_ARGUMENT", "unknown app", null)
            return
        }
        if (pending != null) {
            result.error("BUSY", "A chooser is already open.", null)
            return
        }

        pending = result
        try {
            activity.startActivityForResult(chooser(app), REQUEST_CODE)
        } catch (error: ActivityNotFoundException) {
            pending = null
            result.error("UNAVAILABLE", error.message, null)
        }
    }

    /** True when the result was the chooser's. */
    fun onActivityResult(requestCode: Int, resultCode: Int, data: Intent?): Boolean {
        if (requestCode != REQUEST_CODE) {
            return false
        }

        val result = pending ?: return true
        pending = null

        val uri = data?.data
        if (resultCode != Activity.RESULT_OK || uri == null) {
            // Closed without choosing: nothing, not an error.
            result.success(null)
            return true
        }

        // A large file from the cloud is downloaded here: off the main thread.
        worker.execute {
            try {
                val copy = copy(uri)
                activity.runOnUiThread {
                    result.success(mapOf("path" to copy.absolutePath, "name" to copy.name))
                }
            } catch (error: TooLargeException) {
                activity.runOnUiThread { result.error("TOO_LARGE", null, null) }
            } catch (error: Exception) {
                activity.runOnUiThread { result.error("FAILED", error.message, null) }
            }
        }
        return true
    }

    private fun copy(uri: Uri): File {
        val folder = File(activity.cacheDir, CACHE_FOLDER)
        // One copy at a time: the last one has been uploaded or abandoned.
        folder.listFiles()?.forEach { it.delete() }
        folder.mkdirs()

        val output = File(folder, safeName(displayName(uri)))
        try {
            activity.contentResolver.openInputStream(uri).use { input ->
                requireNotNull(input) { "The file could not be read." }
                output.outputStream().use { stream ->
                    val buffer = ByteArray(BUFFER_SIZE)
                    var total = 0L
                    while (true) {
                        val read = input.read(buffer)
                        if (read < 0) break
                        total += read
                        if (total > MAX_BYTES) throw TooLargeException()
                        stream.write(buffer, 0, read)
                    }
                }
            }
        } catch (error: Exception) {
            output.delete()
            throw error
        }
        return output
    }

    private fun displayName(uri: Uri): String? =
        activity.contentResolver
            .query(uri, arrayOf(OpenableColumns.DISPLAY_NAME), null, null, null)
            ?.use { cursor -> if (cursor.moveToFirst()) cursor.getString(0) else null }

    /** The cloud's name for the file, without anything that could leave the folder. */
    private fun safeName(name: String?): String {
        val clean = name.orEmpty()
            .replace(Regex("[\\\\/:*?\"<>|\\p{Cntrl}]"), "_")
            .trim()
            .trimStart('.')
            .take(MAX_NAME)
        return clean.ifEmpty { "cloud-${System.nanoTime()}" }
    }

    private class TooLargeException : Exception()

    private companion object {
        const val CHANNEL = "az.neoprint/cloud_picker"
        const val REQUEST_CODE = 0xC10D
        const val CACHE_FOLDER = "cloud"
        const val BUFFER_SIZE = 64 * 1024
        const val MAX_NAME = 120

        // Far above any upload limit; only keeps a wrong choice off the phone.
        const val MAX_BYTES = 200L * 1024 * 1024

        // Google Drive, OneDrive, Dropbox: in this order in the app.
        val APPS = listOf(
            "com.google.android.apps.docs",
            "com.microsoft.skydrive",
            "com.dropbox.android",
        )
    }
}
