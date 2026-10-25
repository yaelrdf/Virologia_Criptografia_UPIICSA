using System.Collections.Concurrent;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using SignalTrace.Core;
using SignalTrace.Server.Data;
using SignalTrace.Server.Services;

namespace SignalTrace.Server.Endpoints;

/// <summary>Short-lived one-time enrollment codes, shown as a QR in the dashboard and redeemed by the Android app.</summary>
public sealed class EnrollmentCodes(TimeProvider clock)
{
    private readonly ConcurrentDictionary<string, (int UserId, DateTimeOffset Expires)> _codes = new();
    private const string Alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";

    public (string Code, DateTimeOffset Expires) Create(int userId)
    {
        var code = new string(RandomNumberGenerator.GetItems<char>(Alphabet, 8));
        var exp = clock.GetUtcNow().AddMinutes(10);
        _codes[code] = (userId, exp);
        return (code, exp);
    }

    public int? Redeem(string code)
    {
        if (!_codes.TryRemove(code.Trim().ToUpperInvariant(), out var e)) return null;
        return e.Expires > clock.GetUtcNow() ? e.UserId : null;
    }
}

public sealed record EnrollRedeemRequest(string Code, string Name, string Kind);
public sealed record SigMixAttackRequest(string? Plaintext, string? SecondPlaintext);

public static class LabEndpoints
{
    public static void MapEnrollment(this WebApplication app)
    {
        app.MapPost("/api/enroll", (HttpContext ctx, ClaimsPrincipal me, EnrollmentCodes codes, ServerIdentity id, IConfiguration cfg) =>
        {
            var (code, exp) = codes.Create(me.UserId());
            var url = cfg["Server:PublicUrl"] is { Length: > 0 } pub ? pub.TrimEnd('/') : $"{ctx.Request.Scheme}://{ctx.Request.Host}";
            // QR content: compact JSON the app parses to fill url, pinned fingerprint and code.
            var qr = $$"""{"st":1,"url":"{{url}}","fp":"{{id.Fingerprint}}","code":"{{code}}"}""";
            return Results.Ok(new { code, expiresAt = exp.ToUnixTimeMilliseconds(), url, fingerprint = id.Fingerprint, qr });
        }).RequireAuthorization("user");

        app.MapPost("/api/enroll/redeem", async (EnrollRedeemRequest req, EnrollmentCodes codes, AppDb db, TokenService tokens, TimeProvider clock) =>
        {
            if (string.IsNullOrWhiteSpace(req.Code) || string.IsNullOrWhiteSpace(req.Name) || !CoreEndpoints.DeviceKinds.Contains(req.Kind))
                return Http.Error(400, "malformed", "Código, nombre y tipo son obligatorios");
            if (codes.Redeem(req.Code) is not { } userId) return Http.Error(401, "unauthorized", "Código inválido, usado o expirado");
            var device = await CoreEndpoints.CreateDevice(db, userId, req.Name.Trim()[..Math.Min(60, req.Name.Trim().Length)], req.Kind, clock);
            return Results.Created($"/api/devices/{device.Id:D}", new RegisterDeviceResponse(device.Id.ToString("D"), tokens.IssueDevice(device.Id, userId)));
        }).RequireRateLimiting("login");
    }

    /// <summary>Lab helpers (admin + Lab:Enabled). Most scenarios run in the browser (lab.html) against the real endpoints.</summary>
    public static void MapLab(this WebApplication app)
    {
        var lab = app.MapGroup("/api/lab").RequireAuthorization("admin").AddEndpointFilter(async (ctx, next) =>
        {
            var cfg = ctx.HttpContext.RequestServices.GetRequiredService<IConfiguration>();
            return cfg.GetValue("Lab:Enabled", false) ? await next(ctx) : Http.Error(404, "lab_disabled", "Lab:Enabled = false");
        });

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

        // Simulates a server restart: every in-memory session disappears, clients get 404 and redo the handshake.
        lab.MapPost("/reset-sessions", (SessionStore s) =>
        {
            var n = s.Count;
            s.Clear();
            return Results.Ok(new { cleared = n });
        });

        // Known-plaintext attack on 1-round SigMix with reused parameters, then decryption of a second message.
        lab.MapPost("/sigmix-attack", (SigMixAttackRequest req) =>
        {
            var p1 = Encoding.UTF8.GetBytes(req.Plaintext ?? "{\"t\":1760000000000,\"src\":\"sim\",\"bat\":{\"lvl\":0.5}}");
            var p2 = Encoding.UTF8.GetBytes(req.SecondPlaintext ?? "{\"gps\":{\"lat\":19.5046,\"lon\":-99.1468}}");
            var secret = SigMix.DeriveParams(RandomNumberGenerator.GetBytes(32), 1, rounds: 1);
            var c1 = SigMix.EncryptWithParams(p1, secret);
            var c2 = SigMix.EncryptWithParams(p2, secret);
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var attack = SigMixCryptanalysis.KnownPlaintextOneRound(p1, c1);
            var ms = sw.Elapsed.TotalMilliseconds;
            string? recovered = null;
            if (attack.Params is { } found)
            {
                try { recovered = Encoding.UTF8.GetString(SigMix.DecryptWithParams(c2, found)); }
                catch (CryptographicException) { recovered = null; }
            }
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
                elapsedMs = Math.Round(ms, 2),
                log = attack.Log,
                note = "Con AES-256-GCM encima, Eve nunca ve la salida de SigMix: la captura solo contiene ct||tag con entropía ≈ 8 bits/byte.",
            });
        });
    }
}
