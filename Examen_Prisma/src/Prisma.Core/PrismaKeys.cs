// PrismaKeys.cs — Derivación de claves del algoritmo ABCO.
// A partir de la frase y la sal (PBKDF2-SHA256) se obtienen todos los parámetros secretos:
// la matriz de Hill, los parámetros del César dinámico, el bloque inicial del encadenamiento,
// la semilla del recorrido Fisher–Yates y la clave del HMAC.
using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace Prisma.Core;

/// <summary>
/// Claves de ABCO derivadas de una frase y una sal.
/// A: matriz de Hill 2x2 con det impar; S0, Mult, Inc: César dinámico (LCG con Hull–Dobell);
/// C0: bloque inicial del encadenamiento; Seed: semilla del recorrido; KMac: clave del HMAC.
/// </summary>
/// <param name="A">Matriz de Hill 2×2 invertible en Z_256 (det impar).</param>
/// <param name="S0">Desplazamiento César inicial s₀ en [0, 256).</param>
/// <param name="Mult">Multiplicador a del LCG de desplazamientos (a ≡ 1 mod 4, a ≠ 1).</param>
/// <param name="Inc">Incremento c del LCG de desplazamientos (impar).</param>
/// <param name="C0">Bloque inicial C₀ del encadenamiento XOR.</param>
/// <param name="Seed">Semilla de 64 bits del recorrido Fisher–Yates.</param>
/// <param name="KMac">Clave de 32 bytes del HMAC-SHA256 que autentica el cifrado.</param>
public sealed record PrismaKeys(Matrix2 A, int S0, int Mult, int Inc, (int, int) C0, ulong Seed, byte[] KMac)
{
    /// <summary>Iteraciones de PBKDF2 al derivar desde la frase.</summary>
    /// <remarks>
    /// Encarecen cada intento de un ataque de diccionario contra la frase. Es menor que el de las
    /// contraseñas de la API (<c>Passwords.Iterations</c>) porque se paga en cada ocultar/revelar.
    /// </remarks>
    public const int Pbkdf2Iterations = 100_000;

    /// <summary>Inversa de <see cref="A"/> módulo 256, calculada una vez al construir el registro.</summary>
    public Matrix2 AInv { get; } = ModMath.Inverse(A);

    /// <summary>Deriva todas las claves de ABCO a partir de la frase y la sal.</summary>
    /// <param name="passphrase">Frase secreta del usuario (en el 2FA, su contraseña).</param>
    /// <param name="salt">Sal aleatoria de 16 bytes, guardada en claro en la imagen.</param>
    /// <returns>Las claves derivadas; la misma frase y sal siempre producen las mismas claves.</returns>
    /// <remarks>
    /// La sal hace que la misma frase genere claves distintas en cada imagen. Los 32 bytes k de PBKDF2 se
    /// reparten así: k[4] → s₀, k[5] → a, k[6] → c, k[7..8] → C₀, k[16..23] → semilla; A y KMac se obtienen
    /// con SHA-256(k ‖ etiqueta), con etiquetas distintas para separar dominios. Debe coincidir byte a byte
    /// con el prototipo de Python (ver VectorTests).
    /// </remarks>
    public static PrismaKeys Derive(string passphrase, byte[] salt)
    {
        byte[] k = Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(passphrase), salt, Pbkdf2Iterations, HashAlgorithmName.SHA256, 32);
        Matrix2 a;
        for (byte ctr = 0; ; ctr++)
        {
            // Muestreo con rechazo: se repite hasta obtener det(A) impar (unidad de Z_256).
            // Solo las matrices con det impar tienen inversa módulo 256; la probabilidad es 6/16 = 3/8,
            // así que en promedio bastan menos de 3 intentos. Cada intento usa un contador distinto.
            byte[] h = SHA256.HashData([.. k, .. "hill"u8, ctr]);
            a = new Matrix2(h[0], h[1], h[2], h[3]);
            if ((ModMath.Det(a) & 1) == 1) break;
        }
        // Teorema de Hull–Dobell para m = 2⁸: el LCG s ← (a·s + c) mod m tiene periodo completo (256) si y solo si
        // c es coprimo con m (impar) y a − 1 es múltiplo de 4 (porque 4 divide a m). Así ningún desplazamiento
        // se repite antes de 256 bloques. Se excluye a = 1, que daría un simple contador s₀ + i·c.
        int mult = 4 * (k[5] % 63 + 1) + 1;   // a ≡ 1 (mod 4), a ≠ 1
        int inc = 2 * (k[6] % 128) + 1;       // c impar
        ulong seed = BinaryPrimitives.ReadUInt64LittleEndian(k.AsSpan(16, 8));
        byte[] kmac = SHA256.HashData([.. k, .. "mac"u8]);
        return new PrismaKeys(a, k[4], mult, inc, (k[7], k[8]), seed, kmac);
    }

    /// <summary>Siguiente desplazamiento César: s ← (a·s + c) mod 256.</summary>
    /// <param name="s">Desplazamiento actual sᵢ₋₁.</param>
    /// <returns>El desplazamiento sᵢ en [0, 256).</returns>
    public int NextShift(int s) => (Mult * s + Inc) & 0xFF;
}
