using SignalTrace.Server.Analytics;

namespace SignalTrace.Server.Tests;

/// <summary>Métodos numéricos contra casos con respuesta analítica conocida (PLAN §10).</summary>
public class NumericTests
{
    /// <summary>Una senoidal de 5 Hz muestreada a 32 Hz tiene su pico en el índice esperado y con magnitud A/2.</summary>
    [Fact]
    public void Dft_Sine5Hz_At32Hz_PeaksAtExpectedBin()
    {
        const int n = 64; const double fs = 32, f = 5;
        var x = Enumerable.Range(0, n).Select(i => Math.Sin(2 * Math.PI * f * i / fs)).ToArray();
        var mags = Numeric.DftMagnitudes(x);
        var peak = Array.IndexOf(mags, mags.Max());
        Assert.Equal(10, peak); // índice k = f · N / fs = 5 · 64 / 32
        Assert.Equal(0.5, mags[peak], 6); // |X_k| / N = A / 2
        // Con un desplazamiento como la gravedad, el espectro (sin componente continua) sigue dando 5 Hz.
        var sp = AnalyticsEngine.Spectrum(x.Select(v => 9.8 + v).ToArray(), fs);
        Assert.Equal(5.0, sp.DominantHz, 6);
    }

    /// <summary>La FFT da el mismo resultado que la DFT directa sobre datos aleatorios.</summary>
    [Fact]
    public void Fft_Equals_Dft()
    {
        var rnd = new Random(7);
        var x = Enumerable.Range(0, 64).Select(_ => rnd.NextDouble()).ToArray();
        var a = Numeric.DftMagnitudes(x);
        var b = Numeric.FftMagnitudes(x);
        for (var k = 0; k < a.Length; k++) Assert.Equal(a[k], b[k], 9);
    }

    /// <summary>Interpolar una recta no tiene error, y los puntos interpolados quedan marcados.</summary>
    [Fact]
    public void Interpolation_OfALine_HasZeroError_AndFlagsPoints()
    {
        // y = 2t + 1 muestreada a los 0, 1, 2, (hueco en 3 y 4) y 5 segundos.
        var samples = new List<(long, double)> { (0, 1), (1000, 3), (2000, 5), (5000, 11) };
        var grid = Numeric.ResampleLinear(samples, 1000);
        Assert.Equal(6, grid.Count);
        foreach (var g in grid) Assert.Equal(2 * g.T / 1000.0 + 1, g.Value!.Value, 9);
        Assert.Equal([false, false, false, true, true, false], grid.Select(g => g.Interpolated).ToArray());
    }

    /// <summary>Un hueco más largo que el máximo permitido queda vacío.</summary>
    [Fact]
    public void Interpolation_LeavesLongGapsEmpty()
    {
        var grid = Numeric.ResampleLinear([(0, 0), (10_000, 10)], 1000, maxGapSteps: 5);
        Assert.Contains(grid, g => g.Value is null);
    }

    /// <summary>La regresión sobre una recta exacta recupera ordenada, pendiente y r² = 1.</summary>
    [Fact]
    public void Regression_ExactLine()
    {
        double[] x = [0, 1, 2, 3, 4];
        var y = x.Select(v => -0.5 * v + 80).ToArray();
        var (a, b, r2) = Numeric.LinearRegression(x, y);
        Assert.Equal(80, a, 9);
        Assert.Equal(-0.5, b, 9);
        Assert.Equal(1, r2, 9);
    }

    /// <summary>Valores calculados a mano de la media móvil simple, la exponencial y la puntuación z.</summary>
    [Fact]
    public void Sma_Ema_ZScore()
    {
        double[] x = [1, 2, 3, 4, 5];
        Assert.Equal([1, 1.5, 2, 3, 4], Numeric.Sma(x, 3));
        var e = Numeric.Ema(x, 0.5);
        Assert.Equal(1, e[0]);
        Assert.Equal(1.5, e[1]);
        Assert.Equal(2.25, e[2]);
        var window = Enumerable.Repeat(10.0, 9).Append(12.0).ToArray(); // μ = 10.2, σ = 0.6
        Assert.Equal(3.0, Numeric.ZScore(window, 12.0), 6);
    }
}
