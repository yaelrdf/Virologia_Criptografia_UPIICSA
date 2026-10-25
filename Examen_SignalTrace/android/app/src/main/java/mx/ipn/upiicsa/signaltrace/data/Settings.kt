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
 * Secrets at rest on the phone: AES-256-GCM with a non-exportable key inside the Android Keystore.
 * The device token and the anonymisation key are stored only in this encrypted form.
 */
object SecureStore {
    private const val ALIAS = "signaltrace-secrets"

    private fun key(): SecretKey {
        val ks = KeyStore.getInstance("AndroidKeyStore").apply { load(null) }
        (ks.getKey(ALIAS, null) as? SecretKey)?.let { return it }
        val gen = KeyGenerator.getInstance(KeyProperties.KEY_ALGORITHM_AES, "AndroidKeyStore")
        gen.init(KeyGenParameterSpec.Builder(ALIAS, KeyProperties.PURPOSE_ENCRYPT or KeyProperties.PURPOSE_DECRYPT)
            .setBlockModes(KeyProperties.BLOCK_MODE_GCM).setEncryptionPaddings(KeyProperties.ENCRYPTION_PADDING_NONE).setKeySize(256).build())
        return gen.generateKey()
    }

    fun encrypt(plain: ByteArray): String {
        val c = Cipher.getInstance("AES/GCM/NoPadding").apply { init(Cipher.ENCRYPT_MODE, key()) }
        return Base64.encodeToString(c.iv + c.doFinal(plain), Base64.NO_WRAP)
    }

    fun decrypt(stored: String): ByteArray? = runCatching {
        val raw = Base64.decode(stored, Base64.NO_WRAP)
        Cipher.getInstance("AES/GCM/NoPadding").apply { init(Cipher.DECRYPT_MODE, key(), GCMParameterSpec(128, raw, 0, 12)) }
            .doFinal(raw, 12, raw.size - 12)
    }.getOrNull()
}

/** App configuration (SharedPreferences). Secrets go through [SecureStore]. */
class Settings(context: Context) {
    private val p = context.getSharedPreferences("signaltrace", Context.MODE_PRIVATE)

    var serverUrl: String
        get() = p.getString("url", null) ?: BuildConfig.DEFAULT_SERVER_URL
        set(v) = p.edit().putString("url", v.trim().trimEnd('/')).apply()

    /** Pinned long-term server key (Base64 of the 65-byte point) and its SHA-256 fingerprint. */
    var pinnedKey: String?
        get() = p.getString("pinKey", null)
        set(v) = p.edit().putString("pinKey", v).apply()
    var fingerprint: String?
        get() = p.getString("pinFp", null)
        set(v) = p.edit().putString("pinFp", v).apply()

    var deviceId: String?
        get() = p.getString("did", null)
        set(v) = p.edit().putString("did", v).apply()
    var deviceName: String
        get() = p.getString("dname", null) ?: android.os.Build.MODEL
        set(v) = p.edit().putString("dname", v).apply()
    var deviceToken: String?
        get() = p.getString("dtok", null)?.let { SecureStore.decrypt(it)?.toString(Charsets.UTF_8) }
        set(v) = p.edit().putString("dtok", v?.let { SecureStore.encrypt(it.toByteArray()) }).apply()

    var intervalSec: Float
        get() = p.getFloat("interval", 5f)
        set(v) = p.edit().putFloat("interval", v).apply()
    var suite: String
        get() = p.getString("suite", Protocol.SUITE_AES)!!
        set(v) = p.edit().putString("suite", v).apply()
    var anonymize: Boolean
        get() = p.getBoolean("anon", true)
        set(v) = p.edit().putBoolean("anon", v).apply()

    fun source(name: String) = p.getBoolean("src.$name", true)
    fun setSource(name: String, on: Boolean) = p.edit().putBoolean("src.$name", on).apply()

    /** Local HMAC key for anonymising SSID/BSSID/cell id; created once, never sent. */
    val anonKey: ByteArray
        get() {
            p.getString("anonKey", null)?.let { s -> SecureStore.decrypt(s)?.let { return it } }
            val k = Protocol.randomBytes(32)
            p.edit().putString("anonKey", SecureStore.encrypt(k)).apply()
            return k
        }

    val isPinned get() = pinnedKey != null && fingerprint != null
    val isRegistered get() = deviceId != null && deviceToken != null

    fun forgetDevice() = p.edit().remove("did").remove("dtok").apply()
    fun forgetPin() = p.edit().remove("pinKey").remove("pinFp").apply()

    companion object {
        val SOURCES = listOf("gps" to "GPS", "mot" to "Movimiento", "bat" to "Batería", "net" to "Red", "probe" to "Sondeo de enlace", "wifi" to "Wi-Fi (RSSI)", "cell" to "Celular")
    }
}
