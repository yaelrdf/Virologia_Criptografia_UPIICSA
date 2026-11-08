// Manejo de archivos de ABCO: lectura de documentos (content://), decodificación de vistas previas,
// guardado en el documento que elige el usuario y compartir PNG mediante FileProvider.
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

/**
 * Utilidades de archivos: lectura de content:// URIs, decodificación, guardado y compartir.
 *
 * Los bytes de una imagen estego se manejan siempre íntegros: cualquier recompresión o conversión
 * (la que hacen algunos selectores de fotos o apps de mensajería) destruiría el mensaje oculto.
 */
object Files {
    /** Límite de lectura local; el servidor rechaza archivos más grandes de todos modos. */
    private const val MAX_BYTES = 25L * 1024 * 1024

    /**
     * Nombre visible del documento según su proveedor (`OpenableColumns.DISPLAY_NAME`).
     *
     * @return el nombre, el último segmento del URI si el proveedor no lo da, o "imagen".
     */
    fun displayName(context: Context, uri: Uri): String {
        context.contentResolver.query(uri, arrayOf(OpenableColumns.DISPLAY_NAME), null, null, null)?.use { c ->
            if (c.moveToFirst()) {
                val idx = c.getColumnIndex(OpenableColumns.DISPLAY_NAME)
                if (idx >= 0) c.getString(idx)?.let { return it }
            }
        }
        return uri.lastPathSegment?.substringAfterLast('/') ?: "imagen"
    }

    /**
     * Lee el documento completo para subirlo. Corre en Dispatchers.IO porque el proveedor puede ser lento
     * (almacenamiento en la nube, tarjeta SD) y la lectura es bloqueante.
     *
     * @throws ApiException 413 si pasa de 25 MB; 0 si el proveedor no permite abrirlo.
     */
    suspend fun read(context: Context, uri: Uri): LocalFile = withContext(Dispatchers.IO) {
        val resolver = context.contentResolver
        val name = displayName(context, uri)
        val mime = resolver.getType(uri) ?: guessMime(name)
        val bytes = resolver.openInputStream(uri)?.use { input ->
            val out = java.io.ByteArrayOutputStream()
            val buf = ByteArray(64 * 1024)
            var total = 0L
            // Se lee por bloques y se corta en cuanto pasa del límite, sin cargar en memoria un archivo enorme.
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

    /** Tipo MIME por extensión, para proveedores que no informan el tipo. */
    private fun guessMime(name: String): String = when (name.substringAfterLast('.', "").lowercase()) {
        "png" -> "image/png"
        "jpg", "jpeg" -> "image/jpeg"
        "bmp" -> "image/bmp"
        "webp" -> "image/webp"
        "gif" -> "image/gif"
        else -> "application/octet-stream"
    }

    /**
     * Decodifica reduciendo para que el lado mayor no pase de [maxSide] px. Solo se usa para mostrar
     * vistas previas: los bytes originales no se modifican.
     *
     * Usa Dispatchers.Default porque decodificar es trabajo de CPU, no de E/S.
     *
     * @return la imagen, o null si los bytes no son una imagen válida.
     */
    suspend fun decode(bytes: ByteArray, maxSide: Int = 1280): ImageBitmap? = withContext(Dispatchers.Default) {
        // Primera pasada: solo lee las dimensiones (inJustDecodeBounds) para calcular el submuestreo.
        val bounds = BitmapFactory.Options().apply { inJustDecodeBounds = true }
        BitmapFactory.decodeByteArray(bytes, 0, bytes.size, bounds)
        val opts = BitmapFactory.Options().apply { inSampleSize = sampleSize(bounds, maxSide) }
        BitmapFactory.decodeByteArray(bytes, 0, bytes.size, opts)?.asImageBitmap()
    }

    /**
     * Igual que [decode] con bytes, pero leyendo desde un URI. Abre el flujo dos veces (dimensiones y
     * decodificación) porque un InputStream de content:// no se puede rebobinar. Cualquier falla del
     * proveedor devuelve null en vez de propagarse.
     */
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

    /** Mayor potencia de 2 que deja el lado mayor en al menos [maxSide] px (requisito de inSampleSize). */
    private fun sampleSize(bounds: BitmapFactory.Options, maxSide: Int): Int {
        var sample = 1
        val largest = maxOf(bounds.outWidth, bounds.outHeight)
        while (largest / (sample * 2) >= maxSide) sample *= 2
        return sample
    }

    /**
     * Escribe [bytes] en el documento que el usuario eligió con CreateDocument. El modo "wt" trunca el
     * archivo: si el usuario elige uno existente, no quedan restos del contenido anterior.
     */
    suspend fun write(context: Context, uri: Uri, bytes: ByteArray) = withContext(Dispatchers.IO) {
        context.contentResolver.openOutputStream(uri, "wt")?.use { it.write(bytes) }
            ?: throw ApiException(0, "No se pudo escribir el archivo.")
    }

    /**
     * Comparte el PNG como archivo (content:// vía FileProvider). Se copia a cache/shared,
     * borrando antes lo compartido previamente para no acumular archivos.
     *
     * FileProvider expone solo esa carpeta (ver `res/xml`) con un URI temporal; la app receptora recibe
     * permiso de lectura únicamente para ese archivo. Se comparte como archivo y no como imagen de la
     * galería para que no se recomprima.
     *
     * @param fileName nombre con el que lo verá la app receptora.
     */
    suspend fun sharePng(context: Context, bytes: ByteArray, fileName: String) {
        val uri = withContext(Dispatchers.IO) {
            val dir = File(context.cacheDir, "shared").apply { mkdirs() }
            dir.listFiles()?.forEach { it.delete() }
            val file = File(dir, fileName)
            file.writeBytes(bytes)
            FileProvider.getUriForFile(context, "${context.packageName}.fileprovider", file)
        }
        // EXTRA_STREAM es lo que leen las apps; clipData con el mismo URI hace que el permiso de lectura
        // se conceda también al selector, que muestra una vista previa del archivo.
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
