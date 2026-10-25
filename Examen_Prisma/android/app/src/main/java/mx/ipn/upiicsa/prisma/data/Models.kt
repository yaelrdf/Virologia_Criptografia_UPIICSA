package mx.ipn.upiicsa.prisma.data

import kotlinx.serialization.Serializable

// Modelos del contrato de la API (docs/API.md). JSON en camelCase.

@Serializable
data class HealthResponse(val status: String = "", val service: String = "", val time: String = "")

@Serializable
data class Credentials(val username: String, val password: String)

@Serializable
data class LoginResponse(
    val token: String? = null,
    val username: String? = null,
    val role: String? = null,
    val twoFactorEnabled: Boolean = false,
    val mustChangePassword: Boolean = false,
    val requires2fa: Boolean = false,
)

@Serializable
data class RegisterResponse(val id: Long = 0, val username: String = "")

@Serializable
data class AccountInfo(
    val id: Long = 0,
    val username: String = "",
    val role: String = "",
    val twoFactorEnabled: Boolean = false,
    val mustChangePassword: Boolean = false,
    val createdUtc: String = "",
)

@Serializable
data class PasswordBody(val password: String)

@Serializable
data class ChangePasswordRequest(val currentPassword: String, val newPassword: String)

/** Sesión que devuelve el servidor tras cambiar la contraseña. */
@Serializable
data class SessionBody(
    val token: String = "",
    val username: String = "",
    val role: String = "user",
    val twoFactorEnabled: Boolean = false,
    val mustChangePassword: Boolean = false,
)

@Serializable
data class ChangePasswordResponse(val twoFactorDisabled: Boolean = false, val session: SessionBody = SessionBody())

// ---- Administración ----

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

@Serializable
data class TextBody(val text: String)

@Serializable
data class RoleBody(val role: String)

@Serializable
data class RoleResponse(val id: Long = 0, val role: String = "")

@Serializable
data class TemporaryPasswordResponse(val temporaryPassword: String = "")

/** Traza de la última operación ABCO; [text] es la versión legible paso a paso. */
@Serializable
data class TraceResponse(
    val user: String? = null,
    val trace: kotlinx.serialization.json.JsonElement? = null,
    val text: String = "",
)

@Serializable
data class CatalogItem(
    val id: String,
    val name: String,
    val width: Int = 0,
    val height: Int = 0,
    val prismaCapacity: Int = 0,
)

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

@Serializable
data class ExtractResponse(val mode: String = "", val message: String = "", val bytes: Int = 0)

@Serializable
data class ErrorBody(val error: String? = null, val rawSequentialHex: String? = null)

@Serializable
data class CaesarRequest(val text: String, val key: Int, val enye: Boolean)

@Serializable
data class CaesarResponse(val result: String = "", val alphabet: String = "")

@Serializable
data class CrackRequest(val text: String, val enye: Boolean)

@Serializable
data class CrackCandidate(val key: Int = 0, val chiSquare: Double = 0.0, val preview: String = "")

@Serializable
data class CrackResponse(
    val best: CrackCandidate = CrackCandidate(),
    val candidates: List<CrackCandidate> = emptyList(),
    val ms: Double = 0.0,
)

@Serializable
data class CompareRequest(val message: String, val passphrase: String, val caesarKey: Int)

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

@Serializable
data class CompareResponse(
    val plainBytes: Int = 0,
    val plainEntropy: Double = 0.0,
    val results: List<CompareResult> = emptyList(),
)

/** Modo de ocultamiento: valor que espera el servidor en el campo `mode`. */
enum class StegoMode(val apiValue: String, val label: String) {
    PRISMA("prisma", "ABCO"),
    CLASSIC("clasico", "Esteganografía clásica"),
}

/** Resultado de /api/stego/embed: PNG + cabeceras X-Prisma-*. */
class EmbedResult(
    val png: ByteArray,
    val psnr: Double?,
    val payloadBytes: Int?,
    val capacity: Int?,
    val resized: Boolean,
)

/** Archivo local leído desde un content:// URI, listo para subirse. */
class LocalFile(val name: String, val mimeType: String, val bytes: ByteArray)

/** Origen de una imagen: del catálogo del servidor o un archivo local. */
sealed interface ImageSource {
    data class Catalog(val item: CatalogItem) : ImageSource
    data class Local(val uri: android.net.Uri, val displayName: String) : ImageSource
}

/** Sesión activa; el token vive solo en memoria. */
data class Session(
    val token: String,
    val username: String,
    val role: String,
    val twoFactorEnabled: Boolean,
    /** El administrador restableció la contraseña: solo se permite cambiarla. */
    val mustChangePassword: Boolean = false,
)
