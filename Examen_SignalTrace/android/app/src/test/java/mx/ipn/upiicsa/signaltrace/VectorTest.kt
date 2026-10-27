package mx.ipn.upiicsa.signaltrace

import kotlinx.serialization.json.Json
import kotlinx.serialization.json.JsonObject
import kotlinx.serialization.json.int
import kotlinx.serialization.json.jsonArray
import kotlinx.serialization.json.jsonObject
import kotlinx.serialization.json.jsonPrimitive
import kotlinx.serialization.json.long
import mx.ipn.upiicsa.signaltrace.crypto.Protocol
import mx.ipn.upiicsa.signaltrace.crypto.Protocol.hex
import mx.ipn.upiicsa.signaltrace.crypto.Protocol.unhex
import mx.ipn.upiicsa.signaltrace.crypto.YaelCryptoMarch1
import org.junit.Assert.assertArrayEquals
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test
import java.io.File
import java.security.SecureRandom
import java.util.Base64
import javax.crypto.AEADBadTagException

/** Appendix A vectors: the Kotlin port must produce exactly the same bytes as C# and Web Crypto. */
class VectorTest {
    private val v: JsonObject = Json.parseToJsonElement(File(System.getProperty("st.vectors")!!).readText()).jsonObject
    private val hs = v["handshake"]!!.jsonObject
    private val pk = v["packet"]!!.jsonObject
    private fun JsonObject.s(k: String) = this[k]!!.jsonPrimitive.content
    private fun out(label: String) = hs["hkdf"]!!.jsonObject["outputs"]!!.jsonObject.s(label)

    @Test fun ecdhSharedSecret() {
        val priv = Protocol.privateKeyFromScalar(unhex(hs.s("client_ephemeral_private")))
        assertEquals(hs.s("shared_secret_x"), hex(Protocol.sharedSecret(priv, unhex(hs.s("server_ephemeral_public")))))
        val priv2 = Protocol.privateKeyFromScalar(unhex(hs.s("server_ephemeral_private")))
        assertEquals(hs.s("shared_secret_x"), hex(Protocol.sharedSecret(priv2, unhex(hs.s("client_ephemeral_public")))))
    }

    @Test fun hkdfOutputs() {
        val k = Protocol.deriveKeys(unhex(hs.s("shared_secret_x")), unhex(hs.s("client_nonce")), unhex(hs.s("server_nonce")))
        assertEquals(out("st1/aes-up(32)"), hex(k.kAead))
        assertEquals(out("st1/ycm1(32)"), hex(k.kSig))
        assertEquals(out("st1/iv-up(4)"), hex(k.ivSalt))
    }

    @Test fun transcriptAndSignature() {
        val t = Protocol.transcript(unhex(hs.s("client_ephemeral_public")), unhex(hs.s("server_ephemeral_public")),
            unhex(hs.s("client_nonce")), unhex(hs.s("server_nonce")), hs.s("device_id"), hs.s("session_id"))
        assertEquals(hs.s("transcript"), hex(t))
        val sig = unhex(hs.s("server_signature_p1363_VERIFY_ONLY"))
        assertTrue(Protocol.verifySignature(unhex(hs.s("server_longterm_public")), t, sig))
        t[t.size - 1] = (t.last() + 1).toByte()
        assertFalse(Protocol.verifySignature(unhex(hs.s("server_longterm_public")), t, sig))
    }

    @Test fun ycm1WorkedExample() {
        val ex = v["ycm1_round_example"]!!.jsonObject
        val stages = mutableMapOf<String, List<Int>>()
        val c = YaelCryptoMarch1.encryptWithParams(byteArrayOf(72, 105), YaelCryptoMarch1.Params(ex["seed"]!!.jsonPrimitive.int,
            listOf(YaelCryptoMarch1.Round(ex["k"]!!.jsonPrimitive.int, ex["m"]!!.jsonPrimitive.int, 0))),
            { n, b -> stages[n] = b.map { it.toInt() and 0xFF } }, usePad = false)
        assertEquals(listOf(110, 42), stages["r1.stream"])
        assertEquals(listOf(236, 198), c.map { it.toInt() and 0xFF })
    }

