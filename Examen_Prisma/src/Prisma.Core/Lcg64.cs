namespace Prisma.Core;

/// <summary>Generador congruencial lineal de 64 bits (constantes MMIX de Knuth). Determinista, no criptográfico.</summary>
public sealed class Lcg64(ulong seed)
{
    private ulong _x = seed;

    public uint Next32()
    {
        unchecked { _x = _x * 6364136223846793005UL + 1442695040888963407UL; }
        return (uint)(_x >> 32);
    }

    /// <summary>Entero uniforme en [0, n) por muestreo con rechazo (evita el sesgo de módulo).</summary>
    public int Uniform(int n)
    {
        ulong limit = (4294967296UL / (ulong)n) * (ulong)n;
        while (true)
        {
            uint v = Next32();
            if (v < limit) return (int)(v % (uint)n);
        }
    }
}

public static class FisherYates
{
    /// <summary>Permutación de los índices [start, total) barajados con Fisher–Yates y un LCG sembrado.</summary>
    public static int[] Path(int total, ulong seed, int start = 128)
    {
        var idx = new int[total - start];
        for (int i = 0; i < idx.Length; i++) idx[i] = start + i;
        var g = new Lcg64(seed);
        for (int i = idx.Length - 1; i > 0; i--)
        {
            int j = g.Uniform(i + 1);
            (idx[i], idx[j]) = (idx[j], idx[i]);
        }
        return idx;
    }
}
