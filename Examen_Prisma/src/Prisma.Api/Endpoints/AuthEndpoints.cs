// AuthEndpoints.cs — Rutas de autenticación (/api/auth) y de cuenta (/api/account).
// Registro, inicio de sesión en uno o dos pasos (2FA opcional con imagen-llave), perfil, cambio de
// contraseña y activación/desactivación del 2FA. La imagen-llave es una portadora en la que ABCO
// oculta un secreto aleatorio de 32 B usando la contraseña del usuario como frase.
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Prisma.Api.Data;
using Prisma.Api.Services;
using Prisma.Core;

namespace Prisma.Api.Endpoints;

/// <summary>Cuerpo JSON de registro e inicio de sesión.</summary>
/// <param name="Username">Nombre de usuario.</param>
/// <param name="Password">Contraseña en claro (solo viaja por HTTPS).</param>
public sealed record Credentials(string Username, string Password);
/// <summary>Cuerpo JSON con solo la contraseña, para confirmar acciones sensibles.</summary>
/// <param name="Password">Contraseña actual.</param>
public sealed record PasswordBody(string Password);
/// <summary>Cuerpo JSON del cambio de contraseña.</summary>
/// <param name="CurrentPassword">Contraseña actual (o la temporal).</param>
/// <param name="NewPassword">Contraseña nueva (al menos 8 caracteres y distinta de la actual).</param>
public sealed record ChangePasswordBody(string CurrentPassword, string NewPassword);

/// <summary>Registro de las rutas de autenticación y de cuenta.</summary>
public static partial class AuthEndpoints
{
    /// <summary>
    /// Error genérico de inicio de sesión: no indica si falló el usuario o la contraseña, para no
    /// confirmar qué nombres de usuario existen.
    /// </summary>
    private const string BadCredentials = "Usuario o contraseña incorrectos.";
    /// <summary>Error genérico de la imagen-llave: no distingue imagen equivocada, alterada o sin mensaje.</summary>
    private const string BadKey = "La imagen-llave no es válida para este usuario.";

    /// <summary>Regla de nombres de usuario: 3 a 32 caracteres de letras ASCII, dígitos, "_", "." o "-".</summary>
    [GeneratedRegex("^[A-Za-z0-9_.-]{3,32}$")]
    private static partial Regex UsernameRule();

