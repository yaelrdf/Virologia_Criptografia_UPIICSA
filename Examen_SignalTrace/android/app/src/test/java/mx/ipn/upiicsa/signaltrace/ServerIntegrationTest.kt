package mx.ipn.upiicsa.signaltrace

import mx.ipn.upiicsa.signaltrace.crypto.Protocol
import mx.ipn.upiicsa.signaltrace.data.Api
import mx.ipn.upiicsa.signaltrace.data.ApiException
import org.junit.Assert.assertEquals
import org.junit.Assume.assumeTrue
import org.junit.Test
import java.util.Base64

/**
 * End-to-end check of the Kotlin client against a running SignalTrace server, without a phone:
 *   ST_TEST_SERVER=http://localhost:5090 ST_TEST_PASSWORD=... [ST_TEST_TLS=https://localhost:5443] ./gradlew :app:testDebugUnitTest
 * Skipped when ST_TEST_SERVER is not set.
 */
class ServerIntegrationTest {
    private val url = System.getenv("ST_TEST_SERVER")
    private val password = System.getenv("ST_TEST_PASSWORD")

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
            // Replay of a sealed packet: the server must answer 409 replay.
            val ts = System.currentTimeMillis()
            session.seq -= 1
            val (replay, _) = api.send(session, dev.deviceToken, ts, """{"t":$ts,"src":"android"}""")
            assertEquals("replay", replay)
        }

        // A wrong pinned key must abort the handshake (MITM protection).
        val other = Protocol.exportPublic(Protocol.generateEphemeral().public as java.security.interfaces.ECPublicKey)
        try { api.handshake(dev.deviceId, dev.deviceToken, other); throw AssertionError("accepted a foreign key") } catch (_: SecurityException) { }

        // The QR path: HTTPS on the LAN port with the self-signed certificate pinned by its SHA-256 (ST_TEST_TLS = https URL).
        System.getenv("ST_TEST_TLS")?.let { tlsUrl ->
            val qr = Api(url).enroll(token)
            val https = Api(tlsUrl, qr.tls)
            assertEquals(key.fingerprint, https.serverKey().fingerprint)
            val wrongPin = Api(tlsUrl, "00".repeat(32))
            try { wrongPin.serverKey(); throw AssertionError("accepted an unpinned certificate") } catch (e: ApiException) { assertEquals("network", e.code) }
            val linked = https.redeemEnrollment(qr.code, "Prueba JVM por QR")
            val s = https.handshake(linked.deviceId, linked.deviceToken, pinned)
            val ts = System.currentTimeMillis()
            assertEquals("ok", https.send(s, linked.deviceToken, ts, """{"t":$ts,"src":"android","env":{"lux":10}}""").first)
        }

        // Wrong password → 401.
        try { api.login("admin", "wrong-password"); throw AssertionError("login accepted") } catch (e: ApiException) { assertEquals(401, e.status) }
    }
}
