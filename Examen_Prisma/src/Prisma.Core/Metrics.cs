using System.Numerics;

namespace Prisma.Core;

/// <summary>Métricas numéricas usadas para evaluar los algoritmos.</summary>
public static class Metrics
{
    /// <summary>Entropía de Shannon en bits por byte: H = −Σ pᵢ·log₂ pᵢ (máximo 8).</summary>
    public static double Entropy(ReadOnlySpan<byte> data)
    {
        if (data.Length == 0) return 0;
        Span<int> f = stackalloc int[256];
        foreach (byte b in data) f[b]++;
        double h = 0;
        foreach (int c in f)
            if (c > 0) { double p = (double)c / data.Length; h -= p * Math.Log2(p); }
        return h;
    }

    public static double Mse(byte[] a, byte[] b)
    {
        if (a.Length != b.Length) throw new ArgumentException("Tamaños distintos.");
        double s = 0;
        for (int i = 0; i < a.Length; i++) { int d = a[i] - b[i]; s += d * d; }
        return s / a.Length;
    }

    /// <summary>PSNR = 10·log₁₀(255² / MSE) en dB. Infinito si las imágenes son idénticas.</summary>
    public static double Psnr(byte[] a, byte[] b)
    {
        double mse = Mse(a, b);
        return mse == 0 ? double.PositiveInfinity : 10 * Math.Log10(255.0 * 255.0 / mse);
    }

    /// <summary>Mapa de diferencias |original − estego| amplificado ×255 (en gris).</summary>
    public static RgbImage DiffMap(RgbImage a, RgbImage b)
    {
        var o = new byte[a.Channels.Length];
        for (int p = 0; p < o.Length; p += 3)
        {
            bool diff = a.Channels[p] != b.Channels[p] || a.Channels[p + 1] != b.Channels[p + 1] || a.Channels[p + 2] != b.Channels[p + 2];
            o[p] = o[p + 1] = o[p + 2] = diff ? (byte)255 : (byte)0;
        }
        return new RgbImage(a.Width, a.Height, o);
    }

    /// <summary>
    /// Avalancha estimada por Monte Carlo: se cambia 1 bit aleatorio del texto claro y se mide la fracción
    /// de bits que cambian en el cifrado (ideal ≈ 0.5 para un cifrador moderno).
    /// </summary>
    public static double Avalanche(Func<byte[], byte[]> encrypt, int messageBytes, int trials, Random rng)
    {
        double sum = 0;
        for (int t = 0; t < trials; t++)
        {
            var m = new byte[messageBytes]; rng.NextBytes(m);
            var m2 = (byte[])m.Clone();
            int bit = rng.Next(messageBytes * 8);
            m2[bit >> 3] ^= (byte)(1 << (bit & 7));
            byte[] c1 = encrypt(m), c2 = encrypt(m2);
            int n = Math.Min(c1.Length, c2.Length), diff = 0;
            for (int i = 0; i < n; i++) diff += BitOperations.PopCount((uint)(c1[i] ^ c2[i]));
            sum += (double)diff / (n * 8);
        }
        return sum / trials;
    }
}

/// <summary>
/// Ataque χ² de Westfeld–Pfitzmann contra LSB por reemplazo (métodos numéricos):
/// compara la frecuencia de cada par de valores (2k, 2k+1) y obtiene la probabilidad de que haya
/// un mensaje mediante la función gamma incompleta regularizada.
/// </summary>
public static class Steganalysis
{
    public sealed record ChiResult(double ChiSquare, int Dof, double EmbeddingProbability);

    /// <summary>Analiza la fracción inicial (0..1] de los canales en orden secuencial.</summary>
    public static ChiResult ChiSquareAttack(RgbImage img, double fraction = 1.0)
    {
        int n = Math.Max(1, (int)(img.Channels.Length * fraction));
        var h = new int[256];
        for (int i = 0; i < n; i++) h[img.Channels[i]]++;
        double chi = 0; int dof = -1;
        for (int k = 0; k < 128; k++)
        {
            double expected = (h[2 * k] + h[2 * k + 1]) / 2.0;
            if (expected < 5) continue;   // regla práctica de validez de χ²
            double d = h[2 * k] - expected;
            chi += d * d / expected;
            dof++;
        }
        if (dof < 1) return new ChiResult(chi, 0, 0);
        // p = 1 − P(dof/2, χ²/2): probabilidad de que los pares estén "igualados" por la inserción.
        double p = 1 - RegularizedGammaP(dof / 2.0, chi / 2.0);
        return new ChiResult(Math.Round(chi, 2), dof, Math.Clamp(p, 0, 1));
    }

    /// <summary>P(a, x) por serie (x &lt; a+1) o fracción continua de Lentz (x ≥ a+1). Numerical Recipes §6.2.</summary>
    public static double RegularizedGammaP(double a, double x)
    {
        if (x <= 0) return 0;
        double gln = LogGamma(a);
        if (x < a + 1)
        {
            double ap = a, sum = 1 / a, del = sum;
            for (int i = 0; i < 1000; i++)
            {
                ap++; del *= x / ap; sum += del;
                if (Math.Abs(del) < Math.Abs(sum) * 1e-15) break;
            }
            return sum * Math.Exp(-x + a * Math.Log(x) - gln);
        }
        double b = x + 1 - a, c = 1 / 1e-300, d = 1 / b, hh = d;
        for (int i = 1; i < 1000; i++)
        {
            double an = -i * (i - a);
            b += 2;
            d = an * d + b; if (Math.Abs(d) < 1e-300) d = 1e-300;
            c = b + an / c; if (Math.Abs(c) < 1e-300) c = 1e-300;
            d = 1 / d;
            double del = d * c; hh *= del;
            if (Math.Abs(del - 1) < 1e-15) break;
        }
        return 1 - Math.Exp(-x + a * Math.Log(x) - gln) * hh;
    }

    /// <summary>ln Γ(x) por la aproximación de Lanczos (g = 7, 9 coeficientes).</summary>
    public static double LogGamma(double x)
    {
        double[] g = [0.99999999999980993, 676.5203681218851, -1259.1392167224028, 771.32342877765313,
            -176.61502916214059, 12.507343278686905, -0.13857109526572012, 9.9843695780195716e-6, 1.5056327351493116e-7];
        if (x < 0.5) return Math.Log(Math.PI / Math.Abs(Math.Sin(Math.PI * x))) - LogGamma(1 - x);
        x -= 1;
        double s = g[0];
        for (int i = 1; i < 9; i++) s += g[i] / (x + i);
        double t = x + 7.5;
        return 0.5 * Math.Log(2 * Math.PI) + (x + 0.5) * Math.Log(t) - t + Math.Log(s);
    }
}
