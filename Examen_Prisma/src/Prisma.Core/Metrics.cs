// Metrics.cs — Métricas numéricas y estegoanálisis de ABCO.
// Entropía, MSE/PSNR, mapa de diferencias y avalancha se usan en el comparador de la API, en la
// traza de la consola admin y en tools/Prisma.Metrics (docs/metricas.md). El ataque χ² de
// Westfeld–Pfitzmann mide qué tan detectable es el ocultamiento en el bit menos significativo.
using System.Numerics;

namespace Prisma.Core;

/// <summary>Métricas numéricas usadas para evaluar los algoritmos.</summary>
public static class Metrics
{
    /// <summary>Entropía de Shannon en bits por byte: H = −Σ pᵢ·log₂ pᵢ (máximo 8).</summary>
    /// <param name="data">Bytes a analizar.</param>
    /// <returns>Entropía en [0, 8]; 0 para una secuencia vacía. Un buen cifrado se acerca a 8; el texto en español ronda 4.</returns>
    public static double Entropy(ReadOnlySpan<byte> data)
    {
        if (data.Length == 0) return 0;
        // Histograma de los 256 valores posibles; stackalloc evita reservar memoria en el montón.
        Span<int> f = stackalloc int[256];
        foreach (byte b in data) f[b]++;
        double h = 0;
        foreach (int c in f)
            if (c > 0) { double p = (double)c / data.Length; h -= p * Math.Log2(p); }
        return h;
    }

    /// <summary>Error cuadrático medio entre dos arreglos de canales: MSE = (1/N)·Σ (aᵢ − bᵢ)².</summary>
    /// <param name="a">Canales de la imagen original.</param>
    /// <param name="b">Canales de la imagen modificada.</param>
    /// <returns>MSE promediado sobre todos los canales.</returns>
    /// <exception cref="ArgumentException">Si los arreglos tienen longitudes distintas.</exception>
    public static double Mse(byte[] a, byte[] b)
    {
        if (a.Length != b.Length) throw new ArgumentException("Tamaños distintos.");
        double s = 0;
        for (int i = 0; i < a.Length; i++) { int d = a[i] - b[i]; s += d * d; }
        return s / a.Length;
    }

    /// <summary>PSNR = 10·log₁₀(255² / MSE) en dB. Infinito si las imágenes son idénticas.</summary>
    /// <param name="a">Canales de la portadora.</param>
    /// <param name="b">Canales de la imagen estego.</param>
    /// <returns>Relación señal a ruido pico en decibeles.</returns>
    /// <remarks>
    /// 255 es el valor máximo de un canal de 8 bits. Cambiar el bit menos significativo altera cada canal
    /// en a lo sumo ±1, así que MSE ≤ 1 y PSNR ≥ 48 dB; con mensajes cortos supera con facilidad 60 dB,
    /// por encima del umbral habitual de imperceptibilidad (≈ 40 dB).
    /// </remarks>
    public static double Psnr(byte[] a, byte[] b)
    {
        double mse = Mse(a, b);
        return mse == 0 ? double.PositiveInfinity : 10 * Math.Log10(255.0 * 255.0 / mse);
    }

    /// <summary>Mapa de diferencias entre la portadora y la imagen estego, amplificado para que se vea.</summary>
    /// <param name="a">Portadora original.</param>
    /// <param name="b">Imagen estego del mismo tamaño.</param>
    /// <returns>Imagen en blanco y negro: blanco donde algún canal del píxel cambió, negro donde no.</returns>
    /// <remarks>
    /// Las diferencias reales son de ±1 y serían invisibles; por eso cualquier cambio se lleva a 255.
    /// En la consola admin muestra cómo ABCO dispersa los bits por toda la imagen.
    /// </remarks>
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
    /// <param name="encrypt">Función de cifrado a evaluar (con la clave ya fijada).</param>
    /// <param name="messageBytes">Longitud de los mensajes aleatorios.</param>
    /// <param name="trials">Número de ensayos.</param>
    /// <param name="rng">Generador para mensajes y posiciones (con semilla fija, el resultado es reproducible).</param>
    /// <returns>Fracción promedio de bits de salida que cambian, en [0, 1].</returns>
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
            // Distancia de Hamming entre ambos cifrados, normalizada por el número de bits comparados.
            int n = Math.Min(c1.Length, c2.Length), diff = 0;
            for (int i = 0; i < n; i++) diff += BitOperations.PopCount((uint)(c1[i] ^ c2[i]));
            sum += (double)diff / (n * 8);
        }
        return sum / trials;
    }
}

/// <summary>
/// Ataque χ² de Westfeld–Pfitzmann contra el reemplazo del bit menos significativo (métodos numéricos):
/// compara la frecuencia de cada par de valores (2k, 2k+1) y obtiene la probabilidad de que haya
/// un mensaje mediante la función gamma incompleta regularizada.
/// </summary>
/// <remarks>
/// Reemplazar el bit menos significativo con bits de mensaje (que parecen aleatorios) convierte 2k en 2k+1
/// y viceversa con probabilidad ½, así que las frecuencias de cada par tienden a igualarse. En una imagen
/// natural suelen ser distintas. Un χ² pequeño respecto a la media del par indica pares "igualados" y,
/// por tanto, una alta probabilidad de mensaje oculto.
/// </remarks>
public static class Steganalysis
{
    /// <summary>Resultado del ataque.</summary>
    /// <param name="ChiSquare">Estadístico χ² (redondeado a 2 decimales).</param>
    /// <param name="Dof">Grados de libertad: número de pares válidos − 1.</param>
    /// <param name="EmbeddingProbability">Probabilidad estimada de que la región analizada contenga un mensaje, en [0, 1].</param>
    public sealed record ChiResult(double ChiSquare, int Dof, double EmbeddingProbability);

