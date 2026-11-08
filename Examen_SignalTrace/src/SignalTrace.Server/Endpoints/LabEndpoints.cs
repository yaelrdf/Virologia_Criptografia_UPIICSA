using System.Collections.Concurrent;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using SignalTrace.Core;
using SignalTrace.Server.Data;
using SignalTrace.Server.Services;

namespace SignalTrace.Server.Endpoints;

/// <summary>
/// Códigos de vinculación de un solo uso y de vida corta (10 minutos). El tablero los muestra como QR y los
/// canjean el agente web o la aplicación Android para registrar un teléfono sin iniciar sesión en él.
/// </summary>
/// <param name="clock">Reloj para calcular el vencimiento.</param>
public sealed class EnrollmentCodes(TimeProvider clock)
{
    /// <summary>Códigos vigentes con el usuario que los generó y su vencimiento.</summary>
    private readonly ConcurrentDictionary<string, (int UserId, DateTimeOffset Expires)> _codes = new();
    /// <summary>Alfabeto sin caracteres ambiguos (sin 0/O ni 1/I), por si hay que teclear el código.</summary>
    private const string Alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";

    /// <summary>Genera un código aleatorio de 8 caracteres para el usuario.</summary>
    /// <param name="userId">Usuario que será dueño del dispositivo vinculado.</param>
    public (string Code, DateTimeOffset Expires) Create(int userId)
    {
        var code = new string(RandomNumberGenerator.GetItems<char>(Alphabet, 8));
        var exp = clock.GetUtcNow().AddMinutes(10);
        _codes[code] = (userId, exp);
        return (code, exp);
    }

    /// <summary>
    /// Canjea un código: lo elimina siempre (un solo uso) y devuelve el usuario si todavía no vencía.
    /// </summary>
    /// <param name="code">Código recibido; no distingue mayúsculas ni espacios alrededor.</param>
    /// <returns>El id del usuario, o null si el código no existe, ya se usó o venció.</returns>
    public int? Redeem(string code)
    {
        if (!_codes.TryRemove(code.Trim().ToUpperInvariant(), out var e)) return null;
        return e.Expires > clock.GetUtcNow() ? e.UserId : null;
    }
}

/// <summary>Canje de un código de vinculación con el nombre y el tipo del dispositivo nuevo.</summary>
public sealed record EnrollRedeemRequest(string Code, string Name, string Kind);
/// <summary>Textos opcionales para la demostración del ataque a YCM1 (el conocido y el que se quiere recuperar).</summary>
public sealed record Ycm1AttackRequest(string? Plaintext, string? SecondPlaintext);

/// <summary>Endpoints de vinculación por QR y del laboratorio de ataques.</summary>
public static class LabEndpoints
{
    /// <summary>Indica si los bytes son UTF-8 válido y JSON bien formado; es el criterio del atacante para reconocer un descifrado correcto.</summary>
    private static bool LooksLikeJson(byte[] b)
    {
        try
        {
            using var _ = System.Text.Json.JsonDocument.Parse(new UTF8Encoding(false, true).GetString(b));
            return true;
        }
        catch (Exception e) when (e is System.Text.Json.JsonException or DecoderFallbackException) { return false; }
    }

    /// <summary>Registra la generación y el canje de códigos de vinculación.</summary>
    public static void MapEnrollment(this WebApplication app)
    {
        // Genera un código y arma el enlace del QR con la dirección que el teléfono podrá abrir.
        app.MapPost("/api/enroll", (HttpContext ctx, ClaimsPrincipal me, EnrollmentCodes codes, ServerIdentity id, IConfiguration cfg, LanHttps lan) =>
        {
            var (code, exp) = codes.Create(me.UserId());
            // Detrás de un proxy que termina TLS (Cloudflare Tunnel, Caddy, Nginx) la solicitud llega por HTTP simple, pero el
            // navegador usó https://{Host}: esa es la dirección que hay que compartir, y su certificado pertenece al proxy.
            var viaHttpsProxy = string.Equals(ctx.Request.Headers["X-Forwarded-Proto"], "https", StringComparison.OrdinalIgnoreCase);
            var origin = $"{(viaHttpsProxy ? "https" : ctx.Request.Scheme)}://{ctx.Request.Host}";
            var publicUrl = cfg["Server:PublicUrl"] is { Length: > 0 } pub ? pub.TrimEnd('/') : null;
            // URL base que puede abrir un teléfono: la URL pública; si no, este origen HTTPS; si no, la dirección HTTPS de la red local.
            var url = publicUrl ?? (ctx.Request.IsHttps || viaHttpsProxy || lan.LanUrl is null ? origin : lan.LanUrl);
            // Fija el certificado autofirmado solo cuando el teléfono de verdad hablará con nuestro propio HTTPS de la red local.
            var ownTls = url == lan.LanUrl || (ctx.Request.IsHttps && ctx.Connection.LocalPort == lan.Port);
            var pinTls = lan.CertSha256 is not null && publicUrl is null && !viaHttpsProxy && ownTls;
            // Contenido del QR: una URL normal. Cualquier cámara de teléfono abre el agente web; la aplicación Android lee el mismo enlace.
            // El fragmento (#…) nunca llega al servidor: lleva el código, la huella de la llave fijada y el hash del certificado TLS.
            var qr = $"{url}/agent.html#enroll={code}&fp={id.Fingerprint}" + (pinTls ? $"&tls={lan.CertSha256}" : "");
            return Results.Ok(new { code, expiresAt = exp.ToUnixTimeMilliseconds(), url, fingerprint = id.Fingerprint, tls = pinTls ? lan.CertSha256 : null, qr });
        }).RequireAuthorization("user");

        // Canjea el código desde el teléfono (sin sesión de usuario) y registra el dispositivo a nombre de quien generó el QR.
        app.MapPost("/api/enroll/redeem", async (EnrollRedeemRequest req, EnrollmentCodes codes, AppDb db, TokenService tokens, TimeProvider clock) =>
        {
            if (string.IsNullOrWhiteSpace(req.Code) || string.IsNullOrWhiteSpace(req.Name) || !CoreEndpoints.DeviceKinds.Contains(req.Kind))
                return Http.Error(400, "malformed", "Código, nombre y tipo son obligatorios");
            if (codes.Redeem(req.Code) is not { } userId) return Http.Error(401, "unauthorized", "Código inválido, usado o expirado");
            // El nombre se recorta a 60 caracteres.
            var device = await CoreEndpoints.CreateDevice(db, userId, req.Name.Trim()[..Math.Min(60, req.Name.Trim().Length)], req.Kind, clock);
            return Results.Created($"/api/devices/{device.Id:D}", new RegisterDeviceResponse(device.Id.ToString("D"), tokens.IssueDevice(device.Id, userId)));
        }).RequireRateLimiting("login");
    }

