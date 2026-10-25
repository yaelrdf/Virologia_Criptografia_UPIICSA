using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Prisma.Api.Services;
using Prisma.Core;

namespace Prisma.Api.Endpoints;

public sealed record CaesarBody(string Text, int Key, bool Enye = false);
public sealed record CrackBody(string Text, bool Enye = false);
public sealed record CompareBody(string Message, string Passphrase, int CaesarKey = 3);

public static class ToolEndpoints
{
    public static void MapCatalog(this WebApplication app)
    {
        app.MapGet("/api/catalog", (CatalogService c) => c.Items);
        app.MapGet("/api/catalog/{id}", (string id, CatalogService c) =>
            c.Png(id) is { } png ? Results.File(png, "image/png") : Http.Error(404, "No existe esa imagen."));
    }

    public static void MapStego(this WebApplication app)
    {
        var g = app.MapGroup("/api/stego").RequireAuthorization();

        g.MapPost("/capacity", async (HttpRequest req, CatalogService catalog) =>
        {
            if (!req.HasFormContentType) return Http.Error(400, "Se espera multipart/form-data.");
            var (img, err) = await Http.ReadImageAsync(await req.ReadFormAsync(), catalog);
            if (err is not null) return err;
            var fit = ImageIO.FitWithin(img!, Http.MaxCoverSide);
            return Results.Ok(new
            {
                originalWidth = img!.Width, originalHeight = img.Height, width = fit.Width, height = fit.Height,
                resized = !ReferenceEquals(fit, img),
                prismaCapacity = PrismaStego.Capacity(fit.Channels.Length),
                classicCapacity = LsbClassic.Capacity(fit.Channels.Length),
            });
        });

        // Ocultar: modo "prisma" (ABCO, requiere frase) o "clasico" (esteganografía secuencial, sin clave).
        g.MapPost("/embed", async (HttpContext ctx, CatalogService catalog, AuditService audit, TraceStore traces) =>
        {
            var sw = Stopwatch.StartNew();
            if (!ctx.Request.HasFormContentType) return Http.Error(400, "Se espera multipart/form-data.");
            var form = await ctx.Request.ReadFormAsync();
            string message = form["message"].ToString(), pass = form["passphrase"].ToString();
            string mode = (form["mode"].ToString() is { Length: > 0 } m ? m : "prisma").ToLowerInvariant();
            if (message.Length == 0) return Http.Error(400, "Escribe el mensaje a ocultar.");
            if (mode is not ("prisma" or "clasico")) return Http.Error(400, "Modo no válido: usa 'prisma' o 'clasico'.");
            if (mode == "prisma" && pass.Length < 4) return Http.Error(400, "La frase debe tener al menos 4 caracteres.");

            var (img, err) = await Http.ReadImageAsync(form, catalog);
            if (err is not null) return err;
            var cover = ImageIO.FitWithin(img!, Http.MaxCoverSide);
            byte[] payload = Encoding.UTF8.GetBytes(message);
            try
            {
                var trace = mode == "prisma" ? traces.New("ocultar mensaje") : null;
                var stego = mode == "prisma" ? PrismaStego.Embed(cover, payload, pass, trace) : LsbClassic.Embed(cover, payload);
                double psnr = Metrics.Psnr(cover.Channels, stego.Channels);
                traces.Save(trace, ctx.User.Identity!.Name!, ImageIO.EncodePng(Metrics.DiffMap(cover, stego)));
                await audit.LogAsync(ctx.User, "ocultar", mode, payload.Length, sw, true);
                int cap = mode == "prisma" ? PrismaStego.Capacity(cover.Channels.Length) : LsbClassic.Capacity(cover.Channels.Length);
                var h = ctx.Response.Headers;
                h["X-Prisma-Psnr"] = double.IsInfinity(psnr) ? "inf" : psnr.ToString("F2", CultureInfo.InvariantCulture);
                h["X-Prisma-Payload-Bytes"] = payload.Length.ToString();
                h["X-Prisma-Capacity"] = cap.ToString();
                h["X-Prisma-Resized"] = (!ReferenceEquals(cover, img)).ToString().ToLowerInvariant();
                return Results.File(ImageIO.EncodePng(stego), "image/png", $"prisma_{mode}_{DateTime.UtcNow:yyyyMMdd_HHmmss}.png");
            }
            catch (InvalidOperationException ex)
            {
                await audit.LogAsync(ctx.User, "ocultar", mode, payload.Length, sw, false);
                return Http.Error(413, ex.Message);
            }
        });

        g.MapPost("/extract", async (HttpContext ctx, CatalogService catalog, AuditService audit, TraceStore traces) =>
        {
            var sw = Stopwatch.StartNew();
            if (!ctx.Request.HasFormContentType) return Http.Error(400, "Se espera multipart/form-data.");
            var form = await ctx.Request.ReadFormAsync();
            string pass = form["passphrase"].ToString();
            string mode = (form["mode"].ToString() is { Length: > 0 } m ? m : "prisma").ToLowerInvariant();
            var (img, err) = await Http.ReadImageAsync(form, catalog);
            if (err is not null) return err;
            try
            {
                var trace = mode == "prisma" ? traces.New("revelar mensaje") : null;
                byte[] data = mode == "prisma" ? PrismaStego.Reveal(img!, pass, trace) : LsbClassic.Extract(img!);
                traces.Save(trace, ctx.User.Identity!.Name!);
                await audit.LogAsync(ctx.User, "revelar", mode, data.Length, sw, true);
                return Results.Ok(new { mode, message = Encoding.UTF8.GetString(data), bytes = data.Length });
            }
            catch (Exception ex) when (ex is StegoException or InvalidDataException)
            {
                await audit.LogAsync(ctx.User, "revelar", mode, 0, sw, false);
                // Lo que vería un atacante leyendo los bits menos significativos en orden (escenario E2).
                return Results.Json(new { error = ex.Message, rawSequentialHex = Http.Hex(LsbClassic.RawSequential(img!, 32)) }, statusCode: 422);
            }
        });
    }

