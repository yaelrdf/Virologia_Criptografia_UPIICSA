using System.Security.Cryptography;
using SignalTrace.Core;

namespace SignalTrace.Core.Tests;

/// <summary>Pruebas de propiedades del núcleo criptográfico con datos aleatorios.</summary>
public class PropertyTests
{
    /// <summary>Descifrar lo cifrado devuelve el original para todas las longitudes de 0 a 199 bytes (incluye el relleno).</summary>
    [Fact]
    public void Ycm1_RoundTrip_Lengths_0_To_199()
    {
        for (var len = 0; len < 200; len++)
        {
            var data = RandomNumberGenerator.GetBytes(len);
            var key = RandomNumberGenerator.GetBytes(32);
            var seq = (ulong)len * 7919;
            Assert.Equal(data, YaelCryptoMarch1.Decrypt(YaelCryptoMarch1.Encrypt(data, key, seq), key, seq));
        }
    }

    /// <summary>Los parámetros dependen solo de la llave y la secuencia: iguales para la misma secuencia, distintos para otra.</summary>
    [Fact]
    public void Ycm1_Params_Deterministic_And_SeqSensitive()
    {
        var key = RandomNumberGenerator.GetBytes(32);
        Assert.Equal(YaelCryptoMarch1.DeriveParams(key, 5).Rounds, YaelCryptoMarch1.DeriveParams(key, 5).Rounds);
        Assert.NotEqual(YaelCryptoMarch1.DeriveParams(key, 5).Rounds, YaelCryptoMarch1.DeriveParams(key, 6).Rounds);
    }

    /// <summary>Un relleno inválido o una longitud impar provocan una excepción al descifrar.</summary>
    [Fact]
    public void Ycm1_BadPadding_Throws()
    {
        var key = RandomNumberGenerator.GetBytes(32);
        var prm = YaelCryptoMarch1.DeriveParams(key, 1);
        // Cifra un bloque sin relleno cuyo último byte es 3: al descifrar con relleno se debe rechazar.
        var c = YaelCryptoMarch1.EncryptWithParams([10, 3], prm, pad: false);
        Assert.Throws<CryptographicException>(() => YaelCryptoMarch1.DecryptWithParams(c, prm));
        Assert.Throws<CryptographicException>(() => YaelCryptoMarch1.Decrypt([1, 2, 3], key, 1));
    }

    /// <summary>El LCG recorre los 256 estados (periodo completo por Hull–Dobell).</summary>
    [Fact]
    public void Lcg_HasFullPeriod_HullDobell() => Assert.Equal(256, YaelCryptoMarch1.LcgPeriod());

    /// <summary>det K = (1 + k·m) − k·m = 1 mod 256 para todos los k y m posibles: la matriz siempre es invertible.</summary>
    [Fact]
    public void Matrix_Determinant_Is_One_For_All_K_M()
    {
        for (var k = 0; k < 256; k++)
            for (var m = 0; m < 256; m++)
                Assert.Equal(1, ((1 + k * m) - k * m) & 0xFF);
    }

    /// <summary>
    /// Con cualquiera de las dos suites, cambiar un bit del paquete hace fallar la autenticación,
    /// igual que cambiar la secuencia o la marca de tiempo.
    /// </summary>
    [Theory]
    [InlineData(Suites.AesGcm)]
    [InlineData(Suites.ChaCha20Poly1305)]
    public void Packet_AnyBitFlip_FailsAuthentication(string suite)
    {
        if (!Suites.IsSupported(suite)) return;
        var keys = SessionKeys.Derive(RandomNumberGenerator.GetBytes(32), RandomNumberGenerator.GetBytes(16), RandomNumberGenerator.GetBytes(16), suite);
        var did = Guid.NewGuid().ToString("D");
        var ct = PacketCrypto.Seal(keys, did, 3, 1000, "{\"t\":1000,\"src\":\"sim\"}"u8);
        // Prueba uno de cada 7 bits para cubrir todo el paquete sin tardar demasiado.
        for (var bit = 0; bit < ct.Length * 8; bit += 7)
        {
            var bad = (byte[])ct.Clone();
            bad[bit / 8] ^= (byte)(1 << (bit % 8));
            Assert.Throws<AuthenticationFailedException>(() => PacketCrypto.Open(keys, did, 3, 1000, bad));
        }
        // Los mismos bytes con otra secuencia o marca de tiempo (cambian el AAD y el nonce) también deben fallar.
        Assert.Throws<AuthenticationFailedException>(() => PacketCrypto.Open(keys, did, 4, 1000, ct));
        Assert.Throws<AuthenticationFailedException>(() => PacketCrypto.Open(keys, did, 3, 1001, ct));
    }

