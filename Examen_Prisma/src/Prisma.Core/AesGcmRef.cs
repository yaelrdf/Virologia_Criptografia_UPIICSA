using System.Security.Cryptography;
using System.Text;

namespace Prisma.Core;

/// <summary>
/// AES-256-GCM (algoritmo comercial de referencia). Salida: sal(16) ‖ nonce(12) ‖ C ‖ etiqueta(16).
/// La clave se deriva de la frase con PBKDF2-SHA256, igual que en PRISMA-L, para comparar en igualdad.
/// </summary>
public static class AesGcmRef
{
    public static byte[] Encrypt(byte[] plain, string passphrase)
    {
        byte[] salt = RandomNumberGenerator.GetBytes(16), nonce = RandomNumberGenerator.GetBytes(12);
        byte[] key = DeriveKey(passphrase, salt);
        byte[] c = new byte[plain.Length], tag = new byte[16];
        using (var aes = new AesGcm(key, 16)) aes.Encrypt(nonce, plain, c, tag);
        return [.. salt, .. nonce, .. c, .. tag];
    }

    public static byte[] Decrypt(byte[] blob, string passphrase)
    {
        if (blob.Length < 44) throw new CryptographicException("Datos demasiado cortos.");
        byte[] salt = blob[..16], nonce = blob[16..28], c = blob[28..^16], tag = blob[^16..];
        byte[] plain = new byte[c.Length];
        using var aes = new AesGcm(DeriveKey(passphrase, salt), 16);
        aes.Decrypt(nonce, c, tag, plain);   // lanza AuthenticationTagMismatchException si hubo alteración
        return plain;
    }

    private static byte[] DeriveKey(string passphrase, byte[] salt) =>
        Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(passphrase), salt, PrismaKeys.Pbkdf2Iterations, HashAlgorithmName.SHA256, 32);
}
