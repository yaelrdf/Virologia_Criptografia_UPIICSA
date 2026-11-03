package mx.ipn.upiicsa.signaltrace.telemetry

import android.Manifest
import android.content.Context
import android.content.Intent
import android.content.IntentFilter
import android.content.pm.PackageManager
import android.hardware.Sensor
import android.hardware.SensorEvent
import android.hardware.SensorEventListener
import android.hardware.SensorManager
import android.net.ConnectivityManager
import android.net.Network
import android.net.NetworkCapabilities
import android.net.NetworkRequest
import android.net.wifi.WifiInfo
import android.net.wifi.WifiManager
import android.os.BatteryManager
import android.os.Build
import android.telephony.CellSignalStrength
import android.telephony.CellSignalStrengthLte
import android.telephony.CellSignalStrengthNr
import android.telephony.TelephonyManager
import androidx.core.content.ContextCompat
import kotlinx.serialization.json.JsonObject
import kotlinx.serialization.json.buildJsonObject
import kotlinx.serialization.json.put
import kotlinx.serialization.json.putJsonArray
import kotlin.math.abs
import kotlin.math.round
import kotlin.math.sqrt

/** Cada colector agrega su parte de la carga (banco de sensores), o nada si el sensor está apagado, no existe o se negó el permiso. */
interface Collector {
    /** Empieza a escuchar el sensor o la fuente. */
    fun start() {}
    /** Deja de escuchar. */
    fun stop() {}
    /** Texto de estado para la pantalla. */
    fun status(): String
}

/** Indica si la aplicación tiene concedido un permiso. */
fun Context.granted(p: String) = ContextCompat.checkSelfPermission(this, p) == PackageManager.PERMISSION_GRANTED
/** Redondea v a d decimales. */
private fun r(v: Double, d: Int): Double { val f = Math.pow(10.0, d.toDouble()); return round(v * f) / f }

/**
 * Acelerómetro remuestreado a 32 Hz. Por intervalo: ejes medios, RMS y pico de la parte dinámica de |a|; además,
 * una ventana de 64 muestras para la DFT del servidor.
 */
class MotionCollector(ctx: Context) : Collector, SensorEventListener {
    private val sm = ctx.getSystemService(SensorManager::class.java)
    private val sensor = sm.getDefaultSensor(Sensor.TYPE_ACCELEROMETER)
    /** Últimas 64 magnitudes. */
    private val window = ArrayDeque<Double>()
    /** Muestras del intervalo actual: [x, y, z, |a|]. */
    private val interval = ArrayList<DoubleArray>()
    /** Marca de tiempo de la última muestra aceptada (ns). */
    private var lastNs = 0L

    override fun start() { sensor?.let { sm.registerListener(this, it, SensorManager.SENSOR_DELAY_GAME) } }
    override fun stop() = sm.unregisterListener(this)
    override fun onAccuracyChanged(s: Sensor?, a: Int) {}

    /** Descarta eventos demasiado seguidos para quedarse cerca de 32 Hz y guarda la muestra. */
    override fun onSensorChanged(e: SensorEvent) {
        if (e.timestamp - lastNs < 1_000_000_000L / FS - 2_000_000) return
        lastNs = e.timestamp
        val (x, y, z) = Triple(e.values[0].toDouble(), e.values[1].toDouble(), e.values[2].toDouble())
        val m = sqrt(x * x + y * y + z * z)
        // El sensor escribe en su propio hilo; el envío lee en otro.
        synchronized(this) {
            interval.add(doubleArrayOf(x, y, z, m))
            window.addLast(m)
            if (window.size > 64) window.removeFirst()
        }
    }

    override fun status() = if (sensor == null) "sin acelerómetro" else "${window.size} muestras a $FS Hz"