    /// <summary>Analiza la fracción inicial (0..1] de los canales en orden secuencial.</summary>
    /// <param name="img">Imagen a analizar.</param>
    /// <param name="fraction">Fracción inicial de canales que se examina (p. ej. 0.10 para el 10 % inicial).</param>
    /// <returns>χ², grados de libertad y probabilidad de mensaje.</returns>
    /// <remarks>
    /// Analizar solo el inicio detecta bien la esteganografía clásica, que escribe en orden desde el canal 0;
    /// ABCO reparte los bits por toda la imagen y, con mensajes pequeños, no satura ninguna región.
    /// </remarks>
    public static ChiResult ChiSquareAttack(RgbImage img, double fraction = 1.0)
    {
        int n = Math.Max(1, (int)(img.Channels.Length * fraction));
        var h = new int[256];
        for (int i = 0; i < n; i++) h[img.Channels[i]]++;
        double chi = 0; int dof = -1;
        for (int k = 0; k < 128; k++)
        {
            // Frecuencia esperada si hubiera mensaje: la media del par (2k, 2k+1).
            double expected = (h[2 * k] + h[2 * k + 1]) / 2.0;
            if (expected < 5) continue;   // regla práctica de validez de χ²
            double d = h[2 * k] - expected;
            chi += d * d / expected;
            dof++;   // empieza en −1: grados de libertad = categorías válidas − 1
        }
        if (dof < 1) return new ChiResult(chi, 0, 0);
        // p = 1 − P(dof/2, χ²/2): probabilidad de que los pares estén "igualados" por la inserción.
        // Es la cola superior de la distribución χ² con dof grados de libertad: F(χ²; k) = P(k/2, χ²/2).
        double p = 1 - RegularizedGammaP(dof / 2.0, chi / 2.0);
        return new ChiResult(Math.Round(chi, 2), dof, Math.Clamp(p, 0, 1));
    }

    /// <summary>P(a, x) por serie (x &lt; a+1) o fracción continua de Lentz (x ≥ a+1). Numerical Recipes §6.2.</summary>
    /// <param name="a">Parámetro de forma (a &gt; 0).</param>
    /// <param name="x">Límite superior de integración (x ≥ 0).</param>
    /// <returns>Función gamma incompleta inferior regularizada P(a, x) = γ(a, x) / Γ(a), en [0, 1].</returns>
    /// <remarks>
    /// Cada método converge rápido en su región: la serie cuando x es pequeño respecto a a, y la fracción
    /// continua (que calcula Q = 1 − P) cuando x es grande. Ambos se detienen al alcanzar precisión de 1e-15.
    /// </remarks>
    public static double RegularizedGammaP(double a, double x)
    {
        if (x <= 0) return 0;
        double gln = LogGamma(a);
        if (x < a + 1)
        {
            // Serie: P(a, x) = e^{−x}·x^a / Γ(a+1) · Σₙ xⁿ / ((a+1)(a+2)…(a+n)).
            // "del" es el término actual y "sum" la suma acumulada (dividida entre a).
            double ap = a, sum = 1 / a, del = sum;
            for (int i = 0; i < 1000; i++)
            {
                ap++; del *= x / ap; sum += del;
                if (Math.Abs(del) < Math.Abs(sum) * 1e-15) break;
            }
            // El prefactor se evalúa en escala logarítmica para no desbordar con a o x grandes.
            return sum * Math.Exp(-x + a * Math.Log(x) - gln);
        }
        // Fracción continua para Q(a, x) con el método de Lentz modificado:
        // Q = e^{−x}·x^a / Γ(a) · 1/(x+1−a− 1·(1−a)/(x+3−a− 2·(2−a)/(x+5−a− …))).
        // 1e-300 sustituye a los ceros para evitar divisiones entre cero (FPMIN en Numerical Recipes).
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
    /// <param name="x">Argumento real distinto de 0, −1, −2, …</param>
    /// <returns>Logaritmo natural de |Γ(x)|, con precisión cercana a 15 dígitos.</returns>
    /// <remarks>
    /// Γ(z+1) ≈ √(2π)·t^{z+½}·e^{−t}·A_g(z), con t = z + g + ½ y A_g(z) = c₀ + Σ cᵢ/(z+i).
    /// Se trabaja con el logaritmo para que Γ, que crece más rápido que la exponencial, no desborde.
    /// </remarks>
    public static double LogGamma(double x)
    {
        double[] g = [0.99999999999980993, 676.5203681218851, -1259.1392167224028, 771.32342877765313,
            -176.61502916214059, 12.507343278686905, -0.13857109526572012, 9.9843695780195716e-6, 1.5056327351493116e-7];
        // Fórmula de reflexión de Euler, Γ(x)·Γ(1−x) = π / sen(πx): la aproximación solo es precisa para x ≥ ½.
        if (x < 0.5) return Math.Log(Math.PI / Math.Abs(Math.Sin(Math.PI * x))) - LogGamma(1 - x);
        x -= 1;   // la fórmula aproxima Γ(z+1); con z = x − 1 se obtiene Γ(x)
        double s = g[0];
        for (int i = 1; i < 9; i++) s += g[i] / (x + i);
        double t = x + 7.5;   // t = z + g + ½ con g = 7
        return 0.5 * Math.Log(2 * Math.PI) + (x + 0.5) * Math.Log(t) - t + Math.Log(s);
    }
}
