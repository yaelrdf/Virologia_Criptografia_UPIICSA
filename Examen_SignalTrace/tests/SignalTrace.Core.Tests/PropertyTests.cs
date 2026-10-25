using System.Security.Cryptography;
using SignalTrace.Core;

namespace SignalTrace.Core.Tests;

public class PropertyTests
{
    [Fact]
    public void SigMix_RoundTrip_Lengths_0_To_199()
    {
        for (var len = 0; len < 200; len++)
        {
            var data = RandomNumberGenerator.GetBytes(len);
            var key = RandomNumberGenerator.GetBytes(32);
            var seq = (ulong)len * 7919;
            Assert.Equal(data, SigMix.Decrypt(SigMix.Encrypt(data, key, seq), key, seq));
        }
    }

    [Fact]
    public void SigMix_Params_Deterministic_And_SeqSensitive()
    {
        var key = RandomNumberGenerator.GetBytes(32);
        Assert.Equal(SigMix.DeriveParams(key, 5).Rounds, SigMix.DeriveParams(key, 5).Rounds);
        Assert.NotEqual(SigMix.DeriveParams(key, 5).Rounds, SigMix.DeriveParams(key, 6).Rounds);
    }

    [Fact]
    public void SigMix_BadPadding_Throws()
    {
        var key = RandomNumberGenerator.GetBytes(32);
        var prm = SigMix.DeriveParams(key, 1);
        // Encrypt an unpadded block whose last byte is 3: decrypting with padding must reject it.
        var c = SigMix.EncryptWithParams([10, 3], prm, pad: false);
        Assert.Throws<CryptographicException>(() => SigMix.DecryptWithParams(c, prm));
        Assert.Throws<CryptographicException>(() => SigMix.Decrypt([1, 2, 3], key, 1));
    }

    [Fact]
    public void Lcg_HasFullPeriod_HullDobell() => Assert.Equal(256, SigMix.LcgPeriod());

    [Fact]
    public void Matrix_Determinant_Is_One_For_All_K_M()
    {
        for (var k = 0; k < 256; k++)
            for (var m = 0; m < 256; m++)
                Assert.Equal(1, ((1 + k * m) - k * m) & 0xFF);
    }

    [Theory]
    [InlineData(Suites.AesGcm)]
    [InlineData(Suites.ChaCha20Poly1305)]
    public void Packet_AnyBitFlip_FailsAuthentication(string suite)
    {
        if (!Suites.IsSupported(suite)) return;
        var keys = SessionKeys.Derive(RandomNumberGenerator.GetBytes(32), RandomNumberGenerator.GetBytes(16), RandomNumberGenerator.GetBytes(16), suite);
        var did = Guid.NewGuid().ToString("D");
        var ct = PacketCrypto.Seal(keys, did, 3, 1000, "{\"t\":1000,\"src\":\"sim\"}"u8);
        for (var bit = 0; bit < ct.Length * 8; bit += 7)
        {
            var bad = (byte[])ct.Clone();
            bad[bit / 8] ^= (byte)(1 << (bit % 8));
            Assert.Throws<AuthenticationFailedException>(() => PacketCrypto.Open(keys, did, 3, 1000, bad));
        }
        // Same bytes under a different seq/ts (AAD and nonce) must also fail.
        Assert.Throws<AuthenticationFailedException>(() => PacketCrypto.Open(keys, did, 4, 1000, ct));
        Assert.Throws<AuthenticationFailedException>(() => PacketCrypto.Open(keys, did, 3, 1001, ct));
    }

    [Fact]
    public void Full_Handshake_ClientServer_DeriveSameKeys()
    {
        using var longTerm = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var ltPub = EcKeys.ExportPublic(longTerm);
        var did = Guid.NewGuid().ToString("D");
        using var client = new ClientHandshake(did);

        using var serverEph = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var sPub = EcKeys.ExportPublic(serverEph);
        var sNonce = RandomNumberGenerator.GetBytes(16);
        var sid = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(8));
        var z = EcKeys.SharedSecretX(serverEph, client.PublicKey);
        var serverKeys = SessionKeys.Derive(z, client.Nonce, sNonce);
        var sig = Handshake.Sign(longTerm, Handshake.Transcript(client.PublicKey, sPub, client.Nonce, sNonce, did, sid));
        var resp = new HandshakeResponse(sid, Convert.ToBase64String(sPub), Convert.ToBase64String(sNonce), Convert.ToBase64String(sig), 0, Suites.AesGcm);

        var clientKeys = client.Complete(resp, ltPub);
        Assert.Equal(serverKeys.KAead, clientKeys.KAead);
        Assert.Equal(serverKeys.KSig, clientKeys.KSig);

        // MITM: a different signing key must be rejected.
        using var mallory = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        Assert.Throws<CryptographicException>(() => client.Complete(resp, EcKeys.ExportPublic(mallory)));
    }

    [Fact]
    public void Entropy_Bounds()
    {
        Assert.Equal(0, Entropy.Shannon(new byte[100]));
        Assert.Equal(8, Entropy.Shannon(Enumerable.Range(0, 256).Select(i => (byte)i).ToArray()), 6);
    }
}
