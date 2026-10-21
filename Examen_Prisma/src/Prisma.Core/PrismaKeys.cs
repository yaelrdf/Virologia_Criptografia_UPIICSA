using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace Prisma.Core;

/// <summary>
/// Material de clave de PRISMA-L derivado de una frase y una sal.
/// A: matriz de Hill 2x2 con det impar; S0, Mult, Inc: César dinámico (LCG con Hull–Dobell);
/// C0: bloque inicial del encadenamiento; Seed: semilla del recorrido; KMac: clave del HMAC.
/// </summary>
public sealed record PrismaKeys(Matrix2 A, int S0, int Mult, int Inc, (int, int) C0, ulong Seed, byte[] KMac)
{
    public const int Pbkdf2Iterations = 100_000;

    public Matrix2 AInv { get; } = ModMath.Inverse(A);

    public static PrismaKeys Derive(string passphrase, byte[] salt)
    {
        byte[] k = Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(passphrase), salt, Pbkdf2Iterations, HashAlgorithmName.SHA256, 32);
        Matrix2 a;
        for (byte ctr = 0; ; ctr++)
        {
            // Muestreo con rechazo: se repite hasta obtener det(A) impar (unidad de Z_256).
            byte[] h = SHA256.HashData([.. k, .. "hill"u8, ctr]);
            a = new Matrix2(h[0], h[1], h[2], h[3]);
            if ((ModMath.Det(a) & 1) == 1) break;
        }
        int mult = 4 * (k[5] % 63 + 1) + 1;   // a ≡ 1 (mod 4), a ≠ 1
        int inc = 2 * (k[6] % 128) + 1;       // c impar
        ulong seed = BinaryPrimitives.ReadUInt64LittleEndian(k.AsSpan(16, 8));
        byte[] kmac = SHA256.HashData([.. k, .. "mac"u8]);
        return new PrismaKeys(a, k[4], mult, inc, (k[7], k[8]), seed, kmac);
    }

    /// <summary>Siguiente desplazamiento César: s ← (a·s + c) mod 256.</summary>
    public int NextShift(int s) => (Mult * s + Inc) & 0xFF;
}
