using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SignalTrace.Core;
using SignalTrace.Server.Analytics;
using SignalTrace.Server.Data;
using SignalTrace.Server.Hubs;

namespace SignalTrace.Server.Services;

public sealed record PipelineResult(int Status, string Code, string? Message = null, ulong? Ack = null)
{
    public bool Ok => Status == StatusCodes.Status202Accepted;
}

/// <summary>
/// Receives an encrypted envelope (from HTTP or MQTT) and applies, in this order (PLAN §6.4):
/// token did == envelope did → session lookup → seq freshness → AEAD tag → SigMix⁻¹ → payload validation →
/// only then lastSeq update → persist → analytics → broadcast.
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
    private readonly bool _window = cfg.GetValue("Telemetry:ReplayWindow", false);
    private readonly int _maxBody = cfg.GetValue("Telemetry:MaxBodyBytes", 65536);

    public async Task<PipelineResult> ProcessAsync(byte[] body, string? tokenDeviceId, string sourceAddress, string transport, CancellationToken ct = default)
    {
        var now = clock.GetUtcNow().ToUnixTimeMilliseconds();
        var packet = new Packet { TsServer = now, WireBytes = body.Length, SourceHash = SourceHash(sourceAddress), Transport = transport };
        var trace = new PacketTraceDto { TsServer = now, Transport = transport, WireBytes = body.Length, Wire = Truncate(Encoding.UTF8.GetString(body), 4096) };

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
        packet.CtB64 = env.Ct.Length <= 8192 ? env.Ct : null;

        // 1) The device token must belong to the device named in the envelope, and the device must not be revoked.
        if (tokenDeviceId is null || !string.Equals(tokenDeviceId, env.Did, StringComparison.Ordinal))
            return await Reject(packet, trace, 401, "unauthorized", "El token no corresponde al dispositivo");
        var device = await db.Devices.FindAsync([did], ct);
        if (device is null || device.Revoked)
            return await Reject(packet, trace, 401, "unauthorized", "Dispositivo revocado o inexistente");

        // 2) Session.
        var session = sessions.Get(env.Sid);
        if (session is null || session.DeviceId != env.Did)
            return await Reject(packet, trace, 404, "unknown_session", "Sesión desconocida o expirada: repite el handshake");
        trace.Suite = session.Keys.Suite;

        var traceData = new PacketTraceData();
        byte[] plain = [];
        TelemetryPayload? payload = null;
        (int Status, string Code, string Message)? rejection = null;
        lock (session.Gate)
        {
            // 3) Freshness (anti-replay), checked before decrypting but committed only after validation.
            if (!ReplayGuard.IsFresh(session, env.Seq, _window))
            {
                trace.LastSeq = session.LastSeq;
                rejection = (409, "replay", $"seq {env.Seq} <= último aceptado {session.LastSeq}");
            }
            else
            {
                rejection = OpenAndValidate(session, env, ctBytes, traceData, trace, out plain, out payload);
                // 7) Only now the sequence number is consumed.
                if (rejection is null) ReplayGuard.Accept(session, env.Seq);
            }
        }
        if (rejection is { } r) return await Reject(packet, trace, r.Status, r.Code, r.Message);

        FillTrace(trace, traceData, session);
        var skew = Math.Abs(now - env.Ts);
        if (skew > 5 * 60_000) trace.Warning = $"Desfase de reloj de {skew / 1000} s";

        // 8) Persist.
        var json = Encoding.UTF8.GetString(plain);
        var reading = ReadingMapper.From(payload!, device.Id, env, now, atRest.Protect(json));
        db.Readings.Add(reading);
        packet.Status = "ok";
        packet.Reason = trace.Warning;
        db.Packets.Add(packet);
        if (device.LastSeenAt is { } prev && env.Ts > 0)
        {
            var delta = Math.Clamp(now - prev, 50, 600_000);
            device.IntervalMs = device.IntervalMs is { } iv ? 0.8 * iv + 0.2 * delta : delta;
        }
        device.LastSeenAt = now;
        if (payload!.Bat is { } bat) device.LastBattery = bat.Lvl;
        if (payload.Wifi?.Rssi is { } rssi) device.LastRssi = rssi;
        else if (payload.Cell?.Dbm is { } dbm) device.LastRssi = dbm;
        var result = analytics.OnReading(device, payload, reading);
        foreach (var a in result.Alerts) db.Alerts.Add(a);
        await db.SaveChangesAsync(ct);

        // 9) Broadcast.
        trace.Status = "ok";
        trace.Code = "ok";
        traces.Add(trace);
        await live.Reading(device.Id, ReadingDto.From(reading, payload, result));
        await live.DeviceStatus(device, online: true);
        foreach (var a in result.Alerts) await live.Alert(a, device);
        if (result.Spectrum is { } sp) await live.Spectrum(device.Id, sp);
        await live.PacketTrace(trace);
        log.LogDebug("Packet ok did={Did} seq={Seq}", env.Did, env.Seq);
        return new PipelineResult(202, "ok", null, env.Seq);
    }

    /// <summary>Steps 4–6: AEAD tag, SigMix inverse and content validation. Returns the rejection, if any.</summary>
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
            trace.Nonce = Bytes.ToHex(PacketCrypto.Nonce(session.Keys.IvSalt, env.Seq));
            trace.Aad = Bytes.ToHex(PacketCrypto.Aad(env.Did, env.Seq, env.Ts));
            return (422, "auth_failed", "Etiqueta AEAD inválida: paquete alterado o llave errónea");
        }
        catch (CryptographicException)
        {
            return (422, "invalid_payload", "Relleno SigMix inválido");
        }
        var error = PayloadValidator.Validate(plain, env.Ts, out payload);
        if (error is null) return null;
        FillTrace(trace, data, session);
        return (422, "invalid_payload", error);
    }

    private void FillTrace(PacketTraceDto t, PacketTraceData d, CryptoSession s)
        => PacketTraceDto.Fill(t, d, cfg.GetValue("Console:ExposePlaintext", true), cfg.GetValue("Lab:ExposeKeys", false) ? s.Keys : null);

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

    private static string SourceHash(string address)
    {
        var day = DateTime.UtcNow.ToString("yyyyMMdd");
        return Bytes.ToHex(SHA256.HashData(Encoding.UTF8.GetBytes("st-src|" + day + "|" + address)))[..16];
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "…";
}
