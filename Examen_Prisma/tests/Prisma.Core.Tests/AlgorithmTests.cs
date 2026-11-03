// AlgorithmTests.cs — Pruebas xUnit de los algoritmos de Prisma.Core (el núcleo de ABCO).
// Cubren las propiedades matemáticas (inversa modular, |GL₂|, periodo del César dinámico,
// permutación del recorrido, gamma incompleta), la capa de cifrado de ABCO, César y su ataque χ²,
// y la esteganografía (ABCO y clásica), incluido el estegoanálisis.
using System.Security.Cryptography;
using System.Text;
using Prisma.Core;

namespace Prisma.Core.Tests;

/// <summary>Propiedades matemáticas en las que se apoya ABCO.</summary>
public class MathTests
{
    /// <summary>
    /// Para 2000 matrices aleatorias con det impar, A·A⁻¹ = I (columna a columna); las de det par deben
    /// rechazarse con <see cref="ArgumentException"/>.
    /// </summary>
    [Fact]
    public void InverseTimesMatrixIsIdentity_ForRandomOddDeterminants()
    {
        var rng = new Random(1);
        int tested = 0;
        while (tested < 2000)
        {
            var a = new Matrix2(rng.Next(256), rng.Next(256), rng.Next(256), rng.Next(256));
            if ((ModMath.Det(a) & 1) == 0) { Assert.Throws<ArgumentException>(() => ModMath.Inverse(a)); continue; }
            var ai = ModMath.Inverse(a);
            Assert.Equal((1, 0), a.Mul(ai.A, ai.C));
            Assert.Equal((0, 1), a.Mul(ai.B, ai.D));
            tested++;
        }
    }

    /// <summary>
    /// Cuenta por fuerza bruta las matrices invertibles sobre Z_16 (k = 4) y comprueba la fórmula que la
    /// documentación usa para el espacio de claves de Hill en Z_256.
    /// </summary>
    [Fact]
    public void GL2CountOverZ16_MatchesFormula()
    {
        int count = 0;
        for (int a = 0; a < 16; a++) for (int b = 0; b < 16; b++) for (int c = 0; c < 16; c++) for (int d = 0; d < 16; d++)
            if (((a * d - b * c) & 1) != 0) count++;
        Assert.Equal((1 << 12) * 6, count);   // |GL₂(ℤ_{2^k})| = 2^{4(k−1)}·6; aquí k = 4 → 2¹²·6; con k = 8: 2²⁸·6
    }

    /// <summary>
    /// Los parámetros derivados cumplen Hull–Dobell (a ≡ 1 mod 4, a ≠ 1, c impar) y, en efecto, la secuencia
    /// de desplazamientos recorre los 256 valores antes de repetirse.
    /// </summary>
    /// <param name="pass">Frase de prueba.</param>
    [Theory]
    [InlineData("uno")]
    [InlineData("dos")]
    [InlineData("contraseña larga 123")]
    public void DynamicCaesarShift_HasFullPeriod256(string pass)
    {
        var k = PrismaKeys.Derive(pass, new byte[16]);
        Assert.Equal(1, k.Mult % 4); Assert.NotEqual(1, k.Mult); Assert.Equal(1, k.Inc % 2);
        var seen = new HashSet<int>(); int s = k.S0;
        for (int i = 0; i < 256; i++) { s = k.NextShift(s); seen.Add(s); }
        Assert.Equal(256, seen.Count);
    }

    /// <summary>El recorrido es una permutación de los canales 128..total−1: sin repetidos y sin tocar la zona de la sal.</summary>
    [Fact]
    public void PathIsAPermutation()
    {
        int[] p = FisherYates.Path(100_000, 12345UL);
        Assert.Equal(100_000 - 128, p.Length);
        Assert.Equal(p.Length, p.Distinct().Count());
        Assert.All(p, x => Assert.InRange(x, 128, 99_999));
    }

    /// <summary>La gamma incompleta y ln Γ coinciden con formas cerradas conocidas (ambas ramas de P(a, x)).</summary>
    [Fact]
    public void IncompleteGamma_MatchesClosedForms()
    {
        foreach (double x in new[] { 0.1, 1, 2.5, 10 })
            Assert.Equal(1 - Math.Exp(-x), Steganalysis.RegularizedGammaP(1, x), 10);   // P(1, x) = 1 − e^{−x}
        Assert.Equal(Math.Log(24), Steganalysis.LogGamma(5), 10);                         // Γ(5) = 4! = 24
    }
}

