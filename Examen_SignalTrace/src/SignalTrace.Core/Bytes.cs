using System.Buffers.Binary;
using System.Text;

namespace SignalTrace.Core;

/// <summary>Small byte helpers shared by every component (hex, concat, big-endian integers).</summary>
public static class Bytes
{
    public static string ToHex(ReadOnlySpan<byte> data) => Convert.ToHexStringLower(data);

    public static byte[] FromHex(string hex) => Convert.FromHexString(hex);

    public static byte[] Utf8(string s) => Encoding.UTF8.GetBytes(s);

    public static byte[] U64Be(ulong value)
    {
        var b = new byte[8];
        BinaryPrimitives.WriteUInt64BigEndian(b, value);
        return b;
    }

    public static byte[] Concat(params byte[][] parts)
    {
        var total = 0;
        foreach (var p in parts) total += p.Length;
        var result = new byte[total];
        var offset = 0;
        foreach (var p in parts)
        {
            Buffer.BlockCopy(p, 0, result, offset, p.Length);
            offset += p.Length;
        }
        return result;
    }

    /// <summary>Lower-case GUID text with hyphens (36 chars), the canonical form used in every concatenation.</summary>
    public static string DeviceIdText(Guid id) => id.ToString("D");
}
