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

/**
 * Protocolo SignalTrace v1 (PLAN §6) con la JCA (Java Cryptography Architecture): produce los mismos bytes que
 * SignalTrace.Core (C#) y session.js. Contiene HKDF, la codificación de llaves P-256, el transcript y su firma, y el
 * sellado de paquetes YCM1 + AEAD.
 */
object Protocol {
    /** Nombre de la suite AES-256-GCM. */
    const val SUITE_AES = "aes-256-gcm"
    /** Nombre de la suite ChaCha20-Poly1305. */
    const val SUITE_CHACHA = "chacha20-poly1305"
    /** Generador aleatorio criptográfico compartido. */
    private val random = SecureRandom()

    /** n bytes aleatorios. */
    fun randomBytes(n: Int) = ByteArray(n).also { random.nextBytes(it) }
    /** Entero de 64 bits en 8 bytes big-endian. */
    fun u64(v: Long): ByteArray = ByteBuffer.allocate(8).putLong(v).array()
    /** Bytes → hexadecimal en minúsculas. */
    fun hex(b: ByteArray) = b.joinToString("") { "%02x".format(it) }
    /** Hexadecimal → bytes. */
    fun unhex(s: String) = ByteArray(s.length / 2) { s.substring(2 * it, 2 * it + 2).toInt(16).toByte() }
    /** SHA-256 de un bloque. */
    fun sha256(b: ByteArray): ByteArray = MessageDigest.getInstance("SHA-256").digest(b)
    /** Huella de una llave pública: hex(SHA-256(punto de 65 bytes)). */
    fun fingerprint(publicKey: ByteArray) = hex(sha256(publicKey))

    /** ChaCha20-Poly1305 existe en la JCA a partir de Android 9 (API 28) y del JDK 11. */
    fun chachaSupported(): Boolean = runCatching { Cipher.getInstance("ChaCha20-Poly1305"); true }.getOrDefault(false)

    // ---------- HKDF-SHA256 (RFC 5869), escrito paso a paso para que se vean las etapas
    /** HMAC-SHA256 de data con la llave key. */
    fun hmac(key: ByteArray, data: ByteArray): ByteArray =
        Mac.getInstance("HmacSHA256").apply { init(SecretKeySpec(key, "HmacSHA256")) }.doFinal(data)

    /**
     * HKDF-SHA256: extracción PRK = HMAC(sal, ikm) y expansión T(i) = HMAC(PRK, T(i−1) ‖ info ‖ i) hasta juntar
     * [length] bytes.
     */
    fun hkdf(ikm: ByteArray, salt: ByteArray, info: String, length: Int): ByteArray {
        val prk = hmac(salt, ikm)                                   // extracción
        val out = ByteArrayOutputStream()
        var t = ByteArray(0)
        var counter = 1
        while (out.size() < length) {                               // expansión: T(i) = HMAC(PRK, T(i-1) ‖ info ‖ i)
            t = hmac(prk, t + info.toByteArray() + byteArrayOf(counter++.toByte()))
            out.write(t)
        }
        return out.toByteArray().copyOf(length)
    }

    /**
     * Llaves de una sesión.
     * @property kAead Llave de 32 bytes de la suite AEAD.
     * @property kSig Llave de 32 bytes de YCM1.
     * @property ivSalt Sal de 4 bytes del nonce.
     * @property suite Suite negociada.
     */
    data class SessionKeys(val kAead: ByteArray, val kSig: ByteArray, val ivSalt: ByteArray, val suite: String)

    /** Deriva las tres llaves del secreto compartido con sal = nonceC ‖ nonceS, igual que el servidor. */
    fun deriveKeys(z: ByteArray, nonceC: ByteArray, nonceS: ByteArray, suite: String = SUITE_AES): SessionKeys {
        val salt = nonceC + nonceS
        return SessionKeys(
            hkdf(z, salt, if (suite == SUITE_CHACHA) "st1/c20-up" else "st1/aes-up", 32),
            hkdf(z, salt, "st1/ycm1", 32),
            hkdf(z, salt, "st1/iv-up", 4),
            suite,
        )
    }

    // ---------- Llaves P-256 como puntos X9.62 sin comprimir de 65 bytes (04 ‖ X ‖ Y)
    /** Parámetros de la curva P-256 (secp256r1). */
    private val p256: ECParameterSpec by lazy {
        AlgorithmParameters.getInstance("EC").apply { init(ECGenParameterSpec("secp256r1")) }.getParameterSpec(ECParameterSpec::class.java)
    }

    /**
     * Coordenada como 32 bytes exactos. BigInteger puede dar 33 bytes (un 0 de signo al inicio) o menos de 32;
     * se recorta o se rellena con ceros a la izquierda.
     */
    private fun fixed32(v: BigInteger): ByteArray {
        val b = v.toByteArray()
        return when {
            b.size == 32 -> b
            b.size > 32 -> b.copyOfRange(b.size - 32, b.size)
            else -> ByteArray(32 - b.size) + b
        }
    }

    /** Exporta una llave pública como punto sin comprimir de 65 bytes. */
    fun exportPublic(key: ECPublicKey): ByteArray = byteArrayOf(4) + fixed32(key.w.affineX) + fixed32(key.w.affineY)

