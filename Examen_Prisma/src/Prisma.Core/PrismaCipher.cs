// PrismaCipher.cs — Capa de cifrado del algoritmo ABCO (el "César matricial").
// Cada bloque de 2 bytes se encadena con el bloque cifrado anterior (XOR), se multiplica por la
// matriz de Hill A y se le suma un desplazamiento César que cambia en cada bloque según un LCG.
// PrismaStego usa esta clase para producir el cifrado que luego se oculta en la portadora.
namespace Prisma.Core;

/// <summary>
/// Capa de cifrado de ABCO sobre bloques de 2 bytes:
/// sᵢ = a·sᵢ₋₁ + c;  X = Pᵢ ⊕ Cᵢ₋₁;  Y = A·X;  Cᵢ = Y + sᵢ   (todo mod 256).
/// </summary>
/// <remarks>
/// Cada capa corrige una debilidad de la anterior: Hill solo es lineal (cae con dos bloques conocidos),
/// el desplazamiento dinámico lo vuelve afín y distinto en cada posición, y el encadenamiento XOR hace
/// que bloques claros iguales produzcan cifrados distintos. La difusión es local y solo hacia adelante:
/// cambiar un bloque altera ese bloque y los siguientes, nunca los anteriores.
/// </remarks>
public static class PrismaCipher
{
    /// <summary>Cifra un mensaje con las claves de ABCO.</summary>
    /// <param name="plain">Texto claro en bytes.</param>
    /// <param name="k">Claves derivadas de la frase y la sal.</param>
    /// <param name="trace">Traza opcional para la consola admin; registra los primeros bloques.</param>
    /// <returns>Cifrado de longitud par (el mensaje se rellena con un byte 0 si su longitud es impar).</returns>
    public static byte[] Encrypt(byte[] plain, PrismaKeys k, PrismaTrace? trace = null)
    {
        // Relleno a longitud par: la longitud real viaja en la trama, así que el byte extra se descarta al revelar.
        byte[] p = plain.Length % 2 == 0 ? plain : [.. plain, 0];
        var output = new byte[p.Length];
        var (prev0, prev1) = k.C0;   // C₀: bloque inicial del encadenamiento (hace de vector de inicialización)
        int s = k.S0;
        for (int i = 0; i < p.Length; i += 2)
        {
            // El desplazamiento avanza antes de usarse: el primer bloque emplea s₁ = a·s₀ + c.
            s = k.NextShift(s);
            int x = p[i] ^ prev0, y = p[i + 1] ^ prev1;
            var (yx, yy) = k.A.Mul(x, y);
            // El mismo desplazamiento sᵢ se suma a los dos bytes del bloque.
            int c0 = (yx + s) & 0xFF, c1 = (yy + s) & 0xFF;
            trace?.AddBlock(i / 2, p[i], p[i + 1], x, y, yx, yy, s, c0, c1);
            output[i] = (byte)c0; output[i + 1] = (byte)c1;
            (prev0, prev1) = (c0, c1);
        }
        return output;
    }

    /// <summary>Descifra invirtiendo los pasos en orden contrario: Y = Cᵢ − sᵢ;  X = A⁻¹·Y;  Pᵢ = X ⊕ Cᵢ₋₁.</summary>
    /// <param name="cipher">Cifrado de longitud par.</param>
    /// <param name="k">Las mismas claves usadas al cifrar.</param>
    /// <param name="trace">Traza opcional para la consola admin.</param>
    /// <returns>Texto claro con el posible byte de relleno incluido; el llamador lo recorta a la longitud real.</returns>
    /// <exception cref="ArgumentException">Si la longitud del cifrado es impar.</exception>
    public static byte[] Decrypt(byte[] cipher, PrismaKeys k, PrismaTrace? trace = null)
    {
        if (cipher.Length % 2 != 0) throw new ArgumentException("El cifrado debe tener longitud par.");
        var output = new byte[cipher.Length];
        var ai = k.AInv;
        var (prev0, prev1) = k.C0;
        int s = k.S0;
        for (int i = 0; i < cipher.Length; i += 2)
        {
            // La secuencia de desplazamientos se regenera desde s₀ igual que al cifrar.
            s = k.NextShift(s);
            int y0 = (cipher[i] - s) & 0xFF, y1 = (cipher[i + 1] - s) & 0xFF;
            var (x, y) = ai.Mul(y0, y1);
            output[i] = (byte)(x ^ prev0); output[i + 1] = (byte)(y ^ prev1);
            trace?.AddBlock(i / 2, output[i], output[i + 1], x, y, y0, y1, s, cipher[i], cipher[i + 1]);
            // El encadenamiento usa el bloque cifrado recibido, no el descifrado.
            (prev0, prev1) = (cipher[i], cipher[i + 1]);
        }
        return output;
    }
}
