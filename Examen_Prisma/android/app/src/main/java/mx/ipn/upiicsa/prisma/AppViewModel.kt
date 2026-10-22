package mx.ipn.upiicsa.prisma

import android.app.Application
import android.net.Uri
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateListOf
import androidx.compose.runtime.mutableStateMapOf
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.setValue
import androidx.compose.ui.graphics.ImageBitmap
import androidx.lifecycle.AndroidViewModel
import androidx.lifecycle.viewModelScope
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.Job
import kotlinx.coroutines.channels.Channel
import kotlinx.coroutines.flow.Flow
import kotlinx.coroutines.flow.receiveAsFlow
import kotlinx.coroutines.launch
import mx.ipn.upiicsa.prisma.data.ApiClient
import mx.ipn.upiicsa.prisma.data.ApiClient.UploadSource
import mx.ipn.upiicsa.prisma.data.ApiException
import mx.ipn.upiicsa.prisma.data.CatalogItem
import mx.ipn.upiicsa.prisma.data.Files
import mx.ipn.upiicsa.prisma.data.ImageSource
import mx.ipn.upiicsa.prisma.data.LoginResponse
import mx.ipn.upiicsa.prisma.data.ServerPrefs
import mx.ipn.upiicsa.prisma.data.Session
import mx.ipn.upiicsa.prisma.data.SessionExpiredException
import mx.ipn.upiicsa.prisma.data.StegoMode
import mx.ipn.upiicsa.prisma.ui.AccountState
import mx.ipn.upiicsa.prisma.ui.CaesarState
import mx.ipn.upiicsa.prisma.ui.CompareState
import mx.ipn.upiicsa.prisma.ui.HideState
import mx.ipn.upiicsa.prisma.ui.LoginState
import mx.ipn.upiicsa.prisma.ui.PickedImage
import mx.ipn.upiicsa.prisma.ui.RevealState
import mx.ipn.upiicsa.prisma.ui.ServerTestState
import okhttp3.HttpUrl.Companion.toHttpUrlOrNull

/** ViewModel único de la app: sesión, servidor, catálogo y la lógica de cada pantalla. */
class AppViewModel(app: Application) : AndroidViewModel(app) {

    private val context get() = getApplication<Application>()
    private val prefs = ServerPrefs(app)

    // ---- Servidor y sesión ----
    var serverUrl by mutableStateOf(prefs.serverUrl)
        private set
    var session by mutableStateOf<Session?>(null)
        private set

    private val api = ApiClient(baseUrl = { serverUrl }, token = { session?.token })

    /** Avisos breves para el Snackbar. */
    private val noticeChannel = Channel<String>(Channel.BUFFERED)
    val notices: Flow<String> = noticeChannel.receiveAsFlow()
    fun notify(message: String) { noticeChannel.trySend(message) }

    // ---- Estado por pantalla ----
    var login by mutableStateOf(LoginState()); private set
    var hide by mutableStateOf(HideState()); private set
    var reveal by mutableStateOf(RevealState()); private set
    var caesar by mutableStateOf(CaesarState()); private set
    var compare by mutableStateOf(CompareState()); private set
    var account by mutableStateOf(AccountState()); private set
    val serverTest = ServerTestState()

    // ---- Catálogo ----
    val catalog = mutableStateListOf<CatalogItem>()
    val thumbs = mutableStateMapOf<String, ImageBitmap>()
    var catalogLoading by mutableStateOf(false); private set
    var catalogError by mutableStateOf<String?>(null); private set

    /**
     * Ejecuta una llamada a la API con indicador de carga. Un 401 sin cuerpo cierra la sesión;
     * cualquier otro error se entrega a [onError] con un mensaje listo para mostrarse.
     */
    private fun launchApi(
        setLoading: (Boolean) -> Unit,
        onError: (ApiException) -> Unit,
        block: suspend () -> Unit,
    ): Job = viewModelScope.launch {
        setLoading(true)
        try {
            block()
        } catch (e: SessionExpiredException) {
            expireSession()
        } catch (e: ApiException) {
            onError(e)
        } catch (e: CancellationException) {
            throw e
        } catch (e: Exception) {
            onError(ApiException(0, e.message ?: "Error inesperado."))
        } finally {
            setLoading(false)
        }
    }

