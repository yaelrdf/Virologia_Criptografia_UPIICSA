using System.Security.Cryptography;

namespace SignalTrace.Core;

/// <summary>
/// Constantes del protocolo SignalTrace versión 1 («ST1», PLAN §6). Las etiquetas <c>Info*</c> son el
/// parámetro «info» del HKDF: separan el uso de cada llave derivada del mismo secreto compartido.
/// </summary>
public static class Protocol
{
    /// <summary>Versión del sobre de telemetría que aceptan el servidor y los clientes.</summary>
    public const int Version = 1;
    /// <summary>Etiqueta HKDF de la llave AES-256-GCM de subida (dispositivo → servidor).</summary>
    public const string InfoAesUp = "st1/aes-up";
    /// <summary>Etiqueta HKDF de la llave ChaCha20-Poly1305 de subida.</summary>
    public const string InfoChaChaUp = "st1/c20-up";
    /// <summary>Etiqueta HKDF de la llave del cifrado propio Yael Crypto March1 (k_sig).</summary>
    public const string InfoYcm1 = "st1/ycm1";
    /// <summary>Etiqueta HKDF de la sal de 4 bytes del nonce AEAD de subida.</summary>
    public const string InfoIvUp = "st1/iv-up";
    /// <summary>Reservada para comandos del servidor al dispositivo (no se usa todavía).</summary>
    public const string InfoAesDown = "st1/aes-down"; // reservada para comandos servidor → dispositivo
    /// <summary>Reservada, sal del nonce de bajada.</summary>
    public const string InfoIvDown = "st1/iv-down";   // reservada
    /// <summary>Prefijo del transcript que firma el servidor en el handshake.</summary>
    public static readonly byte[] TranscriptLabel = "ST1-HS"u8.ToArray();
    /// <summary>Prefijo de los datos asociados (AAD) de cada paquete.</summary>
    public static readonly byte[] AadLabel = "ST1"u8.ToArray();
    /// <summary>Longitud en bytes de los nonces del handshake.</summary>
    public const int NonceLength = 16;
    /// <summary>Longitud en bytes de la etiqueta de autenticación AEAD.</summary>
    public const int TagLength = 16;
}

/// <summary>
/// Suites AEAD que puede negociar el handshake. AES-256-GCM es la predeterminada y la que usan los
/// vectores de prueba; ChaCha20-Poly1305 es la alternativa para equipos sin aceleración AES.
/// </summary>
public static class Suites
{
    /// <summary>Nombre de la suite AES-256-GCM.</summary>
    public const string AesGcm = "aes-256-gcm";
    /// <summary>Nombre de la suite ChaCha20-Poly1305.</summary>
    public const string ChaCha20Poly1305 = "chacha20-poly1305";

    /// <summary>Indica si el nombre corresponde a una suite que el protocolo define.</summary>
    /// <param name="suite">Nombre recibido del cliente; puede ser nulo.</param>
    public static bool IsKnown(string? suite) => suite is AesGcm or ChaCha20Poly1305;

    /// <summary>Indica si la plataforma donde corre el servidor implementa la suite.</summary>
    /// <param name="suite">Nombre de la suite.</param>
    public static bool IsSupported(string suite) => suite switch
    {
        AesGcm => System.Security.Cryptography.AesGcm.IsSupported,
        ChaCha20Poly1305 => System.Security.Cryptography.ChaCha20Poly1305.IsSupported,
        _ => false,
    };
}

