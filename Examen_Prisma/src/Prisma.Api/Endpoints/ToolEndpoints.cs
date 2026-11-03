// ToolEndpoints.cs — Rutas de las herramientas del sistema ABCO.
// Catálogo de portadoras (público), esteganografía (capacidad, ocultar y revelar en modo ABCO o
// clásico), laboratorio de César (cifrar, descifrar y romper con χ²) y el comparador que ejecuta
// los tres algoritmos sobre el mismo mensaje. El servidor procesa los mensajes en memoria y no los guarda.
using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Prisma.Api.Services;
using Prisma.Core;

namespace Prisma.Api.Endpoints;

/// <summary>Cuerpo JSON de cifrar/descifrar con César.</summary>
/// <param name="Text">Texto de entrada.</param>
/// <param name="Key">Desplazamiento.</param>
/// <param name="Enye">Usar el alfabeto español de 27 letras (con Ñ).</param>
public sealed record CaesarBody(string Text, int Key, bool Enye = false);
/// <summary>Cuerpo JSON del ataque a César.</summary>
/// <param name="Text">Texto cifrado.</param>
/// <param name="Enye">Usar el alfabeto español de 27 letras.</param>
public sealed record CrackBody(string Text, bool Enye = false);
/// <summary>Cuerpo JSON del comparador.</summary>
/// <param name="Message">Mensaje a procesar con los tres algoritmos.</param>
/// <param name="Passphrase">Frase para ABCO (al menos 4 caracteres).</param>
/// <param name="CaesarKey">Clave de César (3 por defecto).</param>
public sealed record CompareBody(string Message, string Passphrase, int CaesarKey = 3);

/// <summary>Registro de las rutas de catálogo, esteganografía, César y comparador.</summary>
public static class ToolEndpoints
{
    /// <summary>Registra las rutas públicas del catálogo.</summary>
    /// <param name="app">Aplicación web.</param>
    public static void MapCatalog(this WebApplication app)
    {
        // GET /api/catalog — público. 200 [{"id","name","width","height","prismaCapacity"}].
        app.MapGet("/api/catalog", (CatalogService c) => c.Items);
        // GET /api/catalog/{id} — público. 200 image/png · 404 si el id no existe.
        app.MapGet("/api/catalog/{id}", (string id, CatalogService c) =>
            c.Png(id) is { } png ? Results.File(png, "image/png") : Http.Error(404, "No existe esa imagen."));
    }

