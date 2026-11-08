namespace SignalTrace.Core;

/// <summary>
/// Ataque de texto claro conocido contra una versión reducida de YCM1 (1 ronda, con los mismos parámetros
/// reutilizados en varios mensajes). Muestra por qué YCM1 solo es académico: la ronda es afín sobre Z_256,
/// así que con un solo par (texto claro, texto cifrado) se pueden despejar k y m con congruencias lineales
/// para cada suposición de la semilla (256 opciones) y de la rotación (n opciones).
/// </summary>
public static class Ycm1Cryptanalysis
{
    /// <summary>
    /// Resultado del ataque. <paramref name="Params"/> guarda la rotación reducida módulo n, que es todo lo que
    /// revela el par conocido. El byte real de rotación es uno de <paramref name="RotationCandidates"/>
    /// (rot + j·n ≤ 255); para descifrar un mensaje de otra longitud el atacante los prueba todos.
    /// </summary>
    /// <param name="Params">Parámetros recuperados, o <c>null</c> si no hubo solución.</param>
    /// <param name="Candidates">Número de combinaciones (rotación, semilla) probadas.</param>
    /// <param name="Log">Explicación paso a paso para mostrar en el laboratorio.</param>
    /// <param name="RotationCandidates">Valores posibles del byte de rotación original.</param>
    public sealed record AttackResult(Ycm1Params? Params, long Candidates, List<string> Log, int[] RotationCandidates);

    /// <summary>
    /// Descifra otro texto cifrado hecho con los mismos parámetros. Prueba cada rotación candidata y se queda
    /// con el primer resultado que <paramref name="plausible"/> acepte (por ejemplo, que sea JSON válido).
    /// </summary>
    /// <param name="r">Resultado del ataque.</param>
    /// <param name="cipher">Otro texto cifrado con los mismos parámetros.</param>
    /// <param name="plausible">Criterio para reconocer un texto claro correcto.</param>
    /// <returns>El texto claro recuperado, o <c>null</c> si ninguna rotación funciona.</returns>
    public static byte[]? DecryptOther(AttackResult r, byte[] cipher, Func<byte[], bool> plausible)
    {
        if (r.Params is not { } p) return null;
        var round = p.Rounds[0];
        foreach (var rot in r.RotationCandidates)
        {
            try
            {
                var plain = YaelCryptoMarch1.DecryptWithParams(cipher, new Ycm1Params(p.Seed, [round with { Rot = rot }]));
                if (plausible(plain)) return plain;
            }
            // Un relleno inválido solo indica que esta rotación no es la correcta.
            catch (System.Security.Cryptography.CryptographicException) { }
        }
        return null;
    }

