using System.Security.Cryptography;
using System.Text.Json;
using SignalTrace.Core;

namespace SignalTrace.Core.Tests;

/// <summary>Appendix A test vectors: the interoperability source of truth.</summary>
public class VectorTests
{
    private static readonly JsonElement V = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "test-vectors.json"))).RootElement;
    private static JsonElement Hs => V.GetProperty("handshake");
    private static JsonElement Pk => V.GetProperty("packet");
    private static byte[] H(JsonElement e, string name) => Bytes.FromHex(e.GetProperty(name).GetString()!);
    private static string Out(string label) => Hs.GetProperty("hkdf").GetProperty("outputs").GetProperty(label).GetString()!;

    private static ECDiffieHellman Ecdh(string priv, string pub)
        => ECDiffieHellman.Create(EcKeys.PrivateParameters(H(Hs, priv), H(Hs, pub)));

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

    [Fact]
    public void Hkdf_Outputs_Match()
    {
        var keys = SessionKeys.Derive(H(Hs, "shared_secret_x"), H(Hs, "client_nonce"), H(Hs, "server_nonce"));
        Assert.Equal(Out("st1/aes-up(32)"), Bytes.ToHex(keys.KAead));
        Assert.Equal(Out("st1/sigmix(32)"), Bytes.ToHex(keys.KSig));
        Assert.Equal(Out("st1/iv-up(4)"), Bytes.ToHex(keys.IvSalt));
    }

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

    [Fact]
    public void Sign_With_LongTermKey_Verifies()
    {
        using var lt = ECDsa.Create(EcKeys.PrivateParameters(H(Hs, "server_longterm_private"), H(Hs, "server_longterm_public")));
        var sig = Handshake.Sign(lt, H(Hs, "transcript"));
        Assert.Equal(64, sig.Length);
        Assert.True(Handshake.Verify(H(Hs, "server_longterm_public"), H(Hs, "transcript"), sig));
    }

    [Fact]
    public void SigMix_RoundExample_ByHand()
    {
        var ex = V.GetProperty("sigmix_round_example");
        var prm = new SigMixParams(ex.GetProperty("seed").GetInt32(), [new SigMixRound(ex.GetProperty("k").GetInt32(), ex.GetProperty("m").GetInt32(), 0)]);
        var trace = new SigMixTraceRecorder();
        var c = SigMix.EncryptWithParams([72, 105], prm, trace, pad: false);
        Assert.Equal([110, 42], trace.Stages.Single(s => s.Name == "r1.stream").State);
        Assert.Equal([236, 198], c);
        Assert.Equal([72, 105], SigMix.DecryptWithParams(c, prm, pad: false));
    }

    [Fact]
    public void SigMix_Packet_Params_Stages_Output()
    {
        var kSig = Bytes.FromHex(Out("st1/sigmix(32)"));
        var seq = Pk.GetProperty("seq").GetUInt64();
        var prm = SigMix.DeriveParams(kSig, seq);
        var want = Pk.GetProperty("sigmix_params");
        Assert.Equal(want.GetProperty("seed").GetInt32(), prm.Seed);
        var rounds = want.GetProperty("rounds").EnumerateArray().ToArray();
        for (var i = 0; i < 3; i++)
            Assert.Equal(new SigMixRound(rounds[i].GetProperty("k").GetInt32(), rounds[i].GetProperty("m").GetInt32(), rounds[i].GetProperty("rot").GetInt32()), prm.Rounds[i]);

        var trace = new SigMixTraceRecorder();
        var sm = SigMix.Encrypt(H(Pk, "plaintext_hex"), kSig, seq, 3, trace);
        foreach (var stage in Pk.GetProperty("sigmix_stages").EnumerateObject())
            Assert.Equal(stage.Value.GetString(), Bytes.ToHex(trace.Stages.Single(s => s.Name == stage.Name).State));
        Assert.Equal(Pk.GetProperty("sigmix_output").GetString(), Bytes.ToHex(sm));
        Assert.Equal(Pk.GetProperty("plaintext_hex").GetString(), Bytes.ToHex(SigMix.Decrypt(sm, kSig, seq)));
    }

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

    [Fact]
    public void Pbkdf2_Matches()
    {
        var pb = V.GetProperty("pbkdf2");
        var dk = Rfc2898DeriveBytes.Pbkdf2(pb.GetProperty("password").GetString()!, H(pb, "salt"), pb.GetProperty("iterations").GetInt32(), HashAlgorithmName.SHA256, 32);
        Assert.Equal(pb.GetProperty("derived").GetString(), Bytes.ToHex(dk));
    }
}
