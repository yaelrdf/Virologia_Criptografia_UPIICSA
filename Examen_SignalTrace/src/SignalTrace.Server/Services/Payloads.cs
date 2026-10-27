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
        if (p.Bat is { } b)
        {
            if (!In(b.Lvl, 0, 1)) return "Nivel de batería fuera de [0,1]";
            if (b.Temp is < -40 or > 100 || b.Volt is < 0 or > 10) return "Batería fuera de rango";
        }
        if (p.Mot is { } m)
        {
            if (m.Mag is { Length: > 256 }) return "mot.mag admite como máximo 256 muestras";
            if (m.Fs is <= 0 or > 1000) return "mot.fs fuera de rango";
            if (m.Rms is < 0 || m.Peak is < 0) return "Movimiento fuera de rango";
            if (m.Mag?.Any(x => !double.IsFinite(x) || Math.Abs(x) > 1000) == true) return "mot.mag fuera de rango";
        }
        if (!Vec(p.Lin, 200) || !Vec(p.Grv, 20) || !Vec(p.Gyr, 100)) return "Movimiento fuera de rango";
        if (!Vec(p.Mfl, 5000)) return "Campo magnético fuera de rango";
        if (p.Ori is { } o && (!In(o.Az, 0, 360) || !In(o.Pi, -180, 180) || !In(o.Ro, -180, 180))) return "Orientación fuera de rango";
        if (p.Env is { } env && (env.Lux is < 0 or > 200_000 || env.Prx is < 0 or > 1000 || env.Hpa is < 100 or > 1200
                                   || env.Tmp is < -60 or > 100 || env.Hum is < 0 or > 100)) return "Sensor de entorno fuera de rango";
        if (p.Stp is { N: < 0 }) return "Pasos fuera de rango";
        if (p.Net?.Probe is { } pr && (pr.Loss is < 0 or > 1 || pr.Rtt is < 0 || pr.Jit is < 0 || pr.Kbps is < 0)) return "Sondeo fuera de rango";
        if (p.Wifi is { } w && (w.Rssi is < -127 or > 0 || w.Freq is < 2000 or > 7200 || w.Link is < 0 || w.Tx is < 0 || w.Rx is < 0)) return "Wi-Fi fuera de rango";
        if (p.Cell is { } c && (c.Dbm is < -160 or > 0 || c.Rsrp is < -160 or > 0 || c.Level is < 0 or > 4)) return "Celular fuera de rango";
        return null;
    }

    private static bool In(double v, double lo, double hi) => double.IsFinite(v) && v >= lo && v <= hi;

    private static bool Vec(Vec3? v, double max) => v is null || (In(v.X, -max, max) && In(v.Y, -max, max) && In(v.Z, -max, max));
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
        AccRms = p.Mot?.Rms,
        GyrNorm = p.Gyr?.Norm(),
        Lux = p.Env?.Lux,
        Hpa = p.Env?.Hpa,
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

/// <summary>
/// Reading sent to dashboards (REST and SignalR): the flattened columns used by the charts, the analytics of the
/// moment (EMA, trends, z-scores) and the full decrypted payload <c>P</c> for the live panel.
/// </summary>
public sealed record ReadingDto(
    string DeviceId, long Ts, long TsServer, long Seq, string Src,
    double? AccRms, double? AccPeak, double? GyrNorm, double? Lux, double? Hpa,
    double? BatLvl, bool? BatChg, string? NetType, string? NetEff,
    double? ProbeRtt, double? ProbeJit, double? ProbeLoss, double? ProbeKbps,
    int? WifiRssi, int? WifiFreq, int? CellDbm, string? CellType, bool? Visible,
    double? RmsEma, double? RttEma, double? RssiEma,
    double? BatSlopeH, double? BatEtaMin, double? RssiSlopeMin, Dictionary<string, double>? Z, TelemetryPayload? P)
{
    public static ReadingDto From(Reading r, TelemetryPayload? p, AnalyticsResult? a, Dictionary<string, double>? z = null) => new(
        r.DeviceId.ToString("D"), r.TsDevice, r.TsServer, r.Seq, r.Src,
        r.AccRms, p?.Mot?.Peak, r.GyrNorm, r.Lux, r.Hpa,
        r.BatLvl, p?.Bat?.Chg, r.NetType, p?.Net?.Eff,
        r.ProbeRtt, p?.Net?.Probe?.Jit, r.ProbeLoss, p?.Net?.Probe?.Kbps,
        r.WifiRssi, p?.Wifi?.Freq, r.CellDbm, r.CellType, p?.App?.Vis,
        a?.RmsEma, a?.RttEma, a?.RssiEma,
        a?.BatterySlopePerHour, a?.BatteryEtaMinutes, a?.RssiSlopePerMinute, a is { Z.Count: > 0 } ? a.Z : z is { Count: > 0 } ? z : null, WithoutSpectrum(p));

    /// <summary>The magnitude window (up to 256 numbers) is only needed for the spectrum, which travels separately.</summary>
    private static TelemetryPayload? WithoutSpectrum(TelemetryPayload? p)
    {
        if (p?.Mot?.Mag is null) return p;
        p.Mot.Mag = null;
        return p;
    }
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
