using System.Security.Claims;
using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using SignalTrace.Core;
using SignalTrace.Server.Data;
using SignalTrace.Server.Hubs;
using SignalTrace.Server.Services;

namespace SignalTrace.Server.Endpoints;

public static class Http
{
    public static IResult Error(int status, string code, string message) => Results.Json(new ErrorResponse(code, message), statusCode: status);

    public static long Now(this TimeProvider clock) => clock.GetUtcNow().ToUnixTimeMilliseconds();

    public static string Source(HttpContext ctx) => ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown";
}

/// <summary>Login, users, server key, devices, handshake and telemetry (PLAN §7).</summary>
public static class CoreEndpoints
{
    public static readonly string[] DeviceKinds = ["web", "android", "sim"];
    private static readonly byte[] DummySalt = new byte[16], DummyHash = new byte[32];
    private const string PasswordAlphabet = "ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz23456789";

    private static string? PasswordError(string? password)
        => (password ?? "").Length is < 8 or > 128 ? "La contraseña debe tener entre 8 y 128 caracteres" : null;

    private static void SetPassword(User user, string password, PasswordHasher hasher)
    {
        var (salt, it, hash) = hasher.Hash(password);
        (user.Salt, user.Iterations, user.PasswordHash) = (salt, it, hash);
    }

