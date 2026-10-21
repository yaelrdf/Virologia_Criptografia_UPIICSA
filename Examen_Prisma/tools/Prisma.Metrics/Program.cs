// Mide las métricas que cita la documentación. Uso (desde Examen_Prisma):
//   dotnet run --project tools/Prisma.Metrics -c Release -- src/Prisma.Api/data/catalog docs/metricas.md
using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Prisma.Core;

CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
string catalogDir = args.Length > 0 ? args[0] : "src/Prisma.Api/data/catalog";
string outFile = args.Length > 1 ? args[1] : "docs/metricas.md";
var md = new StringBuilder();
var rng = new Random(2026);
void Line(string s = "") { md.AppendLine(s); Console.WriteLine(s); }

Line("# Métricas medidas de PRISMA-L");
Line($"Generado por `tools/Prisma.Metrics` el {DateTime.Now:yyyy-MM-dd HH:mm}. Máquina: {Environment.ProcessorCount} núcleos, .NET {Environment.Version}.");
Line();

// 1. Entropía
// Sin tildes, para que César (que las normaliza) se compare con el mismo texto.
const string Spanish = "La criptografia estudia como proteger la informacion de los mensajes que se envian entre dos personas. " +
    "El cifrado Cesar desplaza cada letra un numero fijo de posiciones del alfabeto; la esteganografia oculta la existencia del mensaje. ";
string text = string.Concat(Enumerable.Repeat(Spanish, 10));
byte[] plain = Encoding.UTF8.GetBytes(text);
var keys = PrismaKeys.Derive("frase-secreta", Encoding.ASCII.GetBytes("sal-fija-16bytes"));
Line("## 1. Entropía de Shannon (bits por byte, máximo 8)");
Line($"Texto en español (sin tildes) de {plain.Length} bytes.");
Line();
Line("| Algoritmo | Entropía | Observación |");
Line("|---|---|---|");
Line($"| Texto claro | {Metrics.Entropy(plain):F3} | Redundancia del idioma |");
Line($"| César (k = 3) | {Metrics.Entropy(Encoding.UTF8.GetBytes(Caesar.Encrypt(text, 3))):F3} | Solo renombra símbolos: el histograma se conserva |");
Line($"| LSB clásico | {Metrics.Entropy(plain):F3} | No cifra: los bits del mensaje van tal cual |");
Line($"| PRISMA-L | {Metrics.Entropy(PrismaCipher.Encrypt(plain, keys)):F3} | Matriz + César dinámico + encadenamiento |");
Line($"| AES-256-GCM | {Metrics.Entropy(AesGcmRef.Encrypt(plain, "frase-secreta")[28..^16]):F3} | Referencia comercial |");
Line();

// 2. Avalancha
Line("## 2. Difusión (avalancha, Monte Carlo con 2 000 ensayos)");
Line("Se cambia 1 bit al azar del texto claro de 64 bytes y se mide la fracción de bits que cambian en la salida. Un cifrador ideal da ≈ 50 %.");
Line();
byte[] aesKey = RandomNumberGenerator.GetBytes(32), nonce = new byte[12];
byte[] AesFixed(byte[] m) { var c = new byte[m.Length]; var t = new byte[16]; using var a = new AesGcm(aesKey, 16); a.Encrypt(nonce, m, c, t); return c; }
byte[] CaesarBytes(byte[] m) => m.Select(b => (byte)(b + 3)).ToArray();
double avP = Metrics.Avalanche(m => PrismaCipher.Encrypt(m, keys), 64, 2000, rng);
double avC = Metrics.Avalanche(CaesarBytes, 64, 2000, rng);
double avA = Metrics.Avalanche(AesFixed, 64, 2000, rng);
// Avalancha "hacia adelante": solo los bloques a partir del bit cambiado.
double fwd = 0; int trials = 2000;
for (int t = 0; t < trials; t++)
{
    var m = new byte[64]; rng.NextBytes(m); var m2 = (byte[])m.Clone();
    int bit = rng.Next(512); m2[bit >> 3] ^= (byte)(1 << (bit & 7));
    var c1 = PrismaCipher.Encrypt(m, keys); var c2 = PrismaCipher.Encrypt(m2, keys);
    int from = (bit >> 3) & ~1, diff = 0;
    for (int i = from; i < 64; i++) diff += System.Numerics.BitOperations.PopCount((uint)(c1[i] ^ c2[i]));
    fwd += (double)diff / ((64 - from) * 8);
}
fwd /= trials;
Line("| Algoritmo | Bits de salida que cambian |");
Line("|---|---|");
Line($"| César | {100 * avC:F2} % |");
Line($"| PRISMA-L (mensaje completo) | {100 * avP:F2} % |");
Line($"| PRISMA-L (solo del bloque alterado en adelante) | {100 * fwd:F2} % |");
Line($"| AES-256-GCM (modo contador, misma clave y nonce) | {100 * avA:F2} % |");
Line();
Line("Lectura: el encadenamiento propaga el cambio **hacia adelante**, no hacia atrás; por eso el promedio sobre el mensaje completo es menor. AES-GCM es un modo de flujo: un bit del claro cambia un bit del cifrado, y su protección contra alteraciones viene de la etiqueta GCM, no de la avalancha.");
Line();

