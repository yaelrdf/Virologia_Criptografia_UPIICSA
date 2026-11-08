package mx.ipn.upiicsa.signaltrace

import mx.ipn.upiicsa.signaltrace.crypto.Protocol
import mx.ipn.upiicsa.signaltrace.data.Api
import mx.ipn.upiicsa.signaltrace.data.ApiException
import org.junit.Assert.assertEquals
import org.junit.Assume.assumeTrue
import org.junit.Test
import java.util.Base64

/**
 * Prueba de extremo a extremo del cliente en Kotlin contra un servidor SignalTrace en ejecución, sin teléfono:
 *   ST_TEST_SERVER=http://localhost:5090 ST_TEST_PASSWORD=... [ST_TEST_TLS=https://localhost:5443] ./gradlew :app:testDebugUnitTest
 * Se omite si ST_TEST_SERVER no está definida.
 */
class ServerIntegrationTest {
    private val url = System.getenv("ST_TEST_SERVER")
    private val password = System.getenv("ST_TEST_PASSWORD")

    /**
     * Flujo completo: llave del servidor, inicio de sesión, registro, handshake con cada suite, tres envíos, un
     * reenvío rechazado, rechazo de una llave ajena, vinculación por QR con HTTPS fijado y contraseña incorrecta.
     */
    @Test fun fullFlowAgainstRealServer() {
        assumeTrue("ST_TEST_SERVER not set", url != null && password != null)
        val api = Api(url!!)
        val key = api.serverKey()
        val pinned = Base64.getDecoder().decode(key.publicKey)
        assertEquals(key.fingerprint, Protocol.fingerprint(pinned))
        val token = api.login("admin", password!!).token
        val dev = api.registerDevice(token, "Prueba JVM (Kotlin)")

        for (suite in listOf(Protocol.SUITE_AES, Protocol.SUITE_CHACHA).filter { it == Protocol.SUITE_AES || Protocol.chachaSupported() }) {
            val session = api.handshake(dev.deviceId, dev.deviceToken, pinned, suite)
            repeat(3) {
                val ts = System.currentTimeMillis()
                val payload = """{"t":$ts,"src":"android","gyr":{"x":0.01,"y":-0.02,"z":0.03},"ori":{"az":120.5,"pi":-4.2,"ro":1.1},"env":{"lux":250,"hpa":779.6},"stp":{"n":42},"wifi":{"rssi":-61,"freq":5180},"cell":{"type":"LTE","dbm":-97,"level":3},"bat":{"lvl":0.66,"chg":false}}"""
                val (code, _) = api.send(session, dev.deviceToken, ts, payload)
                assertEquals("ok", code)
            }
            // Reenvío de un número de secuencia ya usado: el servidor debe responder 409 replay.
            val ts = System.currentTimeMillis()
            session.seq -= 1
            val (replay, _) = api.send(session, dev.deviceToken, ts, """{"t":$ts,"src":"android"}""")
            assertEquals("replay", replay)
        }

        // Una llave fijada equivocada debe abortar el handshake (protección contra intermediario).
        val other = Protocol.exportPublic(Protocol.generateEphemeral().public as java.security.interfaces.ECPublicKey)
        try { api.handshake(dev.deviceId, dev.deviceToken, other); throw AssertionError("accepted a foreign key") } catch (_: SecurityException) { }

        // El camino del QR: HTTPS en el puerto de la red local con el certificado autofirmado fijado por su SHA-256 (ST_TEST_TLS = URL https).
        System.getenv("ST_TEST_TLS")?.let { tlsUrl ->
            val qr = Api(url).enroll(token)
            val https = Api(tlsUrl, qr.tls)
            assertEquals(key.fingerprint, https.serverKey().fingerprint)
            // Con una huella de certificado incorrecta la conexión TLS se rechaza.
            val wrongPin = Api(tlsUrl, "00".repeat(32))
            try { wrongPin.serverKey(); throw AssertionError("accepted an unpinned certificate") } catch (e: ApiException) { assertEquals("network", e.code) }
            val linked = https.redeemEnrollment(qr.code, "Prueba JVM por QR")
            val s = https.handshake(linked.deviceId, linked.deviceToken, pinned)
            val ts = System.currentTimeMillis()
            assertEquals("ok", https.send(s, linked.deviceToken, ts, """{"t":$ts,"src":"android","env":{"lux":10}}""").first)
        }

        // Contraseña incorrecta → 401.
        try { api.login("admin", "wrong-password"); throw AssertionError("login accepted") } catch (e: ApiException) { assertEquals(401, e.status) }
    }
}
