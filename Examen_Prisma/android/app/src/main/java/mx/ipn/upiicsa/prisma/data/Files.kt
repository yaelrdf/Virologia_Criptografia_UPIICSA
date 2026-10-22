package mx.ipn.upiicsa.prisma.data

import android.content.ClipData
import android.content.Context
import android.content.Intent
import android.graphics.Bitmap
import android.graphics.BitmapFactory
import android.net.Uri
import android.provider.OpenableColumns
import androidx.compose.ui.graphics.ImageBitmap
import androidx.compose.ui.graphics.asImageBitmap
import androidx.core.content.FileProvider
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.withContext
import java.io.File

/** Utilidades de archivos: lectura de content:// URIs, decodificación, guardado y compartir. */
object Files {
    /** Límite de lectura local; el servidor rechaza archivos más grandes de todos modos. */
    private const val MAX_BYTES = 25L * 1024 * 1024

    fun displayName(context: Context, uri: Uri): String {
        context.contentResolver.query(uri, arrayOf(OpenableColumns.DISPLAY_NAME), null, null, null)?.use { c ->
            if (c.moveToFirst()) {
                val idx = c.getColumnIndex(OpenableColumns.DISPLAY_NAME)
                if (idx >= 0) c.getString(idx)?.let { return it }
            }
        }
        return uri.lastPathSegment?.substringAfterLast('/') ?: "imagen"
    }

    suspend fun read(context: Context, uri: Uri): LocalFile = withContext(Dispatchers.IO) {
        val resolver = context.contentResolver
        val name = displayName(context, uri)
        val mime = resolver.getType(uri) ?: guessMime(name)
        val bytes = resolver.openInputStream(uri)?.use { input ->
            val out = java.io.ByteArrayOutputStream()
            val buf = ByteArray(64 * 1024)
            var total = 0L
            while (true) {
                val n = input.read(buf)
                if (n < 0) break
                total += n
                if (total > MAX_BYTES) throw ApiException(413, "El archivo es demasiado grande (máximo 25 MB).")
                out.write(buf, 0, n)
            }
            out.toByteArray()
        } ?: throw ApiException(0, "No se pudo leer el archivo seleccionado.")
        LocalFile(name, mime, bytes)
    }

    private fun guessMime(name: String): String = when (name.substringAfterLast('.', "").lowercase()) {
        "png" -> "image/png"
        "jpg", "jpeg" -> "image/jpeg"
        "bmp" -> "image/bmp"
        "webp" -> "image/webp"
        "gif" -> "image/gif"
        else -> "application/octet-stream"
    }

    /** Decodifica reduciendo para que el lado mayor no pase de [maxSide] px. */
    suspend fun decode(bytes: ByteArray, maxSide: Int = 1280): ImageBitmap? = withContext(Dispatchers.Default) {
        val bounds = BitmapFactory.Options().apply { inJustDecodeBounds = true }
        BitmapFactory.decodeByteArray(bytes, 0, bytes.size, bounds)
        val opts = BitmapFactory.Options().apply { inSampleSize = sampleSize(bounds, maxSide) }
        BitmapFactory.decodeByteArray(bytes, 0, bytes.size, opts)?.asImageBitmap()
    }

    suspend fun decode(context: Context, uri: Uri, maxSide: Int = 1280): ImageBitmap? = withContext(Dispatchers.IO) {
        runCatching {
            val resolver = context.contentResolver
            val bounds = BitmapFactory.Options().apply { inJustDecodeBounds = true }
            resolver.openInputStream(uri)?.use { BitmapFactory.decodeStream(it, null, bounds) }
            val opts = BitmapFactory.Options().apply {
                inSampleSize = sampleSize(bounds, maxSide)
                inPreferredConfig = Bitmap.Config.ARGB_8888
            }
            resolver.openInputStream(uri)?.use { BitmapFactory.decodeStream(it, null, opts) }?.asImageBitmap()
        }.getOrNull()
    }

    private fun sampleSize(bounds: BitmapFactory.Options, maxSide: Int): Int {
        var sample = 1
        val largest = maxOf(bounds.outWidth, bounds.outHeight)
        while (largest / (sample * 2) >= maxSide) sample *= 2
        return sample
    }

    /** Escribe [bytes] en el documento que el usuario eligió con CreateDocument. */
    suspend fun write(context: Context, uri: Uri, bytes: ByteArray) = withContext(Dispatchers.IO) {
        context.contentResolver.openOutputStream(uri, "wt")?.use { it.write(bytes) }
            ?: throw ApiException(0, "No se pudo escribir el archivo.")
    }

    /**
     * Comparte el PNG como archivo (content:// vía FileProvider). Se copia a cache/shared,
     * borrando antes lo compartido previamente para no acumular archivos.
     */
    suspend fun sharePng(context: Context, bytes: ByteArray, fileName: String) {
        val uri = withContext(Dispatchers.IO) {
            val dir = File(context.cacheDir, "shared").apply { mkdirs() }
            dir.listFiles()?.forEach { it.delete() }
            val file = File(dir, fileName)
            file.writeBytes(bytes)
            FileProvider.getUriForFile(context, "${context.packageName}.fileprovider", file)
        }
        val send = Intent(Intent.ACTION_SEND).apply {
            type = "image/png"
            putExtra(Intent.EXTRA_STREAM, uri)
            clipData = ClipData.newRawUri(fileName, uri)
            addFlags(Intent.FLAG_GRANT_READ_URI_PERMISSION)
        }
        val chooser = Intent.createChooser(send, "Compartir como archivo").apply {
            addFlags(Intent.FLAG_GRANT_READ_URI_PERMISSION)
        }
        context.startActivity(chooser)
    }
}
