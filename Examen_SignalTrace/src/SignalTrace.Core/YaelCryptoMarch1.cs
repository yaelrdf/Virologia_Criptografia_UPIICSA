using System.Security.Cryptography;

namespace SignalTrace.Core;

/// <summary>Recibe los estados intermedios de una ejecución de YCM1 (lo usa la consola del administrador).</summary>
public interface IYcm1Trace
{
    /// <summary>Registra el estado del bloque después de una etapa.</summary>
    /// <param name="name">Nombre de la etapa, por ejemplo <c>"r1.matrix"</c>.</param>
    /// <param name="state">Bytes del bloque en ese momento.</param>
    void Stage(string name, ReadOnlySpan<byte> state);
}

/// <summary>
/// Guarda las etapas de YCM1 en orden: padded, r1.stream, r1.matrix, r1.rotate, r2.stream, …
/// Cada estado se copia, porque el cifrado sigue modificando el mismo arreglo.
/// </summary>
public sealed class Ycm1TraceRecorder : IYcm1Trace
{
    /// <summary>Etapas registradas con su nombre y una copia del estado.</summary>
    public List<(string Name, byte[] State)> Stages { get; } = [];

    /// <inheritdoc />
    public void Stage(string name, ReadOnlySpan<byte> state) => Stages.Add((name, state.ToArray()));
}

/// <summary>Parámetros de una ronda de YCM1.</summary>
/// <param name="K">Coeficiente k de la matriz de mezcla.</param>
/// <param name="M">Coeficiente m de la matriz de mezcla.</param>
/// <param name="Rot">Desplazamiento de la rotación circular (se reduce módulo la longitud del bloque).</param>
public readonly record struct Ycm1Round(int K, int M, int Rot);

/// <summary>Parámetros completos de un cifrado YCM1: la semilla del generador y los de cada ronda.</summary>
/// <param name="Seed">Estado inicial del generador congruencial lineal (LCG).</param>
/// <param name="Rounds">Parámetros de cada ronda.</param>
public sealed record Ycm1Params(int Seed, Ycm1Round[] Rounds);

/// <summary>
/// Yael Crypto March1 (YCM1): cifrado por rondas inventado con fines didácticos, que opera sobre Z_256 (PLAN §6.5).
/// Cada ronda hace tres pasos:
/// <list type="number">
/// <item>suma un flujo de llave generado por un LCG, s ← (5s + 3) mod 256;</item>
/// <item>mezcla cada par de bytes con una matriz 2×2 de determinante 1;</item>
/// <item>rota el bloque completo a la izquierda.</item>
/// </list>
/// NO es seguro por sí solo: es afín sobre Z_256 (ver <see cref="Ycm1Cryptanalysis"/>), por eso siempre va
/// envuelto en un AEAD estándar (AES-GCM o ChaCha20-Poly1305).
/// </summary>
public static class YaelCryptoMarch1
{
    /// <summary>Número de rondas que usa el protocolo.</summary>
    public const int DefaultRounds = 3;
    /// <summary>Etiqueta que separa el hash de parámetros de cualquier otro uso de k_sig.</summary>
    private static readonly byte[] Label = "YCM1"u8.ToArray();

    /// <summary>
    /// Deriva los parámetros de un paquete: h = SHA-256(k_sig ‖ u64be(seq) ‖ "YCM1"); seed = h[0];
    /// ronda i: k = h[1+3i], m = h[2+3i], rot = h[3+3i]. Como depende de la secuencia, cada paquete usa
    /// parámetros distintos.
    /// </summary>
    /// <param name="kSig">Llave YCM1 de la sesión.</param>
    /// <param name="seq">Número de secuencia del paquete.</param>
    /// <param name="rounds">Número de rondas (1 a 10).</param>
    public static Ycm1Params DeriveParams(ReadOnlySpan<byte> kSig, ulong seq, int rounds = DefaultRounds)
    {
        // Con 32 bytes de hash caben la semilla y hasta 10 rondas de 3 bytes.
        if (rounds is < 1 or > 10) throw new ArgumentOutOfRangeException(nameof(rounds));
        var h = SHA256.HashData(Bytes.Concat(kSig.ToArray(), Bytes.U64Be(seq), Label));
        var prm = new Ycm1Round[rounds];
        for (var i = 0; i < rounds; i++)
            prm[i] = new Ycm1Round(h[1 + 3 * i], h[2 + 3 * i], h[3 + 3 * i]);
        return new Ycm1Params(h[0], prm);
    }