    // =========================== Servidor ===========================

    /** Valida y normaliza una URL http(s); devuelve null si no es válida. */
    fun normalizeUrl(raw: String): String? {
        val url = raw.trim().trimEnd('/')
        val parsed = url.toHttpUrlOrNull() ?: return null
        return if (parsed.scheme == "http" || parsed.scheme == "https") url else null
    }

    fun saveServer(raw: String): Boolean {
        val url = normalizeUrl(raw) ?: return false
        prefs.serverUrl = url
        serverUrl = url
        catalog.clear(); thumbs.clear()
        notify("Servidor guardado: $url")
        return true
    }

    fun resetServer() {
        prefs.reset()
        serverUrl = prefs.serverUrl
        catalog.clear(); thumbs.clear()
    }

    fun testConnection(raw: String) {
        val url = normalizeUrl(raw)
        serverTest.ok = null
        if (url == null) {
            serverTest.ok = false
            serverTest.message = "URL no válida. Ejemplo: http://10.0.2.2:5080 o https://midominio"
            return
        }
        serverTest.message = null
        launchApi({ serverTest.testing = it }, { serverTest.ok = false; serverTest.message = it.message }) {
            val h = api.health(url)
            serverTest.ok = h.status.equals("ok", ignoreCase = true)
            serverTest.message = if (serverTest.ok == true) "Conexión correcta: ${h.service} (${h.status})"
            else "El servidor respondió, pero su estado es «${h.status}»."
        }
    }

    // =========================== Login y registro ===========================

    private val usernameRegex = Regex("^[A-Za-z0-9_.-]{3,32}$")

    fun submitCredentials() {
        val s = login
        s.error = null
        if (s.registering) return register()
        if (s.username.isBlank() || s.password.isEmpty()) {
            s.error = "Escribe tu usuario y contraseña."; return
        }
        launchApi({ s.loading = it }, { s.error = it.message }) {
            val r = api.login(s.username.trim(), s.password)
            if (r.requires2fa) {
                s.awaitingKey = true
            } else {
                startSession(r)
            }
        }
    }

    fun pickLoginKey(uri: Uri) {
        val s = login
        s.keyUri = uri
        s.keyName = Files.displayName(context, uri)
        s.keyPreview = null
        s.error = null
        viewModelScope.launch { s.keyPreview = Files.decode(context, uri, 480) }
    }

    fun submitKeyImage() {
        val s = login
        val uri = s.keyUri ?: run { s.error = "Selecciona tu imagen-llave."; return }
        s.error = null
        launchApi({ s.loading = it }, { s.error = it.message }) {
            val key = Files.read(context, uri)
            startSession(api.login2fa(s.username.trim(), s.password, key))
        }
    }

    fun cancelKeyStep() {
        login.awaitingKey = false
        login.keyUri = null
        login.keyPreview = null
        login.error = null
    }

    private fun register() {
        val s = login
        val user = s.username.trim()
        s.error = when {
            !usernameRegex.matches(user) ->
                "El usuario debe tener de 3 a 32 caracteres: letras, números, punto, guion o guion bajo."
            s.password.length < 8 -> "La contraseña debe tener al menos 8 caracteres."
            s.password != s.confirm -> "Las contraseñas no coinciden."
            else -> null
        }
        if (s.error != null) return
        launchApi({ s.loading = it }, { s.error = it.message }) {
            val r = api.register(user, s.password)
            s.registering = false
            s.password = ""
            s.confirm = ""
            s.username = r.username.ifBlank { user }
            notify("Cuenta creada. Ahora inicia sesión.")
        }
    }

