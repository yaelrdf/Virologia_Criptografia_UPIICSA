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

class AppViewModel(app: Application) : AndroidViewModel(app) {
    val settings = Settings(app)
    private val _setup = MutableStateFlow(SetupState())
    val setup: StateFlow<SetupState> = _setup

    init { refresh() }

    fun refresh() = _setup.update {
        it.copy(serverUrl = settings.serverUrl, pinnedFingerprint = settings.fingerprint, deviceId = settings.deviceId.takeIf { settings.isRegistered }, deviceName = settings.deviceName)
    }

    private fun api() = Api(settings.serverUrl, settings.tlsPin)

    private fun say(msg: String, error: Boolean = false) = _setup.update { it.copy(message = msg, error = error, busy = false) }

    private fun io(block: suspend () -> Unit) = viewModelScope.launch {
        _setup.update { it.copy(busy = true, message = null) }
        try { block() }
        catch (e: ApiException) { say(if (e.code == "network") "No se pudo contactar al servidor: ${e.message}" else "${e.code}: ${e.message}", true) }
        catch (e: Exception) { say(e.message ?: "Error", true) }
        finally { _setup.update { it.copy(busy = false) } }
    }

    fun setServerUrl(url: String) {
        if (url.trim().trimEnd('/') != settings.serverUrl) {
            settings.serverUrl = url
            // A different server means a different key: the old pin no longer applies.
            settings.forgetPin(); settings.forgetDevice()
        }
        refresh()
    }

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

    fun pin() {
        val key = _setup.value.fetchedKey ?: return
        settings.pinnedKey = key.publicKey
        settings.fingerprint = key.fingerprint
        refresh(); say("Huella fijada.")
    }

    fun register(username: String, password: String, name: String) = io {
        settings.deviceName = name.ifBlank { android.os.Build.MODEL }
        val api = api()
        val reg = withContext(Dispatchers.IO) { api.registerDevice(api.login(username, password).token, settings.deviceName) }
        settings.deviceId = reg.deviceId; settings.deviceToken = reg.deviceToken
        refresh(); say("Dispositivo registrado.")
    }

    fun redeem(code: String, name: String) = io {
        settings.deviceName = name.ifBlank { android.os.Build.MODEL }
        val reg = withContext(Dispatchers.IO) { api().redeemEnrollment(code.trim(), settings.deviceName) }
        settings.deviceId = reg.deviceId; settings.deviceToken = reg.deviceToken
        refresh(); say("Dispositivo vinculado con el código.")
    }

    /**
     * QR from the dashboard: an ordinary URL, https://host:port/agent.html#enroll=CODE&fp=FINGERPRINT[&tls=CERTSHA256].
     * The origin becomes the server URL; the key is pinned only if the server's key matches fp; tls pins the certificate.
     */
    fun applyQr(text: String, name: String) = io {
        val uri = runCatching { URI(text.trim()) }.getOrNull() ?: error("El QR no es una URL de SignalTrace")
        if (uri.scheme !in listOf("http", "https") || uri.host == null) error("El QR no es una URL de SignalTrace")
        val params = (uri.rawFragment ?: "").split('&').mapNotNull { kv ->
            kv.split('=', limit = 2).takeIf { it.size == 2 }?.let { (k, v) -> k to URLDecoder.decode(v, "UTF-8") }
        }.toMap()
        val code = params["enroll"] ?: error("QR sin código de vinculación")
        val fp = params["fp"] ?: error("QR sin huella del servidor")
        setServerUrl("${uri.scheme}://${uri.rawAuthority}")
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

    fun forget() { settings.forgetDevice(); refresh(); say("Dispositivo olvidado.") }
}