/// <summary>
/// Llaves de una sesión, derivadas del secreto compartido ECDH con HKDF-SHA256 (sal = nonceC ‖ nonceS).
/// </summary>
/// <param name="KAead">Llave de 32 bytes de la suite AEAD.</param>
/// <param name="KSig">Llave de 32 bytes de Yael Crypto March1.</param>
/// <param name="IvSalt">Sal de 4 bytes que forma la primera parte del nonce AEAD.</param>
/// <param name="Suite">Suite AEAD negociada.</param>
public sealed record SessionKeys(byte[] KAead, byte[] KSig, byte[] IvSalt, string Suite)
{
    /// <summary>
    /// Deriva las tres llaves de sesión del mismo material de entrada, cambiando solo la etiqueta «info».
    /// Así una llave comprometida no revela las otras.
    /// </summary>
    /// <param name="sharedSecretX">Coordenada X del punto compartido ECDH (32 bytes).</param>
    /// <param name="clientNonce">Nonce del cliente.</param>
    /// <param name="serverNonce">Nonce del servidor.</param>
    /// <param name="suite">Suite AEAD negociada; decide la etiqueta de la llave AEAD.</param>
    public static SessionKeys Derive(ReadOnlySpan<byte> sharedSecretX, ReadOnlySpan<byte> clientNonce, ReadOnlySpan<byte> serverNonce, string suite = Suites.AesGcm)
    {
        var salt = Bytes.Concat(clientNonce.ToArray(), serverNonce.ToArray());
        var ikm = sharedSecretX.ToArray();
        var aeadInfo = suite == Suites.ChaCha20Poly1305 ? Protocol.InfoChaChaUp : Protocol.InfoAesUp;
        return new SessionKeys(
            Hkdf(ikm, salt, aeadInfo, 32),
            Hkdf(ikm, salt, Protocol.InfoYcm1, 32),
            Hkdf(ikm, salt, Protocol.InfoIvUp, 4),
            suite);
    }

    /// <summary>HKDF-SHA256 completo (extracción y expansión) con la etiqueta «info» en UTF-8.</summary>
    /// <param name="ikm">Material de entrada.</param>
    /// <param name="salt">Sal.</param>
    /// <param name="info">Etiqueta de contexto.</param>
    /// <param name="length">Bytes de salida.</param>
    public static byte[] Hkdf(byte[] ikm, byte[] salt, string info, int length)
        => HKDF.DeriveKey(HashAlgorithmName.SHA256, ikm, length, salt, Bytes.Utf8(info));
}

/// <summary>
/// Codificación de llaves P-256: las llaves públicas viajan como puntos X9.62 sin comprimir de 65 bytes
/// (0x04 ‖ X ‖ Y), el mismo formato «raw» que exportan Web Crypto y Android.
/// </summary>
public static class EcKeys
{
    /// <summary>Exporta la llave pública de una llave EC como punto sin comprimir de 65 bytes.</summary>
    /// <param name="key">Llave ECDH o ECDSA.</param>
    public static byte[] ExportPublic(ECAlgorithm key)
    {
        var q = key.ExportParameters(false).Q;
        return Bytes.Concat([0x04], PadTo32(q.X!), PadTo32(q.Y!));
    }

    /// <summary>Convierte un punto de 65 bytes en parámetros de llave pública P-256.</summary>
    /// <param name="point">Punto sin comprimir.</param>
    /// <exception cref="CryptographicException">Si el punto no mide 65 bytes o no empieza con 0x04.</exception>
    public static ECParameters PublicParameters(ReadOnlySpan<byte> point)
    {
        if (point.Length != 65 || point[0] != 0x04) throw new CryptographicException("Public key must be a 65-byte uncompressed P-256 point.");
        return new ECParameters
        {
            Curve = ECCurve.NamedCurves.nistP256,
            Q = new ECPoint { X = point[1..33].ToArray(), Y = point[33..65].ToArray() },
        };
    }

    /// <summary>Importa un punto público para acuerdo de llaves ECDH.</summary>
    public static ECDiffieHellman ImportEcdhPublic(ReadOnlySpan<byte> point) => ECDiffieHellman.Create(PublicParameters(point));

    /// <summary>Importa un punto público para verificar firmas ECDSA.</summary>
    public static ECDsa ImportEcdsaPublic(ReadOnlySpan<byte> point) => ECDsa.Create(PublicParameters(point));

