// Modelos de datos de la app ABCO: DTO serializables del contrato REST y tipos propios del cliente
// (sesión, archivos locales, origen de imagen, modo de ocultamiento).
package mx.ipn.upiicsa.prisma.data

import kotlinx.serialization.Serializable

// Modelos del contrato de la API (docs/API.md). JSON en camelCase.
// Todos los campos de respuesta tienen valor por defecto para que un campo ausente no rompa la decodificación.

/** Respuesta de `GET /api/health`. */
@Serializable
data class HealthResponse(val status: String = "", val service: String = "", val time: String = "")

/** Cuerpo de `/api/auth/login` y `/api/auth/register`. */
@Serializable
data class Credentials(val username: String, val password: String)

/**
 * Respuesta de `/api/auth/login` y `/api/auth/login/2fa`.
 *
 * @property token null cuando [requires2fa] es true: falta enviar la imagen-llave.
 * @property mustChangePassword el administrador restableció la contraseña; el token solo sirve para
 *   consultar el perfil y cambiarla (las demás rutas responden 403).
 * @property requires2fa la cuenta tiene doble factor y hay que repetir el login con la imagen-llave.
 */
@Serializable
data class LoginResponse(
    val token: String? = null,
    val username: String? = null,
    val role: String? = null,
    val twoFactorEnabled: Boolean = false,
    val mustChangePassword: Boolean = false,
    val requires2fa: Boolean = false,
)

/** Respuesta 201 de `/api/auth/register`. */
@Serializable
data class RegisterResponse(val id: Long = 0, val username: String = "")

/** Perfil del usuario (`GET /api/account/me`). [createdUtc] es una fecha ISO 8601 en UTC. */
@Serializable
data class AccountInfo(
    val id: Long = 0,
    val username: String = "",
    val role: String = "",
    val twoFactorEnabled: Boolean = false,
    val mustChangePassword: Boolean = false,
    val createdUtc: String = "",
)

/** Cuerpo con solo la contraseña (p. ej. desactivar 2FA). */
@Serializable
data class PasswordBody(val password: String)

/** Cuerpo de `POST /api/account/password`. */
@Serializable
data class ChangePasswordRequest(val currentPassword: String, val newPassword: String)

/**
 * Sesión que devuelve el servidor tras cambiar la contraseña. Su [token] reemplaza al anterior, que ya
 * no sirve; [mustChangePassword] llega en false.
 */
@Serializable
data class SessionBody(
    val token: String = "",
    val username: String = "",
    val role: String = "user",
    val twoFactorEnabled: Boolean = false,
    val mustChangePassword: Boolean = false,
)

/**
 * Respuesta de cambiar la contraseña.
 *
 * @property twoFactorDisabled había 2FA y se desactivó, porque la imagen-llave dependía de la contraseña
 *   anterior; hay que generar otra.
 */
@Serializable
data class ChangePasswordResponse(val twoFactorDisabled: Boolean = false, val session: SessionBody = SessionBody())

// ---- Administración ----

/** Usuario en la lista de administración. */
@Serializable
data class AdminUser(
    val id: Long = 0,
    /** Ofuscado con César por el servidor; se revela con /api/admin/deobfuscate. */
    val username: String = "",
    val role: String = "user",
    val twoFactorEnabled: Boolean = false,
    val mustChangePassword: Boolean = false,
    val createdUtc: String = "",
)

/**
 * Entrada del registro de auditoría.
 *
 * @property user nombre ofuscado con César por el servidor, o null en operaciones anónimas.
 * @property mode modo de esteganografía de la operación, si aplica.
 * @property utc fecha ISO 8601 en UTC; la UI la muestra en hora local.
 */
@Serializable
data class AuditEntry(
    val id: Long = 0,
    val user: String? = null,
    val action: String = "",
    val mode: String? = null,
    val bytes: Long = 0,
    val ms: Double = 0.0,
    val ok: Boolean = false,
    val utc: String = "",
)

/** Cuerpo y respuesta de `/api/admin/deobfuscate`. */
@Serializable
data class TextBody(val text: String)

/** Cuerpo para cambiar el rol: "admin" o "user". */
@Serializable
data class RoleBody(val role: String)

/** Respuesta de cambiar el rol. */
@Serializable
data class RoleResponse(val id: Long = 0, val role: String = "")

/** Contraseña temporal generada al restablecer; el servidor la entrega una sola vez. */
@Serializable
data class TemporaryPasswordResponse(val temporaryPassword: String = "")

/**
 * Traza de la última operación ABCO; [text] es la versión legible paso a paso.
 *
 * @property trace la misma traza como JSON genérico (no se modela campo por campo).
 */
@Serializable
data class TraceResponse(
    val user: String? = null,
    val trace: kotlinx.serialization.json.JsonElement? = null,
    val text: String = "",
)

/**
 * Imagen portadora del catálogo del servidor.
 *
 * @property prismaCapacity bytes que caben en modo ABCO (nombre del campo heredado de PRISMA-L).
 */
@Serializable
data class CatalogItem(
    val id: String,
    val name: String,
    val width: Int = 0,
    val height: Int = 0,
    val prismaCapacity: Int = 0,
)