    /// <summary>
    /// Recupera semilla, k, m y rotación de una ronda de YCM1 a partir de un par conocido.
    /// Para cada rotación y cada semilla: deshace la rotación, calcula a = p + s y resuelve
    /// k·b ≡ y₀ − a y m·y₀ ≡ y₁ − b (mod 256) con el primer par; después comprueba que todos los pares cumplan.
    /// </summary>
    /// <param name="plain">Texto claro conocido (sin relleno).</param>
    /// <param name="cipher">Su texto cifrado con una ronda.</param>
    /// <exception cref="ArgumentException">Si las longitudes no coinciden.</exception>
    public static AttackResult KnownPlaintextOneRound(byte[] plain, byte[] cipher)
    {
        var log = new List<string>();
        var p = YaelCryptoMarch1.Pad(plain);
        var n = p.Length;
        if (cipher.Length != n) throw new ArgumentException("Plaintext and ciphertext lengths differ.");
        long tried = 0;
        log.Add($"Texto claro con relleno: {n} bytes ({n / 2} pares). Incógnitas: seed, k, m, rot.");
        for (var rot = 0; rot < n; rot++)
        {
            // Deshace la rotación supuesta: y = rotr(c, rot) debe ser igual a K · (p + s).
            var y = new byte[n];
            for (var i = 0; i < n; i++) y[(i + rot) % n] = cipher[i];
            for (var seed = 0; seed < 256; seed++)
            {
                tried++;
                // Recalcula el flujo de llave para esta semilla y lo suma al texto claro.
                var s = seed;
                var a = new int[n];
                for (var i = 0; i < n; i++)
                {
                    s = (5 * s + 3) & 0xFF;
                    a[i] = (p[i] + s) & 0xFF;
                }
                // Primer par: k·b ≡ y0 − a  (mod 256)  →  valores candidatos de k.
                foreach (var k in SolveLinear(a[1], (y[0] - a[0]) & 0xFF))
                {
                    // m·(a + k·b) ≡ y1 − b  (mod 256), y a + k·b = y0.
                    foreach (var m in SolveLinear(y[0], (y[1] - a[1]) & 0xFF))
                    {
                        if (!Consistent(a, y, k, m)) continue;
                        log.Add($"Rotación {rot}, seed {seed}: k·{a[1]} ≡ {(y[0] - a[0]) & 0xFF} (mod 256) → k = {k}; m·{y[0]} ≡ {(y[1] - a[1]) & 0xFF} (mod 256) → m = {m}.");
                        // Todos los bytes de rotación que dan el mismo residuo módulo n.
                        var rots = Enumerable.Range(0, 256).Where(r => r % n == rot).ToArray();
                        log.Add($"Todos los {n / 2} pares cumplen K·(p+s) = c. Candidatos probados: {tried}.");
                        log.Add($"La rotación real es un byte r con r mod {n} = {rot}: {rots.Length} posibles valores ({string.Join(", ", rots)}).");
                        return new AttackResult(new Ycm1Params(seed, [new Ycm1Round(k, m, rot)]), tried, log, rots);
                    }
                }
            }
        }
        log.Add("Sin solución (¿más de una ronda?).");
        return new AttackResult(null, tried, log, []);
    }

    /// <summary>Comprueba que la matriz con k y m transforme todos los pares de <paramref name="a"/> en los de <paramref name="y"/>.</summary>
    private static bool Consistent(int[] a, byte[] y, int k, int m)
    {
        for (var i = 0; i < a.Length; i += 2)
        {
            if (((a[i] + k * a[i + 1]) & 0xFF) != y[i]) return false;
            if (((m * a[i] + (1 + k * m) * a[i + 1]) & 0xFF) != y[i + 1]) return false;
        }
        return true;
    }

    /// <summary>
    /// Todas las x de Z_256 con a·x ≡ b (mod 256). Tiene solución si y solo si mcd(a, 256) divide a b;
    /// en ese caso hay mcd soluciones, separadas por 256/mcd.
    /// </summary>
    /// <param name="a">Coeficiente.</param>
    /// <param name="b">Término independiente.</param>
    /// <param name="mod">Módulo (256 por defecto).</param>
    public static IEnumerable<int> SolveLinear(int a, int b, int mod = 256)
    {
        a = ((a % mod) + mod) % mod;
        b = ((b % mod) + mod) % mod;
        var g = Gcd(a, mod);
        if (b % g != 0) yield break;
        // Solución particular: divide todo entre g y usa el inverso de a/g módulo mod/g.
        var m2 = mod / g;
        var x0 = m2 == 1 ? 0 : (int)((long)(b / g) * ModInverse(a / g, m2) % m2);
        for (var t = 0; t < g; t++) yield return x0 + t * m2;
    }

    /// <summary>Máximo común divisor por el algoritmo de Euclides.</summary>
    public static int Gcd(int a, int b)
    {
        while (b != 0) (a, b) = (b, a % b);
        return Math.Abs(a);
    }

    /// <summary>Algoritmo extendido de Euclides: a⁻¹ mod m (requiere mcd(a, m) = 1).</summary>
    /// <exception cref="ArgumentException">Si <paramref name="a"/> no es invertible módulo <paramref name="m"/>.</exception>
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
