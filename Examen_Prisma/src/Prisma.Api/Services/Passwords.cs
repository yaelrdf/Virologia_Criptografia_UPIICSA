using System.Security.Cryptography;
using System.Text;

namespace Prisma.Api.Services;

/// <summary>Contraseñas con PBKDF2-HMAC-SHA256 y sal por usuario (recomendación de la guía base).</summary>
public static class Passwords
{
    public const int Iterations = 600_000;

    public static (byte[] Hash, byte[] Salt) Hash(string password)
    {
        byte[] salt = RandomNumberGenerator.GetBytes(16);
        return (Derive(password, salt), salt);
    }

    public static bool Verify(string password, byte[] hash, byte[] salt) =>
        CryptographicOperations.FixedTimeEquals(Derive(password, salt), hash);

    private static byte[] Derive(string password, byte[] salt) =>
        Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(password), salt, Iterations, HashAlgorithmName.SHA256, 32);
}
