package mx.ipn.upiicsa.signaltrace.crypto

import java.nio.ByteBuffer
import java.security.MessageDigest

/**
 * YCM1: cifrado por rondas inventado con fines didácticos, sobre Z_256 (PLAN §6.5). Es la tercera implementación,
 * después de la de C# y la de JavaScript.
 * Cada ronda: suma de un flujo de llave LCG, mezcla con una matriz 2×2 de determinante 1 y rotación circular a la izquierda.
 * Es afín sobre Z_256, así que NO es seguro por sí solo: siempre va envuelto en AES-GCM o ChaCha20-Poly1305.
 */
object YaelCryptoMarch1 {
    /** Parámetros de una ronda: coeficientes de la matriz y rotación. */
    data class Round(val k: Int, val m: Int, val rot: Int)
    /** Semilla del LCG y parámetros de cada ronda. */
    data class Params(val seed: Int, val rounds: List<Round>)

    /** h = SHA-256(k_sig ‖ u64be(seq) ‖ "YCM1"); seed = h[0]; ronda i: k = h[1+3i], m = h[2+3i], rot = h[3+3i]. */
    fun deriveParams(kSig: ByteArray, seq: Long, rounds: Int = 3): Params {
        val buf = ByteBuffer.allocate(kSig.size + 8 + 4).put(kSig).putLong(seq).put("YCM1".toByteArray(Charsets.US_ASCII)).array()
        // Los bytes de Kotlin tienen signo; se pasan a 0..255.
        val h = MessageDigest.getInstance("SHA-256").digest(buf).map { it.toInt() and 0xFF }
        return Params(h[0], List(rounds) { i -> Round(h[1 + 3 * i], h[2 + 3 * i], h[3 + 3 * i]) })
    }

    /** Relleno con bloque de 2: agrega n bytes de valor n (1 o 2). */
    fun pad(data: ByteArray): ByteArray {
        val n = 2 - data.size % 2
        return data + ByteArray(n) { n.toByte() }
    }

    /** Quita y valida el relleno; lanza IllegalArgumentException si no es válido. */
    fun unpad(p: ByteArray): ByteArray {
        require(p.isNotEmpty()) { "bad padding" }
        val n = p.last().toInt() and 0xFF
        require((n == 1 || n == 2) && p.size >= n && (p.size - n until p.size).all { (p[it].toInt() and 0xFF) == n }) { "bad padding" }
        return p.copyOf(p.size - n)
    }

    /** Rotación circular a la izquierda (siempre devuelve una copia). */
    private fun rotl(p: IntArray, r: Int) = if (r == 0) p.copyOf() else IntArray(p.size) { p[(it + r) % p.size] }
    /** Rotación circular a la derecha. */
    private fun rotr(p: IntArray, r: Int) = if (r == 0) p.copyOf() else IntArray(p.size) { p[(it - r + p.size) % p.size] }

    /** Cifra con los parámetros derivados de la llave y la secuencia; trace recibe cada etapa intermedia. */
    fun encrypt(plain: ByteArray, kSig: ByteArray, seq: Long, rounds: Int = 3, trace: ((String, ByteArray) -> Unit)? = null) =
        encryptWithParams(plain, deriveParams(kSig, seq, rounds), trace)

    /** Descifra con los parámetros derivados de la llave y la secuencia. */
    fun decrypt(cipher: ByteArray, kSig: ByteArray, seq: Long, rounds: Int = 3) =
        decryptWithParams(cipher, deriveParams(kSig, seq, rounds))

    /**
     * Cifra con parámetros explícitos. Trabaja con un IntArray de 0..255 para evitar los bytes con signo.
     * @param usePad Si es false, no rellena y la entrada debe tener longitud par.
     */
    fun encryptWithParams(plain: ByteArray, prm: Params, trace: ((String, ByteArray) -> Unit)? = null, usePad: Boolean = true): ByteArray {
        var p = (if (usePad) pad(plain) else plain).map { it.toInt() and 0xFF }.toIntArray()
        require(p.size % 2 == 0) { "odd length" }
        val n = p.size
        var s = prm.seed
        trace?.invoke("padded", p.toBytes())
        prm.rounds.forEachIndexed { r, (k, m, rot) ->
            // 1) Flujo de llave: s ← (5s + 3) mod 256, continúa entre rondas.
            for (i in 0 until n) { s = (5 * s + 3) and 0xFF; p[i] = (p[i] + s) and 0xFF }
            trace?.invoke("r${r + 1}.stream", p.toBytes())
            // 2) Matriz [[1, k], [m, 1 + k·m]] sobre cada par.
            for (i in 0 until n step 2) {
                val a = p[i]; val b = p[i + 1]
                p[i] = (a + k * b) and 0xFF
                p[i + 1] = (m * a + (1 + k * m) * b) and 0xFF
            }
            trace?.invoke("r${r + 1}.matrix", p.toBytes())
            // 3) Rotación circular.
            if (n > 0) p = rotl(p, rot % n)
            trace?.invoke("r${r + 1}.rotate", p.toBytes())
        }
        return p.toBytes()
    }

    /**
     * Descifra con parámetros explícitos: genera todo el flujo y deshace las rondas de la última a la primera con
     * la matriz inversa [[1 + k·m, −k], [−m, 1]].
     */
    fun decryptWithParams(cipher: ByteArray, prm: Params, usePad: Boolean = true): ByteArray {
        val n = cipher.size
        require(n % 2 == 0 && (!usePad || n > 0)) { "bad length" }
        var s = prm.seed
        // Una fila de flujo por ronda, generadas en orden con el mismo estado continuo.
        val stream = prm.rounds.map { IntArray(n) { s = (5 * s + 3) and 0xFF; s } }
        var p = cipher.map { it.toInt() and 0xFF }.toIntArray()
        for (r in prm.rounds.indices.reversed()) {
            val (k, m, rot) = prm.rounds[r]
            p = rotr(p, rot % n)
            for (i in 0 until n step 2) {
                val c0 = p[i]; val c1 = p[i + 1]
                p[i] = ((1 + k * m) * c0 - k * c1) and 0xFF
                p[i + 1] = (-m * c0 + c1) and 0xFF
            }
            for (i in 0 until n) p[i] = (p[i] - stream[r][i]) and 0xFF
        }
        val out = p.toBytes()
        return if (usePad) unpad(out) else out
    }

    /** IntArray de 0..255 → ByteArray. */
    private fun IntArray.toBytes() = ByteArray(size) { this[it].toByte() }
}
