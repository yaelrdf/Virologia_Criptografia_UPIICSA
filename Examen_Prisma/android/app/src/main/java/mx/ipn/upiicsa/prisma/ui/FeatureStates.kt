// Clases de estado observable (Compose) de cada pantalla de ABCO: formularios, indicadores de carga,
// resultados y errores. AppViewModel las crea y las modifica; las pantallas solo las leen y editan campos.
package mx.ipn.upiicsa.prisma.ui

import android.net.Uri
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableIntStateOf
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.setValue
import androidx.compose.ui.graphics.ImageBitmap
import mx.ipn.upiicsa.prisma.data.AccountInfo
import mx.ipn.upiicsa.prisma.data.AdminUser
import mx.ipn.upiicsa.prisma.data.AuditEntry
import mx.ipn.upiicsa.prisma.data.CapacityResponse
import mx.ipn.upiicsa.prisma.data.CompareResponse
import mx.ipn.upiicsa.prisma.data.CrackResponse
import mx.ipn.upiicsa.prisma.data.EmbedResult
import mx.ipn.upiicsa.prisma.data.ExtractResponse
import mx.ipn.upiicsa.prisma.data.ImageSource
import mx.ipn.upiicsa.prisma.data.StegoMode
import mx.ipn.upiicsa.prisma.data.TraceResponse

// Estado observable de cada pantalla. Vive en AppViewModel para sobrevivir a rotaciones
// y se reinicia al cerrar sesión (no deja mensajes ni llaves en memoria).
// Cada propiedad es un `mutableStateOf` independiente: al cambiar una solo se recomponen los Composables
// que la leen, no la pantalla entera.

/**
 * Formulario de inicio de sesión y registro.
 *
 * @property registering el formulario está en modo «Crear cuenta» (muestra la confirmación).
 * @property keyUri documento de la imagen-llave elegido; sus bytes se leen hasta enviar.
 * @property keyPreview miniatura de la imagen-llave (solo para mostrar).
 */
class LoginState {
    var username by mutableStateOf("")
    var password by mutableStateOf("")
    var confirm by mutableStateOf("")
    var registering by mutableStateOf(false)
    /** Paso 2 del login: el servidor pidió la imagen-llave. */
    var awaitingKey by mutableStateOf(false)
    var keyUri by mutableStateOf<Uri?>(null)
    var keyName by mutableStateOf("")
    var keyPreview by mutableStateOf<ImageBitmap?>(null)
    var loading by mutableStateOf(false)
    var error by mutableStateOf<String?>(null)
}

/**
 * Imagen elegida (catálogo o archivo del teléfono) con su vista previa.
 *
 * @property preview miniatura decodificada; null mientras se decodifica o si no es una imagen válida.
 */
class PickedImage {
    var source by mutableStateOf<ImageSource?>(null)
    var preview by mutableStateOf<ImageBitmap?>(null)
}

/**
 * Pantalla Ocultar.
 *
 * @property capacity capacidad de la portadora elegida (ABCO y clásica), para avisar si el mensaje no cabe.
 * @property result PNG con el mensaje oculto y sus métricas; sus bytes son los que se guardan o comparten.
 * @property resultPreview vista previa del resultado, decodificada aparte.
 */
class HideState {
    val image = PickedImage()
    var message by mutableStateOf("")
    var passphrase by mutableStateOf("")
    var mode by mutableStateOf(StegoMode.PRISMA)
    var capacity by mutableStateOf<CapacityResponse?>(null)
    var capacityLoading by mutableStateOf(false)
    var loading by mutableStateOf(false)
    var result by mutableStateOf<EmbedResult?>(null)
    var resultPreview by mutableStateOf<ImageBitmap?>(null)
    var error by mutableStateOf<String?>(null)
}

/**
 * Pantalla Revelar.
 *
 * @property imageUri documento de la imagen estego elegido con el selector de documentos.
 * @property rawHex en un 422, los bytes leídos en orden secuencial (lo que vería un atacante sin la frase).
 */
class RevealState {
    var imageUri by mutableStateOf<Uri?>(null)
    var imageName by mutableStateOf("")
    var preview by mutableStateOf<ImageBitmap?>(null)
    var mode by mutableStateOf(StegoMode.PRISMA)
    var passphrase by mutableStateOf("")
    var loading by mutableStateOf(false)
    var result by mutableStateOf<ExtractResponse?>(null)
    var error by mutableStateOf<String?>(null)
    var rawHex by mutableStateOf<String?>(null)
}