    /**
     * Bloque mot del intervalo y vacía las muestras; null si no hubo ninguna.
     * @param includeWindow Si se adjunta la ventana de 64 magnitudes para el espectro.
     */
    fun json(includeWindow: Boolean): JsonObject? = synchronized(this) {
        if (interval.isEmpty()) return null
        val n = interval.size
        fun mean(i: Int) = interval.sumOf { it[i] } / n
        // Parte dinámica: magnitud menos su media (quita la gravedad).
        val mm = mean(3)
        val dyn = interval.map { it[3] - mm }
        val obj = buildJsonObject {
            put("ax", r(mean(0), 3)); put("ay", r(mean(1), 3)); put("az", r(mean(2), 3))
            put("rms", r(sqrt(dyn.sumOf { it * it } / n), 4)); put("peak", r(dyn.maxOf { abs(it) }, 4)); put("fs", FS)
            if (includeWindow && window.size == 64) putJsonArray("mag") { window.forEach { add(kotlinx.serialization.json.JsonPrimitive(r(it, 3))) } }
        }
        interval.clear()
        obj
    }

    companion object { const val FS = 32 }
}

/** Batería: nivel, si está cargando, temperatura (°C), voltaje (V), salud y fuente de energía. */
class BatteryCollector(private val ctx: Context) : Collector {
    /** Último intent «pegajoso» ACTION_BATTERY_CHANGED (se lee sin registrar un receptor). */
    private fun intent(): Intent? = ctx.registerReceiver(null, IntentFilter(Intent.ACTION_BATTERY_CHANGED))

    /** Nivel de 0 a 1, o null si el sistema no lo reporta. */
    private fun level(i: Intent): Double? {
        val level = i.getIntExtra(BatteryManager.EXTRA_LEVEL, -1)
        val scale = i.getIntExtra(BatteryManager.EXTRA_SCALE, -1)
        return if (level < 0 || scale <= 0) null else level.toDouble() / scale
    }

    /** Si está cargando o ya llena. */
    private fun charging(i: Intent) = i.getIntExtra(BatteryManager.EXTRA_STATUS, -1).let {
        it == BatteryManager.BATTERY_STATUS_CHARGING || it == BatteryManager.BATTERY_STATUS_FULL
    }

    override fun status() = intent()?.let { i -> level(i)?.let { "${(it * 100).toInt()} %${if (charging(i)) " (cargando)" else ""}" } } ?: "no disponible"

    /** Bloque bat de la carga. La temperatura viene en décimas de grado y el voltaje en milivoltios. */
    fun json(): JsonObject? {
        val i = intent() ?: return null
        val lvl = level(i) ?: return null
        return buildJsonObject {
            put("lvl", r(lvl, 2)); put("chg", charging(i))
            i.getIntExtra(BatteryManager.EXTRA_TEMPERATURE, Int.MIN_VALUE).takeIf { it != Int.MIN_VALUE }?.let { put("temp", it / 10.0) }
            i.getIntExtra(BatteryManager.EXTRA_VOLTAGE, -1).takeIf { it > 0 }?.let { put("volt", r(it / 1000.0, 3)) }
            put("health", when (i.getIntExtra(BatteryManager.EXTRA_HEALTH, -1)) {
                BatteryManager.BATTERY_HEALTH_GOOD -> "good"; BatteryManager.BATTERY_HEALTH_OVERHEAT -> "overheat"
                BatteryManager.BATTERY_HEALTH_DEAD -> "dead"; BatteryManager.BATTERY_HEALTH_COLD -> "cold"
                BatteryManager.BATTERY_HEALTH_OVER_VOLTAGE -> "over_voltage"; else -> "unknown"
            })
            put("plug", when (i.getIntExtra(BatteryManager.EXTRA_PLUGGED, 0)) {
                BatteryManager.BATTERY_PLUGGED_AC -> "ac"; BatteryManager.BATTERY_PLUGGED_USB -> "usb"
                BatteryManager.BATTERY_PLUGGED_WIRELESS -> "wireless"; else -> "none"
            })
        }
    }
}

/** Calidad del enlace Wi-Fi conectado: RSSI, frecuencia y velocidades de enlace, transmisión y recepción. Sin SSID ni BSSID (revelan el lugar). */
class WifiCollector(ctx: Context) : Collector {
    private val cm = ctx.getSystemService(ConnectivityManager::class.java)
    private val wm = ctx.applicationContext.getSystemService(WifiManager::class.java)
    /** Última información de la red Wi-Fi recibida por el callback. */
    @Volatile private var info: WifiInfo? = null
    private var callback: ConnectivityManager.NetworkCallback? = null

