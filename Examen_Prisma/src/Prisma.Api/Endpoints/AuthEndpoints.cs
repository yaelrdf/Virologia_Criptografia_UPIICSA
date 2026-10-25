using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Prisma.Api.Data;
using Prisma.Api.Services;
using Prisma.Core;

namespace Prisma.Api.Endpoints;

public sealed record Credentials(string Username, string Password);
public sealed record PasswordBody(string Password);
public sealed record ChangePasswordBody(string CurrentPassword, string NewPassword);

public static partial class AuthEndpoints
{
    private const string BadCredentials = "Usuario o contraseña incorrectos.";
    private const string BadKey = "La imagen-llave no es válida para este usuario.";

    [GeneratedRegex("^[A-Za-z0-9_.-]{3,32}$")]
    private static partial Regex UsernameRule();

    public static void MapAuth(this WebApplication app)
    {
        var auth = app.MapGroup("/api/auth").RequireRateLimiting("auth");

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
            if (!user.TwoFactorEnabled || user.KeyTokenHash is null) return Results.Ok(Session(user, tokens));

            var file = form.Files.GetFile("keyImage");
            if (file is not { Length: > 0 } || file.Length > Http.MaxUploadBytes) return Http.Error(400, "Adjunta tu imagen-llave (llave.png).");
            try
            {
                using var ms = new MemoryStream();
                await file.CopyToAsync(ms);
                var trace = traces.New("revelar imagen-llave (login 2FA)");
                byte[] token = PrismaStego.Reveal(ImageIO.Decode(ms.ToArray()), password, trace);
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

    private static object Session(User u, TokenService tokens) =>
        new { token = tokens.Issue(u), username = u.Username, role = u.Role, twoFactorEnabled = u.TwoFactorEnabled, mustChangePassword = u.MustChangePassword };

    public static void MapAccount(this WebApplication app)
    {
        var acc = app.MapGroup("/api/account").RequireAuthorization();

        acc.MapGet("/me", async (HttpContext ctx, AppDb db) =>
            await db.Users.FindAsync(ctx.User.UserId()) is { } u
                ? Results.Ok(new { u.Id, u.Username, u.Role, u.TwoFactorEnabled, u.MustChangePassword, u.CreatedUtc })
                : Http.Error(404, "Usuario no encontrado."));

        // Activa el 2FA: genera un token aleatorio de 32 B, lo oculta con ABCO (frase = contraseña)
        // en la imagen elegida y devuelve llave.png. Solo se guarda SHA-256(token).
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
            user.TwoFactorEnabled = false; user.KeyTokenHash = null;
            await db.SaveChangesAsync();
            await audit.LogAsync(ctx.User, "cambiar contraseña", null, 0, sw, true);
            // Se entrega una sesión nueva sin la marca de contraseña temporal.
            return Results.Ok(new { twoFactorDisabled = had2fa, session = Session(user, tokens) });
        });

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
