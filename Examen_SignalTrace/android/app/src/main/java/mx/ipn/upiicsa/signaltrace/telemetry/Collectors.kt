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

/** Each collector adds its part of the payload (sensor bank) or nothing when the sensor is off, missing or denied. */
interface Collector {
    fun start() {}
    fun stop() {}
    fun status(): String
}

fun Context.granted(p: String) = ContextCompat.checkSelfPermission(this, p) == PackageManager.PERMISSION_GRANTED
private fun r(v: Double, d: Int): Double { val f = Math.pow(10.0, d.toDouble()); return round(v * f) / f }

/** Accelerometer resampled to 32 Hz; per interval: mean axes, RMS and peak of the dynamic |a|; window of 64 samples for the DFT. */
class MotionCollector(ctx: Context) : Collector, SensorEventListener {
    private val sm = ctx.getSystemService(SensorManager::class.java)
    private val sensor = sm.getDefaultSensor(Sensor.TYPE_ACCELEROMETER)
    private val window = ArrayDeque<Double>()
    private val interval = ArrayList<DoubleArray>()
    private var lastNs = 0L

    override fun start() { sensor?.let { sm.registerListener(this, it, SensorManager.SENSOR_DELAY_GAME) } }
    override fun stop() = sm.unregisterListener(this)
    override fun onAccuracyChanged(s: Sensor?, a: Int) {}

    override fun onSensorChanged(e: SensorEvent) {
        if (e.timestamp - lastNs < 1_000_000_000L / FS - 2_000_000) return
        lastNs = e.timestamp
        val (x, y, z) = Triple(e.values[0].toDouble(), e.values[1].toDouble(), e.values[2].toDouble())
        val m = sqrt(x * x + y * y + z * z)
        synchronized(this) {
            interval.add(doubleArrayOf(x, y, z, m))
            window.addLast(m)
            if (window.size > 64) window.removeFirst()
        }
    }

    override fun status() = if (sensor == null) "sin acelerómetro" else "${window.size} muestras a $FS Hz"