    public static void MapCaesar(this WebApplication app)
    {
        var g = app.MapGroup("/api/caesar").RequireAuthorization();
        g.MapPost("/encrypt", (CaesarBody b) => Results.Ok(new { result = Caesar.Encrypt(b.Text ?? "", b.Key, b.Enye), alphabet = Caesar.AlphabetFor(b.Enye) }));
        g.MapPost("/decrypt", (CaesarBody b) => Results.Ok(new { result = Caesar.Decrypt(b.Text ?? "", b.Key, b.Enye), alphabet = Caesar.AlphabetFor(b.Enye) }));
        g.MapPost("/crack", (CrackBody b) =>
        {
            var sw = Stopwatch.StartNew();
            var all = CaesarCracker.Crack(b.Text ?? "", b.Enye);
            return Results.Ok(new { best = all[0], candidates = all, ms = sw.Elapsed.TotalMilliseconds });
        });
    }

    /// <summary>Corre César, esteganografía clásica y ABCO sobre el mismo mensaje y compara salida, tamaño, tiempo, entropía e integridad.</summary>
    public static void MapCompare(this WebApplication app)
    {
        app.MapPost("/api/compare", (CompareBody b) =>
        {
            if (string.IsNullOrEmpty(b.Message)) return Http.Error(400, "Escribe un mensaje.");
            if ((b.Passphrase ?? "").Length < 4) return Http.Error(400, "La frase debe tener al menos 4 caracteres.");
            byte[] plain = Encoding.UTF8.GetBytes(b.Message);
            var results = new List<object>();

            var sw = Stopwatch.StartNew();
            string caesar = Caesar.Encrypt(b.Message, b.CaesarKey);
            results.Add(Row("César", "Clásico", Encoding.UTF8.GetBytes(caesar), sw, false,
                $"Salida: «{(caesar.Length > 60 ? caesar[..60] + "…" : caesar)}». Conserva el histograma: se rompe con χ²."));

            sw.Restart();
            results.Add(Row("Esteganografía clásica", "Ocultamiento", plain, sw, false,
                "No cifra: los bits del mensaje van tal cual a los bits menos significativos, en orden. Cualquiera los lee."));

            sw.Restart();
            byte[] salt = RandomNumberGenerator.GetBytes(16);
            var k = PrismaKeys.Derive(b.Passphrase!, salt);
            byte[] pc = PrismaCipher.Encrypt(plain, k);
            byte[] tag = HMACSHA256.HashData(k.KMac, pc)[..8];
            sw.Stop();
            byte[] tampered = (byte[])pc.Clone(); tampered[0] ^= 1;
            bool prismaDetects = !CryptographicOperations.FixedTimeEquals(HMACSHA256.HashData(k.KMac, tampered)[..8], tag);
            results.Add(Row("ABCO", "Inventado", [.. salt, .. pc, .. tag], sw, prismaDetects,
                $"A = {k.A}, det = {ModMath.Det(k.A)}; César dinámico sᵢ = ({k.Mult}·sᵢ₋₁ + {k.Inc}) mod 256. Incluye sal(16) y HMAC(8)."));

            return Results.Ok(new { plainBytes = plain.Length, plainEntropy = Math.Round(Metrics.Entropy(plain), 3), results });
        }).RequireAuthorization();
    }

    private static object Row(string algorithm, string kind, byte[] output, Stopwatch sw, bool detectsTampering, string notes) => new
    {
        algorithm, kind, outputHex = Http.Hex(output), outputBytes = output.Length,
        ms = Math.Round(sw.Elapsed.TotalMilliseconds, 2), entropy = Math.Round(Metrics.Entropy(output), 3), detectsTampering, notes,
    };
}
