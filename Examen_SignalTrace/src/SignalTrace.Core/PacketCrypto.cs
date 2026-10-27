using System.Diagnostics;
using System.Security.Cryptography;

namespace SignalTrace.Core;

/// <summary>Wire envelope posted to /api/telemetry (PLAN §6.4).</summary>
public sealed record TelemetryEnvelope(int V, string Sid, string Did, ulong Seq, long Ts, string Ct);

/// <summary>Raised when the AEAD tag does not verify (tampered packet or wrong key).</summary>
public sealed class AuthenticationFailedException() : CryptographicException("AEAD tag mismatch");

/// <summary>Everything the admin console shows about one packet, stage by stage.</summary>
public sealed class PacketTraceData
{
    public byte[] Plaintext { get; set; } = [];
    public Ycm1Params? Ycm1Params { get; set; }
    public Ycm1TraceRecorder Ycm1 { get; } = new();
    public byte[] Ycm1Output { get; set; } = [];
    public byte[] Nonce { get; set; } = [];
    public byte[] Aad { get; set; } = [];
    public byte[] Ciphertext { get; set; } = [];
    public byte[] Tag { get; set; } = [];
    public Dictionary<string, double> Micros { get; } = [];
}

/// <summary>
/// Packet sealing: sm = YCM1(plaintext, k_sig, seq); nonce = iv_salt || u64be(seq);
/// aad = "ST1" || utf8(deviceId) || u64be(seq) || u64be(ts); ct||tag = AEAD(k, nonce, sm, aad).
/// </summary>
public static class PacketCrypto
{
    public static byte[] Nonce(ReadOnlySpan<byte> ivSalt, ulong seq) => Bytes.Concat(ivSalt.ToArray(), Bytes.U64Be(seq));

    public static byte[] Aad(string deviceId, ulong seq, long tsMs)
        => Bytes.Concat(Protocol.AadLabel, Bytes.Utf8(deviceId), Bytes.U64Be(seq), Bytes.U64Be((ulong)tsMs));

    public static byte[] Seal(SessionKeys keys, string deviceId, ulong seq, long tsMs, ReadOnlySpan<byte> plaintext, PacketTraceData? trace = null)
    {
        var sw = Stopwatch.StartNew();
        var sm = YaelCryptoMarch1.Encrypt(plaintext, keys.KSig, seq, YaelCryptoMarch1.DefaultRounds, trace?.Ycm1);
        var tSig = sw.Elapsed.TotalMicroseconds;
        var nonce = Nonce(keys.IvSalt, seq);
        var aad = Aad(deviceId, seq, tsMs);
        var ct = new byte[sm.Length];
        var tag = new byte[Protocol.TagLength];
        AeadEncrypt(keys, nonce, sm, ct, tag, aad);
        if (trace != null)
        {
            trace.Plaintext = plaintext.ToArray();
            trace.Ycm1Params = YaelCryptoMarch1.DeriveParams(keys.KSig, seq);
            trace.Ycm1Output = sm;
            trace.Nonce = nonce;
            trace.Aad = aad;
            trace.Ciphertext = ct;
            trace.Tag = tag;
            trace.Micros["ycm1"] = tSig;
            trace.Micros["aead"] = sw.Elapsed.TotalMicroseconds - tSig;
        }
        return Bytes.Concat(ct, tag);
    }

    /// <summary>Verifies the tag, then reverses YCM1. Fills <paramref name="trace"/> with the same stages as Seal.</summary>
    public static byte[] Open(SessionKeys keys, string deviceId, ulong seq, long tsMs, ReadOnlySpan<byte> ctAndTag, PacketTraceData? trace = null)
    {
        if (ctAndTag.Length < Protocol.TagLength) throw new AuthenticationFailedException();
        var sw = Stopwatch.StartNew();
        var nonce = Nonce(keys.IvSalt, seq);
        var aad = Aad(deviceId, seq, tsMs);
        var ct = ctAndTag[..^Protocol.TagLength].ToArray();
        var tag = ctAndTag[^Protocol.TagLength..].ToArray();
        var sm = new byte[ct.Length];
        try
        {
            AeadDecrypt(keys, nonce, ct, tag, sm, aad);
        }
        catch (CryptographicException)
        {
            throw new AuthenticationFailedException();
        }
        var tAead = sw.Elapsed.TotalMicroseconds;
        var plain = YaelCryptoMarch1.Decrypt(sm, keys.KSig, seq);
        if (trace != null)
        {
            trace.Nonce = nonce;
            trace.Aad = aad;
            trace.Ciphertext = ct;
            trace.Tag = tag;
            trace.Ycm1Output = sm;
            trace.Plaintext = plain;
            trace.Ycm1Params = YaelCryptoMarch1.DeriveParams(keys.KSig, seq);
            YaelCryptoMarch1.Encrypt(plain, keys.KSig, seq, YaelCryptoMarch1.DefaultRounds, trace.Ycm1); // replay forward to record stages
            trace.Micros["aead"] = tAead;
            trace.Micros["ycm1"] = sw.Elapsed.TotalMicroseconds - tAead;
        }
        return plain;
    }

    private static void AeadEncrypt(SessionKeys keys, byte[] nonce, byte[] pt, byte[] ct, byte[] tag, byte[] aad)
    {
        if (keys.Suite == Suites.ChaCha20Poly1305)
        {
            using var c = new ChaCha20Poly1305(keys.KAead);
            c.Encrypt(nonce, pt, ct, tag, aad);
        }
        else
        {
            using var g = new AesGcm(keys.KAead, Protocol.TagLength);
            g.Encrypt(nonce, pt, ct, tag, aad);
        }
    }

    private static void AeadDecrypt(SessionKeys keys, byte[] nonce, byte[] ct, byte[] tag, byte[] pt, byte[] aad)
    {
        if (keys.Suite == Suites.ChaCha20Poly1305)
        {
            using var c = new ChaCha20Poly1305(keys.KAead);
            c.Decrypt(nonce, ct, tag, pt, aad);
        }
        else
        {
            using var g = new AesGcm(keys.KAead, Protocol.TagLength);
            g.Decrypt(nonce, ct, tag, pt, aad);
        }
    }
}
