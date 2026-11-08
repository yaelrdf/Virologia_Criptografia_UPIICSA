package mx.ipn.upiicsa.signaltrace.telemetry

import android.content.Context
import android.util.Base64
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.Job
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.delay
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.update
import kotlinx.coroutines.isActive
import kotlinx.coroutines.launch
import kotlinx.serialization.json.Json
import kotlinx.serialization.json.JsonObject
import kotlinx.serialization.json.buildJsonObject
import kotlinx.serialization.json.put
import mx.ipn.upiicsa.signaltrace.BuildConfig
import mx.ipn.upiicsa.signaltrace.data.Api
import mx.ipn.upiicsa.signaltrace.data.ApiException
import mx.ipn.upiicsa.signaltrace.data.Session
import mx.ipn.upiicsa.signaltrace.data.Settings
import java.io.File
import java.text.SimpleDateFormat
import java.util.Date
import java.util.Locale
import kotlin.math.abs
import kotlin.math.min

/**
 * Estado del motor de telemetría que observa la interfaz.
 * @property running Si está transmitiendo.
 * @property status Texto de estado.
 * @property level Nivel del estado (color de la insignia).
 * @property ok Paquetes aceptados.
 * @property rejected Paquetes rechazados por el servidor.
 * @property networkErrors Errores de red.
 * @property queued Lecturas en la cola.
 * @property sessionId Sesión actual.
 * @property suite Suite AEAD de la sesión.
 * @property seq Último número de secuencia.
 * @property lastSendAt Momento del último envío aceptado.
 * @property lastPayload Último JSON en claro (con formato).
 * @property lastWire Último sobre transmitido.
 * @property sources Estado de cada colector.
 * @property log Últimas 40 líneas de la bitácora.
 */
data class EngineState(
    val running: Boolean = false,
    val status: String = "Detenido",
    val level: Level = Level.OFF,
    val ok: Int = 0,
    val rejected: Int = 0,
    val networkErrors: Int = 0,
    val queued: Int = 0,
    val sessionId: String? = null,
    val suite: String? = null,
    val seq: Long = 0,
    val lastSendAt: Long? = null,
    val lastPayload: String? = null,
    val lastWire: String? = null,
    val sources: Map<String, String> = emptyMap(),
    val log: List<String> = emptyList(),
) { enum class Level { OFF, OK, WARN, BAD, INFO } }

/**
 * Ciclo de muestreo y envío (lo usa el servicio en primer plano). Sigue las mismas reglas que el agente web (PLAN §12.2):
 * cola en claro en disco (máximo 500), cifrado en el momento del envío con un seq estrictamente creciente, envío en
 * serie, nuevo handshake ante un 404, retroceso exponencial ante errores de red y detención ante un 401.
 */
object TelemetryEngine {
    private val _state = MutableStateFlow(EngineState())
    /** Estado observable por la interfaz y la notificación. */
    val state: StateFlow<EngineState> = _state
    private val json = Json { prettyPrint = true }
    /** Ámbito de las corrutinas mientras corre; null cuando está detenido. */
    private var scope: CoroutineScope? = null
    /** Sesión criptográfica actual; null obliga a hacer handshake. */
    private var session: Session? = null
    /** Fallos seguidos, para el retroceso exponencial. */
    private var failures = 0
    /** Paquetes generados (cada 5 se incluye la ventana del acelerómetro). */
    private var packets = 0L
    /** Archivo de la cola: una lectura JSON por línea. */
    private lateinit var queueFile: File
    /** Cola en memoria, sincronizada con el archivo. */
    private val queue = ArrayDeque<String>()
    /** Último resultado del sondeo de enlace. */
    private val probeResult = MutableStateFlow<JsonObject?>(null)
    private val time = SimpleDateFormat("HH:mm:ss", Locale.US)

    /** Agrega una línea con hora a la bitácora (conserva 40). */
    private fun log(msg: String) = _state.update { it.copy(log = (listOf("${time.format(Date())}  $msg") + it.log).take(40)) }
    /** Cambia el nivel y el texto de estado. */
    private fun status(level: EngineState.Level, text: String) = _state.update { it.copy(level = level, status = text) }

