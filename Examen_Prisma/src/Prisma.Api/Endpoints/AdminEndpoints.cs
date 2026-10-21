using Microsoft.EntityFrameworkCore;
using Prisma.Api.Data;
using Prisma.Api.Services;
using Prisma.Core;

namespace Prisma.Api.Endpoints;

public sealed record TextBody(string Text);

/// <summary>Consola de administrador: auditoría (usuarios ofuscados con César), traza PRISMA-L y mapa de diferencias.</summary>
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
            .Select(u => new { u.Id, username = Caesar.Obfuscate(u.Username), u.Role, u.TwoFactorEnabled, u.CreatedUtc }));

        g.MapPost("/deobfuscate", (TextBody b) => Results.Ok(new { text = Caesar.Deobfuscate(b.Text ?? "") }));

        g.MapPost("/users/{id:int}/reset-2fa", async (int id, AppDb db) =>
        {
            var u = await db.Users.FindAsync(id);
            if (u is null) return Http.Error(404, "Usuario no encontrado.");
            u.TwoFactorEnabled = false; u.KeyTokenHash = null;
            await db.SaveChangesAsync();
            return Results.NoContent();
        });

        g.MapGet("/trace/last", (TraceStore t) =>
        {
            var (trace, user) = t.Last;
            if (!t.Enabled) return Http.Error(404, "La traza está desactivada (Demo:Trace = false).");
            return trace is null ? Http.Error(404, "Aún no hay operaciones PRISMA-L.") : Results.Ok(new { user = Caesar.Obfuscate(user!), trace, text = trace.ToString() });
        });

        g.MapGet("/diffmap/last", (TraceStore t) =>
            t.LastDiffPng is { } png ? Results.File(png, "image/png") : Http.Error(404, "Aún no hay un mapa de diferencias."));
    }
}