    public static void MapCore(this WebApplication app)
    {
        var api = app.MapGroup("/api");

        api.MapPost("/auth/login", async (LoginRequest req, AppDb db, TokenService tokens, LoginThrottle throttle, PasswordHasher hasher) =>
        {
            if (string.IsNullOrWhiteSpace(req.Username) || string.IsNullOrEmpty(req.Password))
                return Http.Error(400, "malformed", "Usuario y contraseña son obligatorios");
            if (throttle.RetryAfter(req.Username) is { } wait)
                return Http.Error(429, "rate_limited", $"Demasiados intentos. Espera {Math.Ceiling(wait.TotalSeconds)} s");
            var user = await db.Users.SingleOrDefaultAsync(u => u.Username == req.Username.Trim());
            // Run PBKDF2 even for unknown users so timing does not reveal which usernames exist.
            var ok = user is null
                ? PasswordHasher.Verify(req.Password, DummySalt, hasher.Iterations, DummyHash) && false
                : PasswordHasher.Verify(req.Password, user.Salt, user.Iterations, user.PasswordHash);
            if (!ok || user is null)
            {
                throttle.Fail(req.Username);
                return Http.Error(401, "unauthorized", "Usuario o contraseña incorrectos");
            }
            throttle.Success(req.Username);
            var (token, exp) = tokens.IssueUser(user.Id, user.Username, user.Role);
            return Results.Ok(new LoginResponse(token, exp, user.Role, user.Username));
        }).RequireRateLimiting("login");

        api.MapGet("/auth/me", (ClaimsPrincipal u) => Results.Ok(new { username = u.FindFirstValue("name"), role = u.FindFirstValue("role") }))
            .RequireAuthorization("user");

        // Any signed-in user changes their own password: the current one is verified with PBKDF2 first.
        api.MapPost("/auth/change-password", async (ChangePasswordRequest req, AppDb db, PasswordHasher hasher, ClaimsPrincipal me) =>
        {
            var user = await db.Users.FindAsync(me.UserId());
            if (user is null) return Http.Error(404, "not_found", "Usuario inexistente");
            if (!PasswordHasher.Verify(req.Current ?? "", user.Salt, user.Iterations, user.PasswordHash))
                return Http.Error(400, "wrong_password", "La contraseña actual no es correcta");
            if (PasswordError(req.New) is { } err) return Http.Error(400, "malformed", err);
            if (req.New == req.Current) return Http.Error(400, "malformed", "La contraseña nueva debe ser distinta de la actual");
            SetPassword(user, req.New!, hasher);
            await db.SaveChangesAsync();
            return Results.NoContent();
        }).RequireAuthorization("user").RequireRateLimiting("login");

        api.MapGet("/server-key", (ServerIdentity id, IConfiguration cfg) =>
            Results.Ok(new ServerKeyResponse(Convert.ToBase64String(id.PublicKey), id.Fingerprint, SupportedSuites(cfg))));

        // ---- Users (admin)
        api.MapPost("/users", async (CreateUserRequest req, AppDb db, PasswordHasher hasher, TimeProvider clock) =>
        {
            var name = req.Username?.Trim() ?? "";
            if (name.Length is < 3 or > 32 || !name.All(c => char.IsLetterOrDigit(c) || c is '_' or '.' or '-'))
                return Http.Error(400, "malformed", "El usuario debe tener 3–32 caracteres alfanuméricos");
            if (PasswordError(req.Password) is { } err) return Http.Error(400, "malformed", err);
            if (!Roles.IsValid(req.Role)) return Http.Error(400, "malformed", "Rol inválido (admin | viewer)");
            if (await db.Users.AnyAsync(u => u.Username == name)) return Http.Error(409, "conflict", "El usuario ya existe");
            var (salt, it, hash) = hasher.Hash(req.Password!);
            var user = new User { Username = name, Salt = salt, Iterations = it, PasswordHash = hash, Role = req.Role, CreatedAt = clock.Now() };
            db.Users.Add(user);
            await db.SaveChangesAsync();
            return Results.Created($"/api/users/{user.Id}", new { user.Id, user.Username, user.Role });
        }).RequireAuthorization("admin");

        api.MapGet("/users", async (AppDb db) =>
            Results.Ok(await db.Users.OrderBy(u => u.Id).Select(u => new { u.Id, u.Username, u.Role, u.CreatedAt, u.Iterations }).ToListAsync()))
            .RequireAuthorization("admin");

        api.MapDelete("/users/{id:int}", async (int id, AppDb db, ClaimsPrincipal me) =>
        {
            if (id == me.UserId()) return Http.Error(400, "malformed", "No puedes eliminar tu propio usuario");
            var n = await db.Users.Where(u => u.Id == id).ExecuteDeleteAsync();
            return n == 0 ? Http.Error(404, "not_found", "Usuario inexistente") : Results.NoContent();
        }).RequireAuthorization("admin");

        api.MapPut("/users/{id:int}/role", async (int id, ChangeRoleRequest req, AppDb db) =>
        {
            if (!Roles.IsValid(req.Role)) return Http.Error(400, "malformed", "Rol inválido (admin | viewer)");
            var user = await db.Users.FindAsync(id);
            if (user is null) return Http.Error(404, "not_found", "Usuario inexistente");
            if (user.Role == Roles.Admin && req.Role != Roles.Admin && await db.Users.CountAsync(u => u.Role == Roles.Admin) == 1)
                return Http.Error(400, "last_admin", "Debe quedar al menos un administrador");
            user.Role = req.Role;
            await db.SaveChangesAsync();
            return Results.Ok(new { user.Id, user.Username, user.Role });
        }).RequireAuthorization("admin");

        // The new random password is returned once; only its PBKDF2 hash is stored.
        api.MapPost("/users/{id:int}/reset-password", async (int id, AppDb db, PasswordHasher hasher) =>
        {
            var user = await db.Users.FindAsync(id);
            if (user is null) return Http.Error(404, "not_found", "Usuario inexistente");
            var password = RandomNumberGenerator.GetString(PasswordAlphabet, 16);
            SetPassword(user, password, hasher);
            await db.SaveChangesAsync();
            return Results.Ok(new ResetPasswordResponse(user.Username, password));
        }).RequireAuthorization("admin");

        // ---- Devices
        api.MapPost("/devices", async (RegisterDeviceRequest req, AppDb db, TokenService tokens, ClaimsPrincipal me, TimeProvider clock) =>
        {
            var name = req.Name?.Trim() ?? "";
            if (name.Length is < 1 or > 60) return Http.Error(400, "malformed", "Nombre de dispositivo inválido");
            if (!DeviceKinds.Contains(req.Kind)) return Http.Error(400, "malformed", "kind debe ser web, android o sim");
            var device = await CreateDevice(db, me.UserId(), name, req.Kind, clock);
            return Results.Created($"/api/devices/{device.Id:D}", new RegisterDeviceResponse(device.Id.ToString("D"), tokens.IssueDevice(device.Id, me.UserId())));
        }).RequireAuthorization("user");

        api.MapGet("/devices", async (AppDb db, ClaimsPrincipal me, TimeProvider clock) =>
        {
            var now = clock.Now();
            var list = await db.Devices.Where(d => !d.Revoked && (me.IsAdmin() || d.OwnerUserId == me.UserId())).OrderBy(d => d.CreatedAt).ToListAsync();
            return Results.Ok(list.Select(d => new
            {
                id = d.Id.ToString("D"), d.Name, d.Kind, online = DeviceMonitor.IsOnline(d, now),
                lastSeen = d.LastSeenAt, battery = d.LastBattery, lastRssi = d.LastRssi, intervalMs = d.IntervalMs, d.OwnerUserId,
            }));
        }).RequireAuthorization("user");

        api.MapDelete("/devices/{id:guid}", async (Guid id, AppDb db, SessionStore sessions, Analytics.AnalyticsEngine analytics) =>
        {
            var device = await db.Devices.FindAsync(id);
            if (device is null) return Http.Error(404, "not_found", "Dispositivo inexistente");
            device.Revoked = true;
            await db.SaveChangesAsync();
            sessions.RemoveDevice(id.ToString("D"));
            analytics.Forget(id);
            await db.Readings.Where(r => r.DeviceId == id).ExecuteDeleteAsync();
            await db.Devices.Where(d => d.Id == id).ExecuteDeleteAsync();
            return Results.NoContent();
        }).RequireAuthorization("admin");

        // ---- Handshake (device token)
        api.MapPost("/handshake", async (HandshakeRequest req, HttpContext ctx, AppDb db, ServerIdentity identity, SessionStore sessions,
            IConfiguration cfg, TimeProvider clock, bool? mitm) =>
        {
            if (ctx.User.DeviceId() != req.DeviceId) return Http.Error(401, "unauthorized", "El token no corresponde al dispositivo");
            if (!Guid.TryParse(req.DeviceId, out var did) || await db.Devices.FindAsync(did) is not { Revoked: false })
                return Http.Error(401, "unauthorized", "Dispositivo revocado o inexistente");
            byte[] pubC, nonceC;
            try
            {
                pubC = Convert.FromBase64String(req.ClientPublicKey);
                nonceC = Convert.FromBase64String(req.ClientNonce);
            }
            catch (FormatException) { return Http.Error(400, "malformed", "Base64 inválido"); }
            if (pubC.Length != 65 || pubC[0] != 4 || nonceC.Length != Protocol.NonceLength)
                return Http.Error(400, "malformed", "La llave debe ser un punto P-256 sin comprimir (65 B) y el nonce de 16 B");
            var suite = string.IsNullOrEmpty(req.Suite) ? Suites.AesGcm : req.Suite;
            if (!SupportedSuites(cfg).Contains(suite)) return Http.Error(400, "unsupported_suite", $"Suite no soportada: {suite}");

            using var eph = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
            byte[] z;
            try { z = EcKeys.SharedSecretX(eph, pubC); }
            catch (CryptographicException) { return Http.Error(400, "malformed", "La llave pública no es un punto válido de la curva"); }
            var pubS = EcKeys.ExportPublic(eph);
            var nonceS = RandomNumberGenerator.GetBytes(Protocol.NonceLength);
            var sid = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(8));
            var keys = SessionKeys.Derive(z, nonceC, nonceS, suite);
            sessions.Add(sid, req.DeviceId, keys);
            var transcript = Handshake.Transcript(pubC, pubS, nonceC, nonceS, req.DeviceId, sid, suite);
            // Lab demo: an attacker in the middle signs with a key that is not the pinned one; the client must reject it.
            var signer = mitm == true && cfg.GetValue("Lab:Enabled", false) && cfg.GetValue("Lab:EnableMitmDemo", false) ? identity.AttackerKey : identity.Key;
            var sig = Handshake.Sign(signer, transcript);
            var exp = clock.GetUtcNow().Add(sessions.Lifetime).ToUnixTimeMilliseconds();
            return Results.Ok(new HandshakeResponse(sid, Convert.ToBase64String(pubS), Convert.ToBase64String(nonceS), Convert.ToBase64String(sig), exp, suite));
        }).RequireAuthorization("device");