    private fun startSession(r: LoginResponse) {
        val token = r.token ?: throw ApiException(0, "El servidor no devolvió un token.")
        session = Session(
            token = token,
            username = r.username ?: login.username.trim(),
            role = r.role ?: "user",
            twoFactorEnabled = r.twoFactorEnabled,
        )
        login = LoginState()
        loadCatalog()
        refreshAccount()
    }

    fun logout() {
        session = null
        login = LoginState()
        hide = HideState()
        reveal = RevealState()
        caesar = CaesarState()
        compare = CompareState()
        account = AccountState()
    }

    private fun expireSession() {
        val user = session?.username.orEmpty()
        logout()
        login.username = user
        login.error = "Tu sesión expiró. Vuelve a iniciar sesión."
        notify("Sesión expirada")
    }

    // =========================== Catálogo ===========================

    fun loadCatalog(force: Boolean = false) {
        if (catalogLoading || (catalog.isNotEmpty() && !force)) return
        catalogError = null
        launchApi({ catalogLoading = it }, { catalogError = it.message }) {
            val items = api.catalog()
            catalog.clear(); catalog.addAll(items)
            for (item in items) {
                if (thumbs.containsKey(item.id)) continue
                runCatching { Files.decode(api.catalogImage(item.id), 320) }
                    .getOrNull()?.let { thumbs[item.id] = it }
            }
        }
    }

    private fun selectImage(target: PickedImage, source: ImageSource) {
        target.source = source
        target.preview = null
        when (source) {
            is ImageSource.Catalog -> target.preview = thumbs[source.item.id]
            is ImageSource.Local -> viewModelScope.launch { target.preview = Files.decode(context, source.uri, 1024) }
        }
    }

    fun localSource(uri: Uri) = ImageSource.Local(uri, Files.displayName(context, uri))

    private suspend fun ImageSource.toUpload(): UploadSource = when (this) {
        is ImageSource.Catalog -> UploadSource.Catalog(item.id)
        is ImageSource.Local -> UploadSource.File(Files.read(context, uri))
    }

    // =========================== Ocultar ===========================

    private var capacityJob: Job? = null

    fun selectCover(source: ImageSource) {
        val s = hide
        selectImage(s.image, source)
        s.capacity = null
        s.result = null
        s.resultPreview = null
        s.error = null
        capacityJob?.cancel()
        capacityJob = launchApi({ s.capacityLoading = it }, { s.error = it.message }) {
            s.capacity = api.capacity(source.toUpload())
        }
    }

    fun embed() {
        val s = hide
        val source = s.image.source
        s.error = when {
            source == null -> "Elige una imagen portadora."
            s.message.isEmpty() -> "Escribe el mensaje a ocultar."
            s.mode == StegoMode.PRISMA && s.passphrase.length < 4 -> "La frase debe tener al menos 4 caracteres."
            else -> null
        }
        if (s.error != null || source == null) return
        s.result = null
        s.resultPreview = null
        launchApi({ s.loading = it }, { s.error = it.message }) {
            val phrase = if (s.mode == StegoMode.PRISMA) s.passphrase else ""
            val r = api.embed(source.toUpload(), s.message, phrase, s.mode)
            s.result = r
            s.resultPreview = Files.decode(r.png, 1280)
        }
    }

    // =========================== Revelar ===========================

    fun pickStegoImage(uri: Uri) {
        val s = reveal
        s.imageUri = uri
        s.imageName = Files.displayName(context, uri)
        s.preview = null
        s.result = null; s.error = null; s.rawHex = null
        viewModelScope.launch { s.preview = Files.decode(context, uri, 1024) }
    }

