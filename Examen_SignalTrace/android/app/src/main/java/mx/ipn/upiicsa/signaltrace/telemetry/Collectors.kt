package mx.ipn.upiicsa.signaltrace.telemetry

import android.Manifest
import android.annotation.SuppressLint
import android.content.Context
import android.content.Intent
import android.content.IntentFilter
import android.content.pm.PackageManager
import android.hardware.Sensor
import android.hardware.SensorEvent
import android.hardware.SensorEventListener
import android.hardware.SensorManager
import android.location.Location
import android.location.LocationListener
import android.location.LocationManager
import android.net.ConnectivityManager
import android.net.Network
import android.net.NetworkCapabilities
import android.net.NetworkRequest
import android.net.wifi.WifiInfo
import android.net.wifi.WifiManager
import android.os.BatteryManager
import android.os.Build
import android.os.Looper
import android.telephony.CellIdentityLte
import android.telephony.CellIdentityNr
import android.telephony.CellInfo
import android.telephony.CellInfoGsm
import android.telephony.CellInfoLte
import android.telephony.CellInfoNr
import android.telephony.CellInfoWcdma
import android.telephony.CellSignalStrength
import android.telephony.CellSignalStrengthLte
import android.telephony.CellSignalStrengthNr
import android.telephony.TelephonyManager
import androidx.core.content.ContextCompat
import kotlinx.serialization.json.JsonObject
import kotlinx.serialization.json.JsonObjectBuilder
import kotlinx.serialization.json.buildJsonObject
import kotlinx.serialization.json.put
import kotlinx.serialization.json.putJsonArray
import kotlin.math.abs
import kotlin.math.round
import kotlin.math.sqrt

/** Each collector adds its part of the payload (PLAN §8) or nothing when the source is off, missing or denied. */
interface Collector {
    fun start() {}
    fun stop() {}
    fun status(): String
}

fun Context.granted(p: String) = ContextCompat.checkSelfPermission(this, p) == PackageManager.PERMISSION_GRANTED
private fun r(v: Double, d: Int): Double { val f = Math.pow(10.0, d.toDouble()); return round(v * f) / f }
private fun JsonObjectBuilder.putIf(k: String, v: Number?) { if (v != null) put(k, v) }

class LocationCollector(private val ctx: Context) : Collector, LocationListener {
    private val lm = ctx.getSystemService(LocationManager::class.java)
    @Volatile private var last: Location? = null

    @SuppressLint("MissingPermission")
    override fun start() {
        if (!ctx.granted(Manifest.permission.ACCESS_FINE_LOCATION) && !ctx.granted(Manifest.permission.ACCESS_COARSE_LOCATION)) return
        for (provider in listOf(LocationManager.GPS_PROVIDER, LocationManager.NETWORK_PROVIDER)) {
            if (lm.isProviderEnabled(provider)) lm.requestLocationUpdates(provider, 1000L, 0f, this, Looper.getMainLooper())
        }
    }

    override fun stop() = lm.removeUpdates(this)
    override fun onLocationChanged(location: Location) {
        // Prefer GPS fixes; accept network fixes when they are newer by more than 10 s.
        val l = last
        if (l == null || location.provider == LocationManager.GPS_PROVIDER || location.time - l.time > 10_000) last = location
    }
    override fun status() = last?.let { "±${it.accuracy.toInt()} m (${it.provider})" } ?: if (ctx.granted(Manifest.permission.ACCESS_FINE_LOCATION)) "esperando señal" else "sin permiso"

    fun json(): JsonObject? {
        val l = last ?: return null
        if (System.currentTimeMillis() - l.time > 30_000) return null
        return buildJsonObject {
            put("lat", r(l.latitude, 6)); put("lon", r(l.longitude, 6)); put("acc", r(l.accuracy.toDouble(), 1))
            if (l.hasAltitude()) put("alt", r(l.altitude, 1))
            if (l.hasSpeed()) put("spd", r(l.speed.toDouble(), 2))
            if (l.hasBearing()) put("hdg", r(l.bearing.toDouble(), 0))
        }
    }
}

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

class BatteryCollector(private val ctx: Context) : Collector {
    private fun read(): Pair<Double, Boolean>? {
        val i: Intent = ctx.registerReceiver(null, IntentFilter(Intent.ACTION_BATTERY_CHANGED)) ?: return null
        val level = i.getIntExtra(BatteryManager.EXTRA_LEVEL, -1)
        val scale = i.getIntExtra(BatteryManager.EXTRA_SCALE, -1)
        if (level < 0 || scale <= 0) return null
        val status = i.getIntExtra(BatteryManager.EXTRA_STATUS, -1)
        return level.toDouble() / scale to (status == BatteryManager.BATTERY_STATUS_CHARGING || status == BatteryManager.BATTERY_STATUS_FULL)
    }
    override fun status() = read()?.let { "${(it.first * 100).toInt()} %${if (it.second) " (cargando)" else ""}" } ?: "no disponible"
    fun json() = read()?.let { (lvl, chg) -> buildJsonObject { put("lvl", r(lvl, 2)); put("chg", chg) } }
}