/**
 * Pantalla César.
 *
 * @property enye usa el alfabeto español de 27 letras (con Ñ).
 * @property output texto cifrado o descifrado; null cuando lo último fue romper.
 * @property crack candidatos de romper por fuerza bruta; null cuando lo último fue cifrar o descifrar.
 */
class CaesarState {
    var text by mutableStateOf("")
    var key by mutableIntStateOf(3)
    var enye by mutableStateOf(false)
    var loading by mutableStateOf(false)
    var output by mutableStateOf<String?>(null)
    var outputLabel by mutableStateOf("")
    var alphabet by mutableStateOf("")
    var crack by mutableStateOf<CrackResponse?>(null)
    var error by mutableStateOf<String?>(null)

    /** Clave máxima válida: tamaño del alfabeto menos 1 (26 con Ñ, 25 sin ella). */
    val maxKey: Int get() = if (enye) 26 else 25
}

/** Pantalla Comparador: mensaje, frase de ABCO, clave de César y resultado de los 3 algoritmos. */
class CompareState {
    var message by mutableStateOf("")
    var passphrase by mutableStateOf("")
    var caesarKey by mutableIntStateOf(3)
    var loading by mutableStateOf(false)
    var result by mutableStateOf<CompareResponse?>(null)
    var error by mutableStateOf<String?>(null)
}

/**
 * Pantalla Cuenta: perfil, activar/desactivar 2FA y cambio de contraseña.
 *
 * @property keyPng imagen-llave recién generada (bytes exactos para guardar o compartir); null si no hay.
 */
class AccountState {
    var info by mutableStateOf<AccountInfo?>(null)
    var loadingInfo by mutableStateOf(false)

    // Activar 2FA
    val keyImage = PickedImage()
    var enablePassword by mutableStateOf("")
    var enabling by mutableStateOf(false)
    var keyPng by mutableStateOf<ByteArray?>(null)
    var keyPreview by mutableStateOf<ImageBitmap?>(null)

    // Desactivar 2FA
    var disablePassword by mutableStateOf("")
    var disabling by mutableStateOf(false)

    // Cambiar contraseña
    val password = PasswordChangeState()

    var error by mutableStateOf<String?>(null)
}

/** Formulario de cambio de contraseña (en Cuenta y en la pantalla obligatoria). */
class PasswordChangeState {
    var current by mutableStateOf("")
    var newPassword by mutableStateOf("")
    var confirm by mutableStateOf("")
    var loading by mutableStateOf(false)
    var error by mutableStateOf<String?>(null)
    /** true si el último cambio desactivó el 2FA (hay que generar otra imagen-llave). */
    var twoFactorDisabled by mutableStateOf(false)
}

/** Pantalla de administración. */
    // `loaded` distingue «todavía no se ha cargado» de «se cargó y no hay datos».
class AdminState {
    var users by mutableStateOf<List<AdminUser>>(emptyList())
    var audit by mutableStateOf<List<AuditEntry>>(emptyList())
    var trace by mutableStateOf<TraceResponse?>(null)
    var diffMap by mutableStateOf<ImageBitmap?>(null)
    var loaded by mutableStateOf(false)
    var loading by mutableStateOf(false)
    var error by mutableStateOf<String?>(null)

    /** Nombres revelados por id de usuario (se alternan con el ofuscado). */
    var revealed by mutableStateOf<Map<Long, String>>(emptyMap())
    /** Ids con una acción en curso (deshabilita sus botones). */
    var busy by mutableStateOf<Set<Long>>(emptySet())

    /**
     * Contraseña temporal recién generada: se muestra una sola vez.
     * Par (nombre visible del usuario, contraseña temporal); null cuando el diálogo está cerrado.
     */
    var temporaryPassword by mutableStateOf<Pair<String, String>?>(null)
}

/**
 * Estado del diálogo de servidor ("Probar conexión").
 *
 * @property ok true si respondió bien, false si falló, null si aún no se prueba.
 */
class ServerTestState {
    var testing by mutableStateOf(false)
    var ok by mutableStateOf<Boolean?>(null)
    var message by mutableStateOf<String?>(null)
}
