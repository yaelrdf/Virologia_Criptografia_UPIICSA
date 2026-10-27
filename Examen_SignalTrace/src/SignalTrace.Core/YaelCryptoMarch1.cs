using System.Security.Cryptography;

namespace SignalTrace.Core;

/// <summary>Receives the intermediate states of a YCM1 run (used by the admin console).</summary>
public interface IYcm1Trace
{
    void Stage(string name, ReadOnlySpan<byte> state);
}

/// <summary>Collects YCM1 stages in order: padded, r1.stream, r1.matrix, r1.rotate, r2.stream, ...</summary>
public sealed class Ycm1TraceRecorder : IYcm1Trace
{
    public List<(string Name, byte[] State)> Stages { get; } = [];

    public void Stage(string name, ReadOnlySpan<byte> state) => Stages.Add((name, state.ToArray()));
}

public readonly record struct Ycm1Round(int K, int M, int Rot);

public sealed record Ycm1Params(int Seed, Ycm1Round[] Rounds);

/// <summary>
/// YCM1: invented educational round cipher over Z_256 (see PLAN §6.5).
/// Each round: LCG keystream addition, 2x2 matrix mix with det = 1, circular left rotation.
/// NOT secure on its own: it is affine over Z_256 and is always wrapped by an AEAD (AES-GCM / ChaCha20-Poly1305).
/// </summary>
public static class YaelCryptoMarch1
{
    public const int DefaultRounds = 3;
    private static readonly byte[] Label = "YCM1"u8.ToArray();

    /// <summary>h = SHA-256(k_sig || u64be(seq) || "YCM1"); seed = h[0]; round i: k = h[1+3i], m = h[2+3i], rot = h[3+3i].</summary>
    public static Ycm1Params DeriveParams(ReadOnlySpan<byte> kSig, ulong seq, int rounds = DefaultRounds)
    {
        if (rounds is < 1 or > 10) throw new ArgumentOutOfRangeException(nameof(rounds));
        var h = SHA256.HashData(Bytes.Concat(kSig.ToArray(), Bytes.U64Be(seq), Label));
        var prm = new Ycm1Round[rounds];
        for (var i = 0; i < rounds; i++)
            prm[i] = new Ycm1Round(h[1 + 3 * i], h[2 + 3 * i], h[3 + 3 * i]);
        return new Ycm1Params(h[0], prm);
    }

    public static byte[] Encrypt(ReadOnlySpan<byte> plain, ReadOnlySpan<byte> kSig, ulong seq, int rounds = DefaultRounds, IYcm1Trace? trace = null)
        => EncryptWithParams(plain, DeriveParams(kSig, seq, rounds), trace);

    public static byte[] Decrypt(ReadOnlySpan<byte> cipher, ReadOnlySpan<byte> kSig, ulong seq, int rounds = DefaultRounds)
        => DecryptWithParams(cipher, DeriveParams(kSig, seq, rounds));

    /// <summary>Encrypt with explicit parameters (used by the cryptanalysis lab with reduced rounds).</summary>
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
            // 1) keystream addition with the LCG s <- (5s + 3) mod 256; s continues across rounds.
            for (var i = 0; i < n; i++)
            {
                s = (5 * s + 3) & 0xFF;
                p[i] = (byte)((p[i] + s) & 0xFF);
            }
            trace?.Stage($"r{r + 1}.stream", p);
            // 2) matrix mix K = [[1, k], [m, 1 + k*m]] on each byte pair (det K = 1).
            for (var i = 0; i < n; i += 2)
            {
                int a = p[i], b = p[i + 1];
                p[i] = (byte)((a + k * b) & 0xFF);
                p[i + 1] = (byte)((m * a + (1 + k * m) * b) & 0xFF);
            }
            trace?.Stage($"r{r + 1}.matrix", p);
            // 3) permutation: circular left rotation.
            p = n == 0 ? p : RotateLeft(p, rot % n);
            trace?.Stage($"r{r + 1}.rotate", p);
        }
        return p;
    }

    public static byte[] DecryptWithParams(ReadOnlySpan<byte> cipher, Ycm1Params prm, bool pad = true)
    {
        var n = cipher.Length;
        if (n % 2 != 0 || (pad && n == 0)) throw new CryptographicException("YCM1: invalid length.");
        var rounds = prm.Rounds.Length;
        // Regenerate every keystream row first, in order, with the same continuous LCG state.
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
            // K^-1 = [[1 + k*m, -k], [-m, 1]] (det = 1, so no modular inverse is needed).
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

    /// <summary>PKCS#7-like padding with block size 2: append n bytes of value n, n = 2 - (len mod 2). Always pads.</summary>
    public static byte[] Pad(ReadOnlySpan<byte> data)
    {
        var n = 2 - data.Length % 2;
        var o = new byte[data.Length + n];
        data.CopyTo(o);
        o.AsSpan(data.Length).Fill((byte)n);
        return o;
    }

    public static byte[] Unpad(byte[] p)
    {
        if (p.Length == 0) throw new CryptographicException("YCM1: bad padding.");
        var n = p[^1];
        if (n is not (1 or 2) || p.Length < n) throw new CryptographicException("YCM1: bad padding.");
        for (var i = p.Length - n; i < p.Length; i++)
            if (p[i] != n) throw new CryptographicException("YCM1: bad padding.");
        return p[..^n];
    }

    private static byte[] RotateLeft(byte[] p, int r) => r == 0 ? p : [.. p[r..], .. p[..r]];

    private static byte[] RotateRight(byte[] p, int r) => r == 0 ? p : [.. p[^r..], .. p[..^r]];

    /// <summary>Verifies the Hull–Dobell full period of s &lt;- (5s+3) mod 256 (returns the number of distinct states).</summary>
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