    /// <summary>Registra las rutas públicas de /api/auth, todas con el límite de intentos "auth" (10/min por IP → 429).</summary>
    /// <param name="app">Aplicación web.</param>
    public static void MapAuth(this WebApplication app)
    {
        var auth = app.MapGroup("/api/auth").RequireRateLimiting("auth");

        // POST /api/auth/register — público.
        // Entrada: JSON {"username","password"}; usuario según UsernameRule, contraseña ≥ 8 caracteres.
        // Respuestas: 201 {"id","username"} · 400 si no cumple las reglas · 409 si el usuario ya existe
        // (la comparación ignora mayúsculas por la collation NOCASE). Los usuarios nuevos tienen rol "user".
        auth.MapPost("/register", async (Credentials body, AppDb db, AuditService audit) =>
        {
            var sw = Stopwatch.StartNew();
            if (!UsernameRule().IsMatch(body.Username ?? ""))
                return Http.Error(400, "El usuario debe tener 3 a 32 caracteres: letras, números, punto, guion o guion bajo.");
            if ((body.Password ?? "").Length < 8) return Http.Error(400, "La contraseña debe tener al menos 8 caracteres.");
            if (await db.Users.AnyAsync(u => u.Username == body.Username)) return Http.Error(409, "Ese usuario ya existe.");
            var (hash, salt) = Passwords.Hash(body.Password!);
            var user = new User { Username = body.Username!, PwdHash = hash, PwdSalt = salt };
            db.Users.Add(user);
            await db.SaveChangesAsync();
            await audit.LogAsync(null, "registro", null, 0, sw, true, user.Username);
            return Results.Created($"/api/account/me", new { user.Id, user.Username });
        });

        // Paso 1: usuario + contraseña. Si el usuario tiene 2FA, no se entrega token: se pide la imagen-llave.
        // POST /api/auth/login — público.
        // Entrada: JSON {"username","password"}.
        // Respuestas: 200 {"token","username","role","twoFactorEnabled":false,"mustChangePassword"} ·
        // 200 {"requires2fa":true} sin token · 401 con el error genérico BadCredentials.
        // Seguridad: la contraseña se verifica con PBKDF2 y comparación en tiempo constante. Si el usuario
        // no existe no se calcula PBKDF2, así que el tiempo de respuesta difiere; el límite de intentos
        // reduce la utilidad de esa diferencia. Todos los intentos quedan en la auditoría.
        auth.MapPost("/login", async (Credentials body, AppDb db, TokenService tokens, AuditService audit) =>
        {
            var sw = Stopwatch.StartNew();
            var user = await db.Users.FirstOrDefaultAsync(u => u.Username == body.Username);
            if (user is null || !Passwords.Verify(body.Password ?? "", user.PwdHash, user.PwdSalt))
            {
                await audit.LogAsync(null, "login", null, 0, sw, false, body.Username);
                return Http.Error(401, BadCredentials);
            }
            if (user.TwoFactorEnabled)
            {
                await audit.LogAsync(null, "login: requiere 2FA", null, 0, sw, true, user.Username);
                return Results.Ok(new { requires2fa = true });
            }
            await audit.LogAsync(null, "login", null, 0, sw, true, user.Username);
            return Results.Ok(Session(user, tokens));
        });

        // Paso 2 (solo con 2FA): usuario + contraseña + imagen-llave, en un mismo formulario multipart.
        // POST /api/auth/login/2fa — público.
        // Entrada: multipart con "username", "password" y el archivo "keyImage" (PNG).
        // Respuestas: 200 {"token","username","role","twoFactorEnabled":true,"mustChangePassword"} ·
        // 400 si no es multipart o falta la imagen · 401 con BadCredentials o BadKey.
        // Seguridad: la contraseña se vuelve a verificar aquí (el paso 1 no deja estado en el servidor).
        // Se revela el secreto oculto con ABCO usando la contraseña como frase y se compara su SHA-256
        // con el guardado en tiempo constante. Cualquier fallo al revelar da el mismo error genérico.
        auth.MapPost("/login/2fa", async (HttpRequest req, AppDb db, TokenService tokens, AuditService audit, TraceStore traces) =>
        {
            var sw = Stopwatch.StartNew();
            if (!req.HasFormContentType) return Http.Error(400, "Se espera multipart/form-data.");
            var form = await req.ReadFormAsync();
            string username = form["username"].ToString(), password = form["password"].ToString();
            var user = await db.Users.FirstOrDefaultAsync(u => u.Username == username);
            if (user is null || !Passwords.Verify(password, user.PwdHash, user.PwdSalt))
            {
                await audit.LogAsync(null, "login 2FA", "prisma", 0, sw, false, username);
                return Http.Error(401, BadCredentials);
            }
            // Si el 2FA se desactivó entre el paso 1 y el 2, la contraseña ya basta.
            if (!user.TwoFactorEnabled || user.KeyTokenHash is null) return Results.Ok(Session(user, tokens));

            var file = form.Files.GetFile("keyImage");
            if (file is not { Length: > 0 } || file.Length > Http.MaxUploadBytes) return Http.Error(400, "Adjunta tu imagen-llave (llave.png).");
            try
            {
                using var ms = new MemoryStream();
                await file.CopyToAsync(ms);
                var trace = traces.New("revelar imagen-llave (login 2FA)");
                byte[] token = PrismaStego.Reveal(ImageIO.Decode(ms.ToArray()), password, trace);
                // Solo se guarda SHA-256(token): aunque se filtre la base, no se puede fabricar una imagen-llave.
                bool ok = CryptographicOperations.FixedTimeEquals(SHA256.HashData(token), user.KeyTokenHash);
                traces.Save(trace, user.Username);
                await audit.LogAsync(null, "login 2FA", "prisma", token.Length, sw, ok, user.Username);
                return ok ? Results.Ok(Session(user, tokens)) : Http.Error(401, BadKey);
            }
            catch (Exception ex) when (ex is StegoException or InvalidDataException)
            {
                await audit.LogAsync(null, "login 2FA", "prisma", 0, sw, false, user.Username);
                return Http.Error(401, BadKey);
            }
        });
    }

