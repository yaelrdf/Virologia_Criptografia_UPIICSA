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

/// <summary>Byte-wise Caesar shift: an insecure classical cipher shown in the console only as a didactic contrast.</summary>
public static class Caesar
{
    public static byte[] Encrypt(ReadOnlySpan<byte> data, int shift)
    {
        var o = new byte[data.Length];
        for (var i = 0; i < data.Length; i++) o[i] = (byte)((data[i] + shift) & 0xFF);
        return o;
    }

    public static byte[] Decrypt(ReadOnlySpan<byte> data, int shift) => Encrypt(data, -shift);
}
