namespace SignalTrace.Core;

/// <summary>Shannon entropy in bits per byte (0..8).</summary>
public static class Entropy
{
    public static double Shannon(ReadOnlySpan<byte> data)
    {
        if (data.IsEmpty) return 0;
        Span<int> counts = stackalloc int[256];
        foreach (var b in data) counts[b]++;
        double h = 0;
        foreach (var c in counts)
        {
            if (c == 0) continue;
            var p = (double)c / data.Length;
            h -= p * Math.Log2(p);
        }
        return h;
    }
}