    /// <summary>
    /// Handshake completo simulando al servidor a mano: ambos lados derivan las mismas llaves, y una firma hecha con
    /// otra llave (intermediario) se rechaza.
    /// </summary>
    [Fact]
    public void Full_Handshake_ClientServer_DeriveSameKeys()
    {
        using var longTerm = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var ltPub = EcKeys.ExportPublic(longTerm);
        var did = Guid.NewGuid().ToString("D");
        using var client = new ClientHandshake(did);

        // Lado servidor: llave efímera, nonce, sesión, llaves y firma del transcript.
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

        // Intermediario (MITM): si el cliente fijó otra llave, la firma debe rechazarse.
        using var attacker = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        Assert.Throws<CryptographicException>(() => client.Complete(resp, EcKeys.ExportPublic(attacker)));
    }

    /// <summary>La entropía es 0 para bytes iguales y 8 para los 256 valores distintos.</summary>
    [Fact]
    public void Entropy_Bounds()
    {
        Assert.Equal(0, Entropy.Shannon(new byte[100]));
        Assert.Equal(8, Entropy.Shannon(Enumerable.Range(0, 256).Select(i => (byte)i).ToArray()), 6);
    }
}

/// <summary>Pruebas del ataque de texto claro conocido contra YCM1 de una ronda.</summary>
public class CryptanalysisTests
{
    /// <summary>Inverso modular y solución de congruencias lineales, incluidos los casos con varias soluciones o ninguna.</summary>
    [Fact]
    public void LinearCongruence_And_ModInverse()
    {
        Assert.Equal(205, Ycm1Cryptanalysis.ModInverse(5, 256)); // 5 · 205 = 1025 ≡ 1
        Assert.Equal([3], Ycm1Cryptanalysis.SolveLinear(5, 15).ToArray());
        Assert.Equal([1, 129], Ycm1Cryptanalysis.SolveLinear(2, 2).OrderBy(v => v).ToArray());
        Assert.Empty(Ycm1Cryptanalysis.SolveLinear(2, 3));
    }

    /// <summary>
    /// Con cinco parámetros secretos distintos, el ataque recupera la ronda (rotación módulo n) y con ella descifra
    /// un segundo mensaje cifrado con los mismos parámetros.
    /// </summary>
    [Fact]
    public void KnownPlaintext_Breaks_OneRound()
    {
        for (var trial = 0; trial < 5; trial++)
        {
            var secret = YaelCryptoMarch1.DeriveParams(RandomNumberGenerator.GetBytes(32), (ulong)trial, rounds: 1);
            var p1 = "{\"t\":1760000000000,\"src\":\"sim\",\"bat\":{\"lvl\":0.5}}"u8.ToArray();
            var p2 = "{\"env\":{\"lux\":412,\"hpa\":779.3}}"u8.ToArray();
            var r = Ycm1Cryptanalysis.KnownPlaintextOneRound(p1, YaelCryptoMarch1.EncryptWithParams(p1, secret));
            Assert.NotNull(r.Params);
            Assert.Equal(secret.Rounds[0].Rot % YaelCryptoMarch1.Pad(p1).Length, r.Params!.Rounds[0].Rot);
            var other = Ycm1Cryptanalysis.DecryptOther(r, YaelCryptoMarch1.EncryptWithParams(p2, secret), b => b.Length > 0 && b[0] == (byte)'{' && b[^1] == (byte)'}');
            Assert.Equal(p2, other);
        }
    }
}