    /// <summary>Registra las rutas de /api/stego; todas requieren token.</summary>
    /// <param name="app">Aplicación web.</param>
    public static void MapStego(this WebApplication app)
    {
        var g = app.MapGroup("/api/stego").RequireAuthorization();

        // POST /api/stego/capacity — requiere token.
        // Entrada: multipart con "image" o "catalogId".
        // Respuestas: 200 {"originalWidth","originalHeight","width","height","resized","prismaCapacity","classicCapacity"},
        // calculado sobre la portadora ya reducida a MaxCoverSide · 400/404/413 si falla la imagen.
        g.MapPost("/capacity", async (HttpRequest req, CatalogService catalog) =>
        {
            if (!req.HasFormContentType) return Http.Error(400, "Se espera multipart/form-data.");
            var (img, err) = await Http.ReadImageAsync(await req.ReadFormAsync(), catalog);
            if (err is not null) return err;
            var fit = ImageIO.FitWithin(img!, Http.MaxCoverSide);
            return Results.Ok(new
            {
                originalWidth = img!.Width, originalHeight = img.Height, width = fit.Width, height = fit.Height,
                // FitWithin devuelve la misma instancia si no tuvo que reducir.
                resized = !ReferenceEquals(fit, img),
                prismaCapacity = PrismaStego.Capacity(fit.Channels.Length),
                classicCapacity = LsbClassic.Capacity(fit.Channels.Length),
            });
        });

        // Ocultar: modo "prisma" (ABCO, requiere frase) o "clasico" (esteganografía secuencial, sin clave).
        // POST /api/stego/embed — requiere token.
        // Entrada: multipart con "image" o "catalogId", "message", "passphrase" y "mode" (por defecto "prisma").
        // Respuestas: 200 image/png con las cabeceras X-Prisma-Psnr, X-Prisma-Payload-Bytes, X-Prisma-Capacity y
        // X-Prisma-Resized · 400 si faltan datos, el modo no es válido o la frase tiene menos de 4 caracteres ·
        // 413 si el mensaje no cabe (o el archivo supera 15 MB).
        // La salida es siempre un PNG nuevo: un formato con pérdida destruiría los bits ocultos.
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
            // Las portadoras grandes se reducen antes de ocultar (nunca después, o se perdería el mensaje).
            var cover = ImageIO.FitWithin(img!, Http.MaxCoverSide);
            byte[] payload = Encoding.UTF8.GetBytes(message);
            try
            {
                // Solo ABCO genera traza; en modo clásico trace es null y Save no guarda nada.
                var trace = mode == "prisma" ? traces.New("ocultar mensaje") : null;
                var stego = mode == "prisma" ? PrismaStego.Embed(cover, payload, pass, trace) : LsbClassic.Embed(cover, payload);
                double psnr = Metrics.Psnr(cover.Channels, stego.Channels);
                traces.Save(trace, ctx.User.Identity!.Name!, ImageIO.EncodePng(Metrics.DiffMap(cover, stego)));
                await audit.LogAsync(ctx.User, "ocultar", mode, payload.Length, sw, true);
                int cap = mode == "prisma" ? PrismaStego.Capacity(cover.Channels.Length) : LsbClassic.Capacity(cover.Channels.Length);
                var h = ctx.Response.Headers;
                // PSNR con cultura invariante (punto decimal) e "inf" si la imagen no cambió.
                h["X-Prisma-Psnr"] = double.IsInfinity(psnr) ? "inf" : psnr.ToString("F2", CultureInfo.InvariantCulture);
                h["X-Prisma-Payload-Bytes"] = payload.Length.ToString();
                h["X-Prisma-Capacity"] = cap.ToString();
                h["X-Prisma-Resized"] = (!ReferenceEquals(cover, img)).ToString().ToLowerInvariant();
                return Results.File(ImageIO.EncodePng(stego), "image/png", $"prisma_{mode}_{DateTime.UtcNow:yyyyMMdd_HHmmss}.png");
            }
            catch (InvalidOperationException ex)
            {
                // Embed lanza InvalidOperationException cuando el mensaje excede la capacidad.
                await audit.LogAsync(ctx.User, "ocultar", mode, payload.Length, sw, false);
                return Http.Error(413, ex.Message);
            }
        });

        // POST /api/stego/extract — requiere token.
        // Entrada: multipart con "image", "passphrase" y "mode" (por defecto "prisma"; cualquier otro valor se trata como clásico).
        // Respuestas: 200 {"mode","message","bytes"} · 400/413 si falla la imagen ·
        // 422 {"error","rawSequentialHex"} si no hay un mensaje válido.
        // Seguridad: en modo ABCO el error es genérico (frase incorrecta, imagen alterada o sin mensaje).
        // La imagen no se reduce: cualquier remuestreo destruiría los bits ocultos.
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

    /// <summary>Registra las rutas del laboratorio de César; todas requieren token.</summary>
    /// <param name="app">Aplicación web.</param>
    public static void MapCaesar(this WebApplication app)
    {
        var g = app.MapGroup("/api/caesar").RequireAuthorization();
        // POST /api/caesar/encrypt — requiere token. Entrada: JSON {"text","key","enye"}. 200 {"result","alphabet"}.
        g.MapPost("/encrypt", (CaesarBody b) => Results.Ok(new { result = Caesar.Encrypt(b.Text ?? "", b.Key, b.Enye), alphabet = Caesar.AlphabetFor(b.Enye) }));
        // POST /api/caesar/decrypt — requiere token. Entrada: JSON {"text","key","enye"}. 200 {"result","alphabet"}.
        g.MapPost("/decrypt", (CaesarBody b) => Results.Ok(new { result = Caesar.Decrypt(b.Text ?? "", b.Key, b.Enye), alphabet = Caesar.AlphabetFor(b.Enye) }));
        // POST /api/caesar/crack — requiere token. Entrada: JSON {"text","enye"}.
        // 200 {"best","candidates","ms"}: las n claves ordenadas por χ² (la primera es la más probable) y el tiempo del ataque.
        g.MapPost("/crack", (CrackBody b) =>
        {
            var sw = Stopwatch.StartNew();
            var all = CaesarCracker.Crack(b.Text ?? "", b.Enye);
            return Results.Ok(new { best = all[0], candidates = all, ms = sw.Elapsed.TotalMilliseconds });
        });
    }

    /// <summary>Corre César, esteganografía clásica y ABCO sobre el mismo mensaje y compara salida, tamaño, tiempo, entropía e integridad.</summary>
    /// <param name="app">Aplicación web.</param>
    public static void MapCompare(this WebApplication app)
    {
        // POST /api/compare — requiere token.
        // Entrada: JSON {"message","passphrase","caesarKey"}.
        // Respuestas: 200 {"plainBytes","plainEntropy","results"} con tres filas (César, esteganografía clásica, ABCO) ·
        // 400 si falta el mensaje o la frase tiene menos de 4 caracteres.
        // No usa imagen: compara la salida de cada algoritmo en bytes.
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

            // La esteganografía clásica no transforma los datos: su "salida" son los mismos bytes del mensaje.
            sw.Restart();
            results.Add(Row("Esteganografía clásica", "Ocultamiento", plain, sw, false,
                "No cifra: los bits del mensaje van tal cual a los bits menos significativos, en orden. Cualquiera los lee."));

            // ABCO completo sin imagen: sal ‖ cifrado ‖ HMAC truncado, igual que la trama oculta.
            // El tiempo incluye la derivación PBKDF2, que domina el costo.
            sw.Restart();
            byte[] salt = RandomNumberGenerator.GetBytes(16);
            var k = PrismaKeys.Derive(b.Passphrase!, salt);
            byte[] pc = PrismaCipher.Encrypt(plain, k);
            byte[] tag = HMACSHA256.HashData(k.KMac, pc)[..8];
            sw.Stop();
            // Demostración de integridad: se invierte un bit del cifrado y se comprueba que el HMAC ya no coincide.
            byte[] tampered = (byte[])pc.Clone(); tampered[0] ^= 1;
            bool prismaDetects = !CryptographicOperations.FixedTimeEquals(HMACSHA256.HashData(k.KMac, tampered)[..8], tag);
            results.Add(Row("ABCO", "Inventado", [.. salt, .. pc, .. tag], sw, prismaDetects,
                $"A = {k.A}, det = {ModMath.Det(k.A)}; César dinámico sᵢ = ({k.Mult}·sᵢ₋₁ + {k.Inc}) mod 256. Incluye sal(16) y HMAC(8)."));

            return Results.Ok(new { plainBytes = plain.Length, plainEntropy = Math.Round(Metrics.Entropy(plain), 3), results });
        }).RequireAuthorization();
    }

    /// <summary>Fila del comparador.</summary>
    /// <param name="algorithm">Nombre del algoritmo.</param>
    /// <param name="kind">Clasificación (clásico, ocultamiento, inventado).</param>
    /// <param name="output">Bytes de salida del algoritmo.</param>
    /// <param name="sw">Cronómetro con el tiempo de la operación.</param>
    /// <param name="detectsTampering">Si el algoritmo detecta una alteración de la salida.</param>
    /// <param name="notes">Explicación breve para el usuario.</param>
    /// <returns>Objeto {"algorithm","kind","outputHex","outputBytes","ms","entropy","detectsTampering","notes"}.</returns>
    private static object Row(string algorithm, string kind, byte[] output, Stopwatch sw, bool detectsTampering, string notes) => new
    {
        algorithm, kind, outputHex = Http.Hex(output), outputBytes = output.Length,
        ms = Math.Round(sw.Elapsed.TotalMilliseconds, 2), entropy = Math.Round(Metrics.Entropy(output), 3), detectsTampering, notes,
    };
}
