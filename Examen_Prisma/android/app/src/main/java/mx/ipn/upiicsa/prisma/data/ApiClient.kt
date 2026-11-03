// Cliente HTTP de la API REST de ABCO (contrato en docs/API.md): una función suspend por ruta, más el
// manejo común de URL, token, multipart, cancelación y traducción de errores a mensajes en español.
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
import java.net.ConnectException
import java.net.NoRouteToHostException
import java.net.SocketTimeoutException
import java.net.UnknownHostException
import java.net.UnknownServiceException
import java.util.concurrent.TimeUnit

/**
 * Error de la API con el mensaje (en español) que se muestra al usuario.
 *
 * @property code código HTTP de la respuesta, o 0 si el error es local (red, URL inválida, JSON inesperado).
 * @property rawSequentialHex en un 422 de `/api/stego/extract`, los bytes leídos en orden secuencial (en hex):
 *   lo que vería un atacante sin la frase. Es null en los demás errores.
 */
open class ApiException(
    val code: Int,
    message: String,
    val rawSequentialHex: String? = null,
) : Exception(message)

/**
 * 401 en una ruta protegida sin cuerpo de error: el token ya no es válido.
 * El ViewModel la reconoce para cerrar la sesión; un 401 con mensaje del servidor (p. ej. contraseña
 * incorrecta al cambiarla) se queda como [ApiException] normal y no cierra la sesión.
 */
class SessionExpiredException : ApiException(401, "Tu sesión expiró. Vuelve a iniciar sesión.")

/**
 * Cliente HTTP delgado (OkHttp + kotlinx.serialization). Todas las llamadas son
 * funciones suspend que corren en Dispatchers.IO; el servidor hace toda la criptografía.
 *
 * @param baseUrl proveedor de la URL del servidor; se lee en cada petición para que un cambio en ajustes
 *   aplique de inmediato sin recrear el cliente.
 * @param token proveedor del token de sesión actual (null si no hay sesión).
 */
