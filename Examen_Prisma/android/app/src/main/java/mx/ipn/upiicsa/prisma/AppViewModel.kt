// ViewModel único de la app ABCO: guarda la sesión, la URL del servidor, el tema y el catálogo, y contiene
// la lógica de cada pantalla (validación local, llamada a la API y actualización del estado observable).
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
import kotlinx.coroutines.async
import kotlinx.coroutines.coroutineScope
import kotlinx.coroutines.channels.Channel
import kotlinx.coroutines.flow.Flow
import kotlinx.coroutines.flow.receiveAsFlow
import kotlinx.coroutines.launch
import mx.ipn.upiicsa.prisma.data.AdminUser
import mx.ipn.upiicsa.prisma.data.ApiClient
import mx.ipn.upiicsa.prisma.data.AppPrefs
import mx.ipn.upiicsa.prisma.data.ApiClient.UploadSource
import mx.ipn.upiicsa.prisma.data.ApiException
import mx.ipn.upiicsa.prisma.data.CatalogItem
import mx.ipn.upiicsa.prisma.data.Files
import mx.ipn.upiicsa.prisma.data.ImageSource
import mx.ipn.upiicsa.prisma.data.LoginResponse
import mx.ipn.upiicsa.prisma.data.Session
import mx.ipn.upiicsa.prisma.data.SessionExpiredException
import mx.ipn.upiicsa.prisma.data.StegoMode
import mx.ipn.upiicsa.prisma.data.ThemeMode
import mx.ipn.upiicsa.prisma.ui.AccountState
import mx.ipn.upiicsa.prisma.ui.AdminState
import mx.ipn.upiicsa.prisma.ui.CaesarState
import mx.ipn.upiicsa.prisma.ui.CompareState
import mx.ipn.upiicsa.prisma.ui.HideState
import mx.ipn.upiicsa.prisma.ui.LoginState
import mx.ipn.upiicsa.prisma.ui.PasswordChangeState
import mx.ipn.upiicsa.prisma.ui.PickedImage
import mx.ipn.upiicsa.prisma.ui.RevealState
import mx.ipn.upiicsa.prisma.ui.ServerTestState
import okhttp3.HttpUrl.Companion.toHttpUrlOrNull

/**
 * ViewModel único de la app: sesión, servidor, catálogo y la lógica de cada pantalla.
 *
 * Todo el estado es `mutableStateOf`/listas de Compose, así que las pantallas se recomponen solas al
 * cambiar. Las corrutinas se lanzan en [viewModelScope]: se cancelan al destruirse el ViewModel y, con
 * ellas, las peticiones HTTP en curso (ver `ApiClient.send`). Al ser un AndroidViewModel sobrevive a la
 * rotación y conserva formularios y resultados.
 */
class AppViewModel(app: Application) : AndroidViewModel(app) {

    /** Contexto de la aplicación (no de la Activity), seguro de guardar en un ViewModel. */
    private val context get() = getApplication<Application>()
    /** Preferencias persistentes: URL del servidor y modo de tema. */
    private val prefs = AppPrefs(app)

    // ---- Tema ----
    /** Modo de tema elegido (Automático/Claro/Oscuro); MainActivity lo observa para aplicar el tema. */
    var themeMode by mutableStateOf(prefs.themeMode)
        private set

    /** Guarda el modo de tema en el teléfono y lo aplica de inmediato. */
    fun changeThemeMode(mode: ThemeMode) {
        prefs.themeMode = mode
        themeMode = mode
    }

    // ---- Servidor y sesión ----
    /** URL base del servidor en uso; cambia con [saveServer] y [resetServer]. */
    var serverUrl by mutableStateOf(prefs.serverUrl)
        private set
    /**
     * Sesión activa o null. Con null la UI muestra el login; con `mustChangePassword` muestra la pantalla
     * obligatoria de cambio de contraseña antes de la navegación.
     */
    var session by mutableStateOf<Session?>(null)
        private set