// 3. Avalancha de clave
int keyTrials = 40; double keyAv = 0;
for (int t = 0; t < keyTrials; t++)
{
    var salt = RandomNumberGenerator.GetBytes(16);
    var k1 = PrismaKeys.Derive("frase-secreta", salt); var k2 = PrismaKeys.Derive("frase-secretb", salt);
    var m = new byte[64]; rng.NextBytes(m);
    var c1 = PrismaCipher.Encrypt(m, k1); var c2 = PrismaCipher.Encrypt(m, k2);
    keyAv += c1.Zip(c2, (a, b) => System.Numerics.BitOperations.PopCount((uint)(a ^ b))).Sum() / 512.0;
}
Line($"**Sensibilidad a la clave:** cambiar una letra de la frase (\"frase-secreta\" → \"frase-secretb\") cambia el **{100 * keyAv / keyTrials:F2} %** de los bits del cifrado ({keyTrials} ensayos).");
Line();

// 4. Ataque de texto claro conocido (KPA) a Hill puro vs PRISMA-L
Line("## 3. Ataque de texto claro conocido (álgebra lineal)");
var hill = keys.A;
byte[] HillPure(byte[] m) { var o = new byte[m.Length]; for (int i = 0; i < m.Length; i += 2) { var (x, y) = hill.Mul(m[i], m[i + 1]); o[i] = (byte)x; o[i + 1] = (byte)y; } return o; }
(Matrix2? A, int Hits, int Total) Attack(Func<byte[], byte[]> enc)
{
    // El atacante conoce pares (P, C). Con dos bloques P₁, P₂ cuyo det es impar: A = [C₁ C₂]·[P₁ P₂]⁻¹ (mod 256).
    for (int attempt = 0; attempt < 200; attempt++)
    {
        var m = new byte[4]; rng.NextBytes(m);
        var P = new Matrix2(m[0], m[2], m[1], m[3]);
        if ((ModMath.Det(P) & 1) == 0) continue;
        var c = enc(m);
        var C = new Matrix2(c[0], c[2], c[1], c[3]);
        var Pi = ModMath.Inverse(P);
        var guess = new Matrix2((C.A * Pi.A + C.B * Pi.C) & 0xFF, (C.A * Pi.B + C.B * Pi.D) & 0xFF, (C.C * Pi.A + C.D * Pi.C) & 0xFF, (C.C * Pi.B + C.D * Pi.D) & 0xFF);
        int hits = 0, total = 1000;
        for (int t = 0; t < total; t++)
        {
            var mm = new byte[2]; rng.NextBytes(mm);
            var real = enc(mm); var (gx, gy) = guess.Mul(mm[0], mm[1]);
            if (real[0] == gx && real[1] == gy) hits++;
        }
        return (guess, hits, total);
    }
    return (null, 0, 0);
}
var (aHill, hHill, nHill) = Attack(HillPure);
var (aPrisma, hPrisma, nPrisma) = Attack(m => PrismaCipher.Encrypt(m, keys));
Line($"Matriz secreta real: A = {hill}.");
Line();
Line("| Cifrador atacado | Matriz recuperada con 2 bloques conocidos | Bloques nuevos predichos |");
Line("|---|---|---|");
Line($"| Hill puro 2×2 (C = A·P) | {aHill} | {hHill} de {nHill} |");
Line($"| PRISMA-L | {aPrisma} | {hPrisma} de {nPrisma} |");
Line();

