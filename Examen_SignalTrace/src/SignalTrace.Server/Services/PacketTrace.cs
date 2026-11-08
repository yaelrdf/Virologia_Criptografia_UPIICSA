using System.Collections.Concurrent;
using System.Text;
using SignalTrace.Core;

namespace SignalTrace.Server.Services;

/// <summary>Una etapa intermedia de YCM1 tal como la muestra la consola.</summary>
/// <param name="Name">Nombre de la etapa (padded, r1.stream, …).</param>
/// <param name="Hex">Estado del bloque en hexadecimal.</param>
/// <param name="Entropy">Entropía de Shannon del estado (bits/byte).</param>
public sealed record TraceStage(string Name, string Hex, double Entropy);

/// <summary>
/// Lo que muestra la consola del administrador sobre un paquete (PLAN §11.2): el sobre tal como llegó, el
/// resultado y, si se pudo abrir, cada etapa del descifrado con su entropía y su tiempo. Las llaves de sesión
/// solo aparecen con Lab:ExposeKeys.
/// </summary>
public sealed class PacketTraceDto
{
    /// <summary>Contador global para numerar las trazas.</summary>
    private static long _next;
    /// <summary>Número de traza, creciente.</summary>
    public long Id { get; } = Interlocked.Increment(ref _next);
    /// <summary>Momento de recepción (ms Unix).</summary>
    public long TsServer { get; set; }
    /// <summary>Transporte por el que llegó: http o mqtt.</summary>
    public string Transport { get; set; } = "http";
    /// <summary>Resultado: aceptado o rechazado.</summary>
    public string Status { get; set; } = "";
    /// <summary>Código del resultado.</summary>
    public string Code { get; set; } = "";
    /// <summary>Mensaje legible del resultado.</summary>
    public string? Message { get; set; }
    /// <summary>Advertencia no fatal (por ejemplo, un hueco en la secuencia).</summary>
    public string? Warning { get; set; }
    /// <summary>Sesión del sobre.</summary>
    public string? Sid { get; set; }
    /// <summary>Dispositivo del sobre.</summary>
    public string? Did { get; set; }
    /// <summary>Número de secuencia del sobre.</summary>
    public ulong Seq { get; set; }
    /// <summary>Último número de secuencia aceptado en la sesión, para explicar un rechazo por reenvío.</summary>
    public ulong? LastSeq { get; set; }
    /// <summary>Marca de tiempo del sobre.</summary>
    public long Ts { get; set; }
    /// <summary>Suite AEAD de la sesión.</summary>
    public string? Suite { get; set; }
    /// <summary>Tamaño del sobre en bytes.</summary>
    public int WireBytes { get; set; }
    /// <summary>El cuerpo HTTP exactamente como se recibió: lo que ve quien escucha la red.</summary>
    public string Wire { get; set; } = "";

    /// <summary>JSON en claro (solo si se permite mostrarlo).</summary>
    public string? PlaintextJson { get; set; }
    /// <summary>Entropía del texto claro.</summary>
    public double? PlaintextEntropy { get; set; }
    /// <summary>Parámetros de YCM1 de este paquete.</summary>
    public object? Ycm1Params { get; set; }
    /// <summary>Etapas intermedias de YCM1 (solo si se permite mostrar el texto claro).</summary>
    public List<TraceStage> Ycm1Stages { get; set; } = [];
    /// <summary>Entropía de la salida de YCM1.</summary>
    public double? Ycm1Entropy { get; set; }
    /// <summary>Nonce AEAD en hexadecimal.</summary>
    public string? Nonce { get; set; }
    /// <summary>Datos asociados en hexadecimal.</summary>
    public string? Aad { get; set; }
    /// <summary>Texto cifrado AEAD en hexadecimal.</summary>
    public string? Ciphertext { get; set; }
    /// <summary>Etiqueta de autenticación en hexadecimal.</summary>
    public string? Tag { get; set; }
    /// <summary>Entropía del texto cifrado.</summary>
    public double? CiphertextEntropy { get; set; }
    /// <summary>Tiempo en microsegundos de cada etapa.</summary>
    public Dictionary<string, double> Micros { get; set; } = [];
    /// <summary>Llaves de sesión en hexadecimal (solo en el laboratorio con Lab:ExposeKeys).</summary>
    public Dictionary<string, string>? Keys { get; set; }

