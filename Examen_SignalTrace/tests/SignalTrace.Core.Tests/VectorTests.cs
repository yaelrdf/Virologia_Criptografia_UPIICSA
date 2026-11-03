using System.Security.Cryptography;
using System.Text.Json;
using SignalTrace.Core;

namespace SignalTrace.Core.Tests;

/// <summary>
/// Vectores de prueba del Apéndice A (test-vectors.json): la referencia de interoperabilidad. Los clientes web y
/// Android pasan los mismos vectores, así se garantiza que los tres lados producen exactamente los mismos bytes.
/// </summary>
public class VectorTests
{
    /// <summary>Documento completo de vectores, copiado junto a las pruebas al compilar.</summary>
    private static readonly JsonElement V = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "test-vectors.json"))).RootElement;
    /// <summary>Sección del handshake.</summary>
    private static JsonElement Hs => V.GetProperty("handshake");
    /// <summary>Sección del paquete.</summary>
    private static JsonElement Pk => V.GetProperty("packet");
    /// <summary>Lee un campo hexadecimal como bytes.</summary>
    private static byte[] H(JsonElement e, string name) => Bytes.FromHex(e.GetProperty(name).GetString()!);
    /// <summary>Lee una salida esperada del HKDF por su etiqueta.</summary>
    private static string Out(string label) => Hs.GetProperty("hkdf").GetProperty("outputs").GetProperty(label).GetString()!;

    /// <summary>Reconstruye una llave ECDH a partir de su escalar privado y su punto público.</summary>
    private static ECDiffieHellman Ecdh(string priv, string pub)
        => ECDiffieHellman.Create(EcKeys.PrivateParameters(H(Hs, priv), H(Hs, pub)));

    /// <summary>El secreto compartido ECDH es el mismo calculado desde el cliente y desde el servidor, y coincide con el vector.</summary>
    [Fact]
    public void Ecdh_SharedSecret_MatchesFromBothSides()
    {
        using var client = Ecdh("client_ephemeral_private", "client_ephemeral_public");
        using var server = Ecdh("server_ephemeral_private", "server_ephemeral_public");
        var want = Hs.GetProperty("shared_secret_x").GetString();
        Assert.Equal(want, Bytes.ToHex(EcKeys.SharedSecretX(client, H(Hs, "server_ephemeral_public"))));
        Assert.Equal(want, Bytes.ToHex(EcKeys.SharedSecretX(server, H(Hs, "client_ephemeral_public"))));
        Assert.Equal(Hs.GetProperty("client_ephemeral_public").GetString(), Bytes.ToHex(EcKeys.ExportPublic(client)));
    }

    /// <summary>Las tres llaves derivadas con HKDF coinciden con el vector.</summary>
    [Fact]
    public void Hkdf_Outputs_Match()
    {
        var keys = SessionKeys.Derive(H(Hs, "shared_secret_x"), H(Hs, "client_nonce"), H(Hs, "server_nonce"));
        Assert.Equal(Out("st1/aes-up(32)"), Bytes.ToHex(keys.KAead));
        Assert.Equal(Out("st1/ycm1(32)"), Bytes.ToHex(keys.KSig));
        Assert.Equal(Out("st1/iv-up(4)"), Bytes.ToHex(keys.IvSalt));
    }

    /// <summary>El transcript coincide byte a byte, su firma verifica, y al cambiar un bit deja de verificar.</summary>
    [Fact]
    public void Transcript_Matches_And_Signature_Verifies()
    {
        var t = Handshake.Transcript(H(Hs, "client_ephemeral_public"), H(Hs, "server_ephemeral_public"), H(Hs, "client_nonce"), H(Hs, "server_nonce"),
            Hs.GetProperty("device_id").GetString()!, Hs.GetProperty("session_id").GetString()!);
        Assert.Equal(Hs.GetProperty("transcript").GetString(), Bytes.ToHex(t));
        Assert.True(Handshake.Verify(H(Hs, "server_longterm_public"), t, H(Hs, "server_signature_p1363_VERIFY_ONLY")));
        t[^1] ^= 1;
        Assert.False(Handshake.Verify(H(Hs, "server_longterm_public"), t, H(Hs, "server_signature_p1363_VERIFY_ONLY")));
    }

    /// <summary>
    /// Una firma nueva con la llave de largo plazo mide 64 bytes y verifica. (ECDSA es aleatorio, por eso no se
    /// compara con la firma del vector).
    /// </summary>
    [Fact]
    public void Sign_With_LongTermKey_Verifies()
    {
        using var lt = ECDsa.Create(EcKeys.PrivateParameters(H(Hs, "server_longterm_private"), H(Hs, "server_longterm_public")));
        var sig = Handshake.Sign(lt, H(Hs, "transcript"));
        Assert.Equal(64, sig.Length);
        Assert.True(Handshake.Verify(H(Hs, "server_longterm_public"), H(Hs, "transcript"), sig));
    }

    /// <summary>El ejemplo de una ronda de YCM1 calculado a mano en la documentación («Hi» → [236, 198]).</summary>
    [Fact]
    public void Ycm1_RoundExample_ByHand()
    {
        var ex = V.GetProperty("ycm1_round_example");
        var prm = new Ycm1Params(ex.GetProperty("seed").GetInt32(), [new Ycm1Round(ex.GetProperty("k").GetInt32(), ex.GetProperty("m").GetInt32(), 0)]);
        var trace = new Ycm1TraceRecorder();
        var c = YaelCryptoMarch1.EncryptWithParams([72, 105], prm, trace, pad: false);
        Assert.Equal([110, 42], trace.Stages.Single(s => s.Name == "r1.stream").State);
        Assert.Equal([236, 198], c);
        Assert.Equal([72, 105], YaelCryptoMarch1.DecryptWithParams(c, prm, pad: false));
    }

    /// <summary>Los parámetros de YCM1, cada etapa intermedia y la salida del paquete de ejemplo coinciden con el vector.</summary>
    [Fact]
    public void Ycm1_Packet_Params_Stages_Output()
    {
        var kSig = Bytes.FromHex(Out("st1/ycm1(32)"));
        var seq = Pk.GetProperty("seq").GetUInt64();
        var prm = YaelCryptoMarch1.DeriveParams(kSig, seq);
        var want = Pk.GetProperty("ycm1_params");
        Assert.Equal(want.GetProperty("seed").GetInt32(), prm.Seed);
        var rounds = want.GetProperty("rounds").EnumerateArray().ToArray();
        for (var i = 0; i < 3; i++)
            Assert.Equal(new Ycm1Round(rounds[i].GetProperty("k").GetInt32(), rounds[i].GetProperty("m").GetInt32(), rounds[i].GetProperty("rot").GetInt32()), prm.Rounds[i]);

        var trace = new Ycm1TraceRecorder();
        var sm = YaelCryptoMarch1.Encrypt(H(Pk, "plaintext_hex"), kSig, seq, 3, trace);
        foreach (var stage in Pk.GetProperty("ycm1_stages").EnumerateObject())
            Assert.Equal(stage.Value.GetString(), Bytes.ToHex(trace.Stages.Single(s => s.Name == stage.Name).State));
        Assert.Equal(Pk.GetProperty("ycm1_output").GetString(), Bytes.ToHex(sm));
        Assert.Equal(Pk.GetProperty("plaintext_hex").GetString(), Bytes.ToHex(YaelCryptoMarch1.Decrypt(sm, kSig, seq)));
    }

    /// <summary>El nonce, el AAD y el paquete sellado con AES-256-GCM coinciden con el vector, y se abre de vuelta.</summary>
    [Fact]
    public void Packet_Seal_And_Open_Match_Vector()
    {
        var keys = SessionKeys.Derive(H(Hs, "shared_secret_x"), H(Hs, "client_nonce"), H(Hs, "server_nonce"));
        var did = Hs.GetProperty("device_id").GetString()!;
        var seq = Pk.GetProperty("seq").GetUInt64();
        var ts = Pk.GetProperty("ts_ms").GetInt64();
        var aes = Pk.GetProperty("aes_gcm");
        Assert.Equal(aes.GetProperty("nonce(12)=iv_salt||u64be(seq)").GetString(), Bytes.ToHex(PacketCrypto.Nonce(keys.IvSalt, seq)));
        Assert.Equal(aes.GetProperty("aad = 'ST1'||utf8(device_id)||u64be(seq)||u64be(ts)").GetString(), Bytes.ToHex(PacketCrypto.Aad(did, seq, ts)));

        var plain = Bytes.Utf8(Pk.GetProperty("plaintext_utf8").GetString()!);
        var ct = PacketCrypto.Seal(keys, did, seq, ts, plain);
        Assert.Equal(aes.GetProperty("ciphertext_plus_tag").GetString(), Bytes.ToHex(ct));
        Assert.Equal(aes.GetProperty("ciphertext_plus_tag_b64").GetString(), Convert.ToBase64String(ct));
        Assert.Equal(plain, PacketCrypto.Open(keys, did, seq, ts, ct));
    }

    /// <summary>PBKDF2-HMAC-SHA256 da la llave derivada del vector.</summary>
    [Fact]
    public void Pbkdf2_Matches()
    {
        var pb = V.GetProperty("pbkdf2");
        var dk = Rfc2898DeriveBytes.Pbkdf2(pb.GetProperty("password").GetString()!, H(pb, "salt"), pb.GetProperty("iterations").GetInt32(), HashAlgorithmName.SHA256, 32);
        Assert.Equal(pb.GetProperty("derived").GetString(), Bytes.ToHex(dk));
    }
}
