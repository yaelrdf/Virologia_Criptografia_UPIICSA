namespace SignalTrace.Server.Analytics;

/// <summary>Métodos numéricos implementados a mano (PLAN §10), sin bibliotecas matemáticas externas.</summary>
public static class Numeric
{
    /// <summary>
    /// Media móvil simple sobre los últimos <paramref name="window"/> valores. Al inicio, mientras no hay
    /// suficientes valores, promedia los que existan. Mantiene una suma acumulada para ser O(n).
    /// </summary>
    /// <param name="x">Serie de entrada.</param>
    /// <param name="window">Tamaño de la ventana.</param>
    public static double[] Sma(IReadOnlyList<double> x, int window)
    {
        var o = new double[x.Count];
        double sum = 0;
        for (var i = 0; i < x.Count; i++)
        {
            sum += x[i];
            // Saca de la suma el valor que acaba de salir de la ventana.
            if (i >= window) sum -= x[i - window];
            o[i] = sum / Math.Min(i + 1, window);
        }
        return o;
    }

    /// <summary>Media móvil exponencial de toda una serie: e₀ = x₀, eᵢ = α·xᵢ + (1 − α)·eᵢ₋₁.</summary>
    /// <param name="x">Serie de entrada.</param>
    /// <param name="alpha">Factor de suavizado entre 0 y 1.</param>
    public static double[] Ema(IReadOnlyList<double> x, double alpha)
    {
        var o = new double[x.Count];
        for (var i = 0; i < x.Count; i++) o[i] = i == 0 ? x[0] : alpha * x[i] + (1 - alpha) * o[i - 1];
        return o;
    }

    /// <summary>Un paso de la EMA: si no hay valor anterior, el valor nuevo es la media.</summary>
    /// <param name="previous">EMA anterior, o null si es la primera muestra.</param>
    /// <param name="x">Muestra nueva.</param>
    /// <param name="alpha">Factor de suavizado.</param>
    public static double EmaStep(double? previous, double x, double alpha) => previous is { } p ? alpha * x + (1 - alpha) * p : x;

    /// <summary>Interpolación lineal entre (x0, y0) y (x1, y1) evaluada en x.</summary>
    public static double Lerp(double x0, double y0, double x1, double y1, double x)
        => x1 == x0 ? y0 : y0 + (y1 - y0) * (x - x0) / (x1 - x0);

    /// <summary>Punto de una rejilla de tiempo regular.</summary>
    /// <param name="T">Instante en ms.</param>
    /// <param name="Value">Valor, o null si cae en un hueco demasiado largo.</param>
    /// <param name="Interpolated">Si el valor se calculó por interpolación y no se midió.</param>
    public sealed record GridPoint(long T, double? Value, bool Interpolated);

    /// <summary>
    /// Remuestrea muestras irregulares sobre una rejilla de tiempo regular (paso = intervalo nominal).
    /// Los huecos de hasta <paramref name="maxGapSteps"/> pasos se rellenan con interpolación lineal y se marcan;
    /// los huecos más largos quedan vacíos (null) para no inventar datos.
    /// </summary>
    /// <param name="samples">Muestras ordenadas por tiempo.</param>
    /// <param name="stepMs">Paso de la rejilla en ms.</param>
    /// <param name="maxGapSteps">Máximo de pasos que se pueden interpolar.</param>
    public static List<GridPoint> ResampleLinear(IReadOnlyList<(long T, double V)> samples, long stepMs, int maxGapSteps = 5)
    {
        var o = new List<GridPoint>();
        if (samples.Count == 0 || stepMs <= 0) return o;
        // Una muestra a menos de medio paso de un punto de la rejilla se toma como medida en ese punto.
        var tol = stepMs / 2;
        var j = 0;
        for (var t = samples[0].T; t <= samples[^1].T; t += stepMs)
        {
            // Avanza j hasta la última muestra anterior o igual a t.
            while (j + 1 < samples.Count && samples[j + 1].T <= t) j++;
            var (t0, v0) = samples[j];
            if (Math.Abs(t - t0) <= tol) { o.Add(new GridPoint(t, v0, false)); continue; }
            if (j + 1 < samples.Count && Math.Abs(samples[j + 1].T - t) <= tol) { o.Add(new GridPoint(t, samples[j + 1].V, false)); continue; }
            if (j + 1 >= samples.Count) break;
            var (t1, v1) = samples[j + 1];
            // Interpola solo si el hueco entre las dos muestras vecinas no es demasiado largo.
            o.Add((t1 - t0) <= stepMs * (long)(maxGapSteps + 1)
                ? new GridPoint(t, Lerp(t0, v0, t1, v1, t), true)
                : new GridPoint(t, null, false));
        }
        return o;
    }

    /// <summary>Media y desviación estándar poblacional (divide entre n).</summary>
    /// <param name="x">Valores.</param>
    /// <returns>(media, desviación); (0, 0) si no hay valores.</returns>
    public static (double Mean, double Std) MeanStd(IReadOnlyList<double> x)
    {
        if (x.Count == 0) return (0, 0);
        var mean = x.Average();
        var var = x.Sum(v => (v - mean) * (v - mean)) / x.Count;
        return (mean, Math.Sqrt(var));
    }