    /**
     * Arranca la telemetría: carga la cola, crea solo los colectores de los sensores expuestos y lanza dos corrutinas,
     * el sondeo de enlace y el ciclo muestrear → enviar → esperar el intervalo.
     */
    fun start(context: Context) {
        if (scope != null) return
        val ctx = context.applicationContext
        val settings = Settings(ctx)
        queueFile = File(ctx.filesDir, "queue.jsonl")
        loadQueue()
        // Sensores genéricos del banco que el usuario dejó encendidos.
        val bank = SensorBankCollector.TYPES.keys.filter(settings::source).toSet()
        val collectors = buildMap<String, Collector> {
            if (settings.source("mot")) put("mot", MotionCollector(ctx))
            if (bank.isNotEmpty()) put("bank", SensorBankCollector(ctx, bank))
            if (settings.source("bat")) put("bat", BatteryCollector(ctx))
            if (settings.source("net")) put("net", NetCollector(ctx))
            if (settings.source("wifi")) put("wifi", WifiCollector(ctx))
            if (settings.source("cell")) put("cell", CellCollector(ctx))
        }
        // Un colector que falla (sensor ausente o permiso negado) no impide arrancar a los demás.
        collectors.values.forEach { runCatching { it.start() } }
        val api = Api(settings.serverUrl, settings.tlsPin)
        val s = CoroutineScope(SupervisorJob() + Dispatchers.IO)
        scope = s
        session = null
        _state.update { it.copy(running = true) }
        status(EngineState.Level.INFO, "Iniciando…")
        log("Telemetría iniciada · cada ${settings.intervalSec} s · ${settings.suite} · ${Settings.SOURCES.count { settings.source(it.id) }} sensores expuestos")

        if (settings.source("probe")) s.launch { probeLoop(api) }
        s.launch {
            while (isActive) {
                sample(settings, collectors)
                flush(api, settings)
                delay((settings.intervalSec * 1000).toLong())
            }
        }
        // Al cancelar el ámbito se detienen todos los colectores.
        s.coroutineContext[Job]?.invokeOnCompletion { collectors.values.forEach { runCatching { it.stop() } } }
    }

    /** Detiene la telemetría y olvida la sesión. La cola se conserva en disco. */
    fun stop() {
        scope?.coroutineContext?.get(Job)?.cancel()
        scope = null
        session = null
        _state.update { it.copy(running = false) }
        status(EngineState.Level.OFF, "Detenido")
        log("Telemetría detenida")
    }

    /** Arma una lectura con lo que tenga cada colector y la agrega a la cola. */
    private fun sample(settings: Settings, c: Map<String, Collector>) {
        packets++
        val t = System.currentTimeMillis()
        val bank = LinkedHashMap<String, JsonObject>().also { (c["bank"] as? SensorBankCollector)?.fill(it) }
        val probe = if (settings.source("probe")) probeResult.value else null
        val payload = buildJsonObject {
            put("t", t)
            put("src", "android")
            (c["mot"] as? MotionCollector)?.json(includeWindow = packets % 5 == 0L)?.let { put("mot", it) }
            bank.forEach { (k, v) -> put(k, v) }
            (c["bat"] as? BatteryCollector)?.json()?.let { put("bat", it) }
            // El sondeo viaja dentro de net aunque el tipo de red no esté expuesto.
            val net = c["net"] as? NetCollector
            if (net != null) net.json(probe)?.let { put("net", it) } else if (probe != null) put("net", buildJsonObject { put("probe", probe) })
            (c["wifi"] as? WifiCollector)?.json()?.let { put("wifi", it) }
            (c["cell"] as? CellCollector)?.json()?.let { put("cell", it) }
            put("app", buildJsonObject { put("vis", true); put("ver", BuildConfig.VERSION_NAME) })
        }
        enqueue(payload.toString())
        _state.update { st -> st.copy(lastPayload = json.encodeToString(JsonObject.serializer(), payload), sources = c.mapValues { it.value.status() }) }
    }