    fun extract() {
        val s = reveal
        val uri = s.imageUri
        s.result = null; s.rawHex = null
        s.error = when {
            uri == null -> "Elige la imagen con el mensaje oculto."
            s.mode == StegoMode.PRISMA && s.passphrase.length < 4 -> "La frase debe tener al menos 4 caracteres."
            else -> null
        }
        if (s.error != null || uri == null) return
        launchApi({ s.loading = it }, { s.error = it.message; s.rawHex = it.rawSequentialHex }) {
            val phrase = if (s.mode == StegoMode.PRISMA) s.passphrase else ""
            s.result = api.extract(Files.read(context, uri), phrase, s.mode)
        }
    }

    // =========================== César ===========================

    fun caesarRun(action: CaesarAction) {
        val s = caesar
        s.error = if (s.text.isBlank()) "Escribe un texto." else null
        if (s.error != null) return
        s.key = s.key.coerceIn(0, s.maxKey)
        launchApi({ s.loading = it }, { s.error = it.message }) {
            when (action) {
                CaesarAction.ENCRYPT, CaesarAction.DECRYPT -> {
                    val r = if (action == CaesarAction.ENCRYPT) api.caesarEncrypt(s.text, s.key, s.enye)
                    else api.caesarDecrypt(s.text, s.key, s.enye)
                    s.crack = null
                    s.output = r.result
                    s.alphabet = r.alphabet
                    s.outputLabel = if (action == CaesarAction.ENCRYPT) "Texto cifrado (clave ${s.key})"
                    else "Texto descifrado (clave ${s.key})"
                }
                CaesarAction.CRACK -> {
                    val r = api.caesarCrack(s.text, s.enye)
                    s.output = null
                    s.crack = r
                }
            }
        }
    }

    // =========================== Comparador ===========================

    fun runCompare() {
        val s = compare
        s.error = when {
            s.message.isEmpty() -> "Escribe un mensaje."
            s.passphrase.length < 4 -> "La frase debe tener al menos 4 caracteres."
            else -> null
        }
        if (s.error != null) return
        launchApi({ s.loading = it }, { s.error = it.message }) {
            s.result = api.compare(s.message, s.passphrase, s.caesarKey)
        }
    }

    // =========================== Cuenta ===========================

    fun refreshAccount() {
        val s = account
        launchApi({ s.loadingInfo = it }, { s.error = it.message }) {
            val me = api.me()
            s.info = me
            session = session?.copy(role = me.role, twoFactorEnabled = me.twoFactorEnabled)
        }
    }

    fun selectKeyImage(source: ImageSource) = selectImage(account.keyImage, source)

    fun enable2fa() {
        val s = account
        val source = s.keyImage.source
        s.error = when {
            s.enablePassword.isEmpty() -> "Escribe tu contraseña."
            source == null -> "Elige una imagen del catálogo o de la galería."
            else -> null
        }
        if (s.error != null || source == null) return
        launchApi({ s.enabling = it }, { s.error = it.message }) {
            val png = api.enable2fa(s.enablePassword, source.toUpload())
            s.keyPng = png
            s.keyPreview = Files.decode(png, 1024)
            s.enablePassword = ""
            notify("2FA activado. Guarda tu imagen-llave.")
            refreshAccount()
        }
    }

    fun disable2fa() {
        val s = account
        s.error = if (s.disablePassword.isEmpty()) "Escribe tu contraseña." else null
        if (s.error != null) return
        launchApi({ s.disabling = it }, { s.error = it.message }) {
            api.disable2fa(s.disablePassword)
            s.disablePassword = ""
            s.keyPng = null
            s.keyPreview = null
            notify("2FA desactivado.")
            refreshAccount()
        }
    }

    // =========================== Archivos ===========================

    fun savePng(uri: Uri, bytes: ByteArray) {
        viewModelScope.launch {
            try {
                Files.write(context, uri, bytes)
                notify("Imagen guardada.")
            } catch (e: CancellationException) {
                throw e
            } catch (e: Exception) {
                notify("No se pudo guardar: ${e.message}")
            }
        }
    }
}

enum class CaesarAction { ENCRYPT, DECRYPT, CRACK }