    /**
     * Registra un callback de red Wi-Fi. En Android 12+ es la forma de obtener el RSSI sin el permiso de ubicación:
     * WifiInfo llega dentro de las capacidades de la red.
     */
    override fun start() {
        val req = NetworkRequest.Builder().addTransportType(NetworkCapabilities.TRANSPORT_WIFI).build()
        val cb = object : ConnectivityManager.NetworkCallback() {
            override fun onCapabilitiesChanged(network: Network, caps: NetworkCapabilities) {
                if (Build.VERSION.SDK_INT >= 29) info = caps.transportInfo as? WifiInfo
            }
            override fun onLost(network: Network) { info = null }
        }
        callback = cb
        runCatching { cm.registerNetworkCallback(req, cb) }
    }

    override fun stop() { callback?.let { runCatching { cm.unregisterNetworkCallback(it) } }; callback = null }

    /** Información actual: la del callback en Android 12+, la de WifiManager (obsoleta) en versiones anteriores. */
    @Suppress("DEPRECATION")
    private fun current(): WifiInfo? = if (Build.VERSION.SDK_INT >= 31) info else wm?.connectionInfo?.takeIf { it.networkId != -1 || it.rssi > -127 }

    override fun status() = current()?.let { "${it.rssi} dBm · ${it.frequency} MHz" } ?: "sin Wi-Fi"

    /** Bloque wifi de la carga; null si no hay Wi-Fi o el RSSI no es válido. */
    fun json(): JsonObject? {
        val w = current() ?: return null
        if (w.rssi <= -127 || w.rssi >= 0) return null
        return buildJsonObject {
            put("rssi", w.rssi); put("freq", w.frequency); if (w.linkSpeed > 0) put("link", w.linkSpeed)
            if (Build.VERSION.SDK_INT >= 29) {
                w.txLinkSpeedMbps.takeIf { it > 0 }?.let { put("tx", it) }
                w.rxLinkSpeedMbps.takeIf { it > 0 }?.let { put("rx", it) }
            }
        }
    }
}

/**
 * Intensidad de la señal de la celda en uso (API 28+: TelephonyManager.getSignalStrength, sin permiso de ubicación):
 * tecnología, dBm, nivel y RSRP/RSRQ/SNR para LTE y NR. Nunca se leen los identificadores de celda.
 */
class CellCollector(ctx: Context) : Collector {
    private val tm = ctx.getSystemService(TelephonyManager::class.java)

    /** Primera medición de señal disponible, o null. */
    private fun strength(): CellSignalStrength? =
        if (Build.VERSION.SDK_INT >= 28) runCatching { tm?.signalStrength?.cellSignalStrengths?.firstOrNull() }.getOrNull() else null

    override fun status() = strength()?.let { "${it.dbm} dBm · nivel ${it.level}" } ?: if (Build.VERSION.SDK_INT < 28) "requiere Android 9" else "sin datos de celular"

    /** Bloque cell de la carga. Int.MAX_VALUE significa «no disponible» en la API de Android. */
    fun json(): JsonObject? {
        val s = strength() ?: return null
        if (s.dbm == Int.MAX_VALUE || s.dbm >= 0) return null
        return buildJsonObject {
            put("type", when {
                s is CellSignalStrengthLte -> "LTE"
                Build.VERSION.SDK_INT >= 29 && s is CellSignalStrengthNr -> "NR"
                else -> s.javaClass.simpleName.removePrefix("CellSignalStrength")
            })
            put("dbm", s.dbm); put("level", s.level)
            if (s is CellSignalStrengthLte) {
                s.rsrp.takeIf { it != Int.MAX_VALUE }?.let { put("rsrp", it) }
                s.rsrq.takeIf { it != Int.MAX_VALUE }?.let { put("rsrq", it) }
                // El RSSNR de LTE viene en décimas de dB.
                s.rssnr.takeIf { it != Int.MAX_VALUE }?.let { put("snr", it / 10.0) }
            }
            if (Build.VERSION.SDK_INT >= 29 && s is CellSignalStrengthNr) {
                s.ssRsrp.takeIf { it != Int.MAX_VALUE }?.let { put("rsrp", it) }
                s.ssRsrq.takeIf { it != Int.MAX_VALUE }?.let { put("rsrq", it) }
                s.ssSinr.takeIf { it != Int.MAX_VALUE }?.let { put("snr", it.toDouble()) }
            }
        }
    }
}