    /// <summary>
    /// Ayudas del laboratorio (solo administradores y con Lab:Enabled). La mayoría de los escenarios corren en el
    /// navegador (lab.html) contra los endpoints reales; aquí solo están los que necesitan el servidor.
    /// </summary>
    public static void MapLab(this WebApplication app)
    {
        // Si el laboratorio está desactivado, todas estas rutas responden 404 como si no existieran.
        var lab = app.MapGroup("/api/lab").RequireAuthorization("admin").AddEndpointFilter(async (ctx, next) =>
        {
            var cfg = ctx.HttpContext.RequestServices.GetRequiredService<IConfiguration>();
            return cfg.GetValue("Lab:Enabled", false) ? await next(ctx) : Http.Error(404, "lab_disabled", "Lab:Enabled = false");
        });

        // Opciones activas, para que la página del laboratorio sepa qué escenarios puede ejecutar.
        lab.MapGet("/config", (IConfiguration cfg) => Results.Ok(new
        {
            enabled = cfg.GetValue("Lab:Enabled", false),
            exposeKeys = cfg.GetValue("Lab:ExposeKeys", false),
            mitm = cfg.GetValue("Lab:EnableMitmDemo", false),
            replayWindow = cfg.GetValue("Telemetry:ReplayWindow", false),
            encryptAtRest = cfg.GetValue("Storage:EncryptAtRest", false),
            mqtt = cfg.GetValue("Mqtt:Enabled", false),
            suites = CoreEndpoints.SupportedSuites(cfg),
        }));

        // Simula un reinicio del servidor: desaparecen todas las sesiones en memoria, los clientes reciben 404 y repiten el handshake.
        lab.MapPost("/reset-sessions", (SessionStore s) =>
        {
            var n = s.Count;
            s.Clear();
            return Results.Ok(new { cleared = n });
        });

        // Ataque de texto claro conocido a YCM1 de 1 ronda con parámetros reutilizados, y descifrado de un segundo mensaje.
        lab.MapPost("/ycm1-attack", (Ycm1AttackRequest req) =>
        {
            var p1 = Encoding.UTF8.GetBytes(req.Plaintext ?? "{\"t\":1760000000000,\"src\":\"sim\",\"bat\":{\"lvl\":0.5}}");
            var p2 = Encoding.UTF8.GetBytes(req.SecondPlaintext ?? "{\"env\":{\"lux\":412,\"hpa\":779.3},\"ori\":{\"az\":87,\"pi\":-12,\"ro\":3}}");
            // Parámetros secretos aleatorios de una sola ronda, usados para cifrar los dos mensajes.
            var secret = YaelCryptoMarch1.DeriveParams(RandomNumberGenerator.GetBytes(32), 1, rounds: 1);
            var c1 = YaelCryptoMarch1.EncryptWithParams(p1, secret);
            var c2 = YaelCryptoMarch1.EncryptWithParams(p2, secret);
            // El atacante solo conoce p1, c1 y c2.
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var attack = Ycm1Cryptanalysis.KnownPlaintextOneRound(p1, c1);
            var ms = sw.Elapsed.TotalMilliseconds;
            string? recovered = null;
            // El atacante se queda con la rotación candidata cuya salida es JSON válido en UTF-8.
            if (Ycm1Cryptanalysis.DecryptOther(attack, c2, LooksLikeJson) is { } plain) recovered = Encoding.UTF8.GetString(plain);
            var r0 = secret.Rounds[0];
            return Results.Ok(new
            {
                secret = new { seed = secret.Seed, k = r0.K, m = r0.M, rot = r0.Rot },
                found = attack.Params is { } f ? new { seed = f.Seed, k = f.Rounds[0].K, m = f.Rounds[0].M, rot = f.Rounds[0].Rot } : null,
                knownPlaintext = Encoding.UTF8.GetString(p1),
                knownCipherHex = Bytes.ToHex(c1),
                targetCipherHex = Bytes.ToHex(c2),
                recoveredPlaintext = recovered,
                candidates = attack.Candidates,
                rotationCandidates = attack.RotationCandidates,
                elapsedMs = Math.Round(ms, 2),
                log = attack.Log,
                note = "Con AES-256-GCM encima, quien escucha la red nunca ve la salida de Yael Crypto March1: la captura solo contiene ct||tag con entropía ≈ 8 bits/byte.",
            });
        });
    }
}
