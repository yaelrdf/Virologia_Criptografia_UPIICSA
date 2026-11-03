namespace SignalTrace.Core;

/// <summary>
/// Cálculo de la entropía de Shannon de un bloque de bytes, en bits por byte (de 0 a 8).
/// La consola del administrador la usa para mostrar cómo cada etapa del cifrado vuelve los datos
/// indistinguibles de bytes aleatorios: un texto JSON ronda 4–5 bits/byte y un texto cifrado se acerca a 8.
/// </summary>
public static class Entropy
{
    /// <summary>
    /// Calcula H = −Σ p(b)·log₂ p(b) sobre la frecuencia de cada uno de los 256 valores posibles de byte.
    /// </summary>
    /// <param name="data">Bytes a analizar.</param>
    /// <returns>La entropía en bits por byte; 0 si el bloque está vacío.</returns>
    public static double Shannon(ReadOnlySpan<byte> data)
    {
        if (data.IsEmpty) return 0;
        // Histograma de apariciones de cada valor de byte (en la pila, sin reservar memoria en el heap).
        Span<int> counts = stackalloc int[256];
        foreach (var b in data) counts[b]++;
        double h = 0;
        foreach (var c in counts)
        {
            // Los valores que no aparecen no aportan nada (lím p→0 de p·log p = 0).
            if (c == 0) continue;
            var p = (double)c / data.Length;
            h -= p * Math.Log2(p);
        }
        return h;
    }
}