/** Connected Wi-Fi: RSSI, frequency, link speed (+ SSID/BSSID if location permission; optionally anonymised). */
class WifiCollector(private val ctx: Context, private val anonymize: (String) -> String?) : Collector {
    private val cm = ctx.getSystemService(ConnectivityManager::class.java)
    private val wm = ctx.applicationContext.getSystemService(WifiManager::class.java)
    @Volatile private var info: WifiInfo? = null
    private var callback: ConnectivityManager.NetworkCallback? = null

    override fun start() {
        val req = NetworkRequest.Builder().addTransportType(NetworkCapabilities.TRANSPORT_WIFI).build()
        val cb = if (Build.VERSION.SDK_INT >= 31) object : ConnectivityManager.NetworkCallback(FLAG_INCLUDE_LOCATION_INFO) {
            override fun onCapabilitiesChanged(network: Network, caps: NetworkCapabilities) { info = caps.transportInfo as? WifiInfo }
            override fun onLost(network: Network) { info = null }
        } else object : ConnectivityManager.NetworkCallback() {
            override fun onLost(network: Network) { info = null }
        }
        callback = cb
        runCatching { cm.registerNetworkCallback(req, cb) }
    }

    override fun stop() { callback?.let { runCatching { cm.unregisterNetworkCallback(it) } }; callback = null }

    @Suppress("DEPRECATION")
    private fun current(): WifiInfo? = if (Build.VERSION.SDK_INT >= 31) info else wm?.connectionInfo?.takeIf { it.networkId != -1 }

    override fun status() = current()?.let { "${it.rssi} dBm · ${it.frequency} MHz" } ?: "sin Wi-Fi"

    fun json(): JsonObject? {
        val w = current() ?: return null
        if (w.rssi <= -127 || w.rssi >= 0) return null
        return buildJsonObject {
            put("rssi", w.rssi); put("freq", w.frequency); if (w.linkSpeed > 0) put("link", w.linkSpeed)
            if (ctx.granted(Manifest.permission.ACCESS_FINE_LOCATION)) {
                @Suppress("DEPRECATION") val ssid = w.ssid?.trim('"')?.takeIf { it.isNotEmpty() && it != "<unknown ssid>" }
                val bssid = w.bssid?.takeIf { it != "02:00:00:00:00:00" }
                ssid?.let { s -> anonymize(s)?.let { put("ssid", it) } }
                bssid?.let { b -> anonymize(b)?.let { put("bssid", it) } }
            }
        }
    }
}

/** Serving cell: technology, dBm, level, RSRP/RSRQ/SNR (LTE/NR) and identifiers (need location permission). */
class CellCollector(private val ctx: Context, private val anonymize: (String) -> String?) : Collector {
    private val tm = ctx.getSystemService(TelephonyManager::class.java)

    @SuppressLint("MissingPermission")
    private fun registered(): CellInfo? =
        if (ctx.granted(Manifest.permission.ACCESS_FINE_LOCATION)) runCatching { tm.allCellInfo?.firstOrNull { it.isRegistered } }.getOrNull() else null

    private fun strength(): CellSignalStrength? {
        if (Build.VERSION.SDK_INT >= 28) runCatching { tm.signalStrength?.cellSignalStrengths?.firstOrNull() }.getOrNull()?.let { return it }
        return when (val c = registered()) {
            is CellInfoLte -> c.cellSignalStrength
            is CellInfoGsm -> c.cellSignalStrength
            is CellInfoWcdma -> c.cellSignalStrength
            else -> if (Build.VERSION.SDK_INT >= 29 && c is CellInfoNr) c.cellSignalStrength else null
        }
    }

    override fun status() = strength()?.let { "${it.dbm} dBm · nivel ${it.level}" } ?: "sin datos de celular"

    fun json(): JsonObject? {
        val s = strength() ?: return null
        if (s.dbm == Int.MAX_VALUE || s.dbm >= 0) return null
        val cell = registered()
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
            if (cell is CellInfoLte) identity(cell.cellIdentity)
            if (Build.VERSION.SDK_INT >= 29 && cell is CellInfoNr) (cell.cellIdentity as? CellIdentityNr)?.let { id ->
                id.mccString?.toIntOrNull()?.let { put("mcc", it) }; id.mncString?.toIntOrNull()?.let { put("mnc", it) }
                id.tac.takeIf { it != Int.MAX_VALUE }?.let { put("tac", it) }
            }
        }
    }

    private fun JsonObjectBuilder.identity(id: CellIdentityLte) {
        val mcc = if (Build.VERSION.SDK_INT >= 28) id.mccString?.toIntOrNull() else @Suppress("DEPRECATION") id.mcc.takeIf { it != Int.MAX_VALUE }
        val mnc = if (Build.VERSION.SDK_INT >= 28) id.mncString?.toIntOrNull() else @Suppress("DEPRECATION") id.mnc.takeIf { it != Int.MAX_VALUE }
        putIf("mcc", mcc); putIf("mnc", mnc)
        id.tac.takeIf { it != Int.MAX_VALUE }?.let { put("tac", it) }
        // The cell id identifies a location: it is sent only as a number when anonymisation is off.
        id.ci.takeIf { it != Int.MAX_VALUE }?.let { ci -> if (anonymize(ci.toString()) == ci.toString()) put("cid", ci) }
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
