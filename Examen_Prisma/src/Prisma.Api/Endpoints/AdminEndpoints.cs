// AdminEndpoints.cs — Rutas de la consola de administrador (/api/admin, solo rol "admin").
// Auditoría y lista de usuarios con nombres ofuscados con César, gestión de usuarios (contraseña
// temporal, rol, restablecer 2FA) y la traza paso a paso y el mapa de diferencias de la última
// operación ABCO.
using System.Diagnostics;
using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Prisma.Api.Data;
using Prisma.Api.Services;
using Prisma.Core;

namespace Prisma.Api.Endpoints;

/// <summary>Cuerpo JSON con un texto.</summary>
/// <param name="Text">Texto ofuscado a revertir.</param>
public sealed record TextBody(string Text);
/// <summary>Cuerpo JSON del cambio de rol.</summary>
/// <param name="Role">"admin" o "user".</param>
public sealed record RoleBody(string Role);

/// <summary>Consola de administrador: auditoría (usuarios ofuscados con César), traza ABCO y mapa de diferencias.</summary>
public static class AdminEndpoints
{
    /// <summary>Registra las rutas de /api/admin; todo el grupo exige token con rol admin (si no, 401/403).</summary>
    /// <param name="app">Aplicación web.</param>
    public static void MapAdmin(this WebApplication app)
    {
        var g = app.MapGroup("/api/admin").RequireAuthorization(p => p.RequireRole(Roles.Admin));

        // GET /api/admin/audit?take=100 — rol admin.
        // 200 [{"id","user","action","mode","bytes","ms","ok","utc"}], de la más reciente a la más antigua;
        // take se limita a [1, 500]. El usuario se muestra ofuscado con César para no exponerlo en pantalla.
        g.MapGet("/audit", async (AppDb db, int? take) =>
            (await db.Audit.OrderByDescending(a => a.Id).Take(Math.Clamp(take ?? 100, 1, 500)).ToListAsync())
            .Select(a => new { a.Id, user = Caesar.Obfuscate(a.Username), a.Action, a.Mode, a.Bytes, a.Ms, a.Ok, a.Utc }));

        // GET /api/admin/users — rol admin.
        // 200 [{"id","username","role","twoFactorEnabled","mustChangePassword","createdUtc"}] con el nombre ofuscado.
        // Nunca se devuelven derivaciones de contraseña ni hashes de imagen-llave.
        g.MapGet("/users", async (AppDb db) =>
            (await db.Users.OrderBy(u => u.Id).ToListAsync())
            .Select(u => new { u.Id, username = Caesar.Obfuscate(u.Username), u.Role, u.TwoFactorEnabled, u.MustChangePassword, u.CreatedUtc }));

        // POST /api/admin/deobfuscate — rol admin. Entrada: JSON {"text"}. 200 {"text"} con el texto revertido.
        g.MapPost("/deobfuscate", (TextBody b) => Results.Ok(new { text = Caesar.Deobfuscate(b.Text ?? "") }));

        // POST /api/admin/users/{id}/reset-2fa — rol admin. 204 · 404 si el usuario no existe.
        // Para usuarios que perdieron su imagen-llave: borra el hash y desactiva el segundo factor.
        g.MapPost("/users/{id:int}/reset-2fa", async (int id, AppDb db) =>
        {
            var u = await db.Users.FindAsync(id);
            if (u is null) return Http.Error(404, "Usuario no encontrado.");
            u.TwoFactorEnabled = false; u.KeyTokenHash = null;
            await db.SaveChangesAsync();
            return Results.NoContent();
        });

        // Restablece la contraseña a una temporal aleatoria (se muestra una sola vez) y obliga a cambiarla.
        // El 2FA se desactiva porque la imagen-llave dependía de la contraseña anterior.
        // POST /api/admin/users/{id}/reset-password — rol admin. 200 {"temporaryPassword"} · 404 si el usuario no existe.
        // Seguridad: 12 caracteres elegidos con el generador criptográfico entre 56 símbolos sin ambiguos
        // (sin I, O, l, o, 0 ni 1), ≈ 70 bits. Solo se guarda su derivación PBKDF2. La marca MustChangePassword
        // hace que el próximo token lleve el claim que bloquea las demás rutas con 403.
        g.MapPost("/users/{id:int}/reset-password", async (int id, AppDb db, HttpContext ctx, AuditService audit) =>
        {
            var sw = Stopwatch.StartNew();
            var u = await db.Users.FindAsync(id);
            if (u is null) return Http.Error(404, "Usuario no encontrado.");
            string temp = RandomNumberGenerator.GetString("ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnpqrstuvwxyz23456789", 12);
            (u.PwdHash, u.PwdSalt) = Passwords.Hash(temp);
            u.MustChangePassword = true;
            u.TwoFactorEnabled = false; u.KeyTokenHash = null;
            await db.SaveChangesAsync();
            await audit.LogAsync(ctx.User, $"restablecer contraseña (usuario #{u.Id})", null, 0, sw, true);
            return Results.Ok(new { temporaryPassword = temp });
        });

        // Cambia el rol entre "admin" y "user". No permite quitarse el rol a uno mismo ni dejar el sistema sin administradores.
        // POST /api/admin/users/{id}/role — rol admin. Entrada: JSON {"role":"admin"|"user"}.
        // Respuestas: 200 {"id","role"} (también si ya tenía ese rol) · 400 si el rol no es válido ·
        // 404 si el usuario no existe · 409 si es uno mismo o el último administrador.
        // El rol nuevo se aplica en el siguiente token del usuario; los tokens ya emitidos conservan el anterior hasta expirar.
        g.MapPost("/users/{id:int}/role", async (int id, RoleBody body, AppDb db, HttpContext ctx, AuditService audit) =>
        {
            var sw = Stopwatch.StartNew();
            if (body.Role is not (Roles.Admin or Roles.User)) return Http.Error(400, "Rol no válido: usa 'admin' o 'user'.");
            var u = await db.Users.FindAsync(id);
            if (u is null) return Http.Error(404, "Usuario no encontrado.");
            if (u.Role == body.Role) return Results.Ok(new { u.Id, u.Role });
            if (body.Role == Roles.User)
            {
                if (u.Id == ctx.User.UserId()) return Http.Error(409, "No puedes quitarte el rol de administrador a ti mismo.");
                if (await db.Users.CountAsync(x => x.Role == Roles.Admin) <= 1) return Http.Error(409, "Debe quedar al menos un administrador.");
            }
            u.Role = body.Role;
            await db.SaveChangesAsync();
            await audit.LogAsync(ctx.User, $"cambiar rol (usuario #{u.Id}) a {u.Role}", null, 0, sw, true);
            return Results.Ok(new { u.Id, u.Role });
        });

        // GET /api/admin/trace/last — rol admin.
        // 200 {"user","trace","text"}: usuario ofuscado, la traza como objeto y como texto ·
        // 404 si la traza está desactivada (Demo:Trace = false) o aún no hay operaciones ABCO.
        g.MapGet("/trace/last", (TraceStore t) =>
        {
            var (trace, user) = t.Last;
            if (!t.Enabled) return Http.Error(404, "La traza está desactivada (Demo:Trace = false).");
            // Si hay traza, Save también guardó el usuario: user no es null.
            return trace is null ? Http.Error(404, "Aún no hay operaciones ABCO.") : Results.Ok(new { user = Caesar.Obfuscate(user!), trace, text = trace.ToString() });
        });

        // GET /api/admin/diffmap/last — rol admin. 200 image/png (mapa |original − estego| amplificado) · 404 si no hay.
        g.MapGet("/diffmap/last", (TraceStore t) =>
            t.LastDiffPng is { } png ? Results.File(png, "image/png") : Http.Error(404, "Aún no hay un mapa de diferencias."));
    }
}
