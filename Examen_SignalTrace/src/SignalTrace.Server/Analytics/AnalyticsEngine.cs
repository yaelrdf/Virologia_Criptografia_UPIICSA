using System.Collections.Concurrent;
using SignalTrace.Core;
using SignalTrace.Server.Data;

namespace SignalTrace.Server.Analytics;

public sealed record SpectrumResult(double[] Freqs, double[] Mags, double DominantHz, double DominantMag);

public sealed class AnalyticsResult
{
    public List<Alert> Alerts { get; } = [];
    public SpectrumResult? Spectrum { get; set; }
    public double? RmsEma { get; set; }
    public double? RttEma { get; set; }
    public double? RssiEma { get; set; }
    public double? DerivedSpeed { get; set; }
    public double? TrackLengthM { get; set; }
    public double? BatterySlopePerHour { get; set; }
    public double? BatteryEtaMinutes { get; set; }
    public double? RssiSlopePerMinute { get; set; }
}

/// <summary>Per-device streaming analytics: EMA, rolling z-score, DFT spectrum, least-squares trends, Haversine and alerts.</summary>
public sealed class AnalyticsEngine(IConfiguration cfg, TimeProvider clock)
{
    public const double Alpha = 0.3;
    public const int ZWindow = 30;
    public const double ZThreshold = 3;
    private static readonly TimeSpan Cooldown = TimeSpan.FromSeconds(60);
    private readonly ConcurrentDictionary<Guid, DeviceState> _state = new();
    private readonly double[] _band = cfg.GetSection("Alerts:VibrationBandHz").Get<double[]>() is { Length: 2 } b ? b : [4, 6];

    private sealed class DeviceState
    {
        public double? RmsEma, RttEma, RssiEma;
        public readonly Queue<double> Rms = new(), Rtt = new(), Rssi = new();
        public readonly Queue<(long T, double V)> Battery = new(), RssiTrend = new();
        public (long T, double Lat, double Lon)? LastFix;
        public double TrackM;
        public readonly Dictionary<string, DateTimeOffset> LastAlert = [];
    }

    public AnalyticsResult OnReading(Device device, TelemetryPayload p, Reading r)
    {
        var s = _state.GetOrAdd(device.Id, _ => new DeviceState());
        var res = new AnalyticsResult();
        lock (s)
        {
            var t = r.TsDevice;
            if (p.Mot?.Rms is { } rms)
            {
                s.RmsEma = Numeric.EmaStep(s.RmsEma, rms, Alpha);
                Anomaly(s, res, device, s.Rms, rms, "anomaly_motion", "Movimiento anómalo");
            }
            if (p.Net?.Probe?.Rtt is { } rtt)
            {
                s.RttEma = Numeric.EmaStep(s.RttEma, rtt, Alpha);
                Anomaly(s, res, device, s.Rtt, rtt, "anomaly_rtt", "RTT anómalo");
                if (rtt > 500) Raise(s, res, device, "link_rtt", rtt, $"RTT de sondeo alto: {rtt:F0} ms");
            }
            if (p.Net?.Probe?.Loss is > 0.10 and var loss) Raise(s, res, device, "link_loss", loss, $"Pérdida de sondeo: {loss:P0}");

            var rssi = (double?)(p.Wifi?.Rssi ?? p.Cell?.Dbm);
            if (rssi is { } sig)
            {
                s.RssiEma = Numeric.EmaStep(s.RssiEma, sig, Alpha);
                Anomaly(s, res, device, s.Rssi, sig, "anomaly_signal", "Señal anómala");
                if (sig < -80) Raise(s, res, device, "low_signal", sig, $"Señal débil: {sig:F0} dBm");
                res.RssiSlopePerMinute = Trend(s.RssiTrend, t, sig, 60_000);
            }

            if (p.Bat is { } bat)
            {
                var slope = Trend(s.Battery, t, bat.Lvl, 3_600_000);
                res.BatterySlopePerHour = slope;
                if (slope is < -1e-4 && bat.Chg != true) res.BatteryEtaMinutes = bat.Lvl / -slope * 60;
                if (bat.Lvl < 0.15 && bat.Chg != true) Raise(s, res, device, "low_battery", bat.Lvl, $"Batería baja: {bat.Lvl:P0}");
            }

            if (p.Gps is { } g)
            {
                if (s.LastFix is { } f)
                {
                    var d = Numeric.Haversine(f.Lat, f.Lon, g.Lat, g.Lon);
                    var dt = (t - f.T) / 1000.0;
                    s.TrackM += d;
                    if (dt > 0) res.DerivedSpeed = d / dt;
                }
                s.LastFix = (t, g.Lat, g.Lon);
                res.TrackLengthM = s.TrackM;
            }

            if (p.Mot is { Mag: { Length: >= 8 } mag, Fs: { } fs })
            {
                res.Spectrum = Spectrum(mag, fs);
                if (res.Spectrum.DominantHz >= _band[0] && res.Spectrum.DominantHz <= _band[1] && res.Spectrum.DominantMag > 0.05)
                    Raise(s, res, device, "vibration", res.Spectrum.DominantHz, $"Vibración en banda {_band[0]}–{_band[1]} Hz: {res.Spectrum.DominantHz:F1} Hz");
            }
            res.RmsEma = s.RmsEma;
            res.RttEma = s.RttEma;
            res.RssiEma = s.RssiEma;
        }
        return res;
    }

