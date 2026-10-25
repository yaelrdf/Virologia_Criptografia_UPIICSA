using System.Security.Cryptography;

namespace SignalTrace.Core;

/// <summary>Receives the intermediate states of a SigMix run (used by the admin console).</summary>
public interface ISigMixTrace
{
    void Stage(string name, ReadOnlySpan<byte> state);
}

/// <summary>Collects SigMix stages in order: padded, r1.stream, r1.matrix, r1.rotate, r2.stream, ...</summary>
public sealed class SigMixTraceRecorder : ISigMixTrace
{
    public List<(string Name, byte[] State)> Stages { get; } = [];

    public void Stage(string name, ReadOnlySpan<byte> state) => Stages.Add((name, state.ToArray()));
}

public readonly record struct SigMixRound(int K, int M, int Rot);

public sealed record SigMixParams(int Seed, SigMixRound[] Rounds);

/// <summary>
/// SigMix: invented educational round cipher over Z_256 (see PLAN §6.5).
/// Each round: LCG keystream addition, 2x2 matrix mix with det = 1, circular left rotation.
/// NOT secure on its own: it is affine over Z_256 and is always wrapped by an AEAD (AES-GCM / ChaCha20-Poly1305).
/// </summary>
public static class SigMix
{
    public const int DefaultRounds = 3;
    private static readonly byte[] Label = "SigMix"u8.ToArray();

    /// <summary>h = SHA-256(k_sig || u64be(seq) || "SigMix"); seed = h[0]; round i: k = h[1+3i], m = h[2+3i], rot = h[3+3i].</summary>
    public static SigMixParams DeriveParams(ReadOnlySpan<byte> kSig, ulong seq, int rounds = DefaultRounds)
    {
        if (rounds is < 1 or > 10) throw new ArgumentOutOfRangeException(nameof(rounds));
        var h = SHA256.HashData(Bytes.Concat(kSig.ToArray(), Bytes.U64Be(seq), Label));
        var prm = new SigMixRound[rounds];
        for (var i = 0; i < rounds; i++)
            prm[i] = new SigMixRound(h[1 + 3 * i], h[2 + 3 * i], h[3 + 3 * i]);
        return new SigMixParams(h[0], prm);
    }

    public static byte[] Encrypt(ReadOnlySpan<byte> plain, ReadOnlySpan<byte> kSig, ulong seq, int rounds = DefaultRounds, ISigMixTrace? trace = null)
        => EncryptWithParams(plain, DeriveParams(kSig, seq, rounds), trace);

    public static byte[] Decrypt(ReadOnlySpan<byte> cipher, ReadOnlySpan<byte> kSig, ulong seq, int rounds = DefaultRounds)
        => DecryptWithParams(cipher, DeriveParams(kSig, seq, rounds));

    /// <summary>Encrypt with explicit parameters (used by the cryptanalysis lab with reduced rounds).</summary>
    public static byte[] EncryptWithParams(ReadOnlySpan<byte> plain, SigMixParams prm, ISigMixTrace? trace = null, bool pad = true)
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

    public static byte[] DecryptWithParams(ReadOnlySpan<byte> cipher, SigMixParams prm, bool pad = true)
    {
        var n = cipher.Length;
        if (n % 2 != 0 || (pad && n == 0)) throw new CryptographicException("SigMix: invalid length.");
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
        if (p.Length == 0) throw new CryptographicException("SigMix: bad padding.");
        var n = p[^1];
        if (n is not (1 or 2) || p.Length < n) throw new CryptographicException("SigMix: bad padding.");
        for (var i = p.Length - n; i < p.Length; i++)
            if (p[i] != n) throw new CryptographicException("SigMix: bad padding.");
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
