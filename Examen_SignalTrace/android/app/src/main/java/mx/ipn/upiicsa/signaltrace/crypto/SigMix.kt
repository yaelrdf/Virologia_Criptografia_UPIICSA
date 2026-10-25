package mx.ipn.upiicsa.signaltrace.crypto

import java.nio.ByteBuffer
import java.security.MessageDigest

/**
 * SigMix — invented educational round cipher over Z_256 (PLAN §6.5), third implementation after C# and JS.
 * Round: LCG keystream addition, 2×2 matrix mix with det = 1, circular left rotation.
 * Affine over Z_256 ⇒ NOT secure alone: it is always wrapped by AES-GCM / ChaCha20-Poly1305.
 */
object SigMix {
    data class Round(val k: Int, val m: Int, val rot: Int)
    data class Params(val seed: Int, val rounds: List<Round>)

    /** h = SHA-256(k_sig ‖ u64be(seq) ‖ "SigMix"); seed = h[0]; round i: k = h[1+3i], m = h[2+3i], rot = h[3+3i]. */
    fun deriveParams(kSig: ByteArray, seq: Long, rounds: Int = 3): Params {
        val buf = ByteBuffer.allocate(kSig.size + 8 + 6).put(kSig).putLong(seq).put("SigMix".toByteArray(Charsets.US_ASCII)).array()
        val h = MessageDigest.getInstance("SHA-256").digest(buf).map { it.toInt() and 0xFF }
        return Params(h[0], List(rounds) { i -> Round(h[1 + 3 * i], h[2 + 3 * i], h[3 + 3 * i]) })
    }

    fun pad(data: ByteArray): ByteArray {
        val n = 2 - data.size % 2
        return data + ByteArray(n) { n.toByte() }
    }

    fun unpad(p: ByteArray): ByteArray {
        require(p.isNotEmpty()) { "bad padding" }
        val n = p.last().toInt() and 0xFF
        require((n == 1 || n == 2) && p.size >= n && (p.size - n until p.size).all { (p[it].toInt() and 0xFF) == n }) { "bad padding" }
        return p.copyOf(p.size - n)
    }

    private fun rotl(p: IntArray, r: Int) = if (r == 0) p.copyOf() else IntArray(p.size) { p[(it + r) % p.size] }
    private fun rotr(p: IntArray, r: Int) = if (r == 0) p.copyOf() else IntArray(p.size) { p[(it - r + p.size) % p.size] }

    fun encrypt(plain: ByteArray, kSig: ByteArray, seq: Long, rounds: Int = 3, trace: ((String, ByteArray) -> Unit)? = null) =
        encryptWithParams(plain, deriveParams(kSig, seq, rounds), trace)

    fun decrypt(cipher: ByteArray, kSig: ByteArray, seq: Long, rounds: Int = 3) =
        decryptWithParams(cipher, deriveParams(kSig, seq, rounds))

    fun encryptWithParams(plain: ByteArray, prm: Params, trace: ((String, ByteArray) -> Unit)? = null, usePad: Boolean = true): ByteArray {
        var p = (if (usePad) pad(plain) else plain).map { it.toInt() and 0xFF }.toIntArray()
        require(p.size % 2 == 0) { "odd length" }
        val n = p.size
        var s = prm.seed
        trace?.invoke("padded", p.toBytes())
        prm.rounds.forEachIndexed { r, (k, m, rot) ->
            for (i in 0 until n) { s = (5 * s + 3) and 0xFF; p[i] = (p[i] + s) and 0xFF }
            trace?.invoke("r${r + 1}.stream", p.toBytes())
            for (i in 0 until n step 2) {
                val a = p[i]; val b = p[i + 1]
                p[i] = (a + k * b) and 0xFF
                p[i + 1] = (m * a + (1 + k * m) * b) and 0xFF
            }
            trace?.invoke("r${r + 1}.matrix", p.toBytes())
            if (n > 0) p = rotl(p, rot % n)
            trace?.invoke("r${r + 1}.rotate", p.toBytes())
        }
        return p.toBytes()
    }

    fun decryptWithParams(cipher: ByteArray, prm: Params, usePad: Boolean = true): ByteArray {
        val n = cipher.size
        require(n % 2 == 0 && (!usePad || n > 0)) { "bad length" }
        var s = prm.seed
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

    private fun IntArray.toBytes() = ByteArray(size) { this[it].toByte() }
}
