using System.Security.Claims;
using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using SignalTrace.Core;
using SignalTrace.Server.Data;
using SignalTrace.Server.Hubs;
using SignalTrace.Server.Services;

namespace SignalTrace.Server.Endpoints;

/// <summary>Atajos comunes para los endpoints HTTP.</summary>
public static class Http
{
    /// <summary>Respuesta de error JSON con el formato de la API ({ code, message }).</summary>
    /// <param name="status">Código de estado HTTP.</param>
    /// <param name="code">Código estable del error.</param>
    /// <param name="message">Mensaje para la persona usuaria.</param>
    public static IResult Error(int status, string code, string message) => Results.Json(new ErrorResponse(code, message), statusCode: status);

    /// <summary>Hora actual en milisegundos Unix según el reloj inyectado (en las pruebas se puede simular).</summary>
    public static long Now(this TimeProvider clock) => clock.GetUtcNow().ToUnixTimeMilliseconds();

    /// <summary>Dirección IP del cliente, o «unknown» si no se conoce.</summary>
    public static string Source(HttpContext ctx) => ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown";
}

/// <summary>Endpoints principales (PLAN §7): inicio de sesión, usuarios, llave del servidor, dispositivos, handshake y telemetría.</summary>
public static class CoreEndpoints
{
    /// <summary>Tipos de dispositivo que se pueden registrar.</summary>
    public static readonly string[] DeviceKinds = ["web", "android", "sim"];
    /// <summary>Sal y hash de relleno para verificar contraseñas de usuarios que no existen.</summary>
    private static readonly byte[] DummySalt = new byte[16], DummyHash = new byte[32];
    /// <summary>Alfabeto de las contraseñas temporales; omite caracteres que se confunden (0/O, 1/l/I).</summary>
    private const string PasswordAlphabet = "ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz23456789";

    /// <summary>Valida la longitud de una contraseña nueva (8 a 128 caracteres).</summary>
    /// <returns>El mensaje de error, o null si es válida.</returns>
    private static string? PasswordError(string? password)
        => (password ?? "").Length is < 8 or > 128 ? "La contraseña debe tener entre 8 y 128 caracteres" : null;

    /// <summary>Calcula el hash PBKDF2 de la contraseña y lo guarda en el usuario junto con su sal e iteraciones.</summary>
    private static void SetPassword(User user, string password, PasswordHasher hasher)
    {
        var (salt, it, hash) = hasher.Hash(password);
        (user.Salt, user.Iterations, user.PasswordHash) = (salt, it, hash);
    }

