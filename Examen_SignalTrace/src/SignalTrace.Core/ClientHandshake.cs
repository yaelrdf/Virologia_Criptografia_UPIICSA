using System.Security.Cryptography;

namespace SignalTrace.Core;

/// <summary>Client side of the handshake (used by the simulator and the tests; the web and Android clients mirror it).</summary>
public sealed class ClientHandshake : IDisposable
{
    private readonly ECDiffieHellman _ephemeral;

    public byte[] PublicKey { get; }
    public byte[] Nonce { get; }
    public string DeviceId { get; }
    public string Suite { get; }

    public ClientHandshake(string deviceId, string suite = Suites.AesGcm)
    {
        _ephemeral = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        PublicKey = EcKeys.ExportPublic(_ephemeral);
        Nonce = RandomNumberGenerator.GetBytes(Protocol.NonceLength);
        DeviceId = deviceId;
        Suite = suite;
    }

    public HandshakeRequest Request() => new(DeviceId, Convert.ToBase64String(PublicKey), Convert.ToBase64String(Nonce), Suite);

    /// <summary>Verifies the server signature with the pinned long-term key; throws if it fails (MITM), then derives the keys.</summary>
    public SessionKeys Complete(HandshakeResponse response, byte[] pinnedServerPublic)
    {
        var serverPub = Convert.FromBase64String(response.ServerPublicKey);
        var serverNonce = Convert.FromBase64String(response.ServerNonce);
        var transcript = Handshake.Transcript(PublicKey, serverPub, Nonce, serverNonce, DeviceId, response.SessionId, response.Suite);
        if (!Handshake.Verify(pinnedServerPublic, transcript, Convert.FromBase64String(response.Signature)))
            throw new CryptographicException("Server signature does not verify with the pinned key (possible MITM).");
        var z = EcKeys.SharedSecretX(_ephemeral, serverPub);
        return SessionKeys.Derive(z, Nonce, serverNonce, response.Suite);
    }

    public void Dispose() => _ephemeral.Dispose();
}