    /// <summary>Puntuación z = (x − μ) / σ de un valor contra una ventana de referencia (0 cuando σ ≈ 0).</summary>
    /// <param name="window">Valores de referencia.</param>
    /// <param name="x">Valor a evaluar.</param>
    public static double ZScore(IReadOnlyList<double> window, double x)
    {
        var (m, s) = MeanStd(window);
        return s < 1e-9 ? 0 : (x - m) / s;
    }

    /// <summary>
    /// DFT directa, O(n²): X_k = Σ x_n·e^{−2πikn/N}. Devuelve las magnitudes |X_k|/N para k = 0..N/2
    /// (la otra mitad es simétrica porque la señal es real).
    /// </summary>
    /// <param name="x">Señal en el tiempo.</param>
    public static double[] DftMagnitudes(IReadOnlyList<double> x)
    {
        var n = x.Count;
        var half = n / 2;
        var mags = new double[half + 1];
        for (var k = 0; k <= half; k++)
        {
            // Parte real e imaginaria de X_k usando e^{iθ} = cos θ + i·sen θ.
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

    /// <summary>
    /// FFT iterativa radix-2 de Cooley–Tukey, O(n log n). La longitud debe ser potencia de dos.
    /// Da el mismo resultado que <see cref="DftMagnitudes"/>.
    /// </summary>
    /// <param name="x">Señal en el tiempo.</param>
    /// <exception cref="ArgumentException">Si la longitud no es potencia de dos.</exception>
    public static double[] FftMagnitudes(IReadOnlyList<double> x)
    {
        var n = x.Count;
        if (n == 0 || (n & (n - 1)) != 0) throw new ArgumentException("Length must be a power of two.");
        var re = x.ToArray();
        var im = new double[n];
        // 1) Reordena las muestras por inversión de bits del índice, para poder combinar en el lugar.
        for (int i = 1, j = 0; i < n; i++)
        {
            var bit = n >> 1;
            for (; (j & bit) != 0; bit >>= 1) j ^= bit;
            j ^= bit;
            if (i < j) { (re[i], re[j]) = (re[j], re[i]); (im[i], im[j]) = (im[j], im[i]); }
        }
        // 2) Mariposas: combina transformadas de tamaño len/2 en transformadas de tamaño len.
        for (var len = 2; len <= n; len <<= 1)
        {
            var ang = -2 * Math.PI / len;
            for (var i = 0; i < n; i += len)
                for (var k = 0; k < len / 2; k++)
                {
                    // Factor de giro w = e^{−2πik/len}.
                    double wr = Math.Cos(ang * k), wi = Math.Sin(ang * k);
                    int a = i + k, b = i + k + len / 2;
                    double tr = re[b] * wr - im[b] * wi, ti = re[b] * wi + im[b] * wr;
                    re[b] = re[a] - tr; im[b] = im[a] - ti;
                    re[a] += tr; im[a] += ti;
                }
        }
        // 3) Magnitudes normalizadas de la primera mitad del espectro.
        var mags = new double[n / 2 + 1];
        for (var k = 0; k <= n / 2; k++) mags[k] = Math.Sqrt(re[k] * re[k] + im[k] * im[k]) / n;
        return mags;
    }

    /// <summary>
    /// Mínimos cuadrados ordinarios para y = a + b·x con las fórmulas cerradas de las sumas.
    /// </summary>
    /// <param name="x">Valores independientes.</param>
    /// <param name="y">Valores dependientes.</param>
    /// <returns>(ordenada al origen, pendiente, coeficiente de determinación r²).</returns>
    public static (double Intercept, double Slope, double R2) LinearRegression(IReadOnlyList<double> x, IReadOnlyList<double> y)
    {
        var n = x.Count;
        if (n < 2) return (n == 1 ? y[0] : 0, 0, 0);
        double sx = 0, sy = 0, sxx = 0, sxy = 0;
        for (var i = 0; i < n; i++) { sx += x[i]; sy += y[i]; sxx += x[i] * x[i]; sxy += x[i] * y[i]; }
        // Si todos los x son iguales el denominador es 0 y no hay pendiente definida.
        var den = n * sxx - sx * sx;
        if (Math.Abs(den) < 1e-12) return (sy / n, 0, 0);
        var b = (n * sxy - sx * sy) / den;
        var a = (sy - b * sx) / n;
        // r² = 1 − SS_res / SS_tot.
        double ssTot = 0, ssRes = 0, my = sy / n;
        for (var i = 0; i < n; i++) { ssTot += (y[i] - my) * (y[i] - my); ssRes += Math.Pow(y[i] - (a + b * x[i]), 2); }
        return (a, b, ssTot < 1e-12 ? 1 : 1 - ssRes / ssTot);
    }
}
