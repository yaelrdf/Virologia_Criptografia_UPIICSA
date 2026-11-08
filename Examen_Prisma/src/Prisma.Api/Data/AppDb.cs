// AppDb.cs — Modelo de datos de la API (Entity Framework Core sobre SQLite).
// Define los usuarios (con su contraseña derivada, rol, 2FA por imagen-llave y marca de contraseña
// temporal), los roles y la bitácora de auditoría que consulta la consola de administrador.
using Microsoft.EntityFrameworkCore;

namespace Prisma.Api.Data;

/// <summary>Cuenta de usuario.</summary>
public sealed class User
{
    /// <summary>Identificador autoincremental; viaja en el token como claim NameIdentifier.</summary>
    public int Id { get; set; }
    /// <summary>Nombre de usuario, único sin distinguir mayúsculas de minúsculas.</summary>
    public required string Username { get; set; }
    /// <summary>Derivación PBKDF2-HMAC-SHA256 de la contraseña (32 B); nunca se guarda la contraseña.</summary>
    public required byte[] PwdHash { get; set; }
    /// <summary>Sal aleatoria de 16 B propia de este usuario.</summary>
    public required byte[] PwdSalt { get; set; }
    /// <summary>Si el inicio de sesión exige además la imagen-llave.</summary>
    public bool TwoFactorEnabled { get; set; }
    /// <summary>SHA-256 del token de 32 B oculto en la imagen-llave (nunca se guarda el token).</summary>
    public byte[]? KeyTokenHash { get; set; }
    /// <summary>Rol: <see cref="Roles.User"/> o <see cref="Roles.Admin"/>.</summary>
    public string Role { get; set; } = Roles.User;
    /// <summary>true tras un restablecimiento por el administrador: el usuario debe elegir otra contraseña.</summary>
    public bool MustChangePassword { get; set; }
    /// <summary>Fecha de registro (UTC).</summary>
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
}

/// <summary>Nombres de los roles; son los valores del claim de rol en el token.</summary>
public static class Roles
{
    /// <summary>Rol normal y rol con acceso a la consola de administrador.</summary>
    public const string User = "user", Admin = "admin";
}

/// <summary>Entrada de la bitácora de auditoría: quién hizo qué operación, con qué resultado y cuánto tardó.</summary>
/// <remarks>Nunca se guarda el mensaje ni la frase; solo metadatos. La consola admin muestra el usuario ofuscado con César.</remarks>
public sealed class AuditEntry
{
    /// <summary>Identificador autoincremental.</summary>
    public long Id { get; set; }
    /// <summary>Id del usuario autenticado, o null en acciones anónimas (registro, inicio de sesión).</summary>
    public int? UserId { get; set; }
    /// <summary>Nombre de usuario en el momento de la acción.</summary>
    public string Username { get; set; } = "";
    /// <summary>Acción realizada (p. ej. "ocultar", "login 2FA").</summary>
    public required string Action { get; set; }
    /// <summary>Modo de esteganografía ("prisma" o "clasico"), si aplica.</summary>
    public string? Mode { get; set; }
    /// <summary>Bytes de mensaje procesados.</summary>
    public int Bytes { get; set; }
    /// <summary>Duración en milisegundos.</summary>
    public long Ms { get; set; }
    /// <summary>Si la operación tuvo éxito.</summary>
    public bool Ok { get; set; }
    /// <summary>Momento de la acción (UTC).</summary>
    public DateTime Utc { get; set; } = DateTime.UtcNow;
}

/// <summary>Contexto de Entity Framework con las tablas Users y Audit.</summary>
/// <param name="options">Opciones configuradas en Program.cs (proveedor SQLite).</param>
public sealed class AppDb(DbContextOptions<AppDb> options) : DbContext(options)
{
    /// <summary>Tabla de usuarios.</summary>
    public DbSet<User> Users => Set<User>();
    /// <summary>Tabla de auditoría.</summary>
    public DbSet<AuditEntry> Audit => Set<AuditEntry>();

    /// <summary>Índices y collation del esquema.</summary>
    protected override void OnModelCreating(ModelBuilder b)
    {
        // Índice único con collation NOCASE: "Ana" y "ana" son el mismo usuario (evita cuentas suplantadoras).
        b.Entity<User>().HasIndex(u => u.Username).IsUnique();
        b.Entity<User>().Property(u => u.Username).UseCollation("NOCASE");
        b.Entity<AuditEntry>().HasIndex(a => a.Utc);
    }
}