    /// <summary>Respuesta de sesión: token nuevo y datos básicos del usuario.</summary>
    /// <param name="u">Usuario autenticado.</param>
    /// <param name="tokens">Emisor de JWT.</param>
    /// <returns>Objeto anónimo {"token","username","role","twoFactorEnabled","mustChangePassword"}.</returns>
    private static object Session(User u, TokenService tokens) =>
        new { token = tokens.Issue(u), username = u.Username, role = u.Role, twoFactorEnabled = u.TwoFactorEnabled, mustChangePassword = u.MustChangePassword };

    /// <summary>Registra las rutas de /api/account; todas requieren token.</summary>
    /// <param name="app">Aplicación web.</param>
    public static void MapAccount(this WebApplication app)
    {
        var acc = app.MapGroup("/api/account").RequireAuthorization();

        // GET /api/account/me — requiere token (permitida también con contraseña temporal).
        // Respuestas: 200 {"id","username","role","twoFactorEnabled","mustChangePassword","createdUtc"} ·
        // 404 si el usuario del token ya no existe.
        acc.MapGet("/me", async (HttpContext ctx, AppDb db) =>
            await db.Users.FindAsync(ctx.User.UserId()) is { } u
                ? Results.Ok(new { u.Id, u.Username, u.Role, u.TwoFactorEnabled, u.MustChangePassword, u.CreatedUtc })
                : Http.Error(404, "Usuario no encontrado."));

        // Activa el 2FA: genera un token aleatorio de 32 B, lo oculta con ABCO (frase = contraseña)
        // en la imagen elegida y devuelve llave.png. Solo se guarda SHA-256(token).
        // POST /api/account/2fa/enable — requiere token.
        // Entrada: multipart con "password" y la portadora ("image" o "catalogId").
        // Respuestas: 200 image/png como adjunto llave_<usuario>.png · 400/404/413 si falla la imagen ·
        // 401 si la contraseña es incorrecta.
        // Seguridad: se pide la contraseña aunque haya sesión (reautenticación), porque es la frase de ABCO.
        // Volver a activarlo reemplaza el hash y deja inservible la imagen-llave anterior.
        acc.MapPost("/2fa/enable", async (HttpContext ctx, AppDb db, CatalogService catalog, AuditService audit, TraceStore traces) =>
        {
            var sw = Stopwatch.StartNew();
            if (!ctx.Request.HasFormContentType) return Http.Error(400, "Se espera multipart/form-data.");
            var form = await ctx.Request.ReadFormAsync();
            var user = await db.Users.FindAsync(ctx.User.UserId());
            if (user is null) return Http.Error(404, "Usuario no encontrado.");
            string password = form["password"].ToString();
            if (!Passwords.Verify(password, user.PwdHash, user.PwdSalt)) return Http.Error(401, "La contraseña no es correcta.");

            var (img, err) = await Http.ReadImageAsync(form, catalog);
            if (err is not null) return err;
            // 512 px por lado bastan de sobra para 32 B y mantienen ligera la imagen-llave.
            var cover = ImageIO.FitWithin(img!, 512);

            byte[] token = RandomNumberGenerator.GetBytes(32);
            var trace = traces.New("ocultar token 2FA en imagen-llave");
            var stego = PrismaStego.Embed(cover, token, password, trace);
            user.KeyTokenHash = SHA256.HashData(token);
            user.TwoFactorEnabled = true;
            await db.SaveChangesAsync();
            traces.Save(trace, user.Username, ImageIO.EncodePng(Metrics.DiffMap(cover, stego)));
            await audit.LogAsync(ctx.User, "activar 2FA", "prisma", token.Length, sw, true);
            return Results.File(ImageIO.EncodePng(stego), "image/png", $"llave_{user.Username}.png");
        });

        // Cambio de contraseña. La imagen-llave estaba cifrada con la contraseña anterior,
        // así que si el 2FA estaba activo se desactiva y el usuario debe generar otra llave.
        // POST /api/account/password — requiere token (permitida también con contraseña temporal).
        // Entrada: JSON {"currentPassword","newPassword"}; la nueva debe tener ≥ 8 caracteres y ser distinta.
        // Respuestas: 200 {"twoFactorDisabled","session":{...}} · 400 si la nueva no es válida ·
        // 401 si la actual es incorrecta · 404 si el usuario no existe.
        // Seguridad: se genera una sal nueva y se quita la marca de contraseña temporal. El cliente debe
        // reemplazar su token por session.token; el anterior conserva la marca hasta expirar.
        acc.MapPost("/password", async (ChangePasswordBody body, HttpContext ctx, AppDb db, AuditService audit, TokenService tokens) =>
        {
            var sw = Stopwatch.StartNew();
            var user = await db.Users.FindAsync(ctx.User.UserId());
            if (user is null) return Http.Error(404, "Usuario no encontrado.");
            if (!Passwords.Verify(body.CurrentPassword ?? "", user.PwdHash, user.PwdSalt)) return Http.Error(401, "La contraseña actual no es correcta.");
            if ((body.NewPassword ?? "").Length < 8) return Http.Error(400, "La nueva contraseña debe tener al menos 8 caracteres.");
            if (body.NewPassword == body.CurrentPassword) return Http.Error(400, "La nueva contraseña debe ser distinta de la actual.");
            bool had2fa = user.TwoFactorEnabled;
            (user.PwdHash, user.PwdSalt) = Passwords.Hash(body.NewPassword!);
            user.MustChangePassword = false;
            // La frase con que se ocultó el secreto de la imagen-llave era la contraseña anterior: con la nueva
            // ya no se podría revelar, así que el 2FA se apaga en lugar de dejar al usuario sin acceso.
            user.TwoFactorEnabled = false; user.KeyTokenHash = null;
            await db.SaveChangesAsync();
            await audit.LogAsync(ctx.User, "cambiar contraseña", null, 0, sw, true);
            // Se entrega una sesión nueva sin la marca de contraseña temporal.
            return Results.Ok(new { twoFactorDisabled = had2fa, session = Session(user, tokens) });
        });

        // POST /api/account/2fa/disable — requiere token.
        // Entrada: JSON {"password"}. Respuestas: 204 · 401 si la contraseña es incorrecta · 404 si el usuario no existe.
        // Seguridad: se pide la contraseña para que una sesión robada no pueda quitar el segundo factor.
        acc.MapPost("/2fa/disable", async (PasswordBody body, HttpContext ctx, AppDb db, AuditService audit) =>
        {
            var sw = Stopwatch.StartNew();
            var user = await db.Users.FindAsync(ctx.User.UserId());
            if (user is null) return Http.Error(404, "Usuario no encontrado.");
            if (!Passwords.Verify(body.Password ?? "", user.PwdHash, user.PwdSalt)) return Http.Error(401, "La contraseña no es correcta.");
            user.TwoFactorEnabled = false; user.KeyTokenHash = null;
            await db.SaveChangesAsync();
            await audit.LogAsync(ctx.User, "desactivar 2FA", null, 0, sw, true);
            return Results.NoContent();
        });
    }
}