/// <summary>Pruebas de la capa de cifrado de ABCO (<see cref="PrismaCipher"/>).</summary>
public class CipherTests
{
    /// <summary>Descifrar lo cifrado devuelve el mensaje (recortando el relleno) para 500 mensajes aleatorios de longitud par e impar.</summary>
    [Fact]
    public void RoundTrip_RandomMessages()
    {
        var k = PrismaKeys.Derive("prueba", RandomNumberGenerator.GetBytes(16));
        var rng = new Random(3);
        for (int i = 0; i < 500; i++)
        {
            var m = new byte[rng.Next(1, 300)]; rng.NextBytes(m);
            Assert.Equal(m, PrismaCipher.Decrypt(PrismaCipher.Encrypt(m, k), k)[..m.Length]);
        }
    }

    /// <summary>Gracias al encadenamiento y al César dinámico, 8 bloques claros idénticos ("AA") dan 8 bloques cifrados distintos.</summary>
    [Fact]
    public void EqualBlocksProduceDifferentCiphertext()
    {
        var k = PrismaKeys.Derive("prueba", new byte[16]);
        byte[] c = PrismaCipher.Encrypt(Encoding.ASCII.GetBytes("AAAAAAAAAAAAAAAA"), k);
        Assert.Equal(8, Enumerable.Range(0, 8).Select(i => Convert.ToHexString(c, 2 * i, 2)).Distinct().Count());
    }

    /// <summary>César solo renombra símbolos (misma entropía que el claro); ABCO aplana el histograma (entropía &gt; 7.5 bits/byte).</summary>
    [Fact]
    public void Entropy_CaesarKeepsHistogram_PrismaFlattensIt()
    {
        byte[] text = Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat("la criptografia estudia como proteger la informacion de los mensajes ", 30)));
        var k = PrismaKeys.Derive("prueba", new byte[16]);
        double plain = Metrics.Entropy(text);
        double caesar = Metrics.Entropy(Encoding.UTF8.GetBytes(Caesar.Encrypt(Encoding.UTF8.GetString(text), 3)));
        double prisma = Metrics.Entropy(PrismaCipher.Encrypt(text, k));
        Assert.Equal(plain, caesar, 6);
        Assert.True(prisma > 7.5, $"entropía PRISMA-L = {prisma}");
    }
}

/// <summary>Pruebas del cifrado César, su normalización, la ofuscación y el ataque χ².</summary>
public class CaesarTests
{
    /// <summary>Ejemplo clásico del curso con k = 3.</summary>
    [Fact]
    public void MaterialExample_CRIPTOGRAFIA() => Assert.Equal("FULSWRJUDILD", Caesar.Encrypt("CRIPTOGRAFIA", 3));

    /// <summary>Ejemplo de la guía con k = 3.</summary>
    [Fact]
    public void GuideExample_HOLA() => Assert.Equal("KROD", Caesar.Encrypt("HOLA", 3));

    /// <summary>Ida y vuelta con ambos alfabetos: se conservan mayúsculas, dígitos y signos (el resultado es el texto normalizado).</summary>
    /// <param name="enye">Usar el alfabeto con Ñ.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RoundTripPreservesCaseAndSymbols(bool enye)
    {
        const string t = "Mañana, a las 18:00, ¡en la Biblioteca!";
        Assert.Equal(Caesar.Normalize(t, enye), Caesar.Decrypt(Caesar.Encrypt(t, 11, enye), 11, enye));
    }

    /// <summary>Las vocales con tilde no pasan sin cifrar al resultado (delatarían el texto claro); la Ñ depende del alfabeto.</summary>
    [Fact]
    public void AccentsDoNotLeak()
    {
        Assert.Equal("Informacion cuanto", Caesar.Normalize("Información cuánto", false));
        Assert.Equal("Mañana", Caesar.Normalize("Mañana", true));
        Assert.Equal("Manana", Caesar.Normalize("Mañana", false));
        Assert.DoesNotContain('ó', Caesar.Encrypt("Información", 3));
    }

    /// <summary>El ataque χ² recupera la clave correcta como primer candidato con un texto de unas 100 letras.</summary>
    /// <param name="key">Clave usada para cifrar.</param>
    /// <param name="enye">Usar el alfabeto con Ñ.</param>
    [Theory]
    [InlineData(11, false)]
    [InlineData(3, false)]
    [InlineData(20, true)]
    public void ChiSquareCrackFindsTheKey(int key, bool enye)
    {
        const string plain = "la criptografia estudia como proteger la informacion de los mensajes que se envian entre dos personas";
        var best = CaesarCracker.Crack(Caesar.Encrypt(plain, key, enye), enye)[0];
        Assert.Equal(key, best.Key);
    }

    /// <summary>La ofuscación de la consola admin es reversible sobre todo el ASCII imprimible.</summary>
    [Fact]
    public void ObfuscationRoundTrip() => Assert.Equal("usuario_01@prisma", Caesar.Deobfuscate(Caesar.Obfuscate("usuario_01@prisma")));
}

