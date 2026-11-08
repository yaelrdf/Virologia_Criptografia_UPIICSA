package mx.ipn.upiicsa.signaltrace.data

import kotlinx.serialization.Serializable
import kotlinx.serialization.json.Json
import kotlinx.serialization.json.JsonObject
import kotlinx.serialization.json.jsonObject
import kotlinx.serialization.json.jsonPrimitive
import mx.ipn.upiicsa.signaltrace.crypto.Protocol
import okhttp3.MediaType.Companion.toMediaType
import okhttp3.OkHttpClient
import okhttp3.Request
import okhttp3.RequestBody.Companion.toRequestBody
import java.io.IOException
import java.security.KeyPair
import java.security.MessageDigest
import java.security.cert.CertificateException
import java.security.cert.X509Certificate
import java.util.Base64
import java.util.concurrent.TimeUnit
import javax.net.ssl.SSLContext
import javax.net.ssl.X509TrustManager

// Objetos JSON de la API (mismos nombres de campo que el servidor).
/** Respuesta del inicio de sesión. */
@Serializable data class LoginResponse(val token: String, val expiresAt: Long, val role: String, val username: String)
/** Llave pública de largo plazo del servidor, su huella y las suites disponibles. */
@Serializable data class ServerKey(val publicKey: String, val fingerprint: String, val suites: List<String> = emptyList())
/** Identificador y token de un dispositivo registrado. */
@Serializable data class DeviceRegistration(val deviceId: String, val deviceToken: String)
/** Código de vinculación con el enlace del QR. */
@Serializable data class Enrollment(val code: String, val qr: String, val fingerprint: String, val tls: String? = null)
/** Respuesta del handshake. */
@Serializable data class HandshakeResponse(val sessionId: String, val serverPublicKey: String, val serverNonce: String, val signature: String, val expiresAt: Long, val suite: String)
/** Sobre de telemetría que se transmite. */
@Serializable data class Envelope(val v: Int, val sid: String, val did: String, val seq: Long, val ts: Long, val ct: String)

/** Error HTTP con el { code, message } del servidor; el código es "network" cuando no se pudo llegar al servidor. */
class ApiException(val status: Int, val code: String, message: String) : IOException(message)

/** Sesión del cliente después de un handshake verificado; seq es el último número de secuencia usado. */
class Session(val sid: String, val did: String, val keys: Protocol.SessionKeys, val expiresAt: Long) {
    var seq = 0L
}

/**
 * Confía exactamente en un certificado TLS: el certificado autofirmado de la red local cuyo SHA-256 llegó en el QR
 * (fijado de certificado). Cualquier otro certificado, aunque lo firme una autoridad pública, se rechaza.
 */
class PinnedCertTrust(private val sha256Hex: String) : X509TrustManager {
    /** La aplicación no usa certificados de cliente. */
    override fun checkClientTrusted(chain: Array<out X509Certificate>, authType: String) = throw CertificateException("client certificates not used")
    /** Compara el SHA-256 del certificado del servidor con el fijado. */
    override fun checkServerTrusted(chain: Array<out X509Certificate>, authType: String) {
        val leaf = chain.firstOrNull() ?: throw CertificateException("empty chain")
        val got = MessageDigest.getInstance("SHA-256").digest(leaf.encoded).joinToString("") { "%02x".format(it) }
        if (!got.equals(sha256Hex, ignoreCase = true)) throw CertificateException("TLS certificate does not match the pinned one")
    }
    /** Ninguna autoridad emisora es de confianza por sí misma. */
    override fun getAcceptedIssuers(): Array<X509Certificate> = emptyArray()
}

/**
 * Cliente REST de la API de SignalTrace (PLAN §7). Es JVM pura (OkHttp + kotlinx.serialization), así las pruebas en
 * la JVM pueden ejecutar el flujo completo contra un servidor real. Con [tlsPin] solo se acepta el certificado
 * autofirmado fijado.
 */
class Api(baseUrl: String, tlsPin: String? = null) {
    /** URL base sin la diagonal final. */
    val base = baseUrl.trim().trimEnd('/')
    private val json = Json { ignoreUnknownKeys = true; explicitNulls = false; encodeDefaults = true }
    private val jsonType = "application/json".toMediaType()
    private val http = OkHttpClient.Builder().connectTimeout(8, TimeUnit.SECONDS).readTimeout(15, TimeUnit.SECONDS).apply {
        if (tlsPin != null) {
            val trust = PinnedCertTrust(tlsPin)
            val ssl = SSLContext.getInstance("TLS").apply { init(null, arrayOf(trust), null) }
            sslSocketFactory(ssl.socketFactory, trust)
            // La identidad es el propio certificado fijado: la IP de la red local en la URL puede cambiar entre asignaciones de DHCP.
            hostnameVerifier { _, _ -> true }
        }
    }.build()
    private val b64 = Base64.getEncoder()
    private val unb64 = Base64.getDecoder()

    /**
     * Hace una solicitud y devuelve (código HTTP, cuerpo).
     * @throws ApiException con código "network" si no hubo conexión.
     */
    private fun call(method: String, path: String, body: String? = null, token: String? = null): Pair<Int, String> {
        val req = Request.Builder().url(base + path).method(method, body?.toRequestBody(jsonType)).apply {
            if (token != null) header("Authorization", "Bearer $token")
        }.build()
        try {
            http.newCall(req).execute().use { res -> return res.code to (res.body.string()) }
        } catch (e: IOException) {
            throw ApiException(0, "network", e.message ?: "Sin conexión con el servidor")
        }
    }

