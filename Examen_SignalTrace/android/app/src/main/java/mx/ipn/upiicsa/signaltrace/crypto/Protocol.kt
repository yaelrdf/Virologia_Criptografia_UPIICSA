package mx.ipn.upiicsa.signaltrace.crypto

import java.io.ByteArrayOutputStream
import java.math.BigInteger
import java.nio.ByteBuffer
import java.security.AlgorithmParameters
import java.security.KeyFactory
import java.security.KeyPair
import java.security.KeyPairGenerator
import java.security.MessageDigest
import java.security.SecureRandom
import java.security.Signature
import java.security.interfaces.ECPublicKey
import java.security.spec.ECGenParameterSpec
import java.security.spec.ECParameterSpec
import java.security.spec.ECPoint
import java.security.spec.ECPublicKeySpec
import javax.crypto.AEADBadTagException
import javax.crypto.Cipher
import javax.crypto.KeyAgreement
import javax.crypto.Mac
import javax.crypto.spec.GCMParameterSpec
import javax.crypto.spec.IvParameterSpec
import javax.crypto.spec.SecretKeySpec

/** SignalTrace v1 protocol (PLAN §6) with the JCA: the same bytes as SignalTrace.Core (C#) and session.js. */
object Protocol {
    const val SUITE_AES = "aes-256-gcm"
    const val SUITE_CHACHA = "chacha20-poly1305"
    private val random = SecureRandom()

    fun randomBytes(n: Int) = ByteArray(n).also { random.nextBytes(it) }
    fun u64(v: Long): ByteArray = ByteBuffer.allocate(8).putLong(v).array()
    fun hex(b: ByteArray) = b.joinToString("") { "%02x".format(it) }
    fun unhex(s: String) = ByteArray(s.length / 2) { s.substring(2 * it, 2 * it + 2).toInt(16).toByte() }
    fun sha256(b: ByteArray): ByteArray = MessageDigest.getInstance("SHA-256").digest(b)
    fun fingerprint(publicKey: ByteArray) = hex(sha256(publicKey))

    /** ChaCha20-Poly1305 exists in the JCA on Android 9+ (API 28) and JDK 11+. */
    fun chachaSupported(): Boolean = runCatching { Cipher.getInstance("ChaCha20-Poly1305"); true }.getOrDefault(false)

    // ---------- HKDF-SHA256 (RFC 5869), written out so the steps are visible
    fun hmac(key: ByteArray, data: ByteArray): ByteArray =
        Mac.getInstance("HmacSHA256").apply { init(SecretKeySpec(key, "HmacSHA256")) }.doFinal(data)

    fun hkdf(ikm: ByteArray, salt: ByteArray, info: String, length: Int): ByteArray {
        val prk = hmac(salt, ikm)                                   // extract
        val out = ByteArrayOutputStream()
        var t = ByteArray(0)
        var counter = 1
        while (out.size() < length) {                               // expand: T(i) = HMAC(PRK, T(i-1) ‖ info ‖ i)
            t = hmac(prk, t + info.toByteArray() + byteArrayOf(counter++.toByte()))
            out.write(t)
        }
        return out.toByteArray().copyOf(length)
    }

    data class SessionKeys(val kAead: ByteArray, val kSig: ByteArray, val ivSalt: ByteArray, val suite: String)

    fun deriveKeys(z: ByteArray, nonceC: ByteArray, nonceS: ByteArray, suite: String = SUITE_AES): SessionKeys {
        val salt = nonceC + nonceS
        return SessionKeys(
            hkdf(z, salt, if (suite == SUITE_CHACHA) "st1/c20-up" else "st1/aes-up", 32),
            hkdf(z, salt, "st1/sigmix", 32),
            hkdf(z, salt, "st1/iv-up", 4),
            suite,
        )
    }

    // ---------- P-256 keys as 65-byte X9.62 uncompressed points (04 ‖ X ‖ Y)
    private val p256: ECParameterSpec by lazy {
        AlgorithmParameters.getInstance("EC").apply { init(ECGenParameterSpec("secp256r1")) }.getParameterSpec(ECParameterSpec::class.java)
    }

    private fun fixed32(v: BigInteger): ByteArray {
        val b = v.toByteArray()
        return when {
            b.size == 32 -> b
            b.size > 32 -> b.copyOfRange(b.size - 32, b.size)
            else -> ByteArray(32 - b.size) + b
        }
    }

    fun exportPublic(key: ECPublicKey): ByteArray = byteArrayOf(4) + fixed32(key.w.affineX) + fixed32(key.w.affineY)

    fun importPublic(point: ByteArray): ECPublicKey {
        require(point.size == 65 && point[0] == 4.toByte()) { "Public key must be a 65-byte uncompressed P-256 point" }
        val spec = ECPublicKeySpec(ECPoint(BigInteger(1, point.copyOfRange(1, 33)), BigInteger(1, point.copyOfRange(33, 65))), p256)
        return KeyFactory.getInstance("EC").generatePublic(spec) as ECPublicKey
    }

