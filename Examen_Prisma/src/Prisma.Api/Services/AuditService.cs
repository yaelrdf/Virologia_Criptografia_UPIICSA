// AuditService.cs — Registro de la bitácora de auditoría.
// Cada ruta relevante (registro, inicio de sesión, ocultar/revelar, 2FA, administración) deja una
// entrada con el usuario, la acción, el modo, los bytes, la duración y el resultado. La consola de
// administrador la consulta en GET /api/admin/audit.
using System.Diagnostics;
using System.Security.Claims;
using Prisma.Api.Data;

namespace Prisma.Api.Services;

/// <summary>Escribe entradas de auditoría en la base de datos.</summary>
/// <param name="db">Contexto de datos de la petición actual (servicio scoped).</param>
public sealed class AuditService(AppDb db)
{
    /// <summary>Agrega y guarda una entrada de auditoría.</summary>
    /// <param name="user">Usuario autenticado, o null si la acción es anónima.</param>
    /// <param name="action">Descripción de la acción.</param>
    /// <param name="mode">Modo de esteganografía, si aplica.</param>
    /// <param name="bytes">Bytes de mensaje procesados.</param>
    /// <param name="sw">Cronómetro iniciado al comenzar la operación; se toma su tiempo transcurrido.</param>
    /// <param name="ok">Si la operación tuvo éxito.</param>
    /// <param name="username">
    /// Nombre a registrar cuando aún no hay sesión (registro, inicio de sesión fallido); si se omite,
    /// se toma del token.
    /// </param>
    public async Task LogAsync(ClaimsPrincipal? user, string action, string? mode, int bytes, Stopwatch sw, bool ok, string? username = null)
    {
        db.Audit.Add(new AuditEntry
        {
            UserId = int.TryParse(user?.FindFirstValue(ClaimTypes.NameIdentifier), out var id) ? id : null,
            Username = username ?? user?.Identity?.Name ?? "",
            Action = action, Mode = mode, Bytes = bytes, Ms = sw.ElapsedMilliseconds, Ok = ok,
        });
        await db.SaveChangesAsync();
    }
}