/** Tipo de la red activa y su ancho de banda de bajada estimado; también transporta el resultado del sondeo. */
class NetCollector(ctx: Context) : Collector {
    private val cm = ctx.getSystemService(ConnectivityManager::class.java)
    /** Capacidades de la red activa. */
    private fun caps() = cm.getNetworkCapabilities(cm.activeNetwork)
    /** Tipo de transporte: wifi, cellular, ethernet u other. */
    private fun type(c: NetworkCapabilities?) = when {
        c == null -> null
        c.hasTransport(NetworkCapabilities.TRANSPORT_WIFI) -> "wifi"
        c.hasTransport(NetworkCapabilities.TRANSPORT_CELLULAR) -> "cellular"
        c.hasTransport(NetworkCapabilities.TRANSPORT_ETHERNET) -> "ethernet"
        else -> "other"
    }
    override fun status() = type(caps()) ?: "sin red"
    /** Bloque net de la carga, con el sondeo dentro si lo hay. */
    fun json(probe: JsonObject?): JsonObject? {
        val c = caps()
        val t = type(c) ?: return probe?.let { buildJsonObject { put("probe", it) } }
        return buildJsonObject {
            put("type", t)
            c?.linkDownstreamBandwidthKbps?.takeIf { it > 0 }?.let { put("down", r(it / 1000.0, 1)) }
            probe?.let { put("probe", it) }
        }
    }
}

/**
 * Todos los demás sensores físicos del teléfono, a través de SensorManager. Los vectores (lin, grv, gyr, mfl) y los
 * valores de entorno se promedian en el intervalo; la orientación sale del vector de rotación (azimut, cabeceo y
 * alabeo en grados); los pasos se cuentan desde el inicio de la transmisión (TYPE_STEP_COUNTER acumula desde el
 * encendido del teléfono).
 */
class SensorBankCollector(private val ctx: Context, private val enabled: Set<String>) : Collector, SensorEventListener {
    private val sm = ctx.getSystemService(SensorManager::class.java)
    private val sums = HashMap<String, DoubleArray>()   // id → [x, y, z, n] o [v, 0, 0, n]
    /** Último vector de rotación recibido. */
    @Volatile private var rotation: FloatArray? = null
    /** Lectura del contador de pasos al empezar (−1 hasta la primera). */
    private var stepsAtStart = -1f
    /** Pasos desde el inicio. */
    @Volatile private var steps: Long? = null
    /** Tipo de sensor de Android → id del bloque. */
    private val registered = HashMap<Int, String>()

    /** Registra los sensores elegidos que existen; el podómetro solo si se concedió el permiso de actividad física. */
    override fun start() {
        for ((id, type) in TYPES) {
            if (id !in enabled) continue
            if (id == "stp" && Build.VERSION.SDK_INT >= 29 && !ctx.granted(Manifest.permission.ACTIVITY_RECOGNITION)) continue
            val s = sm.getDefaultSensor(type) ?: continue
            registered[type] = id
            sm.registerListener(this, s, SensorManager.SENSOR_DELAY_NORMAL)
        }
    }

    override fun stop() { sm.unregisterListener(this); registered.clear() }
    override fun onAccuracyChanged(s: Sensor?, a: Int) {}

