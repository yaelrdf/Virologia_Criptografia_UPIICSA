package mx.ipn.upiicsa.signaltrace.ui

import android.app.Application
import android.util.Base64
import androidx.lifecycle.AndroidViewModel
import androidx.lifecycle.viewModelScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.update
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext
import java.net.URI
import java.net.URLDecoder
import mx.ipn.upiicsa.signaltrace.crypto.Protocol
import mx.ipn.upiicsa.signaltrace.data.Api
import mx.ipn.upiicsa.signaltrace.data.ApiException
import mx.ipn.upiicsa.signaltrace.data.ServerKey
import mx.ipn.upiicsa.signaltrace.data.Settings

/**
 * Estado de la pantalla Configurar.
 * @property serverUrl URL del servidor configurada.
 * @property fetchedKey Llave recién obtenida del servidor (todavía sin fijar).
 * @property pinnedFingerprint Huella fijada.
 * @property deviceId Dispositivo registrado, o null.
 * @property deviceName Nombre del dispositivo.
 * @property busy Si hay una operación de red en curso.
 * @property message Mensaje para el usuario.
 * @property error Si el mensaje es un error.
 */
data class SetupState(
    val serverUrl: String = "",
    val fetchedKey: ServerKey? = null,
    val pinnedFingerprint: String? = null,
    val deviceId: String? = null,
    val deviceName: String = "",
    val busy: Boolean = false,
    val message: String? = null,
    val error: Boolean = false,
)

/** Lógica de la configuración: servidor, fijado de la huella, registro y vinculación por QR o código. */
class AppViewModel(app: Application) : AndroidViewModel(app) {
    /** Preferencias de la aplicación. */
    val settings = Settings(app)
    private val _setup = MutableStateFlow(SetupState())
    /** Estado observable por la pantalla. */
    val setup: StateFlow<SetupState> = _setup

    init { refresh() }

    /** Vuelve a leer la configuración guardada. */
    fun refresh() = _setup.update {
        it.copy(serverUrl = settings.serverUrl, pinnedFingerprint = settings.fingerprint, deviceId = settings.deviceId.takeIf { settings.isRegistered }, deviceName = settings.deviceName)
    }

    /** Cliente de la API con la URL y el certificado fijado actuales. */
    private fun api() = Api(settings.serverUrl, settings.tlsPin)

    /** Muestra un mensaje (o un error) y termina el estado ocupado. */
    private fun say(msg: String, error: Boolean = false) = _setup.update { it.copy(message = msg, error = error, busy = false) }

    /** Ejecuta una operación marcando la pantalla como ocupada y convirtiendo las excepciones en mensajes. */
    private fun io(block: suspend () -> Unit) = viewModelScope.launch {
        _setup.update { it.copy(busy = true, message = null) }
        try { block() }
        catch (e: ApiException) { say(if (e.code == "network") "No se pudo contactar al servidor: ${e.message}" else "${e.code}: ${e.message}", true) }
        catch (e: Exception) { say(e.message ?: "Error", true) }
        finally { _setup.update { it.copy(busy = false) } }
    }

    /** Cambia la URL del servidor; si es otra, olvida la huella y el dispositivo. */
    fun setServerUrl(url: String) {
        if (url.trim().trimEnd('/') != settings.serverUrl) {
            settings.serverUrl = url
            // Otro servidor significa otra llave: la huella fijada anterior ya no aplica.
            settings.forgetPin(); settings.forgetDevice()
        }
        refresh()
    }

    /**
     * Pide la llave del servidor, recalcula su huella para comprobar que es coherente y la compara con la fijada.
     * Si cambió, avisa de un posible intermediario.
     */
    fun fetchKey() = io {
        val key = withContext(Dispatchers.IO) { api().serverKey() }
        val computed = Protocol.fingerprint(Base64.decode(key.publicKey, Base64.NO_WRAP))
        if (computed != key.fingerprint) error("La huella no corresponde a la llave recibida")
        _setup.update { it.copy(fetchedKey = key) }
        val pinned = settings.fingerprint
        say(when {
            pinned == null -> "Compara la huella con la que imprime el servidor y fíjala."
            pinned == key.fingerprint -> "La llave coincide con la huella fijada."
            else -> "¡La llave del servidor cambió respecto a la fijada! Posible ataque de intermediario."
        }, error = pinned != null && pinned != key.fingerprint)
    }

