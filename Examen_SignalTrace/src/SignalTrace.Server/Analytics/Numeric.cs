namespace SignalTrace.Server.Analytics;

/// <summary>Numerical methods implemented by hand (PLAN §10). No external math libraries.</summary>
public static class Numeric
{
    /// <summary>Simple moving average over the last <paramref name="window"/> values (shorter at the start).</summary>
    public static double[] Sma(IReadOnlyList<double> x, int window)
    {
        var o = new double[x.Count];
        double sum = 0;
        for (var i = 0; i < x.Count; i++)
        {
            sum += x[i];
            if (i >= window) sum -= x[i - window];
            o[i] = sum / Math.Min(i + 1, window);
        }
        return o;
    }

    /// <summary>Exponential moving average: e_0 = x_0, e_i = α x_i + (1 − α) e_{i−1}.</summary>
    public static double[] Ema(IReadOnlyList<double> x, double alpha)
    {
        var o = new double[x.Count];
        for (var i = 0; i < x.Count; i++) o[i] = i == 0 ? x[0] : alpha * x[i] + (1 - alpha) * o[i - 1];
        return o;
    }

    public static double EmaStep(double? previous, double x, double alpha) => previous is { } p ? alpha * x + (1 - alpha) * p : x;

    /// <summary>Linear interpolation between (x0, y0) and (x1, y1).</summary>
    public static double Lerp(double x0, double y0, double x1, double y1, double x)
        => x1 == x0 ? y0 : y0 + (y1 - y0) * (x - x0) / (x1 - x0);

    public sealed record GridPoint(long T, double? Value, bool Interpolated);

    /// <summary>
    /// Resamples irregular samples onto a regular time grid (step = nominal interval). Gaps up to
    /// <paramref name="maxGapSteps"/> steps are filled by linear interpolation and flagged; longer gaps stay empty (null).
    /// </summary>
    public static List<GridPoint> ResampleLinear(IReadOnlyList<(long T, double V)> samples, long stepMs, int maxGapSteps = 5)
    {
        var o = new List<GridPoint>();
        if (samples.Count == 0 || stepMs <= 0) return o;
        var tol = stepMs / 2;
        var j = 0;
        for (var t = samples[0].T; t <= samples[^1].T; t += stepMs)
        {
            while (j + 1 < samples.Count && samples[j + 1].T <= t) j++;
            var (t0, v0) = samples[j];
            if (Math.Abs(t - t0) <= tol) { o.Add(new GridPoint(t, v0, false)); continue; }
            if (j + 1 < samples.Count && Math.Abs(samples[j + 1].T - t) <= tol) { o.Add(new GridPoint(t, samples[j + 1].V, false)); continue; }
            if (j + 1 >= samples.Count) break;
            var (t1, v1) = samples[j + 1];
            o.Add((t1 - t0) <= stepMs * (long)(maxGapSteps + 1)
                ? new GridPoint(t, Lerp(t0, v0, t1, v1, t), true)
                : new GridPoint(t, null, false));
        }
        return o;
    }

    public static (double Mean, double Std) MeanStd(IReadOnlyList<double> x)
    {
        if (x.Count == 0) return (0, 0);
        var mean = x.Average();
        var var = x.Sum(v => (v - mean) * (v - mean)) / x.Count;
        return (mean, Math.Sqrt(var));
    }

    /// <summary>z = (x − μ) / σ of a value against a reference window (0 when σ ≈ 0).</summary>
    public static double ZScore(IReadOnlyList<double> window, double x)
    {
        var (m, s) = MeanStd(window);
        return s < 1e-9 ? 0 : (x - m) / s;
    }

    /// <summary>Direct DFT, O(n²): X_k = Σ x_n e^{−2πikn/N}. Returns magnitudes |X_k|/N for k = 0..N/2.</summary>
    public static double[] DftMagnitudes(IReadOnlyList<double> x)
    {
        var n = x.Count;
        var half = n / 2;
        var mags = new double[half + 1];
        for (var k = 0; k <= half; k++)
        {
            double re = 0, im = 0;
            for (var t = 0; t < n; t++)
            {
                var ang = -2 * Math.PI * k * t / n;
                re += x[t] * Math.Cos(ang);
                im += x[t] * Math.Sin(ang);
            }
            mags[k] = Math.Sqrt(re * re + im * im) / n;
        }
        return mags;
    }

    /// <summary>Iterative radix-2 Cooley–Tukey FFT (length must be a power of two). Same output as <see cref="DftMagnitudes"/>.</summary>
    public static double[] FftMagnitudes(IReadOnlyList<double> x)
    {
        var n = x.Count;
        if (n == 0 || (n & (n - 1)) != 0) throw new ArgumentException("Length must be a power of two.");
        var re = x.ToArray();
        var im = new double[n];
        for (int i = 1, j = 0; i < n; i++)
        {
            var bit = n >> 1;
            for (; (j & bit) != 0; bit >>= 1) j ^= bit;
            j ^= bit;
            if (i < j) { (re[i], re[j]) = (re[j], re[i]); (im[i], im[j]) = (im[j], im[i]); }
        }
        for (var len = 2; len <= n; len <<= 1)
        {
            var ang = -2 * Math.PI / len;
            for (var i = 0; i < n; i += len)
                for (var k = 0; k < len / 2; k++)
                {
                    double wr = Math.Cos(ang * k), wi = Math.Sin(ang * k);
                    int a = i + k, b = i + k + len / 2;
                    double tr = re[b] * wr - im[b] * wi, ti = re[b] * wi + im[b] * wr;
                    re[b] = re[a] - tr; im[b] = im[a] - ti;
                    re[a] += tr; im[a] += ti;
                }
        }
        var mags = new double[n / 2 + 1];
        for (var k = 0; k <= n / 2; k++) mags[k] = Math.Sqrt(re[k] * re[k] + im[k] * im[k]) / n;
        return mags;
    }

    /// <summary>Ordinary least squares y = a + b x. Returns (intercept, slope, r²).</summary>
    public static (double Intercept, double Slope, double R2) LinearRegression(IReadOnlyList<double> x, IReadOnlyList<double> y)
    {
        var n = x.Count;
        if (n < 2) return (n == 1 ? y[0] : 0, 0, 0);
        double sx = 0, sy = 0, sxx = 0, sxy = 0;
        for (var i = 0; i < n; i++) { sx += x[i]; sy += y[i]; sxx += x[i] * x[i]; sxy += x[i] * y[i]; }
        var den = n * sxx - sx * sx;
        if (Math.Abs(den) < 1e-12) return (sy / n, 0, 0);
        var b = (n * sxy - sx * sy) / den;
        var a = (sy - b * sx) / n;
        double ssTot = 0, ssRes = 0, my = sy / n;
        for (var i = 0; i < n; i++) { ssTot += (y[i] - my) * (y[i] - my); ssRes += Math.Pow(y[i] - (a + b * x[i]), 2); }
        return (a, b, ssTot < 1e-12 ? 1 : 1 - ssRes / ssTot);
    }
}