    /** Importa un punto de 65 bytes como llave pública P-256. */
    fun importPublic(point: ByteArray): ECPublicKey {
        require(point.size == 65 && point[0] == 4.toByte()) { "Public key must be a 65-byte uncompressed P-256 point" }
        val spec = ECPublicKeySpec(ECPoint(BigInteger(1, point.copyOfRange(1, 33)), BigInteger(1, point.copyOfRange(33, 65))), p256)
        return KeyFactory.getInstance("EC").generatePublic(spec) as ECPublicKey
    }

    /** Reconstruye una llave privada P-256 a partir de su escalar de 32 bytes (vectores de prueba). */
    fun privateKeyFromScalar(d: ByteArray): java.security.PrivateKey =
        KeyFactory.getInstance("EC").generatePrivate(java.security.spec.ECPrivateKeySpec(BigInteger(1, d), p256))

    /** Secreto compartido ECDH entre una llave privada propia y un punto público ajeno. */
    fun sharedSecret(own: java.security.PrivateKey, peerPublic: ByteArray): ByteArray =
        KeyAgreement.getInstance("ECDH").apply { init(own); doPhase(importPublic(peerPublic), true) }.generateSecret()

    /** Par de llaves efímeras P-256 para un handshake. */
    fun generateEphemeral(): KeyPair = KeyPairGenerator.getInstance("EC").apply { initialize(ECGenParameterSpec("secp256r1"), random) }.generateKeyPair()

    /** Coordenada X del punto compartido (32 B), igual que deriveBits de Web Crypto y DeriveRawSecretAgreement de .NET. */
    fun sharedSecret(own: KeyPair, peerPublic: ByteArray): ByteArray = sharedSecret(own.private, peerPublic)

    /** Transcript que firma el servidor; una suite distinta de AES-GCM se agrega al final. */
    fun transcript(pubC: ByteArray, pubS: ByteArray, nonceC: ByteArray, nonceS: ByteArray, deviceId: String, sessionId: String, suite: String = SUITE_AES): ByteArray {
        val t = "ST1-HS".toByteArray() + pubC + pubS + nonceC + nonceS + deviceId.toByteArray() + sessionId.toByteArray()
        return if (suite == SUITE_AES) t else t + "|$suite".toByteArray()
    }

    /** IEEE P1363 (r ‖ s, 64 B) → ASN.1 DER, que es lo que espera el verificador «SHA256withECDSA» de la JCA. */
    fun p1363ToDer(sig: ByteArray): ByteArray {
        require(sig.size == 64) { "P1363 signature must be 64 bytes" }
        // Un INTEGER de DER: sin ceros a la izquierda, con un 0 extra si el primer bit vale 1 (para que no sea negativo).
        fun int(b: ByteArray): ByteArray {
            var v = b.dropWhile { it == 0.toByte() }.toByteArray()
            if (v.isEmpty()) v = byteArrayOf(0)
            if (v[0].toInt() and 0x80 != 0) v = byteArrayOf(0) + v
            return byteArrayOf(0x02, v.size.toByte()) + v
        }
        // SEQUENCE { r, s }.
        val body = int(sig.copyOfRange(0, 32)) + int(sig.copyOfRange(32, 64))
        return byteArrayOf(0x30, body.size.toByte()) + body
    }

    /** Verifica la firma del servidor con su llave de largo plazo; cualquier error cuenta como firma inválida. */
    fun verifySignature(serverLongTermPublic: ByteArray, transcript: ByteArray, signatureP1363: ByteArray): Boolean = runCatching {
        Signature.getInstance("SHA256withECDSA").run {
            initVerify(importPublic(serverLongTermPublic))
            update(transcript)
            verify(p1363ToDer(signatureP1363))
        }
    }.getOrDefault(false)

    // ---------- Paquetes
    /** Nonce AEAD de 12 bytes: sal ‖ u64be(seq). */
    fun nonce(ivSalt: ByteArray, seq: Long) = ivSalt + u64(seq)
    /** Datos asociados: "ST1" ‖ dispositivo ‖ u64be(seq) ‖ u64be(ts). */
    fun aad(deviceId: String, seq: Long, ts: Long) = "ST1".toByteArray() + deviceId.toByteArray() + u64(seq) + u64(ts)

    /** Cifra o descifra con la suite de la sesión; la etiqueta de 16 bytes va al final del texto cifrado. */
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

    /** Sella un paquete: sm = YCM1(pt, k_sig, seq); ct‖tag = AEAD(k, iv_salt ‖ u64be(seq), sm, "ST1" ‖ did ‖ seq ‖ ts). */
    fun seal(keys: SessionKeys, deviceId: String, seq: Long, ts: Long, plaintext: ByteArray): ByteArray =
        aead(keys, Cipher.ENCRYPT_MODE, nonce(keys.ivSalt, seq), aad(deviceId, seq, ts), YaelCryptoMarch1.encrypt(plaintext, keys.kSig, seq))

    /** Abre un paquete; lanza [AEADBadTagException] cuando el paquete fue alterado. */
    fun open(keys: SessionKeys, deviceId: String, seq: Long, ts: Long, ctAndTag: ByteArray): ByteArray =
        YaelCryptoMarch1.decrypt(aead(keys, Cipher.DECRYPT_MODE, nonce(keys.ivSalt, seq), aad(deviceId, seq, ts), ctAndTag), keys.kSig, seq)
}
