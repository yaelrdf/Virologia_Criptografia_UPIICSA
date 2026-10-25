using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SignalTrace.Core;
using SignalTrace.Server.Analytics;
using SignalTrace.Server.Data;

namespace SignalTrace.Server.Services;

/// <summary>Schema and range checks of the decrypted payload (PLAN §8). Returns an error message or null.</summary>
public static class PayloadValidator
{
    private static readonly HashSet<string> Sources = ["web", "android", "sim"];

    public static string? Validate(byte[] plain, long envelopeTs, out TelemetryPayload? payload)
    {
        payload = null;
        try { payload = JsonSerializer.Deserialize<TelemetryPayload>(plain, Json.Options); }
        catch (JsonException e) { return "JSON inválido: " + e.Message.Split('.')[0]; }
        if (payload is null) return "Payload vacío";
        var p = payload;
        if (p.T != envelopeTs) return "t del payload no coincide con ts del sobre";
        if (!Sources.Contains(p.Src)) return "src debe ser web, android o sim";
        if (p.Gps is { } g)
        {
            if (!In(g.Lat, -90, 90) || !In(g.Lon, -180, 180)) return "Coordenadas fuera de rango";
            if (g.Acc is < 0 or > 100_000 || g.Spd is < 0 or > 500 || g.Hdg is < 0 or > 360 || g.Alt is < -1000 or > 20_000) return "GPS fuera de rango";
        }
        if (p.Bat is { } b && !In(b.Lvl, 0, 1)) return "Nivel de batería fuera de [0,1]";
        if (p.Mot is { } m)
        {
            if (m.Mag is { Length: > 256 }) return "mot.mag admite como máximo 256 muestras";
            if (m.Fs is <= 0 or > 1000) return "mot.fs fuera de rango";
            if (m.Rms is < 0 || m.Peak is < 0) return "Movimiento fuera de rango";
            if (m.Mag?.Any(x => !double.IsFinite(x) || Math.Abs(x) > 1000) == true) return "mot.mag fuera de rango";
        }
        if (p.Net?.Probe is { } pr && (pr.Loss is < 0 or > 1 || pr.Rtt is < 0 || pr.Jit is < 0 || pr.Kbps is < 0)) return "Sondeo fuera de rango";
        if (p.Wifi is { } w && (w.Rssi is < -127 or > 0 || w.Freq is < 2000 or > 7200 || w.Link is < 0)) return "Wi-Fi fuera de rango";
        if (p.Cell is { } c && (c.Dbm is < -160 or > 0 || c.Rsrp is < -160 or > 0 || c.Level is < 0 or > 4)) return "Celular fuera de rango";
        return null;
    }

    private static bool In(double v, double lo, double hi) => double.IsFinite(v) && v >= lo && v <= hi;
}

public static class ReadingMapper
{
    public static Reading From(TelemetryPayload p, Guid deviceId, TelemetryEnvelope env, long tsServer, string storedJson) => new()
    {
        DeviceId = deviceId,
        TsDevice = env.Ts,
        TsServer = tsServer,
        Seq = (long)env.Seq,
        SessionId = env.Sid,
        Src = p.Src,
        Lat = p.Gps?.Lat,
        Lon = p.Gps?.Lon,
        Acc = p.Gps?.Acc,
        Spd = p.Gps?.Spd,
        AccRms = p.Mot?.Rms,
        BatLvl = p.Bat?.Lvl,
        NetType = p.Net?.Type,
        ProbeRtt = p.Net?.Probe?.Rtt,
        ProbeLoss = p.Net?.Probe?.Loss,
        WifiRssi = p.Wifi?.Rssi,
        CellDbm = p.Cell?.Dbm ?? p.Cell?.Rsrp,
        CellType = p.Cell?.Type,
        Json = storedJson,
    };
}

/// <summary>Flattened reading sent to dashboards (REST and SignalR).</summary>
public sealed record ReadingDto(
    string DeviceId, long Ts, long TsServer, long Seq, string Src,
    double? Lat, double? Lon, double? Acc, double? Spd, double? AccRms, double? AccPeak,
    double? BatLvl, bool? BatChg, string? NetType, string? NetEff,
    double? ProbeRtt, double? ProbeJit, double? ProbeLoss, double? ProbeKbps,
    int? WifiRssi, int? WifiFreq, int? CellDbm, string? CellType, bool? Visible,
    double? RmsEma, double? RttEma, double? RssiEma, double? Speed, double? DistanceM,
    double? BatSlopeH, double? BatEtaMin, double? RssiSlopeMin)
{
    public static ReadingDto From(Reading r, TelemetryPayload? p, AnalyticsResult? a) => new(
        r.DeviceId.ToString("D"), r.TsDevice, r.TsServer, r.Seq, r.Src,
        r.Lat, r.Lon, r.Acc, r.Spd, r.AccRms, p?.Mot?.Peak,
        r.BatLvl, p?.Bat?.Chg, r.NetType, p?.Net?.Eff,
        r.ProbeRtt, p?.Net?.Probe?.Jit, r.ProbeLoss, p?.Net?.Probe?.Kbps,
        r.WifiRssi, p?.Wifi?.Freq, r.CellDbm, r.CellType, p?.App?.Vis,
        a?.RmsEma, a?.RttEma, a?.RssiEma, a?.DerivedSpeed ?? r.Spd, a?.TrackLengthM,
        a?.BatterySlopePerHour, a?.BatteryEtaMinutes, a?.RssiSlopePerMinute);
}

/// <summary>Optional encryption at rest of Readings.Json with AES-256-GCM (Storage:EncryptAtRest).</summary>
public sealed class AtRestProtector
{
    private readonly byte[]? _key;
    private const string Prefix = "enc:";

    public AtRestProtector(IConfiguration cfg, ServerPaths paths)
    {
        if (cfg.GetValue("Storage:EncryptAtRest", false))
            _key = paths.SecretBytes(cfg, "Storage:MasterKey", "at-rest.key", 32);
    }

    public bool Enabled => _key is not null;

    public string Protect(string json)
    {
        if (_key is null) return json;
        var nonce = RandomNumberGenerator.GetBytes(12);
        var pt = Encoding.UTF8.GetBytes(json);
        var ct = new byte[pt.Length];
        var tag = new byte[16];
        using var g = new AesGcm(_key, 16);
        g.Encrypt(nonce, pt, ct, tag, "ST1-REST"u8);
        return Prefix + Convert.ToBase64String(Bytes.Concat(nonce, ct, tag));
    }

    public string Unprotect(string stored)
    {
        if (!stored.StartsWith(Prefix, StringComparison.Ordinal)) return stored;
        if (_key is null) throw new InvalidOperationException("Encrypted reading but Storage:EncryptAtRest is off.");
        var raw = Convert.FromBase64String(stored[Prefix.Length..]);
        var pt = new byte[raw.Length - 28];
        using var g = new AesGcm(_key, 16);
        g.Decrypt(raw[..12], raw[12..^16], raw[^16..], pt, "ST1-REST"u8);
        return Encoding.UTF8.GetString(pt);
    }
}