/**
 * Capacidad de una portadora (`/api/stego/capacity`).
 *
 * @property resized el servidor reduce las imágenes de más de 1280 px por lado; las dimensiones
 *   `width`/`height` son las ya reducidas y `original*` las del archivo.
 * @property prismaCapacity bytes que caben en modo ABCO.
 * @property classicCapacity bytes que caben con esteganografía clásica (bit menos significativo en orden).
 */
@Serializable
data class CapacityResponse(
    val originalWidth: Int = 0,
    val originalHeight: Int = 0,
    val width: Int = 0,
    val height: Int = 0,
    val resized: Boolean = false,
    val prismaCapacity: Int = 0,
    val classicCapacity: Int = 0,
)

/** Mensaje revelado por `/api/stego/extract`. */
@Serializable
data class ExtractResponse(val mode: String = "", val message: String = "", val bytes: Int = 0)

/** Cuerpo de error `{"error": ...}`; en un 422 de revelar incluye también [rawSequentialHex]. */
@Serializable
data class ErrorBody(val error: String? = null, val rawSequentialHex: String? = null)

/** Cuerpo de cifrar/descifrar con César; [enye] activa el alfabeto de 27 letras con Ñ. */
@Serializable
data class CaesarRequest(val text: String, val key: Int, val enye: Boolean)

/** Texto resultante y alfabeto usado. */
@Serializable
data class CaesarResponse(val result: String = "", val alphabet: String = "")

/** Cuerpo de romper César por fuerza bruta. */
@Serializable
data class CrackRequest(val text: String, val enye: Boolean)

/** Candidato de llave: menor [chiSquare] (χ² frente a las frecuencias del español) = más probable. */
@Serializable
data class CrackCandidate(val key: Int = 0, val chiSquare: Double = 0.0, val preview: String = "")

/** Resultado de romper César: el mejor candidato, todos los candidatos y el tiempo en ms. */
@Serializable
data class CrackResponse(
    val best: CrackCandidate = CrackCandidate(),
    val candidates: List<CrackCandidate> = emptyList(),
    val ms: Double = 0.0,
)

/** Cuerpo de `/api/compare`. */
@Serializable
data class CompareRequest(val message: String, val passphrase: String, val caesarKey: Int)

/**
 * Métricas de un algoritmo en el comparador.
 *
 * @property entropy entropía de Shannon de la salida, en bits por byte.
 * @property detectsTampering si el algoritmo detecta que la salida fue alterada.
 */
@Serializable
data class CompareResult(
    val algorithm: String = "",
    val kind: String = "",
    val outputHex: String = "",
    val outputBytes: Int = 0,
    val ms: Double = 0.0,
    val entropy: Double = 0.0,
    val detectsTampering: Boolean = false,
    val notes: String = "",
)

/** Respuesta del comparador: datos del texto plano y 3 resultados (César, esteganografía clásica y ABCO). */
@Serializable
data class CompareResponse(
    val plainBytes: Int = 0,
    val plainEntropy: Double = 0.0,
    val results: List<CompareResult> = emptyList(),
)

/**
 * Modo de ocultamiento: valor que espera el servidor en el campo `mode`.
 *
 * @property apiValue valor enviado al servidor.
 * @property label texto visible.
 */
enum class StegoMode(val apiValue: String, val label: String) {
    /** Modo ABCO (con frase). El nombre `PRISMA` y el valor "prisma" se conservan del nombre anterior. */
    PRISMA("prisma", "ABCO"),
    /** Esteganografía clásica: bits menos significativos en orden, sin clave. */
    CLASSIC("clasico", "Esteganografía clásica"),
}

/**
 * Resultado de /api/stego/embed: PNG + cabeceras X-Prisma-*.
 *
 * @property psnr relación señal a ruido pico en dB (más alto = menos diferencia visible); null si falta.
 * @property payloadBytes bytes ocultos, incluido el encabezado del formato.
 * @property capacity capacidad total de la portadora en bytes.
 * @property resized el servidor redujo la portadora antes de ocultar.
 */
class EmbedResult(
    val png: ByteArray,
    val psnr: Double?,
    val payloadBytes: Int?,
    val capacity: Int?,
    val resized: Boolean,
)

/**
 * Archivo local leído desde un content:// URI, listo para subirse.
 *
 * @property name nombre visible del archivo (se envía como nombre de la parte multipart).
 * @property mimeType tipo MIME informado por el proveedor del documento.
 * @property bytes contenido íntegro, sin recomprimir (necesario para no destruir el mensaje oculto).
 */
class LocalFile(val name: String, val mimeType: String, val bytes: ByteArray)

/** Origen de una imagen: del catálogo del servidor o un archivo local. */
sealed interface ImageSource {
    /** Imagen del catálogo del servidor. */
    data class Catalog(val item: CatalogItem) : ImageSource
    /** Archivo del teléfono elegido con el selector de documentos; se lee al enviar. */
    data class Local(val uri: android.net.Uri, val displayName: String) : ImageSource
}

/**
 * Sesión activa; el token vive solo en memoria. Cerrar la app equivale a cerrar la sesión.
 *
 * @property role "admin" habilita la pantalla de administración.
 */
data class Session(
    val token: String,
    val username: String,
    val role: String,
    val twoFactorEnabled: Boolean,
    /** El administrador restableció la contraseña: solo se permite cambiarla. */
    val mustChangePassword: Boolean = false,
)
