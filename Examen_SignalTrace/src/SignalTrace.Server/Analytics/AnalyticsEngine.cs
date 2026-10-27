using System.Collections.Concurrent;
using SignalTrace.Core;
using SignalTrace.Server.Data;

namespace SignalTrace.Server.Analytics;

public sealed record SpectrumResult(double[] Freqs, double[] Mags, double DominantHz, double DominantMag);

public sealed class AnalyticsResult
{
    public SpectrumResult? Spectrum { get; set; }
    public double? RmsEma { get; set; }
    public double? RttEma { get; set; }
    public double? RssiEma { get; set; }
    public double? BatterySlopePerHour { get; set; }
    public double? BatteryEtaMinutes { get; set; }
    public double? RssiSlopePerMinute { get; set; }
    /// <summary>Rolling z-scores of the current sample (keys: rms, rtt, rssi). |z| &gt; 3 marks an anomaly on the charts.</summary>
    public Dictionary<string, double> Z { get; } = [];
}

/// <summary>Per-device streaming analytics: EMA, rolling z-score, DFT/FFT spectrum and least-squares trends.</summary>
public sealed class AnalyticsEngine
{
    public const double Alpha = 0.3;
    public const int ZWindow = 30;
    public const double ZThreshold = 3;
    private readonly ConcurrentDictionary<Guid, DeviceState> _state = new();

    private sealed class DeviceState
    {
        public double? RmsEma, RttEma, RssiEma;
        public readonly Queue<double> Rms = new(), Rtt = new(), Rssi = new();
        public readonly Queue<(long T, double V)> Battery = new(), RssiTrend = new();
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
                ZScore(res, s.Rms, rms, "rms");
            }
            if (p.Net?.Probe?.Rtt is { } rtt)
            {
                s.RttEma = Numeric.EmaStep(s.RttEma, rtt, Alpha);
                ZScore(res, s.Rtt, rtt, "rtt");
            }
            var rssi = (double?)(p.Wifi?.Rssi ?? p.Cell?.Dbm);
            if (rssi is { } sig)
            {
                s.RssiEma = Numeric.EmaStep(s.RssiEma, sig, Alpha);
                ZScore(res, s.Rssi, sig, "rssi");
                res.RssiSlopePerMinute = Trend(s.RssiTrend, t, sig, 60_000);
            }
            if (p.Bat is { } bat)
            {
                var slope = Trend(s.Battery, t, bat.Lvl, 3_600_000);
                res.BatterySlopePerHour = slope;
                if (slope is < -1e-4 && bat.Chg != true) res.BatteryEtaMinutes = bat.Lvl / -slope * 60;
            }
            if (p.Mot is { Mag: { Length: >= 8 } mag, Fs: { } fs }) res.Spectrum = Spectrum(mag, fs);
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

    /// <summary>z = (x − μ) / σ against the previous ZWindow samples (needs at least 10), then x joins the window.</summary>
    private static void ZScore(AnalyticsResult res, Queue<double> window, double x, string key)
    {
        if (window.Count >= 10) res.Z[key] = Math.Round(Numeric.ZScore(window.ToArray(), x), 2);
        window.Enqueue(x);
        while (window.Count > ZWindow) window.Dequeue();
    }

    /// <summary>
    /// The same rolling z-scores for a stored series (history queries), so old anomalies are marked too.
    /// Returns one dictionary per reading (keys rms, rtt, rssi), or null where no z could be computed yet.
    /// </summary>
    public static List<Dictionary<string, double>?> RollingZ(IReadOnlyList<Reading> rows)
    {
        var windows = new Dictionary<string, Queue<double>> { ["rms"] = new(), ["rtt"] = new(), ["rssi"] = new() };
        var result = new List<Dictionary<string, double>?>(rows.Count);
        foreach (var r in rows)
        {
            var res = new AnalyticsResult();
            if (r.AccRms is { } rms) ZScore(res, windows["rms"], rms, "rms");
            if (r.ProbeRtt is { } rtt) ZScore(res, windows["rtt"], rtt, "rtt");
            if ((r.WifiRssi ?? r.CellDbm) is { } sig) ZScore(res, windows["rssi"], sig, "rssi");
            result.Add(res.Z.Count > 0 ? res.Z : null);
        }
        return result;
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
