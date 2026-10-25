namespace SignalTrace.Core;

/// <summary>
/// Known-plaintext attack against a reduced SigMix (1 round, parameters reused for several messages).
/// It shows why SigMix alone is academic: the round is affine over Z_256, so one (plaintext, ciphertext)
/// pair lets us solve k and m with linear congruences for every guess of seed (256) and rotation (n).
/// </summary>
public static class SigMixCryptanalysis
{
    public sealed record AttackResult(SigMixParams? Params, long Candidates, List<string> Log);

    public static AttackResult KnownPlaintextOneRound(byte[] plain, byte[] cipher)
    {
        var log = new List<string>();
        var p = SigMix.Pad(plain);
        var n = p.Length;
        if (cipher.Length != n) throw new ArgumentException("Plaintext and ciphertext lengths differ.");
        long tried = 0;
        log.Add($"Texto claro con relleno: {n} bytes ({n / 2} pares). Incógnitas: seed, k, m, rot.");
        for (var rot = 0; rot < n; rot++)
        {
            // Undo the rotation guess: y = rotr(c, rot) must equal K · (p + s).
            var y = new byte[n];
            for (var i = 0; i < n; i++) y[(i + rot) % n] = cipher[i];
            for (var seed = 0; seed < 256; seed++)
            {
                tried++;
                var s = seed;
                var a = new int[n];
                for (var i = 0; i < n; i++)
                {
                    s = (5 * s + 3) & 0xFF;
                    a[i] = (p[i] + s) & 0xFF;
                }
                // First pair: k·b ≡ y0 − a  (mod 256)  →  candidate k values.
                foreach (var k in SolveLinear(a[1], (y[0] - a[0]) & 0xFF))
                {
                    // m·(a + k·b) ≡ y1 − b  (mod 256), and a + k·b = y0.
                    foreach (var m in SolveLinear(y[0], (y[1] - a[1]) & 0xFF))
                    {
                        if (!Consistent(a, y, k, m)) continue;
                        log.Add($"Rotación {rot}, seed {seed}: k·{a[1]} ≡ {(y[0] - a[0]) & 0xFF} (mod 256) → k = {k}; m·{y[0]} ≡ {(y[1] - a[1]) & 0xFF} (mod 256) → m = {m}.");
                        log.Add($"Todos los {n / 2} pares cumplen K·(p+s) = c. Candidatos probados: {tried}.");
                        return new AttackResult(new SigMixParams(seed, [new SigMixRound(k, m, rot)]), tried, log);
                    }
                }
            }
        }
        log.Add("Sin solución (¿más de una ronda?).");
        return new AttackResult(null, tried, log);
    }

    private static bool Consistent(int[] a, byte[] y, int k, int m)
    {
        for (var i = 0; i < a.Length; i += 2)
        {
            if (((a[i] + k * a[i + 1]) & 0xFF) != y[i]) return false;
            if (((m * a[i] + (1 + k * m) * a[i + 1]) & 0xFF) != y[i + 1]) return false;
        }
        return true;
    }

    /// <summary>All x in Z_256 with a·x ≡ b (mod 256). Solvable iff gcd(a, 256) | b; then there are gcd solutions.</summary>
    public static IEnumerable<int> SolveLinear(int a, int b, int mod = 256)
    {
        a = ((a % mod) + mod) % mod;
        b = ((b % mod) + mod) % mod;
        var g = Gcd(a, mod);
        if (b % g != 0) yield break;
        var m2 = mod / g;
        var x0 = m2 == 1 ? 0 : (int)((long)(b / g) * ModInverse(a / g, m2) % m2);
        for (var t = 0; t < g; t++) yield return x0 + t * m2;
    }

    public static int Gcd(int a, int b)
    {
        while (b != 0) (a, b) = (b, a % b);
        return Math.Abs(a);
    }

    /// <summary>Extended Euclidean algorithm: a⁻¹ mod m (requires gcd(a, m) = 1).</summary>
    public static int ModInverse(int a, int m)
    {
        int oldR = a, r = m, oldS = 1, s = 0;
        while (r != 0)
        {
            var q = oldR / r;
            (oldR, r) = (r, oldR - q * r);
            (oldS, s) = (s, oldS - q * s);
        }
        if (oldR != 1) throw new ArgumentException("Not invertible.");
        return ((oldS % m) + m) % m;
    }
}
