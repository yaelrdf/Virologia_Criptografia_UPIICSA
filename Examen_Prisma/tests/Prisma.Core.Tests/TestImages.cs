using Prisma.Core;

namespace Prisma.Core.Tests;

internal static class TestImages
{
    /// <summary>Imagen sintética "natural": degradado suave con poco ruido.</summary>
    public static RgbImage Gradient(int w = 256, int h = 192, int seed = 7)
    {
        var rng = new Random(seed);
        var ch = new byte[w * h * 3];
        for (int y = 0, o = 0; y < h; y++)
            for (int x = 0; x < w; x++, o += 3)
            {
                ch[o] = (byte)Math.Clamp(x * 255 / w + rng.Next(-3, 4), 0, 255);
                ch[o + 1] = (byte)Math.Clamp(y * 255 / h + rng.Next(-3, 4), 0, 255);
                ch[o + 2] = (byte)Math.Clamp((x + y) * 255 / (w + h) + rng.Next(-3, 4), 0, 255);
            }
        return new RgbImage(w, h, ch);
    }

    /// <summary>Imagen con todos los canales pares: histograma muy desbalanceado en los pares (2k, 2k+1).</summary>
    public static RgbImage EvenOnly(int w = 256, int h = 256)
    {
        var ch = new byte[w * h * 3];
        for (int i = 0; i < ch.Length; i++) ch[i] = (byte)(2 * ((i / 3 % w + i / 3 / w) % 128));
        return new RgbImage(w, h, ch);
    }
}
