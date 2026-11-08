using System.Diagnostics;
using System.Security.Cryptography;

namespace SignalTrace.Core;

/// <summary>
/// Sobre de telemetría que se envía a <c>POST /api/telemetry</c> (PLAN §6.4). Solo <c>Ct</c> va cifrado;
/// los demás campos viajan en claro pero quedan autenticados porque forman parte del AAD o del nonce.
/// </summary>
/// <param name="V">Versión del protocolo (1).</param>
/// <param name="Sid">Identificador de la sesión abierta en el handshake.</param>
/// <param name="Did">Identificador del dispositivo.</param>
/// <param name="Seq">Número de secuencia; crece de uno en uno y sirve contra el reenvío (replay).</param>
/// <param name="Ts">Marca de tiempo del dispositivo en milisegundos desde la época Unix.</param>
/// <param name="Ct">Texto cifrado y etiqueta AEAD en Base64.</param>
public sealed record TelemetryEnvelope(int V, string Sid, string Did, ulong Seq, long Ts, string Ct);

/// <summary>Se lanza cuando la etiqueta AEAD no verifica: el paquete fue alterado o se usó otra llave.</summary>
public sealed class AuthenticationFailedException() : CryptographicException("AEAD tag mismatch");

/// <summary>
/// Todo lo que la consola del administrador muestra sobre un paquete, etapa por etapa: texto claro,
/// parámetros y estados intermedios de YCM1, nonce, AAD, texto cifrado, etiqueta y tiempos de cada etapa.
/// </summary>
public sealed class PacketTraceData
{
    /// <summary>JSON de telemetría en claro (UTF-8).</summary>
    public byte[] Plaintext { get; set; } = [];
    /// <summary>Parámetros de YCM1 derivados para este número de secuencia.</summary>
    public Ycm1Params? Ycm1Params { get; set; }
    /// <summary>Estados intermedios de cada ronda de YCM1.</summary>
    public Ycm1TraceRecorder Ycm1 { get; } = new();
    /// <summary>Salida de YCM1, que es la entrada del AEAD.</summary>
    public byte[] Ycm1Output { get; set; } = [];
    /// <summary>Nonce AEAD de 12 bytes (sal ‖ secuencia).</summary>
    public byte[] Nonce { get; set; } = [];
    /// <summary>Datos asociados autenticados (no cifrados).</summary>
    public byte[] Aad { get; set; } = [];
    /// <summary>Texto cifrado AEAD sin la etiqueta.</summary>
    public byte[] Ciphertext { get; set; } = [];
    /// <summary>Etiqueta de autenticación de 16 bytes.</summary>
    public byte[] Tag { get; set; } = [];
    /// <summary>Tiempo en microsegundos de cada etapa (<c>"ycm1"</c> y <c>"aead"</c>).</summary>
    public Dictionary<string, double> Micros { get; } = [];
}

/// <summary>
/// Sellado y apertura de paquetes en dos capas:
/// <list type="number">
/// <item>sm = YCM1(texto claro, k_sig, seq): el cifrado propio del proyecto.</item>
/// <item>nonce = iv_salt ‖ u64be(seq); aad = "ST1" ‖ utf8(dispositivo) ‖ u64be(seq) ‖ u64be(ts).</item>
/// <item>ct ‖ tag = AEAD(k_aead, nonce, sm, aad): AES-256-GCM o ChaCha20-Poly1305, que da la seguridad real.</item>
/// </list>
/// </summary>
public static class PacketCrypto
{
    /// <summary>Construye el nonce AEAD de 12 bytes: los 4 bytes de sal de la sesión seguidos de la secuencia.</summary>
    /// <param name="ivSalt">Sal derivada en el handshake.</param>
    /// <param name="seq">Número de secuencia del paquete; nunca se repite en una sesión, así el nonce tampoco.</param>
    public static byte[] Nonce(ReadOnlySpan<byte> ivSalt, ulong seq) => Bytes.Concat(ivSalt.ToArray(), Bytes.U64Be(seq));