    /// <summary>Spectrum of the acceleration magnitude window with the DC component removed. FFT when N is a power of two, DFT otherwise.</summary>
    public static SpectrumResult Spectrum(double[] samples, double fs)
    {
        var mean = samples.Average();
        var x = samples.Select(v => v - mean).ToArray();
        var mags = (x.Length & (x.Length - 1)) == 0 ? Numeric.FftMagnitudes(x) : Numeric.DftMagnitudes(x);
        var freqs = Enumerable.Range(0, mags.Length).Select(k => k * fs / x.Length).ToArray();
        var best = 1;
        for (var k = 2; k < mags.Length; k++) if (mags[k] > mags[best]) best = k;
        return new SpectrumResult(freqs, mags, mags.Length > 1 ? freqs[best] : 0, mags.Length > 1 ? mags[best] : 0);
    }

    public bool Raise(Guid deviceId, string kind, double? value, string message, out Alert alert)
    {
        var s = _state.GetOrAdd(deviceId, _ => new DeviceState());
        lock (s) return TryRaise(s, deviceId, kind, value, message, out alert);
    }

    private bool TryRaise(DeviceState s, Guid deviceId, string kind, double? value, string message, out Alert alert)
    {
        var now = clock.GetUtcNow();
        alert = new Alert { DeviceId = deviceId, Ts = now.ToUnixTimeMilliseconds(), Kind = kind, Value = value, Message = message };
        if (s.LastAlert.TryGetValue(kind, out var last) && now - last < Cooldown) return false;
        s.LastAlert[kind] = now;
        return true;
    }

    private void Raise(DeviceState s, AnalyticsResult res, Device d, string kind, double value, string message)
    {
        if (TryRaise(s, d.Id, kind, value, message, out var a)) res.Alerts.Add(a);
    }

    private void Anomaly(DeviceState s, AnalyticsResult res, Device d, Queue<double> window, double x, string kind, string label)
    {
        if (window.Count >= 10)
        {
            var z = Numeric.ZScore(window.ToArray(), x);
            if (Math.Abs(z) > ZThreshold) Raise(s, res, d, kind, z, $"{label}: z = {z:F1}");
        }
        window.Enqueue(x);
        while (window.Count > ZWindow) window.Dequeue();
    }

    /// <summary>Least-squares slope over the last 5 minutes, expressed per <paramref name="unitMs"/>.</summary>
    private static double? Trend(Queue<(long T, double V)> q, long t, double v, double unitMs)
    {
        q.Enqueue((t, v));
        while (q.Count > 0 && t - q.Peek().T > 5 * 60_000) q.Dequeue();
        if (q.Count < 5) return null;
        var t0 = q.Peek().T;
        var xs = q.Select(e => (e.T - t0) / unitMs).ToArray();
        var ys = q.Select(e => e.V).ToArray();
        return Numeric.LinearRegression(xs, ys).Slope;
    }

    public void Forget(Guid deviceId) => _state.TryRemove(deviceId, out _);
}
