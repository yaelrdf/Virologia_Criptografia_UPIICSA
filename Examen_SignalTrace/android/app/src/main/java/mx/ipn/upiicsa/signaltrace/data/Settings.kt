package mx.ipn.upiicsa.signaltrace.data

import android.content.Context
import android.security.keystore.KeyGenParameterSpec
import android.security.keystore.KeyProperties
import android.util.Base64
import mx.ipn.upiicsa.signaltrace.BuildConfig
import mx.ipn.upiicsa.signaltrace.crypto.Protocol
import java.security.KeyStore
import javax.crypto.Cipher
import javax.crypto.KeyGenerator
import javax.crypto.SecretKey
import javax.crypto.spec.GCMParameterSpec

/**
 * Secretos guardados en el teléfono: AES-256-GCM con una llave no exportable dentro del Android Keystore.
 * El token del dispositivo solo se guarda de esta forma cifrada.
 */
object SecureStore {
    /** Nombre de la llave en el Keystore. */
    private const val ALIAS = "signaltrace-secrets"

    /** Obtiene la llave del Keystore o la crea la primera vez (AES-256, modo GCM, sin relleno). */
    private fun key(): SecretKey {
        val ks = KeyStore.getInstance("AndroidKeyStore").apply { load(null) }
        (ks.getKey(ALIAS, null) as? SecretKey)?.let { return it }
        val gen = KeyGenerator.getInstance(KeyProperties.KEY_ALGORITHM_AES, "AndroidKeyStore")
        gen.init(KeyGenParameterSpec.Builder(ALIAS, KeyProperties.PURPOSE_ENCRYPT or KeyProperties.PURPOSE_DECRYPT)
            .setBlockModes(KeyProperties.BLOCK_MODE_GCM).setEncryptionPaddings(KeyProperties.ENCRYPTION_PADDING_NONE).setKeySize(256).build())
        return gen.generateKey()
    }

    /** Cifra y devuelve Base64(IV de 12 bytes ‖ texto cifrado ‖ etiqueta). El Keystore elige el IV. */
    fun encrypt(plain: ByteArray): String {
        val c = Cipher.getInstance("AES/GCM/NoPadding").apply { init(Cipher.ENCRYPT_MODE, key()) }
        return Base64.encodeToString(c.iv + c.doFinal(plain), Base64.NO_WRAP)
    }

    /** Descifra un valor guardado; null si no se puede (por ejemplo, si la llave se borró al reinstalar). */
    fun decrypt(stored: String): ByteArray? = runCatching {
        val raw = Base64.decode(stored, Base64.NO_WRAP)
        Cipher.getInstance("AES/GCM/NoPadding").apply { init(Cipher.DECRYPT_MODE, key(), GCMParameterSpec(128, raw, 0, 12)) }
            .doFinal(raw, 12, raw.size - 12)
    }.getOrNull()
}

/** Configuración de la aplicación (SharedPreferences). Los secretos pasan por [SecureStore]. */
class Settings(context: Context) {
    private val p = context.getSharedPreferences("signaltrace", Context.MODE_PRIVATE)

    /** URL del servidor; si no se configuró, la que trae la APK. */
    var serverUrl: String
        get() = p.getString("url", null) ?: BuildConfig.DEFAULT_SERVER_URL
        set(v) = p.edit().putString("url", v.trim().trimEnd('/')).apply()

    /** Llave de largo plazo fijada del servidor (Base64 del punto de 65 bytes) y su huella SHA-256. */
    var pinnedKey: String?
        get() = p.getString("pinKey", null)
        set(v) = p.edit().putString("pinKey", v).apply()
    /** Huella fijada en hexadecimal. */
    var fingerprint: String?
        get() = p.getString("pinFp", null)
        set(v) = p.edit().putString("pinFp", v).apply()

    /** Identificador del dispositivo registrado. */
    var deviceId: String?
        get() = p.getString("did", null)
        set(v) = p.edit().putString("did", v).apply()
    /** Nombre visible del dispositivo; por defecto, el modelo del teléfono. */
    var deviceName: String
        get() = p.getString("dname", null) ?: android.os.Build.MODEL
        set(v) = p.edit().putString("dname", v).apply()
    /** Token del dispositivo; se guarda cifrado con [SecureStore] y se descifra al leerlo. */
    var deviceToken: String?
        get() = p.getString("dtok", null)?.let { SecureStore.decrypt(it)?.toString(Charsets.UTF_8) }
        set(v) = p.edit().putString("dtok", v?.let { SecureStore.encrypt(it.toByteArray()) }).apply()

    /** Segundos entre muestras (5 por defecto). */
    var intervalSec: Float
        get() = p.getFloat("interval", 5f)
        set(v) = p.edit().putFloat("interval", v).apply()
    /** Suite AEAD que se propone en el handshake. */
    var suite: String
        get() = p.getString("suite", Protocol.SUITE_AES)!!
        set(v) = p.edit().putString("suite", v).apply()
    /** SHA-256 (hex) del certificado TLS autofirmado del servidor, fijado desde el QR; null = validación TLS normal. */
    var tlsPin: String?
        get() = p.getString("tlsPin", null)
        set(v) = p.edit().putString("tlsPin", v).apply()

    /** Todos los sensores del banco se exponen por defecto; el usuario puede apagar cada uno. */
    fun source(name: String) = p.getBoolean("src.$name", true)
    /** Enciende o apaga un sensor. */
    fun setSource(name: String, on: Boolean) = p.edit().putBoolean("src.$name", on).apply()

    /** Si ya hay una llave de servidor fijada. */
    val isPinned get() = pinnedKey != null && fingerprint != null
    /** Si el teléfono ya está registrado como dispositivo. */
    val isRegistered get() = deviceId != null && deviceToken != null

    /** Olvida el registro del dispositivo. */
    fun forgetDevice() = p.edit().remove("did").remove("dtok").apply()
    /** Olvida la llave y el certificado fijados. */
    fun forgetPin() = p.edit().remove("pinKey").remove("pinFp").remove("tlsPin").apply()

    /** Un sensor del banco: id del bloque en la carga, grupo y texto visible. */
    data class Source(val id: String, val group: String, val label: String)

    companion object {
        /** El banco de sensores, en el mismo orden y con los mismos ids que el agente web y la carga. */
        val SOURCES = listOf(
            Source("mot", "Movimiento", "Acelerómetro (RMS, pico y espectro)"),
            Source("lin", "Movimiento", "Aceleración lineal"),
            Source("grv", "Movimiento", "Gravedad"),
            Source("gyr", "Movimiento", "Giroscopio"),
            Source("mfl", "Orientación", "Magnetómetro"),
            Source("ori", "Orientación", "Orientación (vector de rotación)"),
            Source("lux", "Entorno", "Luz ambiental"),
            Source("prx", "Entorno", "Proximidad"),
            Source("hpa", "Entorno", "Presión barométrica"),
            Source("tmp", "Entorno", "Temperatura ambiente"),
            Source("hum", "Entorno", "Humedad relativa"),
            Source("stp", "Entorno", "Podómetro (pasos)"),
            Source("bat", "Dispositivo", "Batería"),
            Source("net", "Señales", "Red (tipo y ancho de banda)"),
            Source("probe", "Señales", "Sondeo de enlace (RTT, jitter, pérdida)"),
            Source("wifi", "Señales", "Señal Wi-Fi (RSSI)"),
            Source("cell", "Señales", "Señal celular (dBm)"),
        )
        /** Texto visible de un sensor por su id. */
        fun label(id: String) = SOURCES.firstOrNull { it.id == id }?.label ?: id
    }
}