    /// <summary>
    /// Construye los datos asociados. Atan el texto cifrado al dispositivo, a la secuencia y a la marca de
    /// tiempo: si alguien cambia cualquiera de ellos en el sobre, la etiqueta deja de verificar.
    /// </summary>
    public static byte[] Aad(string deviceId, ulong seq, long tsMs)
        => Bytes.Concat(Protocol.AadLabel, Bytes.Utf8(deviceId), Bytes.U64Be(seq), Bytes.U64Be((ulong)tsMs));

    /// <summary>
    /// Cifra un paquete: primero YCM1 y luego AEAD. Si se pasa <paramref name="trace"/>, guarda cada etapa
    /// y su duración para mostrarlas en la consola.
    /// </summary>
    /// <param name="keys">Llaves de la sesión.</param>
    /// <param name="deviceId">Dispositivo que envía.</param>
    /// <param name="seq">Número de secuencia.</param>
    /// <param name="tsMs">Marca de tiempo en milisegundos.</param>
    /// <param name="plaintext">JSON de telemetría.</param>
    /// <param name="trace">Destino opcional de la traza.</param>
    /// <returns>Texto cifrado seguido de la etiqueta de 16 bytes.</returns>
    public static byte[] Seal(SessionKeys keys, string deviceId, ulong seq, long tsMs, ReadOnlySpan<byte> plaintext, PacketTraceData? trace = null)
    {
        var sw = Stopwatch.StartNew();
        // Capa 1: cifrado propio YCM1.
        var sm = YaelCryptoMarch1.Encrypt(plaintext, keys.KSig, seq, YaelCryptoMarch1.DefaultRounds, trace?.Ycm1);
        var tSig = sw.Elapsed.TotalMicroseconds;
        // Capa 2: AEAD estándar sobre la salida de YCM1.
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

    /// <summary>
    /// Descifra un paquete: verifica la etiqueta AEAD y, solo si es válida, revierte YCM1. Llena
    /// <paramref name="trace"/> con las mismas etapas que <see cref="Seal"/>.
    /// </summary>
    /// <param name="keys">Llaves de la sesión.</param>
    /// <param name="deviceId">Dispositivo que dice enviar el paquete.</param>
    /// <param name="seq">Número de secuencia del sobre.</param>
    /// <param name="tsMs">Marca de tiempo del sobre.</param>
    /// <param name="ctAndTag">Texto cifrado con la etiqueta al final.</param>
    /// <param name="trace">Destino opcional de la traza.</param>
    /// <returns>El JSON de telemetría en claro.</returns>
    /// <exception cref="AuthenticationFailedException">Si el paquete es demasiado corto o la etiqueta no verifica.</exception>
    public static byte[] Open(SessionKeys keys, string deviceId, ulong seq, long tsMs, ReadOnlySpan<byte> ctAndTag, PacketTraceData? trace = null)
    {
        if (ctAndTag.Length < Protocol.TagLength) throw new AuthenticationFailedException();
        var sw = Stopwatch.StartNew();
        var nonce = Nonce(keys.IvSalt, seq);
        var aad = Aad(deviceId, seq, tsMs);
        // Separa los últimos 16 bytes, que son la etiqueta.
        var ct = ctAndTag[..^Protocol.TagLength].ToArray();
        var tag = ctAndTag[^Protocol.TagLength..].ToArray();
        var sm = new byte[ct.Length];
        try
        {
            AeadDecrypt(keys, nonce, ct, tag, sm, aad);
        }
        catch (CryptographicException)
        {
            // Cualquier fallo del AEAD se reporta igual, sin dar pistas de la causa.
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
            YaelCryptoMarch1.Encrypt(plain, keys.KSig, seq, YaelCryptoMarch1.DefaultRounds, trace.Ycm1); // repite el cifrado hacia adelante para registrar las etapas
            trace.Micros["aead"] = tAead;
            trace.Micros["ycm1"] = sw.Elapsed.TotalMicroseconds - tAead;
        }
        return plain;
    }

    /// <summary>Cifra con la suite negociada en la sesión (ChaCha20-Poly1305 o AES-256-GCM).</summary>
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

    /// <summary>Descifra y verifica con la suite de la sesión; lanza <see cref="CryptographicException"/> si la etiqueta no coincide.</summary>
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