    /**
     * Envía la cola en orden, una lectura a la vez. Hace el handshake si no hay sesión y reacciona a cada código del
     * servidor: aceptado → se quita de la cola; sesión desconocida → nuevo handshake; token revocado o firma inválida →
     * se detiene; límite de solicitudes o red → espera con retroceso; otro rechazo → se descarta la lectura.
     */
    private suspend fun flush(api: Api, settings: Settings) {
        val token = settings.deviceToken ?: run { status(EngineState.Level.BAD, "Dispositivo sin registrar"); return }
        val did = settings.deviceId ?: return
        val pinned = settings.pinnedKey?.let { Base64.decode(it, Base64.NO_WRAP) } ?: run { status(EngineState.Level.BAD, "Huella sin fijar"); return }
        while (true) {
            val line = synchronized(queue) { queue.firstOrNull() } ?: break
            // El ts del sobre debe ser igual al "t" de la lectura.
            val t = Json.parseToJsonElement(line).let { (it as JsonObject)["t"].toString().toLong() }
            try {
                val sess = session ?: api.handshake(did, token, pinned, settings.suite).also {
                    session = it
                    log("Handshake OK · sesión ${it.sid} · ${it.keys.suite}")
                }
                val (code, env) = api.send(sess, token, t, line)
                _state.update { it.copy(lastWire = Api.envelopeJson(env), sessionId = sess.sid, suite = sess.keys.suite, seq = sess.seq) }
                when (code) {
                    "ok" -> {
                        dequeue(); failures = 0
                        _state.update { it.copy(ok = it.ok + 1, lastSendAt = System.currentTimeMillis()) }
                        status(EngineState.Level.OK, "Enviando")
                    }
                    "unknown_session" -> { session = null; log("Sesión desconocida (¿reinició el servidor?): nuevo handshake") }
                    "replay" -> log("seq repetido; se reintenta con el siguiente")
                    "unauthorized" -> { status(EngineState.Level.BAD, "Token revocado: vuelve a registrar el dispositivo"); log("401 del servidor"); stop(); return }
                    "rate_limited" -> { backoff(); return }
                    else -> { dequeue(); _state.update { it.copy(rejected = it.rejected + 1) }; log("Paquete rechazado: $code") }
                }
            } catch (e: SecurityException) {
                status(EngineState.Level.BAD, "Firma del servidor inválida (posible MITM)")
                log(e.message ?: "firma inválida"); stop(); return
            } catch (e: ApiException) {
                if (e.status == 401) { status(EngineState.Level.BAD, "Token revocado"); stop(); return }
                backoff(); return
            } catch (e: Exception) {
                log("Error: ${e.message}"); backoff(); return
            }
        }
    }

    /** Espera con retroceso exponencial: 1, 2, 4, … hasta 30 s. */
    private suspend fun backoff() {
        val wait = min(30_000L, 1000L shl min(failures++, 5))
        _state.update { it.copy(networkErrors = it.networkErrors + 1) }
        status(EngineState.Level.WARN, "Sin conexión (${queue.size} en cola) · reintento en ${wait / 1000} s")
        delay(wait)
    }

    /** 10 pings cada 15 s → RTT medio, jitter y pérdida; el rendimiento se mide en una de cada 6 rondas. */
    private suspend fun probeLoop(api: Api) {
        var round = 0
        while (scope != null) {
            val rtts = mutableListOf<Double>()
            var lost = 0
            repeat(10) { api.ping()?.let(rtts::add) ?: lost++ }
            val kbps = if (round++ % 6 == 0) api.throughputKbps() else null
            probeResult.value = buildJsonObject {
                put("loss", lost / 10.0)
                if (rtts.isNotEmpty()) {
                    put("rtt", Math.round(rtts.average() * 10) / 10.0)
                    // Jitter: media de la diferencia absoluta entre RTT consecutivos.
                    put("jit", if (rtts.size > 1) Math.round(rtts.zipWithNext { a, b -> abs(b - a) }.average() * 10) / 10.0 else 0.0)
                }
                kbps?.let { put("kbps", Math.round(it)) }
            }
            delay(15_000)
        }
    }

    // ---------- Cola persistente en claro (se cifra solo al enviar)
    /** Lee la cola del disco (las últimas 500 líneas). */
    private fun loadQueue() = synchronized(queue) {
        queue.clear()
        if (queueFile.exists()) queue.addAll(queueFile.readLines().filter { it.isNotBlank() }.takeLast(500))
        _state.update { it.copy(queued = queue.size) }
    }
    /** Escribe la cola completa en el disco. */
    private fun persist() { queueFile.writeText(queue.joinToString("\n")); _state.update { it.copy(queued = queue.size) } }
    /** Agrega una lectura al final y descarta las más antiguas si pasa de 500. */
    private fun enqueue(line: String) = synchronized(queue) { queue.addLast(line); while (queue.size > 500) queue.removeFirst(); persist() }
    /** Quita la primera lectura. */
    private fun dequeue() = synchronized(queue) { queue.removeFirstOrNull(); persist() }
    /** Vacía la cola (botón de la pantalla de estado). */
    fun clearQueue(context: Context) = synchronized(queue) {
        queueFile = File(context.applicationContext.filesDir, "queue.jsonl")
        queue.clear(); persist()
    }
}