// 5. Estego: PSNR, capacidad y estegoanálisis
Line("## 4. Esteganografía: distorsión y detectabilidad");
var files = Directory.Exists(catalogDir) ? Directory.GetFiles(catalogDir, "*.png").OrderBy(f => f).ToArray() : [];
if (files.Length == 0) Line($"(No se encontró el catálogo en `{catalogDir}`; arranca la API una vez para generarlo.)");
else
{
    var cover = ImageIO.Decode(File.ReadAllBytes(files.First(f => f.EndsWith("montanas.png"))));
    int cap = PrismaStego.Capacity(cover.Channels.Length);
    Line($"Portadora: `montanas.png`, {cover.Width}×{cover.Height}, capacidad PRISMA-L {cap:N0} B, LSB clásico {LsbClassic.Capacity(cover.Channels.Length):N0} B.");
    Line();
    Line("| Mensaje | Modo | PSNR (dB) | P(mensaje) χ² al 10 % inicial | P(mensaje) χ² en toda la imagen |");
    Line("|---|---|---|---|---|");
    foreach (int size in new[] { 32, 1024, 10_240, 57_000, 114_000 })
    {
        if (size > cap) continue;
        var payload = RandomNumberGenerator.GetBytes(size);
        var p = PrismaStego.Embed(cover, payload, "frase-secreta");
        var c = LsbClassic.Embed(cover, payload);
        string Pct(double v) => $"{100 * v:F1} %";
        Line($"| {size:N0} B | PRISMA-L | {Metrics.Psnr(cover.Channels, p.Channels):F2} | {Pct(Steganalysis.ChiSquareAttack(p, 0.10).EmbeddingProbability)} | {Pct(Steganalysis.ChiSquareAttack(p).EmbeddingProbability)} |");
        Line($"| {size:N0} B | LSB clásico | {Metrics.Psnr(cover.Channels, c.Channels):F2} | {Pct(Steganalysis.ChiSquareAttack(c, 0.10).EmbeddingProbability)} | {Pct(Steganalysis.ChiSquareAttack(c).EmbeddingProbability)} |");
    }
    Line($"| (sin mensaje) | — | ∞ | {100 * Steganalysis.ChiSquareAttack(cover, 0.10).EmbeddingProbability:F1} % | {100 * Steganalysis.ChiSquareAttack(cover).EmbeddingProbability:F1} % |");
    Line();
    Line("Lectura: el LSB clásico escribe al inicio de la imagen, así que el ataque χ² sobre el 10 % inicial lo detecta en cuanto el mensaje llena esa zona. PRISMA-L reparte los bits por toda la imagen: con mensajes pequeños, ninguna región queda saturada. Con ocupación alta, cualquier LSB por reemplazo se vuelve detectable.");
    Line();

    // Robustez: alteración y recompresión
    Line("## 4b. Integridad y robustez (escenarios E3 y E6)");
    var secret = Encoding.UTF8.GetBytes("Hola Bob, nos vemos a las 18:00");
    var stego = PrismaStego.Embed(cover, secret, "girasol-azul-42");
    string Try(RgbImage img, string pass)
    {
        try { return "se revela: «" + Encoding.UTF8.GetString(PrismaStego.Reveal(img, pass)) + "»"; }
        catch (StegoException) { return "error genérico (rechazado)"; }
    }
    var salt = PrismaStego.ReadBits(stego.Channels, i => i, PrismaStego.SaltBytes);
    var path = FisherYates.Path(stego.Channels.Length, PrismaKeys.Derive("girasol-azul-42", salt).Seed);
    var inPath = stego.Clone(); inPath.Channels[path[200]] ^= 1;
    var outside = stego.Clone(); outside.Channels[path[^1]] ^= 1;
    var saltHit = stego.Clone(); saltHit.Channels[5] ^= 1;
    using var skBmp = SkiaSharp.SKBitmap.Decode(ImageIO.EncodePng(stego));
    var jpeg90 = ImageIO.Decode(skBmp.Encode(SkiaSharp.SKEncodedImageFormat.Jpeg, 90).ToArray());
    var jpeg100 = ImageIO.Decode(skBmp.Encode(SkiaSharp.SKEncodedImageFormat.Jpeg, 100).ToArray());
    int changed = jpeg90.Channels.Zip(stego.Channels, (a, b) => (a & 1) != (b & 1) ? 1 : 0).Sum();
    Line("Mensaje de 31 B oculto con PRISMA-L en `montanas.png`.");
    Line();
    Line("| Caso | Resultado |");
    Line("|---|---|");
    Line($"| Imagen intacta, frase correcta | {Try(stego, "girasol-azul-42")} |");
    Line($"| Imagen intacta, frase incorrecta | {Try(stego, "girasol-rojo-42")} |");
    Line($"| 1 bit alterado **dentro** de la trama (posición 200 del recorrido) | {Try(inPath, "girasol-azul-42")} |");
    Line($"| 1 bit alterado en un canal que la trama **no usa** | {Try(outside, "girasol-azul-42")} |");
    Line($"| 1 bit alterado en la sal (canales 0..127) | {Try(saltHit, "girasol-azul-42")} |");
    Line($"| Recomprimida como JPEG calidad 90 (\"enviar como foto\") | {Try(jpeg90, "girasol-azul-42")}; cambiaron {changed:N0} de {stego.Channels.Length:N0} LSB ({100.0 * changed / stego.Channels.Length:F1} %) |");
    Line($"| Recomprimida como JPEG calidad 100 | {Try(jpeg100, "girasol-azul-42")} |");
    Line();

    // 6. Tiempos
    Line("## 5. Tiempos (promedio de 5 ejecuciones)");
    double Time(Action a) { a(); var sw = Stopwatch.StartNew(); for (int i = 0; i < 5; i++) a(); return sw.Elapsed.TotalMilliseconds / 5; }
    var msg = Encoding.UTF8.GetBytes("Mensaje de prueba de 64 bytes para medir los tiempos de PRISMA-L");
    var st = PrismaStego.Embed(cover, msg, "frase-secreta");
    Line("| Operación | ms |");
    Line("|---|---|");
    Line($"| Derivar claves (PBKDF2-SHA256, {PrismaKeys.Pbkdf2Iterations:N0} iteraciones) | {Time(() => PrismaKeys.Derive("frase-secreta", new byte[16])):F1} |");
    Line($"| Cifrar 1 MB con PRISMA-L (sin derivación) | {Time(() => PrismaCipher.Encrypt(new byte[1 << 20], keys)):F1} |");
    Line($"| Recorrido Fisher–Yates de {cover.Channels.Length:N0} canales | {Time(() => FisherYates.Path(cover.Channels.Length, keys.Seed)):F1} |");
    Line($"| Ocultar 64 B en {cover.Width}×{cover.Height} (total) | {Time(() => PrismaStego.Embed(cover, msg, "frase-secreta")):F1} |");
    Line($"| Revelar 64 B (total) | {Time(() => PrismaStego.Reveal(st, "frase-secreta")):F1} |");
    Line($"| Romper César con χ² (26 claves, 200 caracteres) | {Time(() => CaesarCracker.Crack(Caesar.Encrypt(Spanish, 7))):F2} |");
    Line();
}

// 7. Espacio de claves
Line("## 6. Espacio de claves");
Line("| Algoritmo | Claves posibles |");
Line("|---|---|");
Line("| César (A–Z) | 26 (25 útiles) |");
Line($"| Matriz de Hill 2×2 invertible en ℤ₂₅₆ | \\|GL₂(ℤ₂₅₆)\\| = 2²⁸·6 = {(1L << 28) * 6:N0} |");
Line($"| Parámetros del César dinámico (s₀, a, c) | 256 · 63 · 128 = {256L * 63 * 128:N0} |");
Line("| Semilla del recorrido | 2⁶⁴ |");
Line("| Material de clave de PRISMA-L | Derivado de la frase con PBKDF2 (32 B): la seguridad práctica depende de la frase |");

File.WriteAllText(outFile, md.ToString());
Console.Error.WriteLine($"→ {outFile}");