    /** Devuelve el cuerpo si la respuesta es 2xx; si no, lanza [ApiException] con el código y el mensaje del servidor. */
    private fun ok(r: Pair<Int, String>): String {
        if (r.first in 200..299) return r.second
        val obj = runCatching { json.parseToJsonElement(r.second).jsonObject }.getOrNull()
        throw ApiException(r.first, obj?.get("code")?.jsonPrimitive?.content ?: "http_${r.first}", obj?.get("message")?.jsonPrimitive?.content ?: "HTTP ${r.first}")
    }

    /** Arma un objeto JSON de texto con los pares no nulos. */
    private fun obj(vararg pairs: Pair<String, String?>) =
        JsonObject(pairs.filter { it.second != null }.associate { (k, v) -> k to kotlinx.serialization.json.JsonPrimitive(v) }).toString()

    /** GET /api/server-key. */
    fun serverKey(): ServerKey = json.decodeFromString(ok(call("GET", "/api/server-key")))

    /** Inicia sesión con usuario y contraseña. */
    fun login(username: String, password: String): LoginResponse =
        json.decodeFromString(ok(call("POST", "/api/auth/login", obj("username" to username, "password" to password))))

    /** Registra este teléfono como dispositivo «android» con la sesión del usuario. */
    fun registerDevice(userToken: String, name: String): DeviceRegistration =
        json.decodeFromString(ok(call("POST", "/api/devices", obj("name" to name, "kind" to "android"), userToken)))

    /** Crea un código de vinculación de un solo uso (lo que el dashboard muestra como QR). */
    fun enroll(userToken: String): Enrollment = json.decodeFromString(ok(call("POST", "/api/enroll", "{}", userToken)))

    /** Canjea un código de vinculación sin sesión de usuario. */
    fun redeemEnrollment(code: String, name: String): DeviceRegistration =
        json.decodeFromString(ok(call("POST", "/api/enroll/redeem", obj("code" to code, "name" to name, "kind" to "android"))))

    /**
     * Handshake ECDH: llave efímera y nonce del cliente, verificación de la firma del servidor con la llave fijada
     * y derivación de las llaves de sesión. Aborta con [SecurityException] si la firma no coincide (intermediario).
     */
    fun handshake(deviceId: String, deviceToken: String, pinnedPublicKey: ByteArray, suite: String = Protocol.SUITE_AES): Session {
        val eph: KeyPair = Protocol.generateEphemeral()
        val pubC = Protocol.exportPublic(eph.public as java.security.interfaces.ECPublicKey)
        val nonceC = Protocol.randomBytes(16)
        val body = obj("deviceId" to deviceId, "clientPublicKey" to b64.encodeToString(pubC), "clientNonce" to b64.encodeToString(nonceC), "suite" to suite)
        val r: HandshakeResponse = json.decodeFromString(ok(call("POST", "/api/handshake", body, deviceToken)))
        val pubS = unb64.decode(r.serverPublicKey)
        val nonceS = unb64.decode(r.serverNonce)
        val t = Protocol.transcript(pubC, pubS, nonceC, nonceS, deviceId, r.sessionId, r.suite)
        if (!Protocol.verifySignature(pinnedPublicKey, t, unb64.decode(r.signature)))
            throw SecurityException("La firma del servidor no coincide con la huella fijada (posible MITM)")
        val keys = Protocol.deriveKeys(Protocol.sharedSecret(eph, pubS), nonceC, nonceS, r.suite)
        return Session(r.sessionId, deviceId, keys, r.expiresAt)
    }

    /**
     * Sella una lectura (JSON cuyo "t" es igual a ts) y la envía. Devuelve el código del servidor ("ok" con 202)
     * y el sobre transmitido.
     */
    fun send(session: Session, deviceToken: String, ts: Long, payloadJson: String): Pair<String, Envelope> {
        val seq = ++session.seq
        val ct = Protocol.seal(session.keys, session.did, seq, ts, payloadJson.toByteArray())
        val env = Envelope(1, session.sid, session.did, seq, ts, b64.encodeToString(ct))
        val (status, text) = call("POST", "/api/telemetry", json.encodeToString(Envelope.serializer(), env), deviceToken)
        if (status == 202) return "ok" to env
        val code = runCatching { json.parseToJsonElement(text).jsonObject["code"]?.jsonPrimitive?.content }.getOrNull() ?: "http_$status"
        return code to env
    }

    /** Un sondeo de enlace: POST /api/ping con 64 bytes. Devuelve el RTT en ms, o null si se perdió. */
    fun ping(): Double? = runCatching {
        val t0 = System.nanoTime()
        val (status, _) = call("POST", "/api/ping", String(CharArray(64) { 'x' }))
        if (status == 200) (System.nanoTime() - t0) / 1e6 else null
    }.getOrNull()

    /** Descarga 64 KB y calcula el rendimiento en kbit/s; null si falla. */
    fun throughputKbps(): Double? = runCatching {
        val t0 = System.nanoTime()
        val req = Request.Builder().url("$base/api/ping/download?kb=64").build()
        val n = http.newCall(req).execute().use { it.body.bytes().size }
        n * 8 / ((System.nanoTime() - t0) / 1e9) / 1000
    }.getOrNull()

    companion object {
        /** Sobre como texto JSON (para mostrarlo en la pantalla de estado). */
        fun envelopeJson(env: Envelope) = Json.encodeToString(Envelope.serializer(), env)
    }
}