    fun json(includeWindow: Boolean): JsonObject? = synchronized(this) {
        if (interval.isEmpty()) return null
        val n = interval.size
        fun mean(i: Int) = interval.sumOf { it[i] } / n
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

/** Battery: level, charging, temperature (°C), voltage (V), health and power source. */
class BatteryCollector(private val ctx: Context) : Collector {
    private fun intent(): Intent? = ctx.registerReceiver(null, IntentFilter(Intent.ACTION_BATTERY_CHANGED))

    private fun level(i: Intent): Double? {
        val level = i.getIntExtra(BatteryManager.EXTRA_LEVEL, -1)
        val scale = i.getIntExtra(BatteryManager.EXTRA_SCALE, -1)
        return if (level < 0 || scale <= 0) null else level.toDouble() / scale
    }

    private fun charging(i: Intent) = i.getIntExtra(BatteryManager.EXTRA_STATUS, -1).let {
        it == BatteryManager.BATTERY_STATUS_CHARGING || it == BatteryManager.BATTERY_STATUS_FULL
    }

    override fun status() = intent()?.let { i -> level(i)?.let { "${(it * 100).toInt()} %${if (charging(i)) " (cargando)" else ""}" } } ?: "no disponible"

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

/** Connected Wi-Fi link quality: RSSI, frequency and link/tx/rx speeds. No SSID or BSSID (they reveal the place). */
class WifiCollector(ctx: Context) : Collector {
    private val cm = ctx.getSystemService(ConnectivityManager::class.java)
    private val wm = ctx.applicationContext.getSystemService(WifiManager::class.java)
    @Volatile private var info: WifiInfo? = null
    private var callback: ConnectivityManager.NetworkCallback? = null

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

    @Suppress("DEPRECATION")
    private fun current(): WifiInfo? = if (Build.VERSION.SDK_INT >= 31) info else wm?.connectionInfo?.takeIf { it.networkId != -1 || it.rssi > -127 }

    override fun status() = current()?.let { "${it.rssi} dBm · ${it.frequency} MHz" } ?: "sin Wi-Fi"

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
 * Serving cell signal strength (API 28+: TelephonyManager.getSignalStrength, no location permission needed):
 * technology, dBm, level and RSRP/RSRQ/SNR for LTE/NR. Cell identifiers are never read.
 */
class CellCollector(ctx: Context) : Collector {
    private val tm = ctx.getSystemService(TelephonyManager::class.java)

    private fun strength(): CellSignalStrength? =
        if (Build.VERSION.SDK_INT >= 28) runCatching { tm?.signalStrength?.cellSignalStrengths?.firstOrNull() }.getOrNull() else null

    override fun status() = strength()?.let { "${it.dbm} dBm · nivel ${it.level}" } ?: if (Build.VERSION.SDK_INT < 28) "requiere Android 9" else "sin datos de celular"

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

class NetCollector(ctx: Context) : Collector {
    private val cm = ctx.getSystemService(ConnectivityManager::class.java)
    private fun caps() = cm.getNetworkCapabilities(cm.activeNetwork)
    private fun type(c: NetworkCapabilities?) = when {
        c == null -> null
        c.hasTransport(NetworkCapabilities.TRANSPORT_WIFI) -> "wifi"
        c.hasTransport(NetworkCapabilities.TRANSPORT_CELLULAR) -> "cellular"
        c.hasTransport(NetworkCapabilities.TRANSPORT_ETHERNET) -> "ethernet"
        else -> "other"
    }
    override fun status() = type(caps()) ?: "sin red"
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
 * Every other physical sensor of the phone through SensorManager. Vectors (lin, grv, gyr, mfl) and environment
 * values are averaged over the interval; orientation comes from the rotation vector (azimuth, pitch, roll in degrees);
 * steps are counted from the start of the transmission (TYPE_STEP_COUNTER is cumulative since boot).
 */
class SensorBankCollector(private val ctx: Context, private val enabled: Set<String>) : Collector, SensorEventListener {
    private val sm = ctx.getSystemService(SensorManager::class.java)
    private val sums = HashMap<String, DoubleArray>()   // id → [x, y, z, n] or [v, 0, 0, n]
    @Volatile private var rotation: FloatArray? = null
    private var stepsAtStart = -1f
    @Volatile private var steps: Long? = null
    private val registered = HashMap<Int, String>()

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

    override fun status(): String {
        val missing = enabled.filter { it in TYPES && sm.getDefaultSensor(TYPES[it]!!) == null }
        return "${registered.size} sensores activos" + if (missing.isNotEmpty()) " · sin ${missing.joinToString()}" else ""
    }

    /** Adds lin, grv, gyr, mfl, ori, env and stp blocks with the data received since the previous call. */
    fun fill(out: MutableMap<String, JsonObject>) {
        val snap = synchronized(sums) { HashMap(sums).also { sums.clear() } }
        for (id in VECTORS) snap[id]?.let { a -> val n = a[3]; out[id] = buildJsonObject { put("x", r(a[0] / n, 3)); put("y", r(a[1] / n, 3)); put("z", r(a[2] / n, 3)) } }
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
        val TYPES = linkedMapOf(
            "lin" to Sensor.TYPE_LINEAR_ACCELERATION, "grv" to Sensor.TYPE_GRAVITY, "gyr" to Sensor.TYPE_GYROSCOPE,
            "mfl" to Sensor.TYPE_MAGNETIC_FIELD, "ori" to Sensor.TYPE_ROTATION_VECTOR, "lux" to Sensor.TYPE_LIGHT,
            "prx" to Sensor.TYPE_PROXIMITY, "hpa" to Sensor.TYPE_PRESSURE, "tmp" to Sensor.TYPE_AMBIENT_TEMPERATURE,
            "hum" to Sensor.TYPE_RELATIVE_HUMIDITY, "stp" to Sensor.TYPE_STEP_COUNTER,
        )
        private val VECTORS = listOf("lin", "grv", "gyr", "mfl")
        private val ENV = listOf("lux", "prx", "hpa", "tmp", "hum")

        /** Whether this phone has each sensor of the bank (accelerometer included as "mot"). */
        fun availability(ctx: Context): Map<String, Boolean> {
            val sm = ctx.getSystemService(SensorManager::class.java)
            return TYPES.mapValues { sm.getDefaultSensor(it.value) != null } + ("mot" to (sm.getDefaultSensor(Sensor.TYPE_ACCELEROMETER) != null))
        }
    }
}
