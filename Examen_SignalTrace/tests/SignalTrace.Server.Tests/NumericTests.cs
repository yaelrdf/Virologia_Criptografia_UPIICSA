using SignalTrace.Server.Analytics;
using SignalTrace.Server.Endpoints;

namespace SignalTrace.Server.Tests;

/// <summary>Numerical methods against cases with a known analytic answer (PLAN §10).</summary>
public class NumericTests
{
    [Fact]
    public void Dft_Sine5Hz_At32Hz_PeaksAtExpectedBin()
    {
        const int n = 64; const double fs = 32, f = 5;
        var x = Enumerable.Range(0, n).Select(i => Math.Sin(2 * Math.PI * f * i / fs)).ToArray();
        var mags = Numeric.DftMagnitudes(x);
        var peak = Array.IndexOf(mags, mags.Max());
        Assert.Equal(10, peak); // bin k = f · N / fs = 5 · 64 / 32
        Assert.Equal(0.5, mags[peak], 6); // |X_k| / N = A / 2
        var sp = AnalyticsEngine.Spectrum(x.Select(v => 9.8 + v).ToArray(), fs);
        Assert.Equal(5.0, sp.DominantHz, 6);
    }

    [Fact]
    public void Fft_Equals_Dft()
    {
        var rnd = new Random(7);
        var x = Enumerable.Range(0, 64).Select(_ => rnd.NextDouble()).ToArray();
        var a = Numeric.DftMagnitudes(x);
        var b = Numeric.FftMagnitudes(x);
        for (var k = 0; k < a.Length; k++) Assert.Equal(a[k], b[k], 9);
    }

    [Fact]
    public void Interpolation_OfALine_HasZeroError_AndFlagsPoints()
    {
        // y = 2t + 1 sampled at 0,1,2,(gap 3,4),5 seconds.
        var samples = new List<(long, double)> { (0, 1), (1000, 3), (2000, 5), (5000, 11) };
        var grid = Numeric.ResampleLinear(samples, 1000);
        Assert.Equal(6, grid.Count);
        foreach (var g in grid) Assert.Equal(2 * g.T / 1000.0 + 1, g.Value!.Value, 9);
        Assert.Equal([false, false, false, true, true, false], grid.Select(g => g.Interpolated).ToArray());
    }

    [Fact]
    public void Interpolation_LeavesLongGapsEmpty()
    {
        var grid = Numeric.ResampleLinear([(0, 0), (10_000, 10)], 1000, maxGapSteps: 5);
        Assert.Contains(grid, g => g.Value is null);
    }

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

    [Fact]
    public void Haversine_KnownDistances()
    {
        // 1° of longitude on the equator = 2πR/360 = 111 194.93 m.
        Assert.Equal(111_194.93, Numeric.Haversine(0, 0, 0, 1), 1);
        // Zócalo CDMX → Ángel de la Independencia ≈ 3.6 km.
        var d = Numeric.Haversine(19.4326, -99.1332, 19.4270, -99.1677);
        Assert.InRange(d, 3500, 3700);
    }

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

    [Fact]
    public void Heatmap_GroupsByCell()
    {
        var cells = Heatmap.Build([(19.43260, -99.13320, -60), (19.43261, -99.13321, -70), (19.44, -99.14, -90)], 25);
        Assert.Equal(2, cells.Count);
        Assert.Contains(cells, c => c.Count == 2 && c.Mean == -65);
    }
}
