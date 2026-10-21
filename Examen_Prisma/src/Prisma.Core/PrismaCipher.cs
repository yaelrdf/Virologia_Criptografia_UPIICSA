namespace Prisma.Core;

/// <summary>
/// Capa de cifrado de PRISMA-L sobre bloques de 2 bytes:
/// sᵢ = a·sᵢ₋₁ + c;  X = Pᵢ ⊕ Cᵢ₋₁;  Y = A·X;  Cᵢ = Y + sᵢ   (todo mod 256).
/// </summary>
public static class PrismaCipher
{
    public static byte[] Encrypt(byte[] plain, PrismaKeys k, PrismaTrace? trace = null)
    {
        byte[] p = plain.Length % 2 == 0 ? plain : [.. plain, 0];
        var output = new byte[p.Length];
        var (prev0, prev1) = k.C0;
        int s = k.S0;
        for (int i = 0; i < p.Length; i += 2)
        {
            s = k.NextShift(s);
            int x = p[i] ^ prev0, y = p[i + 1] ^ prev1;
            var (yx, yy) = k.A.Mul(x, y);
            int c0 = (yx + s) & 0xFF, c1 = (yy + s) & 0xFF;
            trace?.AddBlock(i / 2, p[i], p[i + 1], x, y, yx, yy, s, c0, c1);
            output[i] = (byte)c0; output[i + 1] = (byte)c1;
            (prev0, prev1) = (c0, c1);
        }
        return output;
    }

    public static byte[] Decrypt(byte[] cipher, PrismaKeys k, PrismaTrace? trace = null)
    {
        if (cipher.Length % 2 != 0) throw new ArgumentException("El cifrado debe tener longitud par.");
        var output = new byte[cipher.Length];
        var ai = k.AInv;
        var (prev0, prev1) = k.C0;
        int s = k.S0;
        for (int i = 0; i < cipher.Length; i += 2)
        {
            s = k.NextShift(s);
            int y0 = (cipher[i] - s) & 0xFF, y1 = (cipher[i + 1] - s) & 0xFF;
            var (x, y) = ai.Mul(y0, y1);
            output[i] = (byte)(x ^ prev0); output[i + 1] = (byte)(y ^ prev1);
            trace?.AddBlock(i / 2, output[i], output[i + 1], x, y, y0, y1, s, cipher[i], cipher[i + 1]);
            (prev0, prev1) = (cipher[i], cipher[i + 1]);
        }
        return output;
    }
}