class ApiClient(
    private val baseUrl: () -> String,
    private val token: () -> String?,
) {
    /**
     * Cliente OkHttp único (reutiliza conexiones). Las lecturas y escrituras tienen 90 s porque subir una
     * imagen grande y ocultar o revelar el mensaje en el servidor puede tardar más que una petición JSON.
     */
    private val http = OkHttpClient.Builder()
        .connectTimeout(15, TimeUnit.SECONDS)
        .readTimeout(90, TimeUnit.SECONDS)
        .writeTimeout(90, TimeUnit.SECONDS)
        .build()

    // ---- Salud y autenticación ----

    /**
     * Comprueba que el servidor responde (`GET /api/health`).
     *
     * @param base URL a probar; por defecto la guardada. Ajustes la usa para probar una URL antes de guardarla.
     */
    suspend fun health(base: String = baseUrl()): HealthResponse =
        getJson("/api/health", auth = false, base = base)

    /**
     * Primer paso del login. Devuelve el token o, si la cuenta tiene doble factor, `requires2fa = true`
     * sin token.
     */
    suspend fun login(username: String, password: String): LoginResponse =
        postJson("/api/auth/login", Credentials(username, password), auth = false)

    /**
     * Segundo paso del login con 2FA: vuelve a enviar usuario y contraseña junto con la imagen-llave.
     *
     * @param keyImage PNG de la imagen-llave tal como se descargó (sin recomprimir).
     */
    suspend fun login2fa(username: String, password: String, keyImage: LocalFile): LoginResponse {
        val body = multipart {
            addFormDataPart("username", username)
            addFormDataPart("password", password)
            addFile("keyImage", keyImage)
        }
        return send("/api/auth/login/2fa", auth = false) { post(body) }.decode()
    }

    /** Registra una cuenta nueva (`POST /api/auth/register`). */
    suspend fun register(username: String, password: String): RegisterResponse =
        postJson("/api/auth/register", Credentials(username, password), auth = false)

    // ---- Cuenta ----

    /** Perfil del usuario actual; con contraseña temporal es una de las dos rutas permitidas. */
    suspend fun me(): AccountInfo = getJson("/api/account/me")

    /**
     * Cambia la contraseña; el cliente debe usar el token de `session` a partir de aquí.
     * Es la otra ruta permitida con contraseña temporal. Si había 2FA, el servidor lo desactiva.
     */
    suspend fun changePassword(currentPassword: String, newPassword: String): ChangePasswordResponse =
        postJson("/api/account/password", ChangePasswordRequest(currentPassword, newPassword))

    /**
     * Activa el doble factor y devuelve el PNG de la imagen-llave.
     *
     * @param source imagen del catálogo (`catalogId`) o archivo propio (campo `image`).
     * @return bytes PNG que el usuario debe guardar para iniciar sesión.
     */
    suspend fun enable2fa(password: String, source: UploadSource): ByteArray {
        val body = multipart {
            addFormDataPart("password", password)
            addSource("image", source)
        }
        return send("/api/account/2fa/enable") { post(body) }.bytes
    }

    /** Desactiva el doble factor; el servidor exige la contraseña actual. */
    suspend fun disable2fa(password: String) {
        send("/api/account/2fa/disable") { post(jsonBody(PasswordBody(password))) }
    }

    // ---- Catálogo (público) ----

    /** Lista de imágenes portadoras del catálogo del servidor. */
    suspend fun catalog(): List<CatalogItem> = getJson("/api/catalog", auth = false)

    /** PNG de una imagen del catálogo (para la vista previa). */
    suspend fun catalogImage(id: String): ByteArray =
        send("/api/catalog/$id", auth = false) { get() }.bytes

    // ---- Esteganografía ----

    /** Dimensiones y capacidad (modo ABCO y clásico) de una portadora, sin ocultar nada todavía. */
    suspend fun capacity(source: UploadSource): CapacityResponse {
        val body = multipart { addSource("image", source) }
        return send("/api/stego/capacity") { post(body) }.decode()
    }

    /**
     * Oculta [message] en la portadora y devuelve el PNG resultante con sus métricas.
     *
     * @param mode `StegoMode.PRISMA` (ABCO, requiere frase) o esteganografía clásica.
     * @return PNG y valores de las cabeceras `X-Prisma-*` (nombre heredado de PRISMA-L).
     * @throws ApiException 413 si el mensaje no cabe.
     */
    suspend fun embed(source: UploadSource, message: String, passphrase: String, mode: StegoMode): EmbedResult {
        val body = multipart {
            addSource("image", source)
            addFormDataPart("message", message)
            addFormDataPart("passphrase", passphrase)
            addFormDataPart("mode", mode.apiValue)
        }
        val raw = send("/api/stego/embed") { post(body) }
        // Las métricas llegan en cabeceras porque el cuerpo es el PNG binario.
        return EmbedResult(
            png = raw.bytes,
            psnr = raw.headers["X-Prisma-Psnr"]?.toDoubleOrNull(),
            payloadBytes = raw.headers["X-Prisma-Payload-Bytes"]?.toIntOrNull(),
            capacity = raw.headers["X-Prisma-Capacity"]?.toIntOrNull(),
            resized = raw.headers["X-Prisma-Resized"].equals("true", ignoreCase = true),
        )
    }

    /**
     * Revela el mensaje oculto en [image].
     *
     * @throws ApiException 422 con [ApiException.rawSequentialHex] si la frase o el modo no coinciden.
     */
    suspend fun extract(image: LocalFile, passphrase: String, mode: StegoMode): ExtractResponse {
        val body = multipart {
            addFile("image", image)
            addFormDataPart("passphrase", passphrase)
            addFormDataPart("mode", mode.apiValue)
        }
        return send("/api/stego/extract") { post(body) }.decode()
    }

    // ---- César y comparador ----

    /** Cifra con César; [enye] usa el alfabeto de 27 letras con Ñ. */
    suspend fun caesarEncrypt(text: String, key: Int, enye: Boolean): CaesarResponse =
        postJson("/api/caesar/encrypt", CaesarRequest(text, key, enye))

    /** Descifra con César usando la llave conocida. */
    suspend fun caesarDecrypt(text: String, key: Int, enye: Boolean): CaesarResponse =
        postJson("/api/caesar/decrypt", CaesarRequest(text, key, enye))

    /** Rompe César por fuerza bruta; el servidor ordena los candidatos por χ². */
    suspend fun caesarCrack(text: String, enye: Boolean): CrackResponse =
        postJson("/api/caesar/crack", CrackRequest(text, enye))

    /** Compara César, esteganografía clásica y ABCO sobre el mismo mensaje. */
    suspend fun compare(message: String, passphrase: String, caesarKey: Int): CompareResponse =
        postJson("/api/compare", CompareRequest(message, passphrase, caesarKey))

    // ---- Administración (rol admin) ----

    /** Usuarios registrados; el servidor devuelve el nombre ofuscado. */
    suspend fun adminUsers(): List<AdminUser> = getJson("/api/admin/users")

    /** Últimas [take] operaciones del registro de auditoría. */
    suspend fun adminAudit(take: Int = 50): List<AuditEntry> = getJson("/api/admin/audit?take=$take")

    /** Pide al servidor el texto original de un nombre ofuscado (botón «Revelar» de Admin). */
    suspend fun deobfuscate(text: String): String =
        postJson<TextBody, TextBody>("/api/admin/deobfuscate", TextBody(text)).text

    /** Quita el doble factor de otro usuario (p. ej. si perdió su imagen-llave). */
    suspend fun resetUser2fa(id: Long) {
        send("/api/admin/users/$id/reset-2fa") { post(EMPTY_BODY) }
    }

    /**
     * Restablece la contraseña de un usuario y devuelve la contraseña temporal (el servidor la muestra
     * una sola vez). El usuario deberá cambiarla al entrar.
     */
    suspend fun resetUserPassword(id: Long): String =
        send("/api/admin/users/$id/reset-password") { post(EMPTY_BODY) }.decode<TemporaryPasswordResponse>()
            .temporaryPassword

    /**
     * Hace o quita administrador a un usuario.
     *
     * @param role "admin" o "user".
     * @throws ApiException 409 con `error` si es uno mismo o el último administrador.
     */
    suspend fun setUserRole(id: Long, role: String): RoleResponse =
        postJson("/api/admin/users/$id/role", RoleBody(role))

    /** Traza paso a paso de la última operación ABCO; null si todavía no hay operaciones (404). */
    suspend fun lastTrace(): TraceResponse? = try {
        getJson<TraceResponse>("/api/admin/trace/last")
    } catch (e: ApiException) {
        if (e.code == 404) null else throw e
    }

    /** PNG del mapa de diferencias de la última operación; null si no existe (404). */
    suspend fun lastDiffMap(): ByteArray? = try {
        send("/api/admin/diffmap/last") { get() }.bytes
    } catch (e: ApiException) {
        if (e.code == 404) null else throw e
    }

    // ---- Infraestructura ----

    /** Imagen a subir: id del catálogo o archivo local. */
    sealed interface UploadSource {
        /** Imagen del catálogo del servidor; se envía solo su id en el campo `catalogId`. */
        data class Catalog(val id: String) : UploadSource
        /** Archivo elegido por el usuario; se sube como parte binaria del multipart. */
        data class File(val file: LocalFile) : UploadSource
    }

    /** Cuerpo crudo y cabeceras de una respuesta exitosa; el cuerpo ya se leyó y la respuesta está cerrada. */
    private class RawResponse(val bytes: ByteArray, val headers: okhttp3.Headers)

    /** Decodifica el cuerpo como JSON; cualquier falla se reporta como respuesta inesperada. */
    private inline fun <reified T> RawResponse.decode(): T = try {
        json.decodeFromString<T>(bytes.decodeToString())
    } catch (e: Exception) {
        throw ApiException(0, "Respuesta inesperada del servidor.")
    }

    /** GET que decodifica la respuesta JSON como [T]. */
    private suspend inline fun <reified T> getJson(path: String, auth: Boolean = true, base: String = baseUrl()): T =
        send(path, auth, base) { get() }.decode()

    /** POST con cuerpo JSON [B] que decodifica la respuesta como [T]. */
    private suspend inline fun <reified B, reified T> postJson(path: String, body: B, auth: Boolean = true): T =
        send(path, auth) { post(jsonBody(body)) }.decode()

    /** Serializa [body] como JSON UTF-8. */
    private inline fun <reified B> jsonBody(body: B): RequestBody =
        json.encodeToString(body).toRequestBody(JSON)

    /** Construye un cuerpo `multipart/form-data`. */
    private inline fun multipart(block: MultipartBody.Builder.() -> Unit): MultipartBody =
        MultipartBody.Builder().setType(MultipartBody.FORM).apply(block).build()

    /** Agrega un archivo con su nombre y tipo MIME. */
    private fun MultipartBody.Builder.addFile(field: String, file: LocalFile) {
        addFormDataPart(field, file.name, file.bytes.toRequestBody(file.mimeType.toMediaType()))
    }

    /** Agrega la portadora: `catalogId` si es del catálogo o el archivo en [fileField]. */
    private fun MultipartBody.Builder.addSource(fileField: String, source: UploadSource) {
        when (source) {
            is UploadSource.Catalog -> addFormDataPart("catalogId", source.id)
            is UploadSource.File -> addFile(fileField, source.file)
        }
    }

    /**
     * Ejecuta una petición y devuelve el cuerpo si fue exitosa.
     *
     * Corre en Dispatchers.IO porque `execute()` de OkHttp es bloqueante: en el hilo principal congelaría la
     * UI (y Android lanza NetworkOnMainThreadException). Las excepciones de red se traducen a mensajes
     * concretos para que el usuario sepa si falla el nombre, el puerto, la ruta o la política de red.
     *
     * @param auth si es true agrega `Authorization: Bearer <token>` cuando hay sesión.
     * @param base URL base; por defecto la configurada.
     * @param configure define el método y el cuerpo (`get()`, `post(...)`).
     * @throws ApiException con el mensaje para el usuario; [SessionExpiredException] si el token ya no vale.
     * @throws CancellationException si la corrutina se canceló durante la petición.
     */
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
        // Si la corrutina se cancela (p. ej. se cierra la pantalla), cancela la petición: `execute()` es
        // bloqueante y no reacciona sola a la cancelación de la corrutina.
        val cancelHandle = currentCoroutineContext()[Job]?.invokeOnCompletion { if (it != null) call.cancel() }
        try {
            call.execute().use { response -> toRaw(response, auth) }
        } catch (e: ApiException) {
            throw e
        } catch (e: SocketTimeoutException) {
            throw ApiException(0, "El servidor tardó demasiado en responder ($root).")
        } catch (e: UnknownHostException) {
            throw ApiException(0, "No se encontró el servidor «${url.host}». Revisa el nombre o usa su dirección IP.")
        } catch (e: ConnectException) {
            throw ApiException(0, "El servidor rechazó la conexión en ${url.host}:${url.port}. ¿Está encendido y escuchando en ese puerto?")
        } catch (e: NoRouteToHostException) {
            throw ApiException(0, "No hay ruta de red hacia ${url.host}. Revisa que el teléfono y el servidor se puedan comunicar.")
        } catch (e: UnknownServiceException) {
            // Política de seguridad de red de Android (p. ej. HTTP sin cifrar no permitido).
            throw ApiException(0, "Android bloqueó la conexión a $root: ${e.message}")
        } catch (e: IOException) {
            // Una petición cancelada a propósito también llega como IOException; se relanza como cancelación
            // para que no se muestre como error de red.
            if (call.isCanceled()) throw CancellationException("Petición cancelada")
            throw ApiException(0, "No se pudo conectar con el servidor ($root). Revisa tu conexión o la URL en ajustes.")
        } finally {
            // Libera el manejador para no acumular callbacks en el Job si la petición terminó normalmente.
            cancelHandle?.dispose()
        }
    }

    /**
     * Lee el cuerpo y, si el código no es 2xx, lanza [ApiException] con el mensaje del servidor
     * (`{"error": ...}`) o uno genérico según el código.
     *
     * @param auth si la ruta llevaba token; solo entonces un 401 sin mensaje significa sesión expirada.
     */
    private fun toRaw(response: Response, auth: Boolean): RawResponse {
        val bytes = response.body.bytes()
        if (response.isSuccessful) return RawResponse(bytes, response.headers)

        val err = runCatching { json.decodeFromString<ErrorBody>(bytes.decodeToString()) }.getOrNull()
        val serverMsg = err?.error?.takeIf { it.isNotBlank() }
        val code = response.code
        // 401 sin mensaje en una ruta con token = token caducado o inválido. Un 401 con mensaje (login,
        // contraseña actual incorrecta) se muestra tal cual sin cerrar la sesión.
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

    /** Tipo de contenido, cuerpo vacío y configuración de JSON compartidos. */
    companion object {
        private val JSON = "application/json; charset=utf-8".toMediaType()
        private val EMPTY_BODY = ByteArray(0).toRequestBody(null)
        /**
         * Configuración de JSON tolerante: ignora campos nuevos del servidor, omite nulls al serializar y usa
         * el valor por defecto si llega un null o un valor de enum desconocido.
         */
        val json = Json {
            ignoreUnknownKeys = true
            explicitNulls = false
            coerceInputValues = true
        }
    }
}