    /** Cliente de la API; lee la URL y el token en cada petición, así que siempre usa los valores actuales. */
    private val api = ApiClient(baseUrl = { serverUrl }, token = { session?.token })

    /**
     * Avisos breves para el Snackbar. Un Channel con búfer (en vez de un estado) entrega cada aviso una sola
     * vez, aunque la UI se recomponga o se recree tras una rotación.
     */
    private val noticeChannel = Channel<String>(Channel.BUFFERED)
    /** Flujo de avisos que la UI recoge para mostrarlos en el Snackbar. */
    val notices: Flow<String> = noticeChannel.receiveAsFlow()
    /** Encola un aviso; `trySend` no suspende y no falla porque el canal tiene búfer. */
    fun notify(message: String) { noticeChannel.trySend(message) }

    // ---- Estado por pantalla ----
    // Cada pantalla tiene su objeto de estado (FeatureStates.kt). Se reemplazan completos al cerrar sesión
    // para que no queden datos de un usuario en la sesión del siguiente.
    var login by mutableStateOf(LoginState()); private set
    var hide by mutableStateOf(HideState()); private set
    var reveal by mutableStateOf(RevealState()); private set
    var caesar by mutableStateOf(CaesarState()); private set
    var compare by mutableStateOf(CompareState()); private set
    var account by mutableStateOf(AccountState()); private set
    var admin by mutableStateOf(AdminState()); private set
    /** Formulario de la pantalla obligatoria "Cambia tu contraseña". */
    var forcedPassword by mutableStateOf(PasswordChangeState()); private set
    /** Resultado de "Probar conexión" en el diálogo de ajustes del servidor. */
    val serverTest = ServerTestState()

    // ---- Catálogo ----
    /** Imágenes portadoras del catálogo del servidor. */
    val catalog = mutableStateListOf<CatalogItem>()
    /** Miniaturas del catálogo por id, decodificadas a 320 px. */
    val thumbs = mutableStateMapOf<String, ImageBitmap>()
    /** Hay una carga del catálogo en curso. */
    var catalogLoading by mutableStateOf(false); private set
    /** Error de la última carga del catálogo, o null. */
    var catalogError by mutableStateOf<String?>(null); private set

    /**
     * Ejecuta una llamada a la API con indicador de carga. Un 401 sin cuerpo cierra la sesión;
     * cualquier otro error se entrega a [onError] con un mensaje listo para mostrarse.
     *
     * Un 403 (por ejemplo, una ruta bloqueada mientras la contraseña es temporal) llega como [ApiException]
     * normal y se muestra en la pantalla; no cierra la sesión.
     *
     * @param setLoading recibe true al empezar y false al terminar, aun con error o cancelación.
     * @param onError recibe el error con su mensaje en español.
     * @param block la llamada; corre en el hilo principal y suspende mientras ApiClient trabaja en IO.
     * @return el Job, para poder cancelar la operación (p. ej. una consulta de capacidad que quedó vieja).
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
            // La cancelación debe propagarse: si se atrapara como error, la corrutina seguiría como si nada.
            throw e
        } catch (e: Exception) {
            onError(ApiException(0, e.message ?: "Error inesperado."))
        } finally {
            setLoading(false)
        }
    }

    // =========================== Servidor ===========================

    /**
     * Valida y normaliza una URL http(s); devuelve null si no es válida. Se aceptan `http://` (red local,
     * con aviso en la UI) y `https://`; cualquier otro esquema se rechaza.
     */
    fun normalizeUrl(raw: String): String? {
        val url = raw.trim().trimEnd('/')
        val parsed = url.toHttpUrlOrNull() ?: return null
        return if (parsed.scheme == "http" || parsed.scheme == "https") url else null
    }