        // ---- Telemetry (device token). The body is read raw so malformed JSON gets our own 400 code.
        api.MapPost("/telemetry", async (HttpContext ctx, TelemetryPipeline pipeline, IConfiguration cfg) =>
        {
            var max = cfg.GetValue("Telemetry:MaxBodyBytes", 65536);
            using var ms = new MemoryStream();
            var buffer = new byte[8192];
            int n;
            while ((n = await ctx.Request.Body.ReadAsync(buffer)) > 0)
            {
                ms.Write(buffer, 0, n);
                if (ms.Length > max) return Http.Error(400, "malformed", "Cuerpo demasiado grande");
            }
            var r = await pipeline.ProcessAsync(ms.ToArray(), ctx.User.DeviceId(), Http.Source(ctx), "http", ctx.RequestAborted);
            return r.Ok ? Results.Json(new TelemetryAck(r.Ack!.Value), statusCode: 202) : Http.Error(r.Status, r.Code, r.Message ?? r.Code);
        }).RequireAuthorization("device").RequireRateLimiting("telemetry");

        // ---- Link probe (no auth, rate limited): echoes a body of the same size; download for throughput.
        api.MapPost("/ping", async (HttpContext ctx) =>
        {
            using var ms = new MemoryStream();
            await ctx.Request.Body.CopyToAsync(ms);
            if (ms.Length > 1024) return Http.Error(400, "malformed", "Máximo 1 KB");
            return Results.Bytes(RandomNumberGenerator.GetBytes((int)ms.Length), "application/octet-stream");
        }).RequireRateLimiting("ping");

        api.MapGet("/ping/download", (int? kb) => Results.Bytes(RandomNumberGenerator.GetBytes(Math.Clamp(kb ?? 32, 1, 256) * 1024), "application/octet-stream"))
            .RequireRateLimiting("ping");

        app.MapGet("/healthz", (SessionStore s, TimeProvider clock) => Results.Ok(new { status = "ok", time = clock.Now(), sessions = s.Count }));
    }

    public static string[] SupportedSuites(IConfiguration cfg)
        => cfg.GetValue("Crypto:AllowChaCha", true) && Suites.IsSupported(Suites.ChaCha20Poly1305) ? [Suites.AesGcm, Suites.ChaCha20Poly1305] : [Suites.AesGcm];

    public static async Task<Device> CreateDevice(AppDb db, int ownerId, string name, string kind, TimeProvider clock)
    {
        var device = new Device { Id = Guid.NewGuid(), OwnerUserId = ownerId, Name = name, Kind = kind, CreatedAt = clock.Now() };
        db.Devices.Add(device);
        await db.SaveChangesAsync();
        return device;
    }
}