    /// <summary>Reconstruye una llave privada P-256 a partir de su escalar de 32 bytes (se usa con los vectores de prueba).</summary>
    /// <param name="d">Escalar privado.</param>
    /// <param name="publicPoint">Punto público que le corresponde.</param>
    public static ECParameters PrivateParameters(ReadOnlySpan<byte> d, ReadOnlySpan<byte> publicPoint)
    {
        var p = PublicParameters(publicPoint);
        p.D = d.ToArray();
        return p;
    }

    /// <summary>Huella de una llave pública: hex(SHA-256(punto de 65 bytes)). Es lo que el usuario compara al fijar el servidor.</summary>
    public static string Fingerprint(ReadOnlySpan<byte> publicPoint) => Bytes.ToHex(SHA256.HashData(publicPoint));

    /// <summary>
    /// Coordenada X del punto compartido (32 bytes), el mismo valor que devuelve <c>deriveBits</c> en Web Crypto
    /// y <c>KeyAgreement</c> en Android.
    /// </summary>
    /// <param name="own">Llave privada propia.</param>
    /// <param name="peerPublic">Punto público del otro extremo.</param>
    public static byte[] SharedSecretX(ECDiffieHellman own, ReadOnlySpan<byte> peerPublic)
    {
        using var peer = ImportEcdhPublic(peerPublic);
        return own.DeriveRawSecretAgreement(peer.PublicKey);
    }

    /// <summary>Rellena con ceros a la izquierda una coordenada que .NET pudo devolver con menos de 32 bytes.</summary>
    private static byte[] PadTo32(byte[] v)
    {
        if (v.Length == 32) return v;
        var o = new byte[32];
        Buffer.BlockCopy(v, 0, o, 32 - v.Length, v.Length);
        return o;
    }
}

/// <summary>
/// Transcript del handshake y su firma con la llave ECDSA de largo plazo del servidor
/// (formato IEEE P1363: r ‖ s, 64 bytes). La firma demuestra al cliente que habla con el servidor fijado.
/// </summary>
public static class Handshake
{
    /// <summary>
    /// Arma el transcript: "ST1-HS" ‖ llave del cliente ‖ llave del servidor ‖ nonceC ‖ nonceS ‖ dispositivo ‖ sesión.
    /// </summary>
    /// <param name="clientPublic">Llave efímera del cliente.</param>
    /// <param name="serverPublic">Llave efímera del servidor.</param>
    /// <param name="clientNonce">Nonce del cliente.</param>
    /// <param name="serverNonce">Nonce del servidor.</param>
    /// <param name="deviceId">Identificador del dispositivo.</param>
    /// <param name="sessionId">Identificador de la sesión asignada por el servidor.</param>
    /// <param name="suite">Suite AEAD negociada.</param>
    public static byte[] Transcript(byte[] clientPublic, byte[] serverPublic, byte[] clientNonce, byte[] serverNonce, string deviceId, string sessionId, string suite = Suites.AesGcm)
    {
        var t = Bytes.Concat(Protocol.TranscriptLabel, clientPublic, serverPublic, clientNonce, serverNonce, Bytes.Utf8(deviceId), Bytes.Utf8(sessionId));
        // La suite predeterminada conserva el transcript original de v1 (vectores de prueba); cualquier otra
        // suite se agrega al final para que la firma también la proteja contra una degradación.
        return suite == Suites.AesGcm ? t : Bytes.Concat(t, Bytes.Utf8("|" + suite));
    }

    /// <summary>Firma el transcript con SHA-256 y la llave de largo plazo.</summary>
    public static byte[] Sign(ECDsa longTerm, byte[] transcript)
        => longTerm.SignData(transcript, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);

    /// <summary>Verifica la firma del transcript con la llave pública de largo plazo del servidor.</summary>
    /// <returns><c>true</c> si la firma es válida.</returns>
    public static bool Verify(ReadOnlySpan<byte> serverLongTermPublic, byte[] transcript, byte[] signature)
    {
        using var key = EcKeys.ImportEcdsaPublic(serverLongTermPublic);
        return key.VerifyData(transcript, signature, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
    }
}