    /** Rebuilds a P-256 private key from its raw 32-byte scalar (test vectors). */
    fun privateKeyFromScalar(d: ByteArray): java.security.PrivateKey =
        KeyFactory.getInstance("EC").generatePrivate(java.security.spec.ECPrivateKeySpec(BigInteger(1, d), p256))

    fun sharedSecret(own: java.security.PrivateKey, peerPublic: ByteArray): ByteArray =
        KeyAgreement.getInstance("ECDH").apply { init(own); doPhase(importPublic(peerPublic), true) }.generateSecret()

    fun generateEphemeral(): KeyPair = KeyPairGenerator.getInstance("EC").apply { initialize(ECGenParameterSpec("secp256r1"), random) }.generateKeyPair()

    /** Raw X coordinate of the shared point (32 B), like Web Crypto deriveBits and .NET DeriveRawSecretAgreement. */
    fun sharedSecret(own: KeyPair, peerPublic: ByteArray): ByteArray = sharedSecret(own.private, peerPublic)

    fun transcript(pubC: ByteArray, pubS: ByteArray, nonceC: ByteArray, nonceS: ByteArray, deviceId: String, sessionId: String, suite: String = SUITE_AES): ByteArray {
        val t = "ST1-HS".toByteArray() + pubC + pubS + nonceC + nonceS + deviceId.toByteArray() + sessionId.toByteArray()
        return if (suite == SUITE_AES) t else t + "|$suite".toByteArray()
    }

    /** IEEE P1363 (r ‖ s, 64 B) → ASN.1 DER, which is what the JCA "SHA256withECDSA" verifier expects. */
    fun p1363ToDer(sig: ByteArray): ByteArray {
        require(sig.size == 64) { "P1363 signature must be 64 bytes" }
        fun int(b: ByteArray): ByteArray {
            var v = b.dropWhile { it == 0.toByte() }.toByteArray()
            if (v.isEmpty()) v = byteArrayOf(0)
            if (v[0].toInt() and 0x80 != 0) v = byteArrayOf(0) + v
            return byteArrayOf(0x02, v.size.toByte()) + v
        }
        val body = int(sig.copyOfRange(0, 32)) + int(sig.copyOfRange(32, 64))
        return byteArrayOf(0x30, body.size.toByte()) + body
    }

    fun verifySignature(serverLongTermPublic: ByteArray, transcript: ByteArray, signatureP1363: ByteArray): Boolean = runCatching {
        Signature.getInstance("SHA256withECDSA").run {
            initVerify(importPublic(serverLongTermPublic))
            update(transcript)
            verify(p1363ToDer(signatureP1363))
        }
    }.getOrDefault(false)

    // ---------- packets
    fun nonce(ivSalt: ByteArray, seq: Long) = ivSalt + u64(seq)
    fun aad(deviceId: String, seq: Long, ts: Long) = "ST1".toByteArray() + deviceId.toByteArray() + u64(seq) + u64(ts)

    private fun aead(keys: SessionKeys, mode: Int, nonce: ByteArray, aad: ByteArray, data: ByteArray): ByteArray =
        if (keys.suite == SUITE_CHACHA) {
            Cipher.getInstance("ChaCha20-Poly1305").run {
                init(mode, SecretKeySpec(keys.kAead, "ChaCha20"), IvParameterSpec(nonce)); updateAAD(aad); doFinal(data)
            }
        } else {
            Cipher.getInstance("AES/GCM/NoPadding").run {
                init(mode, SecretKeySpec(keys.kAead, "AES"), GCMParameterSpec(128, nonce)); updateAAD(aad); doFinal(data)
            }
        }

    /** sm = SigMix(pt, k_sig, seq); ct‖tag = AEAD(k, iv_salt ‖ u64be(seq), sm, "ST1" ‖ did ‖ seq ‖ ts). */
    fun seal(keys: SessionKeys, deviceId: String, seq: Long, ts: Long, plaintext: ByteArray): ByteArray =
        aead(keys, Cipher.ENCRYPT_MODE, nonce(keys.ivSalt, seq), aad(deviceId, seq, ts), SigMix.encrypt(plaintext, keys.kSig, seq))

    /** Throws [AEADBadTagException] when the packet was altered. */
    fun open(keys: SessionKeys, deviceId: String, seq: Long, ts: Long, ctAndTag: ByteArray): ByteArray =
        SigMix.decrypt(aead(keys, Cipher.DECRYPT_MODE, nonce(keys.ivSalt, seq), aad(deviceId, seq, ts), ctAndTag), keys.kSig, seq)

    /** HMAC-SHA256 anonymisation of identifiers (SSID, BSSID, cell id) with a key that never leaves the phone. */
    fun anonymize(localKey: ByteArray, value: String) = "h:" + hex(hmac(localKey, value.toByteArray())).take(16)
}