    /**
     * Guarda la URL del servidor si es válida. Vacía el catálogo y sus miniaturas porque pertenecen
     * al servidor anterior.
     *
     * @return false si la URL no es válida (no se guarda nada).
     */
    fun saveServer(raw: String): Boolean {
        val url = normalizeUrl(raw) ?: return false
        prefs.serverUrl = url
        serverUrl = url
        catalog.clear(); thumbs.clear()
        notify("Servidor guardado: $url")
        return true
    }

    /** Vuelve a la URL con la que se compiló la app y vacía el catálogo del servidor anterior. */
    fun resetServer() {
        prefs.reset()
        serverUrl = prefs.serverUrl
        catalog.clear(); thumbs.clear()
    }

    /**
     * Prueba una URL todavía no guardada con `GET /api/health` y deja el resultado en [serverTest].
     *
     * @param raw texto escrito por el usuario en el campo de URL.
     */
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

    /** Misma regla de nombre de usuario que el servidor, para avisar antes de enviar. */
    private val usernameRegex = Regex("^[A-Za-z0-9_.-]{3,32}$")

    /**
     * Botón principal del login: registra si el formulario está en modo registro o inicia sesión.
     * Si la cuenta tiene 2FA, el servidor responde sin token y el formulario pasa al paso de la imagen-llave
     * (conservando usuario y contraseña, que se vuelven a enviar en [submitKeyImage]).
     */
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

    /**
     * Guarda la imagen-llave elegida con el selector de documentos y genera su vista previa.
     *
     * @param uri documento elegido con OpenDocument; los bytes se leen hasta enviar.
     */
    fun pickLoginKey(uri: Uri) {
        val s = login
        s.keyUri = uri
        s.keyName = Files.displayName(context, uri)
        s.keyPreview = null
        s.error = null
        // Vista previa reducida a 480 px; solo para mostrar, la llave se envía con sus bytes originales.
        viewModelScope.launch { s.keyPreview = Files.decode(context, uri, 480) }
    }

    /** Segundo paso del login: envía usuario, contraseña y los bytes exactos de la imagen-llave. */
    fun submitKeyImage() {
        val s = login
        val uri = s.keyUri ?: run { s.error = "Selecciona tu imagen-llave."; return }
        s.error = null
        launchApi({ s.loading = it }, { s.error = it.message }) {
            val key = Files.read(context, uri)
            startSession(api.login2fa(s.username.trim(), s.password, key))
        }
    }

    /** Regresa del paso de la imagen-llave al formulario de usuario y contraseña. */
    fun cancelKeyStep() {
        login.awaitingKey = false
        login.keyUri = null
        login.keyPreview = null
        login.error = null
    }

    /** Valida el formulario de registro y crea la cuenta; al terminar vuelve al modo de inicio de sesión. */
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

    /**
     * Crea la sesión con el token del login y carga los datos iniciales (catálogo y perfil).
     *
     * @throws ApiException si el servidor no devolvió token (respuesta inconsistente).
     */
    private fun startSession(r: LoginResponse) {
        val token = r.token ?: throw ApiException(0, "El servidor no devolvió un token.")
        session = Session(
            token = token,
            username = r.username ?: login.username.trim(),
            role = r.role ?: "user",
            twoFactorEnabled = r.twoFactorEnabled,
            mustChangePassword = r.mustChangePassword,
        )
        // Con contraseña temporal se precarga en la pantalla obligatoria de cambio, para que el usuario
        // solo escriba la nueva. El token actual solo sirve para /api/account/me y /api/account/password.
        forcedPassword = PasswordChangeState().apply { if (r.mustChangePassword) current = login.password }
        // Se descarta el formulario de login para no conservar la contraseña en memoria.
        login = LoginState()
        loadCatalog()
        refreshAccount()
    }

    /** Cierra la sesión: olvida el token y reinicia el estado de todas las pantallas. */
    fun logout() {
        session = null
        login = LoginState()
        hide = HideState()
        reveal = RevealState()
        caesar = CaesarState()
        compare = CompareState()
        account = AccountState()
        admin = AdminState()
        forcedPassword = PasswordChangeState()
    }

