using System.Collections.Concurrent;
using SignalTrace.Core;
using SignalTrace.Server.Data;

namespace SignalTrace.Server.Analytics;

/// <summary>Espectro de frecuencias de la magnitud de la aceleración.</summary>
/// <param name="Freqs">Frecuencia en Hz de cada componente.</param>
/// <param name="Mags">Magnitud normalizada de cada componente.</param>
/// <param name="DominantHz">Frecuencia con mayor magnitud (sin contar la componente continua).</param>
/// <param name="DominantMag">Magnitud de esa frecuencia dominante.</param>
public sealed record SpectrumResult(double[] Freqs, double[] Mags, double DominantHz, double DominantMag);

/// <summary>Resultados de la analítica para una lectura; se envían al tablero en vivo junto con la lectura.</summary>
public sealed class AnalyticsResult
{
    /// <summary>Espectro del acelerómetro, si el paquete traía la ventana de magnitudes.</summary>
    public SpectrumResult? Spectrum { get; set; }
    /// <summary>Media móvil exponencial del RMS de aceleración.</summary>
    public double? RmsEma { get; set; }
    /// <summary>Media móvil exponencial del tiempo de ida y vuelta (RTT) del sondeo.</summary>
    public double? RttEma { get; set; }
    /// <summary>Media móvil exponencial de la intensidad de señal (Wi-Fi o celular).</summary>
    public double? RssiEma { get; set; }
    /// <summary>Pendiente del nivel de batería por hora (negativa cuando se descarga).</summary>
    public double? BatterySlopePerHour { get; set; }
    /// <summary>Minutos estimados hasta agotar la batería con la pendiente actual.</summary>
    public double? BatteryEtaMinutes { get; set; }
    /// <summary>Pendiente de la señal en dBm por minuto.</summary>
    public double? RssiSlopePerMinute { get; set; }
    /// <summary>Puntuaciones z móviles de la muestra actual (llaves: rms, rtt, rssi). Un |z| &gt; 3 marca una anomalía en las gráficas.</summary>
    public Dictionary<string, double> Z { get; } = [];
}

/// <summary>
/// Analítica en flujo por dispositivo: media móvil exponencial (EMA), puntuación z móvil, espectro por DFT/FFT
/// y tendencias por mínimos cuadrados. Guarda en memoria un pequeño estado por dispositivo.
/// </summary>
public sealed class AnalyticsEngine
{
    /// <summary>Factor de suavizado de la EMA: 0.3 da peso al valor nuevo sin seguir cada pico.</summary>
    public const double Alpha = 0.3;
    /// <summary>Cantidad de muestras anteriores contra las que se calcula la puntuación z.</summary>
    public const int ZWindow = 30;
    /// <summary>Umbral de |z| a partir del cual una muestra se considera anómala.</summary>
    public const double ZThreshold = 3;
    /// <summary>Estado de cada dispositivo, indexado por su id. Es concurrente porque llegan paquetes de varios a la vez.</summary>
    private readonly ConcurrentDictionary<Guid, DeviceState> _state = new();

    /// <summary>Estado acumulado de un dispositivo: últimas EMA, ventanas para la z y series para las tendencias.</summary>
    private sealed class DeviceState
    {
        public double? RmsEma, RttEma, RssiEma;
        public readonly Queue<double> Rms = new(), Rtt = new(), Rssi = new();
        public readonly Queue<(long T, double V)> Battery = new(), RssiTrend = new();
    }