    /** Fija la llave obtenida (después de que el usuario confirmó la huella). */
    fun pin() {
        val key = _setup.value.fetchedKey ?: return
        settings.pinnedKey = key.publicKey
        settings.fingerprint = key.fingerprint
        refresh(); say("Huella fijada.")
    }

    /** Inicia sesión con usuario y contraseña, registra el teléfono y guarda su id y su token. La contraseña no se guarda. */
    fun register(username: String, password: String, name: String) = io {
        settings.deviceName = name.ifBlank { android.os.Build.MODEL }
        val api = api()
        val reg = withContext(Dispatchers.IO) { api.registerDevice(api.login(username, password).token, settings.deviceName) }
        settings.deviceId = reg.deviceId; settings.deviceToken = reg.deviceToken
        refresh(); say("Dispositivo registrado.")
    }

    /** Canjea un código de vinculación escrito a mano. */
    fun redeem(code: String, name: String) = io {
        settings.deviceName = name.ifBlank { android.os.Build.MODEL }
        val reg = withContext(Dispatchers.IO) { api().redeemEnrollment(code.trim(), settings.deviceName) }
        settings.deviceId = reg.deviceId; settings.deviceToken = reg.deviceToken
        refresh(); say("Dispositivo vinculado con el código.")
    }

    /**
     * QR del dashboard: una URL normal, https://host:puerto/agent.html#enroll=CODIGO&fp=HUELLA[&tls=SHA256CERT].
     * El origen se vuelve la URL del servidor; la llave se fija solo si la del servidor coincide con fp; tls fija el certificado.
     */
    fun applyQr(text: String, name: String) = io {
        val uri = runCatching { URI(text.trim()) }.getOrNull() ?: error("El QR no es una URL de SignalTrace")
        if (uri.scheme !in listOf("http", "https") || uri.host == null) error("El QR no es una URL de SignalTrace")
        // Parámetros del fragmento (lo que va después de #).
        val params = (uri.rawFragment ?: "").split('&').mapNotNull { kv ->
            kv.split('=', limit = 2).takeIf { it.size == 2 }?.let { (k, v) -> k to URLDecoder.decode(v, "UTF-8") }
        }.toMap()
        val code = params["enroll"] ?: error("QR sin código de vinculación")
        val fp = params["fp"] ?: error("QR sin huella del servidor")
        setServerUrl("${uri.scheme}://${uri.rawAuthority}")
        // El certificado solo se fija con HTTPS.
        settings.tlsPin = params["tls"]?.takeIf { uri.scheme == "https" }
        val key = withContext(Dispatchers.IO) { api().serverKey() }
        val computed = Protocol.fingerprint(Base64.decode(key.publicKey, Base64.NO_WRAP))
        if (computed != fp || key.fingerprint != fp) error("La llave del servidor no coincide con la huella del QR: no se fija (posible MITM)")
        settings.pinnedKey = key.publicKey; settings.fingerprint = fp
        _setup.update { it.copy(fetchedKey = key) }
        settings.deviceName = name.ifBlank { android.os.Build.MODEL }
        val reg = withContext(Dispatchers.IO) { api().redeemEnrollment(code, settings.deviceName) }
        settings.deviceId = reg.deviceId; settings.deviceToken = reg.deviceToken
        refresh(); say("Vinculado por QR: huella fijada${if (settings.tlsPin != null) ", certificado TLS fijado" else ""} y dispositivo registrado.")
    }

    /** Olvida el registro del dispositivo en este teléfono. */
    fun forget() { settings.forgetDevice(); refresh(); say("Dispositivo olvidado.") }
}
