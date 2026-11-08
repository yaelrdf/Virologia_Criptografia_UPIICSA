// VectorTests.cs — Pruebas de compatibilidad con el prototipo de referencia en Python.
// vectors.json lo genera tools/prototipo/gen_vectors.py; si el C# y el prototipo divergen en un solo
// byte (derivación de claves, cifrado, LCG o recorrido), estas pruebas fallan. Incluye además el
// ejemplo resuelto a mano de la documentación.
using System.Text;
using System.Text.Json;
using Prisma.Core;

namespace Prisma.Core.Tests;

/// <summary>El C# debe producir exactamente los mismos valores que el prototipo de Python.</summary>
public class VectorTests
{
    /// <summary>Un caso de prueba por cada elemento de "casos" en vectors.json, pasado como JSON crudo.</summary>
    /// <returns>Argumentos para <see cref="MatchesPythonPrototype"/>.</returns>
    /// <remarks>Se pasa el texto y no el <see cref="JsonElement"/> porque el documento se libera al terminar el método.</remarks>
    public static IEnumerable<object[]> Cases()
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "vectors.json")));
        foreach (var c in doc.RootElement.GetProperty("casos").EnumerateArray())
            yield return [c.GetRawText()];
    }

    /// <summary>
    /// Compara cada parámetro derivado de la frase y la sal (A, A⁻¹, s₀, a, c, C₀, semilla, KMac), el cifrado
    /// del mensaje, los primeros valores del LCG y las primeras 40 posiciones del recorrido.
    /// </summary>
    /// <param name="json">Un caso de vectors.json.</param>
    [Theory, MemberData(nameof(Cases))]
    public void MatchesPythonPrototype(string json)
    {
        var c = JsonDocument.Parse(json).RootElement;
        int[] Ints(string name) => [.. c.GetProperty(name).EnumerateArray().Select(e => e.GetInt32())];

        var k = PrismaKeys.Derive(c.GetProperty("frase").GetString()!, Convert.FromHexString(c.GetProperty("sal").GetString()!));
        var a = Ints("A"); var ai = Ints("Ainv"); var c0 = Ints("c0");
        Assert.Equal(new Matrix2(a[0], a[1], a[2], a[3]), k.A);
        Assert.Equal(new Matrix2(ai[0], ai[1], ai[2], ai[3]), k.AInv);
        Assert.Equal(c.GetProperty("s0").GetInt32(), k.S0);
        Assert.Equal(c.GetProperty("a").GetInt32(), k.Mult);
        Assert.Equal(c.GetProperty("c").GetInt32(), k.Inc);
        Assert.Equal((c0[0], c0[1]), k.C0);
        // La semilla viaja como cadena porque un entero de 64 bits sin signo no cabe con exactitud en un número JSON.
        Assert.Equal(ulong.Parse(c.GetProperty("semilla").GetString()!), k.Seed);
        Assert.Equal(c.GetProperty("kmac").GetString(), Convert.ToHexString(k.KMac).ToLowerInvariant());

        byte[] msg = Encoding.UTF8.GetBytes(c.GetProperty("mensaje").GetString()!);
        byte[] cipher = PrismaCipher.Encrypt(msg, k);
        Assert.Equal(c.GetProperty("cifrado").GetString(), Convert.ToHexString(cipher).ToLowerInvariant());
        Assert.Equal(msg, PrismaCipher.Decrypt(cipher, k)[..msg.Length]);

        var g = new Lcg64(k.Seed);
        Assert.Equal(c.GetProperty("lcg32").EnumerateArray().Select(e => e.GetUInt32()), Enumerable.Range(0, 5).Select(_ => g.Next32()));
        Assert.Equal(Ints("recorrido1000"), FisherYates.Path(1000, k.Seed)[..40]);
    }

    /// <summary>Ejemplo "Hi" calculado a mano: det, inverso modular, A⁻¹, cifrado y descifrado de un bloque.</summary>
    [Fact]
    public void HandWorkedExample_Hi()
    {
        // Ejemplo de la documentación: A = [[3,2],[5,7]], s₀ = 7, a = 5, c = 3, C₀ = (10, 20), "Hi" = (72, 105).
        // det = 3·7 − 2·5 = 11; 11·163 = 1793 = 7·256 + 1, así que 11⁻¹ ≡ 163 (mod 256).
        var k = new PrismaKeys(new Matrix2(3, 2, 5, 7), 7, 5, 3, (10, 20), 0, []);
        Assert.Equal(11, ModMath.Det(k.A));
        Assert.Equal(163, ModMath.InvMod(11));
        Assert.Equal(new Matrix2(117, 186, 209, 233), k.AInv);
        Assert.Equal(new byte[] { 230, 219 }, PrismaCipher.Encrypt("Hi"u8.ToArray(), k));
        Assert.Equal("Hi"u8.ToArray(), PrismaCipher.Decrypt([230, 219], k));
    }
}
