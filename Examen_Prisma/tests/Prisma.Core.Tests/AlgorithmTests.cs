using System.Security.Cryptography;
using System.Text;
using Prisma.Core;

namespace Prisma.Core.Tests;

public class MathTests
{
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

    [Fact]
    public void GL2CountOverZ16_MatchesFormula()
    {
        int count = 0;
        for (int a = 0; a < 16; a++) for (int b = 0; b < 16; b++) for (int c = 0; c < 16; c++) for (int d = 0; d < 16; d++)
            if (((a * d - b * c) & 1) != 0) count++;
        Assert.Equal((1 << 12) * 6, count);   // |GL₂(ℤ_{2^k})| = 2^{4(k−1)}·6 → con k = 8: 2²⁸·6
    }

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

    [Fact]
    public void PathIsAPermutation()
    {
        int[] p = FisherYates.Path(100_000, 12345UL);
        Assert.Equal(100_000 - 128, p.Length);
        Assert.Equal(p.Length, p.Distinct().Count());
        Assert.All(p, x => Assert.InRange(x, 128, 99_999));
    }

    [Fact]
    public void IncompleteGamma_MatchesClosedForms()
    {
        foreach (double x in new[] { 0.1, 1, 2.5, 10 })
            Assert.Equal(1 - Math.Exp(-x), Steganalysis.RegularizedGammaP(1, x), 10);   // P(1, x) = 1 − e^{−x}
        Assert.Equal(Math.Log(24), Steganalysis.LogGamma(5), 10);                         // Γ(5) = 4! = 24
    }
}

public class CipherTests
{
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

    [Fact]
    public void EqualBlocksProduceDifferentCiphertext()
    {
        var k = PrismaKeys.Derive("prueba", new byte[16]);
        byte[] c = PrismaCipher.Encrypt(Encoding.ASCII.GetBytes("AAAAAAAAAAAAAAAA"), k);
        Assert.Equal(8, Enumerable.Range(0, 8).Select(i => Convert.ToHexString(c, 2 * i, 2)).Distinct().Count());
    }

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

    [Fact]
    public void AesGcm_RoundTripAndTamperDetection()
    {
        byte[] m = "mensaje secreto"u8.ToArray();
        byte[] blob = AesGcmRef.Encrypt(m, "frase");
        Assert.Equal(m, AesGcmRef.Decrypt(blob, "frase"));
        blob[30] ^= 1;
        Assert.ThrowsAny<CryptographicException>(() => AesGcmRef.Decrypt(blob, "frase"));
        Assert.ThrowsAny<CryptographicException>(() => AesGcmRef.Decrypt(AesGcmRef.Encrypt(m, "frase"), "otra"));
    }
}

public class CaesarTests
{
    [Fact]
    public void MaterialExample_CRIPTOGRAFIA() => Assert.Equal("FULSWRJUDILD", Caesar.Encrypt("CRIPTOGRAFIA", 3));

    [Fact]
    public void GuideExample_HOLA() => Assert.Equal("KROD", Caesar.Encrypt("HOLA", 3));

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RoundTripPreservesCaseAndSymbols(bool enye)
    {
        const string t = "Mañana, a las 18:00, ¡en la Biblioteca!";
        Assert.Equal(Caesar.Normalize(t, enye), Caesar.Decrypt(Caesar.Encrypt(t, 11, enye), 11, enye));
    }

    [Fact]
    public void AccentsDoNotLeak()
    {
        Assert.Equal("Informacion cuanto", Caesar.Normalize("Información cuánto", false));
        Assert.Equal("Mañana", Caesar.Normalize("Mañana", true));
        Assert.Equal("Manana", Caesar.Normalize("Mañana", false));
        Assert.DoesNotContain('ó', Caesar.Encrypt("Información", 3));
    }

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

    [Fact]
    public void ObfuscationRoundTrip() => Assert.Equal("usuario_01@prisma", Caesar.Deobfuscate(Caesar.Obfuscate("usuario_01@prisma")));
}

public class StegoTests
{
    private const string Msg = "Reunión a las 18:00 en la biblioteca. ¡No faltes! 🦄";

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

    [Fact]
    public void Prisma_WrongPassphraseOrTamperingGivesGenericError()
    {
        var stego = PrismaStego.Embed(TestImages.Gradient(), Encoding.UTF8.GetBytes(Msg), "frase-secreta");
        Assert.Throws<StegoException>(() => PrismaStego.Reveal(stego, "otra-frase"));

        var salt = PrismaStego.ReadBits(stego.Channels, i => i, PrismaStego.SaltBytes);
        var path = FisherYates.Path(stego.Channels.Length, PrismaKeys.Derive("frase-secreta", salt).Seed);
        var tampered = stego.Clone();
        tampered.Channels[path[100]] ^= 1;   // un solo bit dentro del recorrido
        Assert.Throws<StegoException>(() => PrismaStego.Reveal(tampered, "frase-secreta"));
    }

    [Fact]
    public void Prisma_CapacityIsEnforced()
    {
        var small = TestImages.Gradient(16, 16);
        int cap = PrismaStego.Capacity(small.Channels.Length);
        Assert.NotNull(PrismaStego.Embed(small, new byte[cap], "x"));
        Assert.Throws<InvalidOperationException>(() => PrismaStego.Embed(small, new byte[cap + 1], "x"));
    }

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

    [Fact]
    public void ChiSquareAttack_DetectsFullSequentialEmbedding()
    {
        var clean = TestImages.EvenOnly();
        var payload = RandomNumberGenerator.GetBytes(LsbClassic.Capacity(clean.Channels.Length));
        var full = LsbClassic.Embed(clean, payload);
        Assert.True(Steganalysis.ChiSquareAttack(clean).EmbeddingProbability < 0.01);
        Assert.True(Steganalysis.ChiSquareAttack(full).EmbeddingProbability > 0.9);
    }

    [Fact]
    public void FitWithinResizesLargeCovers()
    {
        var big = TestImages.Gradient(400, 200);
        var fit = ImageIO.FitWithin(big, 100);
        Assert.Equal((100, 50), (fit.Width, fit.Height));
        Assert.Same(big, ImageIO.FitWithin(big, 1000));
    }
}
