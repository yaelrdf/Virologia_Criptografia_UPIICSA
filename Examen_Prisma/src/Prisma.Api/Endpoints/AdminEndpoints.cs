using System.Diagnostics;
using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Prisma.Api.Data;
using Prisma.Api.Services;
using Prisma.Core;

namespace Prisma.Api.Endpoints;

public sealed record TextBody(string Text);
public sealed record RoleBody(string Role);

/// <summary>Consola de administrador: auditoría (usuarios ofuscados con César), traza ABCO y mapa de diferencias.</summary>
public static class AdminEndpoints
{
    public static void MapAdmin(this WebApplication app)
    {
        var g = app.MapGroup("/api/admin").RequireAuthorization(p => p.RequireRole(Roles.Admin));

        g.MapGet("/audit", async (AppDb db, int? take) =>
            (await db.Audit.OrderByDescending(a => a.Id).Take(Math.Clamp(take ?? 100, 1, 500)).ToListAsync())
            .Select(a => new { a.Id, user = Caesar.Obfuscate(a.Username), a.Action, a.Mode, a.Bytes, a.Ms, a.Ok, a.Utc }));

        g.MapGet("/users", async (AppDb db) =>
            (await db.Users.OrderBy(u => u.Id).ToListAsync())
            .Select(u => new { u.Id, username = Caesar.Obfuscate(u.Username), u.Role, u.TwoFactorEnabled, u.MustChangePassword, u.CreatedUtc }));

        g.MapPost("/deobfuscate", (TextBody b) => Results.Ok(new { text = Caesar.Deobfuscate(b.Text ?? "") }));

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

        g.MapGet("/trace/last", (TraceStore t) =>
        {
            var (trace, user) = t.Last;
            if (!t.Enabled) return Http.Error(404, "La traza está desactivada (Demo:Trace = false).");
            return trace is null ? Http.Error(404, "Aún no hay operaciones ABCO.") : Results.Ok(new { user = Caesar.Obfuscate(user!), trace, text = trace.ToString() });
        });

        g.MapGet("/diffmap/last", (TraceStore t) =>
            t.LastDiffPng is { } png ? Results.File(png, "image/png") : Http.Error(404, "Aún no hay un mapa de diferencias."));
    }
}