    /// <summary>
    /// Copia los datos criptográficos de una traza interna al DTO, convirtiendo los bytes a hexadecimal
    /// y calculando la entropía de cada etapa.
    /// </summary>
    /// <param name="t">DTO de destino.</param>
    /// <param name="d">Traza capturada por <see cref="PacketCrypto.Open"/>.</param>
    /// <param name="exposePlaintext">Si se puede mostrar el texto claro y las etapas de YCM1.</param>
    /// <param name="keys">Llaves de sesión para mostrarlas, o null para ocultarlas.</param>
    public static void Fill(PacketTraceDto t, PacketTraceData d, bool exposePlaintext, SessionKeys? keys)
    {
        if (d.Plaintext.Length > 0)
        {
            // La entropía se muestra siempre; el contenido solo si está permitido.
            t.PlaintextEntropy = Entropy.Shannon(d.Plaintext);
            if (exposePlaintext) t.PlaintextJson = Encoding.UTF8.GetString(d.Plaintext);
        }
        if (d.Ycm1Params is { } p)
            t.Ycm1Params = new { seed = p.Seed, rounds = p.Rounds.Select(r => new { k = r.K, m = r.M, rot = r.Rot }) };
        // Las etapas de YCM1 revelan el texto claro (la primera es el texto con relleno), por eso dependen del mismo permiso.
        t.Ycm1Stages = exposePlaintext
            ? d.Ycm1.Stages.Select(s => new TraceStage(s.Name, Bytes.ToHex(s.State), Entropy.Shannon(s.State))).ToList()
            : [];
        if (d.Ycm1Output.Length > 0) t.Ycm1Entropy = Entropy.Shannon(d.Ycm1Output);
        if (d.Nonce.Length > 0) t.Nonce = Bytes.ToHex(d.Nonce);
        if (d.Aad.Length > 0) t.Aad = Bytes.ToHex(d.Aad);
        if (d.Ciphertext.Length > 0)
        {
            t.Ciphertext = Bytes.ToHex(d.Ciphertext);
            t.CiphertextEntropy = Entropy.Shannon(d.Ciphertext);
        }
        if (d.Tag.Length > 0) t.Tag = Bytes.ToHex(d.Tag);
        foreach (var (k, v) in d.Micros) t.Micros[k] = Math.Round(v, 1);
        if (keys is not null)
            t.Keys = new() { ["kAead"] = Bytes.ToHex(keys.KAead), ["kSig"] = Bytes.ToHex(keys.KSig), ["ivSalt"] = Bytes.ToHex(keys.IvSalt) };
    }
}

/// <summary>Búfer circular con las últimas 100 trazas, para que la consola muestre historial al abrirse.</summary>
public sealed class TraceStore
{
    /// <summary>Cola concurrente de trazas, de la más antigua a la más reciente.</summary>
    private readonly ConcurrentQueue<PacketTraceDto> _q = new();
    /// <summary>Máximo de trazas guardadas.</summary>
    private const int Max = 100;

    /// <summary>Agrega una traza y descarta las más antiguas si se supera el máximo.</summary>
    public void Add(PacketTraceDto t)
    {
        _q.Enqueue(t);
        while (_q.Count > Max && _q.TryDequeue(out _)) { }
    }

    /// <summary>Las <paramref name="n"/> trazas más recientes, de la más nueva a la más antigua.</summary>
    public IEnumerable<PacketTraceDto> Recent(int n) => _q.Reverse().Take(n);
}