    @Test fun ycm1PacketStages() {
        val kSig = unhex(out("st1/ycm1(32)"))
        val seq = pk["seq"]!!.jsonPrimitive.long
        val prm = YaelCryptoMarch1.deriveParams(kSig, seq)
        val want = pk["ycm1_params"]!!.jsonObject
        assertEquals(want["seed"]!!.jsonPrimitive.int, prm.seed)
        want["rounds"]!!.jsonArray.forEachIndexed { i, r ->
            val o = r.jsonObject
            assertEquals(YaelCryptoMarch1.Round(o["k"]!!.jsonPrimitive.int, o["m"]!!.jsonPrimitive.int, o["rot"]!!.jsonPrimitive.int), prm.rounds[i])
        }
        val stages = mutableMapOf<String, String>()
        val sm = YaelCryptoMarch1.encrypt(unhex(pk.s("plaintext_hex")), kSig, seq) { n, b -> stages[n] = hex(b) }
        pk["ycm1_stages"]!!.jsonObject.forEach { (name, value) -> assertEquals(name, value.jsonPrimitive.content, stages[name]) }
        assertEquals(pk.s("ycm1_output"), hex(sm))
        assertEquals(pk.s("plaintext_hex"), hex(YaelCryptoMarch1.decrypt(sm, kSig, seq)))
    }

    @Test fun packetSealMatchesAesGcmVector() {
        val keys = Protocol.deriveKeys(unhex(hs.s("shared_secret_x")), unhex(hs.s("client_nonce")), unhex(hs.s("server_nonce")))
        val seq = pk["seq"]!!.jsonPrimitive.long
        val ts = pk["ts_ms"]!!.jsonPrimitive.long
        val aes = pk["aes_gcm"]!!.jsonObject
        assertEquals(aes.s("nonce(12)=iv_salt||u64be(seq)"), hex(Protocol.nonce(keys.ivSalt, seq)))
        assertEquals(aes.s("aad = 'ST1'||utf8(device_id)||u64be(seq)||u64be(ts)"), hex(Protocol.aad(hs.s("device_id"), seq, ts)))
        val ct = Protocol.seal(keys, hs.s("device_id"), seq, ts, pk.s("plaintext_utf8").toByteArray())
        assertEquals(aes.s("ciphertext_plus_tag"), hex(ct))
        assertEquals(aes.s("ciphertext_plus_tag_b64"), Base64.getEncoder().encodeToString(ct))
        assertEquals(pk.s("plaintext_utf8"), String(Protocol.open(keys, hs.s("device_id"), seq, ts, ct)))
        ct[5] = (ct[5].toInt() xor 1).toByte()
        try { Protocol.open(keys, hs.s("device_id"), seq, ts, ct); throw AssertionError("tampered packet accepted") } catch (_: AEADBadTagException) { }
    }

    @Test fun ycm1RoundTripAllLengths() {
        val rnd = SecureRandom()
        for (len in 0 until 200) {
            val d = ByteArray(len).also(rnd::nextBytes)
            val k = ByteArray(32).also(rnd::nextBytes)
            assertArrayEquals(d, YaelCryptoMarch1.decrypt(YaelCryptoMarch1.encrypt(d, k, len * 7919L), k, len * 7919L))
        }
    }

    @Test fun chachaRoundTrip() {
        if (!Protocol.chachaSupported()) return
        val keys = Protocol.deriveKeys(Protocol.randomBytes(32), Protocol.randomBytes(16), Protocol.randomBytes(16), Protocol.SUITE_CHACHA)
        val ct = Protocol.seal(keys, "d", 3, 1000, "{\"t\":1000,\"src\":\"android\"}".toByteArray())
        assertEquals("{\"t\":1000,\"src\":\"android\"}", String(Protocol.open(keys, "d", 3, 1000, ct)))
    }

    @Test fun derConversion() {
        // r with the high bit set needs a leading 0x00 in DER.
        val sig = ByteArray(64) { if (it == 0) 0x80.toByte() else 1 }
        val der = Protocol.p1363ToDer(sig)
        assertEquals(0x30, der[0].toInt())
        assertEquals(33, der[3].toInt())
    }
}