    /** Cierra la sesión por token inválido (401) conservando el usuario para facilitar el nuevo login. */
    private fun expireSession() {
        val user = session?.username.orEmpty()
        logout()
        login.username = user
        login.error = "Tu sesión expiró. Vuelve a iniciar sesión."
        notify("Sesión expirada")
    }

    // =========================== Catálogo ===========================

    /**
     * Carga el catálogo y sus miniaturas. Las miniaturas se piden una por una y una que falle se omite
     * sin cancelar las demás.
     *
     * @param force recarga aunque ya haya catálogo (botón de reintentar).
     */
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

    /**
     * Asigna la imagen elegida a [target] y prepara su vista previa: la miniatura ya descargada si es del
     * catálogo, o la decodificación del archivo local en segundo plano.
     */
    private fun selectImage(target: PickedImage, source: ImageSource) {
        target.source = source
        target.preview = null
        when (source) {
            is ImageSource.Catalog -> target.preview = thumbs[source.item.id]
            is ImageSource.Local -> viewModelScope.launch { target.preview = Files.decode(context, source.uri, 1024) }
        }
    }

    /** Crea un [ImageSource.Local] con el nombre visible del documento elegido. */
    fun localSource(uri: Uri) = ImageSource.Local(uri, Files.displayName(context, uri))

    /** Convierte la imagen elegida al formato de subida; un archivo local se lee completo aquí. */
    private suspend fun ImageSource.toUpload(): UploadSource = when (this) {
        is ImageSource.Catalog -> UploadSource.Catalog(item.id)
        is ImageSource.Local -> UploadSource.File(Files.read(context, uri))
    }

    // =========================== Ocultar ===========================

    /** Consulta de capacidad en curso; se cancela si el usuario cambia de portadora antes de que termine. */
    private var capacityJob: Job? = null

    /**
     * Elige la portadora de la pantalla Ocultar, borra el resultado anterior y consulta su capacidad.
     */
    fun selectCover(source: ImageSource) {
        val s = hide
        selectImage(s.image, source)
        s.capacity = null
        s.result = null
        s.resultPreview = null
        s.error = null
        // Cancela la consulta anterior para que una respuesta tardía no sobrescriba la capacidad de la nueva
        // imagen.
        capacityJob?.cancel()
        capacityJob = launchApi({ s.capacityLoading = it }, { s.error = it.message }) {
            s.capacity = api.capacity(source.toUpload())
        }
    }

