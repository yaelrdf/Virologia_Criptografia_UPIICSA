using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SignalTrace.Core;
using SignalTrace.Server.Analytics;
using SignalTrace.Server.Data;
using SignalTrace.Server.Hubs;

namespace SignalTrace.Server.Services;

/// <summary>Resultado de procesar un paquete: código HTTP, código de la API, mensaje y, si se aceptó, la secuencia confirmada.</summary>
public sealed record PipelineResult(int Status, string Code, string? Message = null, ulong? Ack = null)
{
    /// <summary>Si el paquete se aceptó (HTTP 202).</summary>
    public bool Ok => Status == StatusCodes.Status202Accepted;
}

/// <summary>
/// Recibe un sobre cifrado (por HTTP o MQTT) y aplica, en este orden (PLAN §6.4):
/// el did del token coincide con el del sobre → búsqueda de la sesión → secuencia nueva → etiqueta AEAD →
/// YCM1⁻¹ → validación de la carga → solo entonces se actualiza lastSeq → guardado → analítica → difusión.
/// Cada rechazo queda registrado en la bitácora de paquetes y en la consola.
/// </summary>
public sealed class TelemetryPipeline(
    AppDb db,
    SessionStore sessions,
    AnalyticsEngine analytics,
    LiveBroadcaster live,
    TraceStore traces,
    AtRestProtector atRest,
    IConfiguration cfg,
    TimeProvider clock,
    ILogger<TelemetryPipeline> log)
{
    /// <summary>Si se usa la ventana de 64 paquetes contra el reenvío (si no, modo estricto).</summary>
    private readonly bool _window = cfg.GetValue("Telemetry:ReplayWindow", false);
    /// <summary>Tamaño máximo del sobre en bytes.</summary>
    private readonly int _maxBody = cfg.GetValue("Telemetry:MaxBodyBytes", 65536);

    /// <summary>
    /// Procesa un sobre completo y devuelve el resultado para el transporte.
    /// </summary>
    /// <param name="body">Bytes del sobre JSON tal como llegaron.</param>
    /// <param name="tokenDeviceId">Id del dispositivo según su token (null si no hay token).</param>
    /// <param name="sourceAddress">Origen (IP o cliente MQTT); solo se guarda su hash.</param>
    /// <param name="transport">«http» o «mqtt».</param>
    /// <param name="ct">Cancelación.</param>
    public async Task<PipelineResult> ProcessAsync(byte[] body, string? tokenDeviceId, string sourceAddress, string transport, CancellationToken ct = default)
    {
        var now = clock.GetUtcNow().ToUnixTimeMilliseconds();
        var packet = new Packet { TsServer = now, WireBytes = body.Length, SourceHash = SourceHash(sourceAddress), Transport = transport };
        var trace = new PacketTraceDto { TsServer = now, Transport = transport, WireBytes = body.Length, Wire = Truncate(Encoding.UTF8.GetString(body), 4096) };

        // Lectura del sobre; un JSON inválido deja env en null y se rechaza abajo.
        TelemetryEnvelope? env = null;
        try
        {
            if (body.Length > _maxBody) return await Reject(packet, trace, 400, "malformed", "Cuerpo demasiado grande");
            env = JsonSerializer.Deserialize<TelemetryEnvelope>(body, Json.Options);
        }
        catch (JsonException) { }

        if (env is null || env.V != Protocol.Version || string.IsNullOrEmpty(env.Sid) || string.IsNullOrEmpty(env.Did) || string.IsNullOrEmpty(env.Ct) || env.Seq == 0 || env.Ts <= 0)
            return await Reject(packet, trace, 400, "malformed", "Sobre mal formado o campos faltantes");

        packet.SessionId = env.Sid;
        packet.Seq = (long)env.Seq;
        trace.Sid = env.Sid; trace.Did = env.Did; trace.Seq = env.Seq; trace.Ts = env.Ts;
        if (Guid.TryParse(env.Did, out var did)) packet.DeviceId = did;

        byte[] ctBytes;
        try { ctBytes = Convert.FromBase64String(env.Ct); }
        catch (FormatException) { return await Reject(packet, trace, 400, "malformed", "ct no es Base64"); }
        // Guarda el texto cifrado para la consola solo si no es demasiado grande.
        packet.CtB64 = env.Ct.Length <= 8192 ? env.Ct : null;

        // 1) El token debe pertenecer al dispositivo indicado en el sobre, y el dispositivo no debe estar revocado.
        if (tokenDeviceId is null || !string.Equals(tokenDeviceId, env.Did, StringComparison.Ordinal))
            return await Reject(packet, trace, 401, "unauthorized", "El token no corresponde al dispositivo");
        var device = await db.Devices.FindAsync([did], ct);
        if (device is null || device.Revoked)
            return await Reject(packet, trace, 401, "unauthorized", "Dispositivo revocado o inexistente");

        // 2) Sesión. Si no existe (venció o el servidor se reinició), el 404 le indica al cliente que repita el handshake.
        var session = sessions.Get(env.Sid);
        if (session is null || session.DeviceId != env.Did)
            return await Reject(packet, trace, 404, "unknown_session", "Sesión desconocida o expirada: repite el handshake");
        trace.Suite = session.Keys.Suite;

        var traceData = new PacketTraceData();
        byte[] plain = [];
        TelemetryPayload? payload = null;
        (int Status, string Code, string Message)? rejection = null;
        // Los pasos 3 a 7 se hacen bajo el candado de la sesión para que dos paquetes simultáneos no acepten la misma secuencia.
        lock (session.Gate)
        {
            // 3) Frescura (contra el reenvío): se revisa antes de descifrar, pero se confirma solo después de validar.
            if (!ReplayGuard.IsFresh(session, env.Seq, _window))
            {
                trace.LastSeq = session.LastSeq;
                rejection = (409, "replay", $"seq {env.Seq} <= último aceptado {session.LastSeq}");
            }
            else
            {
                rejection = OpenAndValidate(session, env, ctBytes, traceData, trace, out plain, out payload);
                // 7) Solo ahora se consume el número de secuencia.
                if (rejection is null) ReplayGuard.Accept(session, env.Seq);
            }
        }
        if (rejection is { } r) return await Reject(packet, trace, r.Status, r.Code, r.Message);

        FillTrace(trace, traceData, session);
        // Advierte (sin rechazar) si el reloj del dispositivo está desfasado más de 5 minutos.
        var skew = Math.Abs(now - env.Ts);
        if (skew > 5 * 60_000) trace.Warning = $"Desfase de reloj de {skew / 1000} s";

        // 8) Guardado de la lectura, del registro del paquete y del estado del dispositivo.
        var json = Encoding.UTF8.GetString(plain);
        var reading = ReadingMapper.From(payload!, device.Id, env, now, atRest.Protect(json));
        db.Readings.Add(reading);
        packet.Status = "ok";
        packet.Reason = trace.Warning;
        db.Packets.Add(packet);
        if (device.LastSeenAt is { } prev)
        {
            // Intervalo nominal de muestreo (EMA de los tiempos entre llegadas). Las pausas (pantalla bloqueada, cortes)
            // se ignoran para que no estiren la rejilla de interpolación.
            var delta = Math.Clamp(now - prev, 50, 600_000);
            if (device.IntervalMs is not { } iv) device.IntervalMs = delta;
            else if (delta < 4 * iv) device.IntervalMs = 0.8 * iv + 0.2 * delta;
        }
        device.LastSeenAt = now;
        if (payload!.Bat is { } bat) device.LastBattery = bat.Lvl;
        if (payload.Wifi?.Rssi is { } rssi) device.LastRssi = rssi;
        else if (payload.Cell?.Dbm is { } dbm) device.LastRssi = dbm;
        var result = analytics.OnReading(device, payload, reading);
        await db.SaveChangesAsync(ct);

        // 9) Difusión al tablero en vivo y a la consola.
        trace.Status = "ok";
        trace.Code = "ok";
        traces.Add(trace);
        await live.Reading(device.Id, ReadingDto.From(reading, payload, result));
        await live.DeviceStatus(device, online: true);
        if (result.Spectrum is { } sp) await live.Spectrum(device.Id, sp);
        await live.PacketTrace(trace);
        log.LogDebug("Packet ok did={Did} seq={Seq}", env.Did, env.Seq);
        return new PipelineResult(202, "ok", null, env.Seq);
    }

    /// <summary>Pasos 4 a 6: etiqueta AEAD, inversa de YCM1 y validación del contenido. Devuelve el rechazo, si lo hay.</summary>
    private (int, string, string)? OpenAndValidate(CryptoSession session, TelemetryEnvelope env, byte[] ctBytes, PacketTraceData data,
        PacketTraceDto trace, out byte[] plain, out TelemetryPayload? payload)
    {
        plain = [];
        payload = null;
        try
        {
            plain = PacketCrypto.Open(session.Keys, env.Did, env.Seq, env.Ts, ctBytes, data);
        }
        catch (AuthenticationFailedException)
        {
            // Para la consola: muestra el nonce y el AAD que se usaron al verificar, así se ve qué campo cambió.
            trace.Nonce = Bytes.ToHex(PacketCrypto.Nonce(session.Keys.IvSalt, env.Seq));
            trace.Aad = Bytes.ToHex(PacketCrypto.Aad(env.Did, env.Seq, env.Ts));
            return (422, "auth_failed", "Etiqueta AEAD inválida: paquete alterado o llave errónea");
        }
        catch (CryptographicException)
        {
            return (422, "invalid_payload", "Relleno YCM1 inválido");
        }
        var error = PayloadValidator.Validate(plain, env.Ts, out payload);
        if (error is null) return null;
        FillTrace(trace, data, session);
        return (422, "invalid_payload", error);
    }

    /// <summary>Completa la traza respetando Console:ExposePlaintext y Lab:ExposeKeys.</summary>
    private void FillTrace(PacketTraceDto t, PacketTraceData d, CryptoSession s)
        => PacketTraceDto.Fill(t, d, cfg.GetValue("Console:ExposePlaintext", true), cfg.GetValue("Lab:ExposeKeys", false) ? s.Keys : null);

    /// <summary>Registra un rechazo en la base de datos y en la consola y arma el resultado de error.</summary>
    private async Task<PipelineResult> Reject(Packet p, PacketTraceDto t, int status, string code, string message)
    {
        p.Status = code;
        p.Reason = message;
        db.Packets.Add(p);
        await db.SaveChangesAsync();
        t.Status = "rejected";
        t.Code = code;
        t.Message = message;
        traces.Add(t);
        await live.PacketTrace(t);
        log.LogInformation("Packet rejected {Code}: {Message}", code, message);
        return new PipelineResult(status, code, message);
    }

    /// <summary>
    /// Hash del origen con una sal que cambia cada día: permite agrupar los paquetes de un mismo origen
    /// en un día sin guardar la IP ni poder seguirla de un día a otro.
    /// </summary>
    private static string SourceHash(string address)
    {
        var day = DateTime.UtcNow.ToString("yyyyMMdd");
        return Bytes.ToHex(SHA256.HashData(Encoding.UTF8.GetBytes("st-src|" + day + "|" + address)))[..16];
    }

    /// <summary>Corta un texto largo y agrega «…».</summary>
    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "…";
}
