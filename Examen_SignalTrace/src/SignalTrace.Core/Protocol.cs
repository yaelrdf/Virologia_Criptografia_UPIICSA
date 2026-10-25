using System.Security.Cryptography;

namespace SignalTrace.Core;

/// <summary>Protocol constants of SignalTrace v1 ("ST1"), PLAN §6.</summary>
public static class Protocol
{
    public const int Version = 1;
    public const string InfoAesUp = "st1/aes-up";
    public const string InfoChaChaUp = "st1/c20-up";
    public const string InfoSigMix = "st1/sigmix";
    public const string InfoIvUp = "st1/iv-up";
    public const string InfoAesDown = "st1/aes-down"; // reserved for server -> device commands
    public const string InfoIvDown = "st1/iv-down";   // reserved
    public static readonly byte[] TranscriptLabel = "ST1-HS"u8.ToArray();
    public static readonly byte[] AadLabel = "ST1"u8.ToArray();
    public const int NonceLength = 16;
    public const int TagLength = 16;
}

/// <summary>AEAD suites the handshake can negotiate. AES-256-GCM is the default and the one in the test vectors.</summary>
public static class Suites
{
    public const string AesGcm = "aes-256-gcm";
    public const string ChaCha20Poly1305 = "chacha20-poly1305";

    public static bool IsKnown(string? suite) => suite is AesGcm or ChaCha20Poly1305;

    public static bool IsSupported(string suite) => suite switch
    {
        AesGcm => System.Security.Cryptography.AesGcm.IsSupported,
        ChaCha20Poly1305 => System.Security.Cryptography.ChaCha20Poly1305.IsSupported,
        _ => false,
    };
}

/// <summary>Session keys derived from the ECDH shared secret with HKDF-SHA256 (salt = nonceC || nonceS).</summary>
public sealed record SessionKeys(byte[] KAead, byte[] KSig, byte[] IvSalt, string Suite)
{
    public static SessionKeys Derive(ReadOnlySpan<byte> sharedSecretX, ReadOnlySpan<byte> clientNonce, ReadOnlySpan<byte> serverNonce, string suite = Suites.AesGcm)
    {
        var salt = Bytes.Concat(clientNonce.ToArray(), serverNonce.ToArray());
        var ikm = sharedSecretX.ToArray();
        var aeadInfo = suite == Suites.ChaCha20Poly1305 ? Protocol.InfoChaChaUp : Protocol.InfoAesUp;
        return new SessionKeys(
            Hkdf(ikm, salt, aeadInfo, 32),
            Hkdf(ikm, salt, Protocol.InfoSigMix, 32),
            Hkdf(ikm, salt, Protocol.InfoIvUp, 4),
            suite);
    }

    public static byte[] Hkdf(byte[] ikm, byte[] salt, string info, int length)
        => HKDF.DeriveKey(HashAlgorithmName.SHA256, ikm, length, salt, Bytes.Utf8(info));
}

/// <summary>P-256 key encoding helpers: public keys as 65-byte X9.62 uncompressed points (04 || X || Y).</summary>
public static class EcKeys
{
    public static byte[] ExportPublic(ECAlgorithm key)
    {
        var q = key.ExportParameters(false).Q;
        return Bytes.Concat([0x04], PadTo32(q.X!), PadTo32(q.Y!));
    }

    public static ECParameters PublicParameters(ReadOnlySpan<byte> point)
    {
        if (point.Length != 65 || point[0] != 0x04) throw new CryptographicException("Public key must be a 65-byte uncompressed P-256 point.");
        return new ECParameters
        {
            Curve = ECCurve.NamedCurves.nistP256,
            Q = new ECPoint { X = point[1..33].ToArray(), Y = point[33..65].ToArray() },
        };
    }

    public static ECDiffieHellman ImportEcdhPublic(ReadOnlySpan<byte> point) => ECDiffieHellman.Create(PublicParameters(point));

    public static ECDsa ImportEcdsaPublic(ReadOnlySpan<byte> point) => ECDsa.Create(PublicParameters(point));

    /// <summary>Rebuilds a P-256 private key from its raw 32-byte scalar (used with the test vectors).</summary>
    public static ECParameters PrivateParameters(ReadOnlySpan<byte> d, ReadOnlySpan<byte> publicPoint)
    {
        var p = PublicParameters(publicPoint);
        p.D = d.ToArray();
        return p;
    }

    /// <summary>fingerprint = hex(SHA-256(public key 65 B)).</summary>
    public static string Fingerprint(ReadOnlySpan<byte> publicPoint) => Bytes.ToHex(SHA256.HashData(publicPoint));

    /// <summary>Raw X coordinate of the shared point (32 B), the same value Web Crypto deriveBits returns.</summary>
    public static byte[] SharedSecretX(ECDiffieHellman own, ReadOnlySpan<byte> peerPublic)
    {
        using var peer = ImportEcdhPublic(peerPublic);
        return own.DeriveRawSecretAgreement(peer.PublicKey);
    }

    private static byte[] PadTo32(byte[] v)
    {
        if (v.Length == 32) return v;
        var o = new byte[32];
        Buffer.BlockCopy(v, 0, o, 32 - v.Length, v.Length);
        return o;
    }
}

/// <summary>Handshake transcript signed by the server's long-term ECDSA key (IEEE P1363 r||s, 64 B).</summary>
public static class Handshake
{
    public static byte[] Transcript(byte[] clientPublic, byte[] serverPublic, byte[] clientNonce, byte[] serverNonce, string deviceId, string sessionId, string suite = Suites.AesGcm)
    {
        var t = Bytes.Concat(Protocol.TranscriptLabel, clientPublic, serverPublic, clientNonce, serverNonce, Bytes.Utf8(deviceId), Bytes.Utf8(sessionId));
        // The default suite keeps the original v1 transcript (test vectors); any other suite is bound into the signature.
        return suite == Suites.AesGcm ? t : Bytes.Concat(t, Bytes.Utf8("|" + suite));
    }

    public static byte[] Sign(ECDsa longTerm, byte[] transcript)
        => longTerm.SignData(transcript, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);

    public static bool Verify(ReadOnlySpan<byte> serverLongTermPublic, byte[] transcript, byte[] signature)
    {
        using var key = EcKeys.ImportEcdsaPublic(serverLongTermPublic);
        return key.VerifyData(transcript, signature, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
    }
}