    /// <summary>Registra todas las rutas de este grupo bajo <c>/api</c>, más <c>/healthz</c>.</summary>
    public static void MapCore(this WebApplication app)
    {
        var api = app.MapGroup("/api");

        // Inicio de sesión: valida usuario y contraseña, aplica el bloqueo por intentos fallidos y entrega un JWT.
        api.MapPost("/auth/login", async (LoginRequest req, AppDb db, TokenService tokens, LoginThrottle throttle, PasswordHasher hasher) =>
        {
            if (string.IsNullOrWhiteSpace(req.Username) || string.IsNullOrEmpty(req.Password))
                return Http.Error(400, "malformed", "Usuario y contraseña son obligatorios");
            if (throttle.RetryAfter(req.Username) is { } wait)
                return Http.Error(429, "rate_limited", $"Demasiados intentos. Espera {Math.Ceiling(wait.TotalSeconds)} s");
            var user = await db.Users.SingleOrDefaultAsync(u => u.Username == req.Username.Trim());
            // Ejecuta PBKDF2 aunque el usuario no exista, para que el tiempo de respuesta no revele qué nombres existen.
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

        // Devuelve el nombre y el rol del usuario del token.
        api.MapGet("/auth/me", (ClaimsPrincipal u) => Results.Ok(new { username = u.FindFirstValue("name"), role = u.FindFirstValue("role") }))
            .RequireAuthorization("user");

        // Cualquier usuario con sesión cambia su propia contraseña: primero se verifica la actual con PBKDF2.
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

        // Llave pública de largo plazo y huella; los clientes la fijan (pin) en el primer contacto.
        api.MapGet("/server-key", (ServerIdentity id, IConfiguration cfg) =>
            Results.Ok(new ServerKeyResponse(Convert.ToBase64String(id.PublicKey), id.Fingerprint, SupportedSuites(cfg))));

        // ---- Usuarios (solo administradores)
        // Alta de usuario con validación de nombre, contraseña y rol.
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

        // Lista de usuarios, sin sal ni hash.
        api.MapGet("/users", async (AppDb db) =>
            Results.Ok(await db.Users.OrderBy(u => u.Id).Select(u => new { u.Id, u.Username, u.Role, u.CreatedAt, u.Iterations }).ToListAsync()))
            .RequireAuthorization("admin");

        // Baja de usuario; un administrador no puede borrarse a sí mismo.
        api.MapDelete("/users/{id:int}", async (int id, AppDb db, ClaimsPrincipal me) =>
        {
            if (id == me.UserId()) return Http.Error(400, "malformed", "No puedes eliminar tu propio usuario");
            var n = await db.Users.Where(u => u.Id == id).ExecuteDeleteAsync();
            return n == 0 ? Http.Error(404, "not_found", "Usuario inexistente") : Results.NoContent();
        }).RequireAuthorization("admin");

        // Cambio de rol; impide quitar el rol al último administrador.
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

        // La contraseña aleatoria nueva se devuelve una sola vez; solo se guarda su hash PBKDF2.
        api.MapPost("/users/{id:int}/reset-password", async (int id, AppDb db, PasswordHasher hasher) =>
        {
            var user = await db.Users.FindAsync(id);
            if (user is null) return Http.Error(404, "not_found", "Usuario inexistente");
            var password = RandomNumberGenerator.GetString(PasswordAlphabet, 16);
            SetPassword(user, password, hasher);
            await db.SaveChangesAsync();
            return Results.Ok(new ResetPasswordResponse(user.Username, password));
        }).RequireAuthorization("admin");

        // ---- Dispositivos
        // Registro de un dispositivo del usuario; responde con su id y su token de dispositivo.
        api.MapPost("/devices", async (RegisterDeviceRequest req, AppDb db, TokenService tokens, ClaimsPrincipal me, TimeProvider clock) =>
        {
            var name = req.Name?.Trim() ?? "";
            if (name.Length is < 1 or > 60) return Http.Error(400, "malformed", "Nombre de dispositivo inválido");
            if (!DeviceKinds.Contains(req.Kind)) return Http.Error(400, "malformed", "kind debe ser web, android o sim");
            var device = await CreateDevice(db, me.UserId(), name, req.Kind, clock);
            return Results.Created($"/api/devices/{device.Id:D}", new RegisterDeviceResponse(device.Id.ToString("D"), tokens.IssueDevice(device.Id, me.UserId())));
        }).RequireAuthorization("user");

        // Lista de dispositivos: el administrador ve todos y un observador solo los suyos.
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

        // Revocación y borrado: primero se marca revocado (corta sesiones nuevas), se cierran las abiertas y
        // se borran sus lecturas y el registro.
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

        // ---- Handshake (token de dispositivo)
        // Valida la solicitud, genera la llave efímera del servidor, deriva las llaves de sesión, guarda la
        // sesión y firma el transcript con la llave de largo plazo.
        api.MapPost("/handshake", async (HandshakeRequest req, HttpContext ctx, AppDb db, ServerIdentity identity, SessionStore sessions,
            IConfiguration cfg, TimeProvider clock, bool? mitm) =>
        {
            // El token debe pertenecer al mismo dispositivo que pide la sesión y el dispositivo debe seguir activo.
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

            // Llave efímera del servidor y secreto compartido; un punto fuera de la curva se rechaza aquí.
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
            // Demostración del laboratorio: un intermediario firma con una llave que no es la fijada; el cliente debe rechazarla.
            var signer = mitm == true && cfg.GetValue("Lab:Enabled", false) && cfg.GetValue("Lab:EnableMitmDemo", false) ? identity.AttackerKey : identity.Key;
            var sig = Handshake.Sign(signer, transcript);
            var exp = clock.GetUtcNow().Add(sessions.Lifetime).ToUnixTimeMilliseconds();
            return Results.Ok(new HandshakeResponse(sid, Convert.ToBase64String(pubS), Convert.ToBase64String(nonceS), Convert.ToBase64String(sig), exp, suite));
        }).RequireAuthorization("device");

        // ---- Telemetría (token de dispositivo). El cuerpo se lee sin procesar para que un JSON mal formado reciba nuestro propio código 400.
        api.MapPost("/telemetry", async (HttpContext ctx, TelemetryPipeline pipeline, IConfiguration cfg) =>
        {
            // Lee por bloques y corta en cuanto se pasa del máximo, sin cargar cuerpos enormes en memoria.
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

        // ---- Sondeo de enlace (sin autenticación, con límite): responde un cuerpo aleatorio del mismo tamaño; la descarga mide el rendimiento.
        api.MapPost("/ping", async (HttpContext ctx) =>
        {
            using var ms = new MemoryStream();
            await ctx.Request.Body.CopyToAsync(ms);
            if (ms.Length > 1024) return Http.Error(400, "malformed", "Máximo 1 KB");
            return Results.Bytes(RandomNumberGenerator.GetBytes((int)ms.Length), "application/octet-stream");
        }).RequireRateLimiting("ping");

        // Descarga de 1 a 256 KB aleatorios (32 KB por defecto) para medir el ancho de banda.
        api.MapGet("/ping/download", (int? kb) => Results.Bytes(RandomNumberGenerator.GetBytes(Math.Clamp(kb ?? 32, 1, 256) * 1024), "application/octet-stream"))
            .RequireRateLimiting("ping");

        // Estado del servidor para monitoreo: hora y número de sesiones abiertas.
        app.MapGet("/healthz", (SessionStore s, TimeProvider clock) => Results.Ok(new { status = "ok", time = clock.Now(), sessions = s.Count }));
    }

    /// <summary>Suites que ofrece el servidor: siempre AES-256-GCM y, si está permitido y disponible, ChaCha20-Poly1305.</summary>
    public static string[] SupportedSuites(IConfiguration cfg)
        => cfg.GetValue("Crypto:AllowChaCha", true) && Suites.IsSupported(Suites.ChaCha20Poly1305) ? [Suites.AesGcm, Suites.ChaCha20Poly1305] : [Suites.AesGcm];

    /// <summary>Crea y guarda un dispositivo nuevo con un GUID aleatorio.</summary>
    /// <param name="db">Base de datos.</param>
    /// <param name="ownerId">Usuario dueño.</param>
    /// <param name="name">Nombre visible.</param>
    /// <param name="kind">Tipo de dispositivo.</param>
    /// <param name="clock">Reloj para la fecha de alta.</param>
    public static async Task<Device> CreateDevice(AppDb db, int ownerId, string name, string kind, TimeProvider clock)
    {
        var device = new Device { Id = Guid.NewGuid(), OwnerUserId = ownerId, Name = name, Kind = kind, CreatedAt = clock.Now() };
        db.Devices.Add(device);
        await db.SaveChangesAsync();
        return device;
    }
}
