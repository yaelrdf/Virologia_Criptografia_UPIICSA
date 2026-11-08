// Passwords.cs — Almacenamiento seguro de contraseñas de la API.
// Las contraseñas nunca se guardan: se guarda una derivación PBKDF2 con sal propia de cada usuario,
// y la verificación compara en tiempo constante.
using System.Security.Cryptography;
using System.Text;

namespace Prisma.Api.Services;

/// <summary>Contraseñas con PBKDF2-HMAC-SHA256 y sal por usuario.</summary>
/// <remarks>
/// PBKDF2 aplica HMAC-SHA256 muchas veces seguidas: verificar una contraseña cuesta poco a un usuario,
/// pero multiplica el costo de cada intento en un ataque de diccionario si la base de datos se filtra.
/// La sal aleatoria impide usar tablas precalculadas y hace que dos usuarios con la misma contraseña
/// tengan derivaciones distintas.
/// </remarks>
public static class Passwords
{
    /// <summary>Iteraciones de PBKDF2 (600 000, el valor recomendado por OWASP para HMAC-SHA256).</summary>
    public const int Iterations = 600_000;

    /// <summary>Deriva una contraseña nueva con una sal aleatoria de 16 B.</summary>
    /// <param name="password">Contraseña en claro.</param>
    /// <returns>La derivación (32 B) y la sal, para guardarlas en el usuario.</returns>
    public static (byte[] Hash, byte[] Salt) Hash(string password)
    {
        byte[] salt = RandomNumberGenerator.GetBytes(16);
        return (Derive(password, salt), salt);
    }

    /// <summary>Comprueba una contraseña contra la derivación guardada.</summary>
    /// <param name="password">Contraseña recibida.</param>
    /// <param name="hash">Derivación guardada.</param>
    /// <param name="salt">Sal guardada.</param>
    /// <returns>true si coincide.</returns>
    /// <remarks>
    /// <c>FixedTimeEquals</c> recorre todos los bytes aunque el primero ya difiera, así que el tiempo de
    /// respuesta no indica cuántos bytes coincidieron.
    /// </remarks>
    public static bool Verify(string password, byte[] hash, byte[] salt) =>
        CryptographicOperations.FixedTimeEquals(Derive(password, salt), hash);

    /// <summary>PBKDF2-HMAC-SHA256 de la contraseña en UTF-8 con la sal dada; produce 32 B.</summary>
    private static byte[] Derive(string password, byte[] salt) =>
        Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(password), salt, Iterations, HashAlgorithmName.SHA256, 32);
}
