package mx.ipn.upiicsa.prisma.data

import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.Job
import kotlinx.coroutines.currentCoroutineContext
import kotlinx.coroutines.withContext
import kotlinx.serialization.json.Json
import okhttp3.HttpUrl.Companion.toHttpUrlOrNull
import okhttp3.MediaType.Companion.toMediaType
import okhttp3.MultipartBody
import okhttp3.OkHttpClient
import okhttp3.Request
import okhttp3.RequestBody
import okhttp3.RequestBody.Companion.toRequestBody
import okhttp3.Response
import java.io.IOException
import java.net.SocketTimeoutException
import java.util.concurrent.TimeUnit

/** Error de la API con el mensaje (en español) que se muestra al usuario. */
open class ApiException(
    val code: Int,
    message: String,
    val rawSequentialHex: String? = null,
) : Exception(message)

/** 401 en una ruta protegida sin cuerpo de error: el token ya no es válido. */
class SessionExpiredException : ApiException(401, "Tu sesión expiró. Vuelve a iniciar sesión.")

/**
 * Cliente HTTP delgado (OkHttp + kotlinx.serialization). Todas las llamadas son
 * funciones suspend que corren en Dispatchers.IO; el servidor hace toda la criptografía.
 */
class ApiClient(
    private val baseUrl: () -> String,
    private val token: () -> String?,
) {
    private val http = OkHttpClient.Builder()
        .connectTimeout(15, TimeUnit.SECONDS)
        .readTimeout(90, TimeUnit.SECONDS)
        .writeTimeout(90, TimeUnit.SECONDS)
        .build()

    // ---- Salud y autenticación ----

    suspend fun health(base: String = baseUrl()): HealthResponse =
        getJson("/api/health", auth = false, base = base)

    suspend fun login(username: String, password: String): LoginResponse =
        postJson("/api/auth/login", Credentials(username, password), auth = false)

    suspend fun login2fa(username: String, password: String, keyImage: LocalFile): LoginResponse {
        val body = multipart {
            addFormDataPart("username", username)
            addFormDataPart("password", password)
            addFile("keyImage", keyImage)
        }
        return send("/api/auth/login/2fa", auth = false) { post(body) }.decode()
    }

    suspend fun register(username: String, password: String): RegisterResponse =
        postJson("/api/auth/register", Credentials(username, password), auth = false)

    // ---- Cuenta ----

    suspend fun me(): AccountInfo = getJson("/api/account/me")

    /** Devuelve el PNG de la imagen-llave. */
    suspend fun enable2fa(password: String, source: UploadSource): ByteArray {
        val body = multipart {
            addFormDataPart("password", password)
            addSource("image", source)
        }
        return send("/api/account/2fa/enable") { post(body) }.bytes
    }

    suspend fun disable2fa(password: String) {
        send("/api/account/2fa/disable") { post(jsonBody(PasswordBody(password))) }
    }

    // ---- Catálogo (público) ----

    suspend fun catalog(): List<CatalogItem> = getJson("/api/catalog", auth = false)

    suspend fun catalogImage(id: String): ByteArray =
        send("/api/catalog/$id", auth = false) { get() }.bytes

    // ---- Esteganografía ----

    suspend fun capacity(source: UploadSource): CapacityResponse {
        val body = multipart { addSource("image", source) }
        return send("/api/stego/capacity") { post(body) }.decode()
    }

    suspend fun embed(source: UploadSource, message: String, passphrase: String, mode: StegoMode): EmbedResult {
        val body = multipart {
            addSource("image", source)
            addFormDataPart("message", message)
            addFormDataPart("passphrase", passphrase)
            addFormDataPart("mode", mode.apiValue)
        }
        val raw = send("/api/stego/embed") { post(body) }
        return EmbedResult(
            png = raw.bytes,
            psnr = raw.headers["X-Prisma-Psnr"]?.toDoubleOrNull(),
            payloadBytes = raw.headers["X-Prisma-Payload-Bytes"]?.toIntOrNull(),
            capacity = raw.headers["X-Prisma-Capacity"]?.toIntOrNull(),
            resized = raw.headers["X-Prisma-Resized"].equals("true", ignoreCase = true),
        )
    }

    suspend fun extract(image: LocalFile, passphrase: String, mode: StegoMode): ExtractResponse {
        val body = multipart {
            addFile("image", image)
            addFormDataPart("passphrase", passphrase)
            addFormDataPart("mode", mode.apiValue)
        }
        return send("/api/stego/extract") { post(body) }.decode()
    }

    // ---- César y comparador ----

    suspend fun caesarEncrypt(text: String, key: Int, enye: Boolean): CaesarResponse =
        postJson("/api/caesar/encrypt", CaesarRequest(text, key, enye))

    suspend fun caesarDecrypt(text: String, key: Int, enye: Boolean): CaesarResponse =
        postJson("/api/caesar/decrypt", CaesarRequest(text, key, enye))

    suspend fun caesarCrack(text: String, enye: Boolean): CrackResponse =
        postJson("/api/caesar/crack", CrackRequest(text, enye))

    suspend fun compare(message: String, passphrase: String, caesarKey: Int): CompareResponse =
        postJson("/api/compare", CompareRequest(message, passphrase, caesarKey))

    // ---- Infraestructura ----

    /** Imagen a subir: id del catálogo o archivo local. */
    sealed interface UploadSource {
        data class Catalog(val id: String) : UploadSource
        data class File(val file: LocalFile) : UploadSource
    }

    private class RawResponse(val bytes: ByteArray, val headers: okhttp3.Headers)

    private inline fun <reified T> RawResponse.decode(): T = try {
        json.decodeFromString<T>(bytes.decodeToString())
    } catch (e: Exception) {
        throw ApiException(0, "Respuesta inesperada del servidor.")
    }

    private suspend inline fun <reified T> getJson(path: String, auth: Boolean = true, base: String = baseUrl()): T =
        send(path, auth, base) { get() }.decode()

    private suspend inline fun <reified B, reified T> postJson(path: String, body: B, auth: Boolean = true): T =
        send(path, auth) { post(jsonBody(body)) }.decode()

    private inline fun <reified B> jsonBody(body: B): RequestBody =
        json.encodeToString(body).toRequestBody(JSON)

    private inline fun multipart(block: MultipartBody.Builder.() -> Unit): MultipartBody =
        MultipartBody.Builder().setType(MultipartBody.FORM).apply(block).build()

    private fun MultipartBody.Builder.addFile(field: String, file: LocalFile) {
        addFormDataPart(field, file.name, file.bytes.toRequestBody(file.mimeType.toMediaType()))
    }

    private fun MultipartBody.Builder.addSource(fileField: String, source: UploadSource) {
        when (source) {
            is UploadSource.Catalog -> addFormDataPart("catalogId", source.id)
            is UploadSource.File -> addFile(fileField, source.file)
        }
    }

    private suspend fun send(
        path: String,
        auth: Boolean = true,
        base: String = baseUrl(),
        configure: Request.Builder.() -> Request.Builder,
    ): RawResponse = withContext(Dispatchers.IO) {
        val root = base.trim().trimEnd('/')
        val url = "$root$path".toHttpUrlOrNull()
            ?: throw ApiException(0, "La URL del servidor no es válida: $base")
        val builder = Request.Builder().url(url).header("Accept", "application/json, image/png")
        if (auth) token()?.let { builder.header("Authorization", "Bearer $it") }
        val call = http.newCall(builder.configure().build())
        // Si la corrutina se cancela (p. ej. se cierra la pantalla), cancela la petición.
        val cancelHandle = currentCoroutineContext()[Job]?.invokeOnCompletion { if (it != null) call.cancel() }
        try {
            call.execute().use { response -> toRaw(response, auth) }
        } catch (e: ApiException) {
            throw e
        } catch (e: SocketTimeoutException) {
            throw ApiException(0, "El servidor tardó demasiado en responder ($root).")
        } catch (e: IOException) {
            if (call.isCanceled()) throw CancellationException("Petición cancelada")
            throw ApiException(0, "No se pudo conectar con el servidor ($root). Revisa tu conexión o la URL en ajustes.")
        } finally {
            cancelHandle?.dispose()
        }
    }

    private fun toRaw(response: Response, auth: Boolean): RawResponse {
        val bytes = response.body.bytes()
        if (response.isSuccessful) return RawResponse(bytes, response.headers)

        val err = runCatching { json.decodeFromString<ErrorBody>(bytes.decodeToString()) }.getOrNull()
        val serverMsg = err?.error?.takeIf { it.isNotBlank() }
        val code = response.code
        if (code == 401 && auth && serverMsg == null) throw SessionExpiredException()
        val msg = serverMsg ?: when (code) {
            400 -> "Solicitud inválida."
            401 -> "Credenciales incorrectas."
            403 -> "No tienes permiso para esta operación."
            404 -> "No se encontró el recurso solicitado."
            409 -> "El recurso ya existe."
            413 -> "El mensaje no cabe en la imagen o el archivo es demasiado grande."
            415 -> "Formato de archivo no admitido."
            422 -> "No se pudo procesar la imagen."
            429 -> "Demasiados intentos. Espera un minuto e inténtalo de nuevo."
            in 500..599 -> "Error interno del servidor ($code)."
            else -> "Error inesperado ($code)."
        }
        // En 429 se agrega una guía aunque el servidor no la incluya.
        val finalMsg = if (code == 429 && serverMsg != null) "$serverMsg (espera un minuto)" else msg
        throw ApiException(code, finalMsg, err?.rawSequentialHex)
    }

    companion object {
        private val JSON = "application/json; charset=utf-8".toMediaType()
        val json = Json {
            ignoreUnknownKeys = true
            explicitNulls = false
            coerceInputValues = true
        }
    }
}