/// <summary>Pruebas de la esteganografía de ABCO, la clásica y el estegoanálisis χ².</summary>
public class StegoTests
{
    /// <summary>Mensaje con tildes, signos y un emoji (UTF-8 de varios bytes).</summary>
    private const string Msg = "Reunión a las 18:00 en la biblioteca. ¡No faltes! 🦄";

    /// <summary>
    /// Ocultar, codificar a PNG, decodificar y revelar devuelve el mensaje: el PNG conserva los canales
    /// exactos. La traza registra PSNR &gt; 60 dB y las primeras 20 posiciones del recorrido.
    /// </summary>
    [Fact]
    public void Prisma_RoundTripThroughPng()
    {
        var cover = TestImages.Gradient();
        var trace = new PrismaTrace();
        var stego = PrismaStego.Embed(cover, Encoding.UTF8.GetBytes(Msg), "frase-secreta", trace);
        var reloaded = ImageIO.Decode(ImageIO.EncodePng(stego));
        Assert.Equal(stego.Channels, reloaded.Channels);
        Assert.Equal(Msg, Encoding.UTF8.GetString(PrismaStego.Reveal(reloaded, "frase-secreta")));
        Assert.True(trace.Psnr > 60, $"PSNR = {trace.Psnr}");
        Assert.Equal(20, trace.FirstPositions.Count);
    }

    /// <summary>Una frase incorrecta y un solo bit alterado dentro de la trama producen el mismo error genérico.</summary>
    [Fact]
    public void Prisma_WrongPassphraseOrTamperingGivesGenericError()
    {
        var stego = PrismaStego.Embed(TestImages.Gradient(), Encoding.UTF8.GetBytes(Msg), "frase-secreta");
        Assert.Throws<StegoException>(() => PrismaStego.Reveal(stego, "otra-frase"));

        // Se reconstruye el recorrido con la frase correcta para alterar un canal que sí pertenece a la trama.
        var salt = PrismaStego.ReadBits(stego.Channels, i => i, PrismaStego.SaltBytes);
        var path = FisherYates.Path(stego.Channels.Length, PrismaKeys.Derive("frase-secreta", salt).Seed);
        var tampered = stego.Clone();
        tampered.Channels[path[100]] ^= 1;   // un solo bit dentro del recorrido
        Assert.Throws<StegoException>(() => PrismaStego.Reveal(tampered, "frase-secreta"));
    }

    /// <summary>Un mensaje exactamente del tamaño de la capacidad cabe; uno de un byte más se rechaza.</summary>
    [Fact]
    public void Prisma_CapacityIsEnforced()
    {
        var small = TestImages.Gradient(16, 16);
        int cap = PrismaStego.Capacity(small.Channels.Length);
        Assert.NotNull(PrismaStego.Embed(small, new byte[cap], "x"));
        Assert.Throws<InvalidOperationException>(() => PrismaStego.Embed(small, new byte[cap + 1], "x"));
    }

    /// <summary>
    /// La esteganografía clásica la lee cualquiera; en una imagen ABCO, la lectura secuencial del bit
    /// menos significativo no deja ver el texto.
    /// </summary>
    [Fact]
    public void Classic_IsReadableByAnyone_PrismaIsNot()
    {
        var cover = TestImages.Gradient();
        var classic = LsbClassic.Embed(cover, Encoding.UTF8.GetBytes(Msg));
        Assert.Equal(Msg, Encoding.UTF8.GetString(LsbClassic.Extract(classic)));

        var prisma = PrismaStego.Embed(cover, Encoding.UTF8.GetBytes(Msg), "frase-secreta");
        byte[] raw = LsbClassic.RawSequential(prisma, 64);
        Assert.DoesNotContain("Reuni", Encoding.UTF8.GetString(raw));
    }

    /// <summary>
    /// El ataque χ² da probabilidad casi nula en una imagen limpia con pares muy desbalanceados y casi
    /// total cuando la esteganografía clásica la llena por completo.
    /// </summary>
    [Fact]
    public void ChiSquareAttack_DetectsFullSequentialEmbedding()
    {
        var clean = TestImages.EvenOnly();
        var payload = RandomNumberGenerator.GetBytes(LsbClassic.Capacity(clean.Channels.Length));
        var full = LsbClassic.Embed(clean, payload);
        Assert.True(Steganalysis.ChiSquareAttack(clean).EmbeddingProbability < 0.01);
        Assert.True(Steganalysis.ChiSquareAttack(full).EmbeddingProbability > 0.9);
    }

    /// <summary>FitWithin reduce conservando la proporción y devuelve la misma instancia si no hace falta reducir.</summary>
    [Fact]
    public void FitWithinResizesLargeCovers()
    {
        var big = TestImages.Gradient(400, 200);
        var fit = ImageIO.FitWithin(big, 100);
        Assert.Equal((100, 50), (fit.Width, fit.Height));
        Assert.Same(big, ImageIO.FitWithin(big, 1000));
    }
}