    /// <summary>
    /// Procesa una lectura nueva: actualiza las EMA y las ventanas z, calcula las tendencias de batería y señal
    /// y, si hay ventana de aceleración, su espectro.
    /// </summary>
    /// <param name="device">Dispositivo que envió la lectura.</param>
    /// <param name="p">Carga de telemetría descifrada.</param>
    /// <param name="r">Fila ya guardada (aporta la marca de tiempo del dispositivo).</param>
    public AnalyticsResult OnReading(Device device, TelemetryPayload p, Reading r)
    {
        var s = _state.GetOrAdd(device.Id, _ => new DeviceState());
        var res = new AnalyticsResult();
        // Un candado por dispositivo: dos paquetes del mismo dispositivo no modifican su estado a la vez.
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
            // La señal es el RSSI del Wi-Fi o, si no hay, la potencia celular.
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
                // Solo estima el tiempo restante si la batería baja de forma apreciable y no está cargando.
                if (slope is < -1e-4 && bat.Chg != true) res.BatteryEtaMinutes = bat.Lvl / -slope * 60;
            }
            // El espectro necesita al menos 8 muestras y la frecuencia de muestreo.
            if (p.Mot is { Mag: { Length: >= 8 } mag, Fs: { } fs }) res.Spectrum = Spectrum(mag, fs);
            res.RmsEma = s.RmsEma;
            res.RttEma = s.RttEma;
            res.RssiEma = s.RssiEma;
        }
        return res;
    }

    /// <summary>
    /// Espectro de la ventana de magnitudes de aceleración, sin la componente continua (se resta la media).
    /// Usa FFT cuando N es potencia de dos y DFT directa en otro caso.
    /// </summary>
    /// <param name="samples">Magnitudes de la ventana.</param>
    /// <param name="fs">Frecuencia de muestreo en Hz.</param>
    public static SpectrumResult Spectrum(double[] samples, double fs)
    {
        var mean = samples.Average();
        var x = samples.Select(v => v - mean).ToArray();
        var mags = (x.Length & (x.Length - 1)) == 0 ? Numeric.FftMagnitudes(x) : Numeric.DftMagnitudes(x);
        // El índice k corresponde a la frecuencia k·fs/N.
        var freqs = Enumerable.Range(0, mags.Length).Select(k => k * fs / x.Length).ToArray();
        // Busca el pico desde k = 1 para ignorar la componente continua.
        var best = 1;
        for (var k = 2; k < mags.Length; k++) if (mags[k] > mags[best]) best = k;
        return new SpectrumResult(freqs, mags, mags.Length > 1 ? freqs[best] : 0, mags.Length > 1 ? mags[best] : 0);
    }

    /// <summary>
    /// Calcula z = (x − μ) / σ contra las <see cref="ZWindow"/> muestras anteriores (necesita al menos 10)
    /// y después agrega x a la ventana, descartando la más antigua si se llena.
    /// </summary>
    private static void ZScore(AnalyticsResult res, Queue<double> window, double x, string key)
    {
        if (window.Count >= 10) res.Z[key] = Math.Round(Numeric.ZScore(window.ToArray(), x), 2);
        window.Enqueue(x);
        while (window.Count > ZWindow) window.Dequeue();
    }

    /// <summary>
    /// Las mismas puntuaciones z móviles, pero para una serie guardada (consultas del historial), para que
    /// también se marquen las anomalías antiguas. Devuelve un diccionario por lectura (llaves rms, rtt, rssi),
    /// o null donde todavía no se podía calcular z.
    /// </summary>
    /// <param name="rows">Lecturas en orden cronológico.</param>
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

    /// <summary>
    /// Pendiente por mínimos cuadrados de los últimos 5 minutos, expresada por <paramref name="unitMs"/>
    /// (por ejemplo, por hora o por minuto). Necesita al menos 5 puntos.
    /// </summary>
    /// <param name="q">Serie del dispositivo; se le agrega el punto nuevo y se le quitan los viejos.</param>
    /// <param name="t">Marca de tiempo del punto nuevo (ms).</param>
    /// <param name="v">Valor del punto nuevo.</param>
    /// <param name="unitMs">Duración en ms de la unidad de tiempo de la pendiente.</param>
    private static double? Trend(Queue<(long T, double V)> q, long t, double v, double unitMs)
    {
        q.Enqueue((t, v));
        while (q.Count > 0 && t - q.Peek().T > 5 * 60_000) q.Dequeue();
        if (q.Count < 5) return null;
        // Tiempos relativos al primer punto para evitar números enormes en la regresión.
        var t0 = q.Peek().T;
        var xs = q.Select(e => (e.T - t0) / unitMs).ToArray();
        var ys = q.Select(e => e.V).ToArray();
        return Numeric.LinearRegression(xs, ys).Slope;
    }

    /// <summary>Olvida el estado de un dispositivo (al revocarlo o borrarlo).</summary>
    public void Forget(Guid deviceId) => _state.TryRemove(deviceId, out _);
}