    /** Acumula cada evento según su tipo: rotación y pasos guardan el último valor; el resto suma para promediar. */
    override fun onSensorChanged(e: SensorEvent) {
        val id = registered[e.sensor.type] ?: return
        when (id) {
            "ori" -> rotation = e.values.copyOf()
            "stp" -> { if (stepsAtStart < 0) stepsAtStart = e.values[0]; steps = (e.values[0] - stepsAtStart).toLong() }
            else -> synchronized(sums) {
                val a = sums.getOrPut(id) { DoubleArray(4) }
                a[0] += e.values[0].toDouble()
                if (e.values.size >= 3 && id in VECTORS) { a[1] += e.values[1].toDouble(); a[2] += e.values[2].toDouble() }
                a[3] += 1.0
            }
        }
    }

    /** Cuántos sensores están activos y cuáles de los elegidos no tiene el teléfono. */
    override fun status(): String {
        val missing = enabled.filter { it in TYPES && sm.getDefaultSensor(TYPES[it]!!) == null }
        return "${registered.size} sensores activos" + if (missing.isNotEmpty()) " · sin ${missing.joinToString()}" else ""
    }

    /** Agrega los bloques lin, grv, gyr, mfl, ori, env y stp con los datos recibidos desde la llamada anterior. */
    fun fill(out: MutableMap<String, JsonObject>) {
        // Toma una copia de las sumas y las reinicia para el siguiente intervalo.
        val snap = synchronized(sums) { HashMap(sums).also { sums.clear() } }
        for (id in VECTORS) snap[id]?.let { a -> val n = a[3]; out[id] = buildJsonObject { put("x", r(a[0] / n, 3)); put("y", r(a[1] / n, 3)); put("z", r(a[2] / n, 3)) } }
        // Vector de rotación → matriz de rotación → ángulos (radianes) → grados.
        rotation?.let { rv ->
            val m = FloatArray(9); val o = FloatArray(3)
            SensorManager.getRotationMatrixFromVector(m, rv)
            SensorManager.getOrientation(m, o)
            val deg = o.map { Math.toDegrees(it.toDouble()) }
            out["ori"] = buildJsonObject { put("az", r((deg[0] + 360) % 360, 1)); put("pi", r(deg[1], 1)); put("ro", r(deg[2], 1)) }
        }
        val env = ENV.mapNotNull { id -> snap[id]?.let { a -> id to a[0] / a[3] } }
        if (env.isNotEmpty()) out["env"] = buildJsonObject { env.forEach { (id, v) -> put(id, r(v, if (id == "hpa") 2 else 1)) } }
        steps?.let { n -> out["stp"] = buildJsonObject { put("n", n) } }
    }

    companion object {
        /** Id del bloque → tipo de sensor de Android. */
        val TYPES = linkedMapOf(
            "lin" to Sensor.TYPE_LINEAR_ACCELERATION, "grv" to Sensor.TYPE_GRAVITY, "gyr" to Sensor.TYPE_GYROSCOPE,
            "mfl" to Sensor.TYPE_MAGNETIC_FIELD, "ori" to Sensor.TYPE_ROTATION_VECTOR, "lux" to Sensor.TYPE_LIGHT,
            "prx" to Sensor.TYPE_PROXIMITY, "hpa" to Sensor.TYPE_PRESSURE, "tmp" to Sensor.TYPE_AMBIENT_TEMPERATURE,
            "hum" to Sensor.TYPE_RELATIVE_HUMIDITY, "stp" to Sensor.TYPE_STEP_COUNTER,
        )
        /** Sensores de tres ejes. */
        private val VECTORS = listOf("lin", "grv", "gyr", "mfl")
        /** Sensores de un solo valor que van en el bloque env. */
        private val ENV = listOf("lux", "prx", "hpa", "tmp", "hum")

        /** Si este teléfono tiene cada sensor del banco (el acelerómetro se incluye como "mot"). */
        fun availability(ctx: Context): Map<String, Boolean> {
            val sm = ctx.getSystemService(SensorManager::class.java)
            return TYPES.mapValues { sm.getDefaultSensor(it.value) != null } + ("mot" to (sm.getDefaultSensor(Sensor.TYPE_ACCELEROMETER) != null))
        }
    }
}