    /// <summary>Cifra con los parámetros derivados de la llave y la secuencia.</summary>
    /// <param name="plain">Texto claro.</param>
    /// <param name="kSig">Llave YCM1.</param>
    /// <param name="seq">Número de secuencia.</param>
    /// <param name="rounds">Número de rondas.</param>
    /// <param name="trace">Destino opcional de las etapas intermedias.</param>
    public static byte[] Encrypt(ReadOnlySpan<byte> plain, ReadOnlySpan<byte> kSig, ulong seq, int rounds = DefaultRounds, IYcm1Trace? trace = null)
        => EncryptWithParams(plain, DeriveParams(kSig, seq, rounds), trace);

    /// <summary>Descifra con los parámetros derivados de la llave y la secuencia.</summary>
    public static byte[] Decrypt(ReadOnlySpan<byte> cipher, ReadOnlySpan<byte> kSig, ulong seq, int rounds = DefaultRounds)
        => DecryptWithParams(cipher, DeriveParams(kSig, seq, rounds));

    /// <summary>
    /// Cifra con parámetros explícitos. El laboratorio de criptoanálisis lo usa con menos rondas.
    /// </summary>
    /// <param name="plain">Texto claro.</param>
    /// <param name="prm">Semilla y parámetros de cada ronda.</param>
    /// <param name="trace">Destino opcional de las etapas.</param>
    /// <param name="pad">Si es <c>false</c>, no agrega relleno y la entrada debe tener longitud par.</param>
    public static byte[] EncryptWithParams(ReadOnlySpan<byte> plain, Ycm1Params prm, IYcm1Trace? trace = null, bool pad = true)
    {
        var p = pad ? Pad(plain) : plain.ToArray();
        if (p.Length % 2 != 0) throw new ArgumentException("Unpadded input must have even length.", nameof(plain));
        var n = p.Length;
        var s = prm.Seed;
        trace?.Stage("padded", p);
        for (var r = 0; r < prm.Rounds.Length; r++)
        {
            var (k, m, rot) = prm.Rounds[r];
            // 1) Suma del flujo de llave con el LCG s ← (5s + 3) mod 256; s continúa de una ronda a la siguiente.
            for (var i = 0; i < n; i++)
            {
                s = (5 * s + 3) & 0xFF;
                p[i] = (byte)((p[i] + s) & 0xFF);
            }
            trace?.Stage($"r{r + 1}.stream", p);
            // 2) Mezcla con la matriz K = [[1, k], [m, 1 + k·m]] sobre cada par de bytes (det K = 1, siempre invertible).
            for (var i = 0; i < n; i += 2)
            {
                int a = p[i], b = p[i + 1];
                p[i] = (byte)((a + k * b) & 0xFF);
                p[i + 1] = (byte)((m * a + (1 + k * m) * b) & 0xFF);
            }
            trace?.Stage($"r{r + 1}.matrix", p);
            // 3) Permutación: rotación circular a la izquierda, para que los pares cambien entre rondas.
            p = n == 0 ? p : RotateLeft(p, rot % n);
            trace?.Stage($"r{r + 1}.rotate", p);
        }
        return p;
    }

