using System.Text;
using System.Text.Json;
using Prisma.Core;

namespace Prisma.Core.Tests;

/// <summary>El C# debe producir exactamente los mismos valores que el prototipo de Python.</summary>
public class VectorTests
{
    public static IEnumerable<object[]> Cases()
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "vectors.json")));
        foreach (var c in doc.RootElement.GetProperty("casos").EnumerateArray())
            yield return [c.GetRawText()];
    }

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

    [Fact]
    public void HandWorkedExample_Hi()
    {
        // Ejemplo de la documentación: A = [[3,2],[5,7]], s₀ = 7, a = 5, c = 3, C₀ = (10, 20), "Hi" = (72, 105).
        var k = new PrismaKeys(new Matrix2(3, 2, 5, 7), 7, 5, 3, (10, 20), 0, []);
        Assert.Equal(11, ModMath.Det(k.A));
        Assert.Equal(163, ModMath.InvMod(11));
        Assert.Equal(new Matrix2(117, 186, 209, 233), k.AInv);
        Assert.Equal(new byte[] { 230, 219 }, PrismaCipher.Encrypt("Hi"u8.ToArray(), k));
        Assert.Equal("Hi"u8.ToArray(), PrismaCipher.Decrypt([230, 219], k));
    }
}