    /** Valida el formulario de Ocultar y pide al servidor el PNG con el mensaje oculto. */
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
            // La esteganografía clásica no usa clave: se envía la frase vacía aunque el campo tenga texto.
            val phrase = if (s.mode == StegoMode.PRISMA) s.passphrase else ""
            val r = api.embed(source.toUpload(), s.message, phrase, s.mode)
            s.result = r
            // La vista previa se decodifica aparte; para guardar o compartir se usan los bytes PNG originales.
            s.resultPreview = Files.decode(r.png, 1280)
        }
    }

    // =========================== Revelar ===========================

    /**
     * Guarda la imagen estego elegida con el selector de documentos y limpia el resultado anterior.
     *
     * @param uri documento elegido con OpenDocument (no con el selector de fotos, que puede entregar una
     *   copia recomprimida y sin el mensaje).
     */
    fun pickStegoImage(uri: Uri) {
        val s = reveal
        s.imageUri = uri
        s.imageName = Files.displayName(context, uri)
        s.preview = null
        s.result = null; s.error = null; s.rawHex = null
        viewModelScope.launch { s.preview = Files.decode(context, uri, 1024) }
    }

    /**
     * Pide al servidor revelar el mensaje. Si falla con 422 también se guarda [ApiException.rawSequentialHex]
     * para mostrar lo que vería un atacante leyendo los bits en orden.
     */
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

    /**
     * Ejecuta la acción de César elegida. Cifrar y descifrar muestran el texto resultante; romper muestra
     * la tabla de candidatos. Cada resultado borra el del otro tipo.
     */
    fun caesarRun(action: CaesarAction) {
        val s = caesar
        s.error = if (s.text.isBlank()) "Escribe un texto." else null
        if (s.error != null) return
        // La clave máxima depende del alfabeto (26 o 27 con Ñ); se ajusta por si cambió el interruptor de Ñ.
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

    /** Valida y ejecuta el comparador de César, esteganografía clásica y ABCO. */
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

    /**
     * Recarga el perfil y sincroniza la sesión con él (rol, 2FA, contraseña temporal); así un cambio hecho
     * por el administrador se refleja sin volver a iniciar sesión.
     */
    fun refreshAccount() {
        val s = account
        launchApi({ s.loadingInfo = it }, { s.error = it.message }) {
            val me = api.me()
            s.info = me
            session = session?.copy(
                role = me.role,
                twoFactorEnabled = me.twoFactorEnabled,
                mustChangePassword = me.mustChangePassword,
            )
        }
    }

    /**
     * Cambia la contraseña con el formulario [s] (Cuenta o pantalla obligatoria).
     * El servidor emite un token nuevo: se reemplaza la sesión con el de la respuesta.
     *
     * Con contraseña temporal es lo único que el servidor permite, además de consultar el perfil; al
     * terminar, la sesión nueva ya no la marca.
     */
    fun changePassword(s: PasswordChangeState) {
        // Mismas reglas que el servidor, para avisar sin hacer la petición.
        s.error = when {
            s.current.isEmpty() -> "Escribe tu contraseña actual."
            s.newPassword.length < 8 -> "La nueva contraseña debe tener al menos 8 caracteres."
            s.newPassword == s.current -> "La nueva contraseña debe ser distinta de la actual."
            s.newPassword != s.confirm -> "Las contraseñas no coinciden."
            else -> null
        }
        if (s.error != null) return
        s.twoFactorDisabled = false
        launchApi({ s.loading = it }, { s.error = it.message }) {
            val r = api.changePassword(s.current, s.newPassword)
            // El token anterior queda inválido: se reemplaza por el de la respuesta. Como el nuevo llega con
            // mustChangePassword = false, la UI sale sola de la pantalla obligatoria.
            val body = r.session
            if (body.token.isBlank()) throw ApiException(0, "El servidor no devolvió un token.")
            session = Session(
                token = body.token,
                username = body.username.ifBlank { session?.username.orEmpty() },
                role = body.role,
                twoFactorEnabled = body.twoFactorEnabled,
                mustChangePassword = body.mustChangePassword,
            )
            s.current = ""; s.newPassword = ""; s.confirm = ""
            s.twoFactorDisabled = r.twoFactorDisabled
            // La imagen-llave anterior dependía de la contraseña vieja: ya no sirve y se deja de mostrar.
            if (r.twoFactorDisabled) {
                account.keyPng = null
                account.keyPreview = null
            }
            notify(
                if (r.twoFactorDisabled) "Contraseña cambiada. El 2FA se desactivó: genera otra imagen-llave."
                else "Contraseña cambiada.",
            )
            refreshAccount()
        }
    }

    /** Elige la imagen (catálogo o archivo) con la que se generará la imagen-llave. */
    fun selectKeyImage(source: ImageSource) = selectImage(account.keyImage, source)

    /** Activa el 2FA: el servidor devuelve la imagen-llave, que se muestra para guardarla o compartirla. */
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

    /** Desactiva el 2FA con la contraseña actual y olvida la imagen-llave mostrada. */
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

    // =========================== Administración ===========================

    /**
     * Carga usuarios, auditoría (50), traza y mapa de diferencias en paralelo.
     * `coroutineScope` hace que, si una de las cuatro peticiones falla, se cancelen las demás y el error
     * llegue a `launchApi`.
     */
    fun refreshAdmin() {
        val s = admin
        if (s.loading) return
        s.error = null
        launchApi({ s.loading = it }, { s.error = it.message }) {
            coroutineScope {
                val users = async { api.adminUsers() }
                val audit = async { api.adminAudit(50) }
                val trace = async { api.lastTrace() }
                val diff = async { api.lastDiffMap()?.let { Files.decode(it, 1280) } }
                s.users = users.await()
                s.audit = audit.await()
                s.trace = trace.await()
                s.diffMap = diff.await()
            }
            s.loaded = true
        }
    }

    /** Recarga solo la lista de usuarios, sin indicador de carga global. */
    private fun refreshAdminUsers() {
        val s = admin
        launchApi({}, { s.error = it.message }) { s.users = api.adminUsers() }
    }

    /**
     * Ejecuta una acción sobre un usuario marcándolo como ocupado (deshabilita sus botones mientras dura).
     * Los errores se muestran en la pantalla y también como aviso.
     */
    private fun adminAction(user: AdminUser, block: suspend () -> Unit) {
        val s = admin
        s.error = null
        launchApi(
            { busy -> s.busy = if (busy) s.busy + user.id else s.busy - user.id },
            { e -> s.error = e.message; e.message?.let(::notify) }, // p. ej. el 409 del cambio de rol
        ) {
            block()
        }
    }

    /**
     * Alterna el nombre ofuscado y el real (POST /api/admin/deobfuscate). El nombre real se guarda solo en
     * memoria y solo se pide cuando el administrador toca «Revelar».
     */
    fun toggleReveal(user: AdminUser) {
        val s = admin
        if (s.revealed.containsKey(user.id)) {
            s.revealed = s.revealed - user.id
            return
        }
        adminAction(user) {
            val plain = api.deobfuscate(user.username)
            s.revealed = s.revealed + (user.id to plain)
        }
    }

    /** Nombre a mostrar: el real si ya se reveló, si no el ofuscado. */
    fun displayName(user: AdminUser): String = admin.revealed[user.id] ?: user.username

    /**
     * Restablece la contraseña del usuario. La contraseña temporal queda en [AdminState.temporaryPassword]
     * para mostrarla una sola vez en un diálogo.
     */
    fun resetUserPassword(user: AdminUser) {
        val s = admin
        adminAction(user) {
            val temp = api.resetUserPassword(user.id)
            s.temporaryPassword = displayName(user) to temp
            s.users = api.adminUsers()
        }
    }

    /**
     * Hace o quita administrador. Si es uno mismo o el último administrador, el servidor responde 409 y el
     * mensaje se muestra como aviso.
     */
    fun setUserRole(user: AdminUser, makeAdmin: Boolean) {
        val s = admin
        adminAction(user) {
            api.setUserRole(user.id, if (makeAdmin) "admin" else "user")
            notify(if (makeAdmin) "Ahora es administrador." else "Se quitó el rol de administrador.")
            s.users = api.adminUsers()
        }
    }

    /** Restablece el 2FA del usuario (p. ej. si perdió su imagen-llave). */
    fun resetUser2fa(user: AdminUser) {
        adminAction(user) {
            api.resetUser2fa(user.id)
            notify("2FA restablecido.")
            refreshAdminUsers()
        }
    }

    /** Cierra el diálogo y olvida la contraseña temporal. */
    fun dismissTemporaryPassword() {
        admin.temporaryPassword = null
    }

    // =========================== Archivos ===========================

    /**
     * Escribe el PNG en el documento elegido con CreateDocument. Se guardan los bytes tal cual llegaron del
     * servidor; volver a codificar la imagen destruiría el mensaje oculto.
     *
     * @param uri destino que devolvió el selector de documentos.
     */
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

/** Acción de la pantalla César. */
enum class CaesarAction { ENCRYPT, DECRYPT, CRACK }
