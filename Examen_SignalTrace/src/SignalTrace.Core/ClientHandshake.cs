using System.Security.Cryptography;

namespace SignalTrace.Core;

/// <summary>
/// Lado cliente del handshake ECDH. Lo usan el simulador y las pruebas; los clientes web y Android
/// implementan los mismos pasos en su propio lenguaje.
/// Flujo: el cliente genera un par de llaves efímeras P-256 y un nonce aleatorio, envía la solicitud,
/// verifica la firma del servidor con la llave fijada (pin) y, si es válida, deriva las llaves de sesión.
/// </summary>
public sealed class ClientHandshake : IDisposable
{
    /// <summary>Par de llaves efímeras de esta sesión; se descarta al terminar (secreto hacia adelante).</summary>
    private readonly ECDiffieHellman _ephemeral;

    /// <summary>Llave pública efímera del cliente como punto P-256 sin comprimir (65 bytes).</summary>
    public byte[] PublicKey { get; }
    /// <summary>Nonce aleatorio del cliente (16 bytes); forma parte de la sal del HKDF y del transcript.</summary>
    public byte[] Nonce { get; }
    /// <summary>Identificador del dispositivo que abre la sesión.</summary>
    public string DeviceId { get; }
    /// <summary>Suite AEAD que el cliente propone (AES-256-GCM por defecto).</summary>
    public string Suite { get; }

    /// <summary>Prepara un handshake nuevo: crea la llave efímera y el nonce.</summary>
    /// <param name="deviceId">Identificador del dispositivo registrado.</param>
    /// <param name="suite">Suite AEAD propuesta; ver <see cref="Suites"/>.</param>
    public ClientHandshake(string deviceId, string suite = Suites.AesGcm)
    {
        _ephemeral = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        PublicKey = EcKeys.ExportPublic(_ephemeral);
        Nonce = RandomNumberGenerator.GetBytes(Protocol.NonceLength);
        DeviceId = deviceId;
        Suite = suite;
    }

    /// <summary>Construye el cuerpo de <c>POST /api/handshake</c> con la llave pública y el nonce en Base64.</summary>
    public HandshakeRequest Request() => new(DeviceId, Convert.ToBase64String(PublicKey), Convert.ToBase64String(Nonce), Suite);

    /// <summary>
    /// Completa el handshake con la respuesta del servidor. Reconstruye el transcript, verifica la firma
    /// con la llave de largo plazo fijada y lanza una excepción si no coincide (posible intermediario, MITM).
    /// Si la firma es válida, calcula el secreto compartido ECDH y deriva las llaves de sesión.
    /// </summary>
    /// <param name="response">Respuesta del servidor (llave efímera, nonce, id de sesión, suite y firma).</param>
    /// <param name="pinnedServerPublic">Llave pública de largo plazo del servidor guardada en el primer contacto.</param>
    /// <returns>Las llaves de sesión derivadas.</returns>
    /// <exception cref="CryptographicException">Si la firma no verifica con la llave fijada.</exception>
    public SessionKeys Complete(HandshakeResponse response, byte[] pinnedServerPublic)
    {
        var serverPub = Convert.FromBase64String(response.ServerPublicKey);
        var serverNonce = Convert.FromBase64String(response.ServerNonce);
        // El transcript cubre las dos llaves, los dos nonces, el dispositivo, la sesión y la suite.
        var transcript = Handshake.Transcript(PublicKey, serverPub, Nonce, serverNonce, DeviceId, response.SessionId, response.Suite);
        if (!Handshake.Verify(pinnedServerPublic, transcript, Convert.FromBase64String(response.Signature)))
            throw new CryptographicException("Server signature does not verify with the pinned key (possible MITM).");
        // Coordenada X del punto compartido; es la entrada del HKDF.
        var z = EcKeys.SharedSecretX(_ephemeral, serverPub);
        return SessionKeys.Derive(z, Nonce, serverNonce, response.Suite);
    }

    /// <summary>Libera la llave efímera.</summary>
    public void Dispose() => _ephemeral.Dispose();
}
