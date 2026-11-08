// Lcg64.cs — Generador pseudoaleatorio determinista y recorrido de píxeles de ABCO.
// El recorrido Fisher–Yates decide en qué canales de la portadora se escriben los bits de la
// trama; como se siembra con la clave derivada de la frase, solo quien conoce la frase puede
// reconstruir el mismo orden. El prototipo de Python reproduce exactamente esta secuencia.
namespace Prisma.Core;

/// <summary>Generador congruencial lineal de 64 bits (constantes MMIX de Knuth). Determinista, no criptográfico.</summary>
/// <remarks>
/// x ← (6364136223846793005·x + 1442695040888963407) mod 2⁶⁴. El módulo 2⁶⁴ sale gratis del desbordamiento
/// de <c>ulong</c>. Los bits bajos de un LCG con módulo potencia de 2 tienen periodos cortos, por eso
/// <see cref="Next32"/> devuelve solo los 32 bits altos, que son los de mejor calidad estadística.
/// </remarks>
/// <param name="seed">Semilla inicial (en ABCO, <see cref="PrismaKeys.Seed"/>).</param>
public sealed class Lcg64(ulong seed)
{
    /// <summary>Estado interno de 64 bits.</summary>
    private ulong _x = seed;

    /// <summary>Avanza el estado un paso y devuelve sus 32 bits altos.</summary>
    /// <returns>Entero sin signo de 32 bits.</returns>
    public uint Next32()
    {
        // unchecked: el desbordamiento es intencional y realiza la reducción mod 2⁶⁴.
        unchecked { _x = _x * 6364136223846793005UL + 1442695040888963407UL; }
        return (uint)(_x >> 32);
    }

    /// <summary>Entero uniforme en [0, n) por muestreo con rechazo (evita el sesgo de módulo).</summary>
    /// <param name="n">Cota superior exclusiva (n ≥ 1).</param>
    /// <returns>Entero en [0, n) con distribución uniforme.</returns>
    /// <remarks>
    /// Tomar <c>v % n</c> directamente favorecería a los residuos pequeños cuando 2³² no es múltiplo de n.
    /// Se acepta v solo si cae por debajo de <c>limit</c>, el mayor múltiplo de n que no excede 2³²;
    /// en ese rango cada residuo aparece exactamente el mismo número de veces. La probabilidad de
    /// rechazo es menor que n / 2³², así que casi nunca se repite.
    /// </remarks>
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

/// <summary>Recorrido pseudoaleatorio de canales para la capa esteganográfica de ABCO.</summary>
public static class FisherYates
{
    /// <summary>Permutación de los índices [start, total) barajados con Fisher–Yates y un LCG sembrado.</summary>
    /// <param name="total">Número total de canales de la imagen (ancho × alto × 3).</param>
    /// <param name="seed">Semilla derivada de la frase y la sal.</param>
    /// <param name="start">Primer canal elegible; los canales 0..127 se reservan para la sal.</param>
    /// <returns>Arreglo de <c>total − start</c> índices distintos: el orden en que se escriben los bits de la trama.</returns>
    /// <remarks>
    /// Variante de Durstenfeld: para i de m−1 a 1 se intercambia idx[i] con idx[j], j uniforme en [0, i].
    /// Con un j uniforme, las m! permutaciones son equiprobables (tanto como lo permita el generador).
    /// </remarks>
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