    /// <summary>
    /// Descifra con parámetros explícitos aplicando las rondas al revés: rotación a la derecha,
    /// matriz inversa y resta del flujo de llave.
    /// </summary>
    /// <param name="cipher">Texto cifrado (longitud par).</param>
    /// <param name="prm">Los mismos parámetros que se usaron al cifrar.</param>
    /// <param name="pad">Si es <c>true</c>, quita y valida el relleno al final.</param>
    /// <exception cref="CryptographicException">Si la longitud o el relleno no son válidos.</exception>
    public static byte[] DecryptWithParams(ReadOnlySpan<byte> cipher, Ycm1Params prm, bool pad = true)
    {
        var n = cipher.Length;
        if (n % 2 != 0 || (pad && n == 0)) throw new CryptographicException("YCM1: invalid length.");
        var rounds = prm.Rounds.Length;
        // Primero regenera todas las filas del flujo de llave, en orden y con el mismo estado continuo del LCG,
        // porque al descifrar las rondas se recorren de la última a la primera.
        var stream = new byte[rounds][];
        var s = prm.Seed;
        for (var r = 0; r < rounds; r++)
        {
            stream[r] = new byte[n];
            for (var i = 0; i < n; i++)
            {
                s = (5 * s + 3) & 0xFF;
                stream[r][i] = (byte)s;
            }
        }
        var p = cipher.ToArray();
        for (var r = rounds - 1; r >= 0; r--)
        {
            var (k, m, rot) = prm.Rounds[r];
            p = RotateRight(p, rot % n);
            // K⁻¹ = [[1 + k·m, −k], [−m, 1]]; como det = 1, no hace falta un inverso modular.
            for (var i = 0; i < n; i += 2)
            {
                int c0 = p[i], c1 = p[i + 1];
                p[i] = (byte)(((1 + k * m) * c0 - k * c1) & 0xFF);
                p[i + 1] = (byte)((-m * c0 + c1) & 0xFF);
            }
            for (var i = 0; i < n; i++)
                p[i] = (byte)((p[i] - stream[r][i]) & 0xFF);
        }
        return pad ? Unpad(p) : p;
    }

    /// <summary>
    /// Relleno estilo PKCS#7 con bloque de 2 bytes: agrega n bytes de valor n, con n = 2 − (longitud mod 2).
    /// Siempre agrega relleno (1 o 2 bytes), así el descifrado sabe cuánto quitar sin ambigüedad.
    /// </summary>
    public static byte[] Pad(ReadOnlySpan<byte> data)
    {
        var n = 2 - data.Length % 2;
        var o = new byte[data.Length + n];
        data.CopyTo(o);
        o.AsSpan(data.Length).Fill((byte)n);
        return o;
    }

    /// <summary>Quita el relleno y comprueba que todos sus bytes valgan lo mismo (1 o 2).</summary>
    /// <exception cref="CryptographicException">Si el relleno no es válido.</exception>
    public static byte[] Unpad(byte[] p)
    {
        if (p.Length == 0) throw new CryptographicException("YCM1: bad padding.");
        var n = p[^1];
        if (n is not (1 or 2) || p.Length < n) throw new CryptographicException("YCM1: bad padding.");
        for (var i = p.Length - n; i < p.Length; i++)
            if (p[i] != n) throw new CryptographicException("YCM1: bad padding.");
        return p[..^n];
    }

    /// <summary>Rotación circular a la izquierda de <paramref name="r"/> posiciones.</summary>
    private static byte[] RotateLeft(byte[] p, int r) => r == 0 ? p : [.. p[r..], .. p[..r]];

    /// <summary>Rotación circular a la derecha de <paramref name="r"/> posiciones (inversa de <see cref="RotateLeft"/>).</summary>
    private static byte[] RotateRight(byte[] p, int r) => r == 0 ? p : [.. p[^r..], .. p[..^r]];

    /// <summary>
    /// Comprueba que el LCG s ← (5s+3) mod 256 tiene periodo completo según el teorema de Hull–Dobell
    /// (c = 3 es impar y a − 1 = 4 es múltiplo de 4). Devuelve cuántos estados distintos recorre (256).
    /// </summary>
    public static int LcgPeriod()
    {
        var seen = new HashSet<int>();
        var x = 0;
        for (var i = 0; i < 256; i++)
        {
            x = (5 * x + 3) & 0xFF;
            seen.Add(x);
        }
        return seen.Count;
    }
}
