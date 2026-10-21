using Microsoft.EntityFrameworkCore;

namespace Prisma.Api.Data;

public sealed class User
{
    public int Id { get; set; }
    public required string Username { get; set; }
    public required byte[] PwdHash { get; set; }
    public required byte[] PwdSalt { get; set; }
    public bool TwoFactorEnabled { get; set; }
    /// <summary>SHA-256 del token de 32 B oculto en la imagen-llave (nunca se guarda el token).</summary>
    public byte[]? KeyTokenHash { get; set; }
    public string Role { get; set; } = Roles.User;
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
}

public static class Roles
{
    public const string User = "user", Admin = "admin";
}

public sealed class AuditEntry
{
    public long Id { get; set; }
    public int? UserId { get; set; }
    public string Username { get; set; } = "";
    public required string Action { get; set; }
    public string? Mode { get; set; }
    public int Bytes { get; set; }
    public long Ms { get; set; }
    public bool Ok { get; set; }
    public DateTime Utc { get; set; } = DateTime.UtcNow;
}

public sealed class AppDb(DbContextOptions<AppDb> options) : DbContext(options)
{
    public DbSet<User> Users => Set<User>();
    public DbSet<AuditEntry> Audit => Set<AuditEntry>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<User>().HasIndex(u => u.Username).IsUnique();
        b.Entity<User>().Property(u => u.Username).UseCollation("NOCASE");
        b.Entity<AuditEntry>().HasIndex(a => a.Utc);
    }
}
