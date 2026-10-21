using System.Diagnostics;
using System.Security.Claims;
using Prisma.Api.Data;

namespace Prisma.Api.Services;

public sealed class AuditService(AppDb db)
{
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
