using Microsoft.EntityFrameworkCore;

namespace SignalTrace.Server.Data;

public sealed class User
{
    public int Id { get; set; }
    public string Username { get; set; } = "";
    public byte[] Salt { get; set; } = [];
    public int Iterations { get; set; }
    public byte[] PasswordHash { get; set; } = [];
    public string Role { get; set; } = Roles.Viewer;
    public long CreatedAt { get; set; }
}

public static class Roles
{
    public const string Admin = "admin";
    public const string Viewer = "viewer";
    public static bool IsValid(string? r) => r is Admin or Viewer;
}

public sealed class Device
{
    public Guid Id { get; set; }
    public int OwnerUserId { get; set; }
    public string Name { get; set; } = "";
    public string Kind { get; set; } = "web";
    public long CreatedAt { get; set; }
    public long? LastSeenAt { get; set; }
    public bool Revoked { get; set; }
    public double? LastBattery { get; set; }
    public int? LastRssi { get; set; }
    public double? IntervalMs { get; set; }
}

public sealed class Reading
{
    public long Id { get; set; }
    public Guid DeviceId { get; set; }
    public long TsDevice { get; set; }
    public long TsServer { get; set; }
    public long Seq { get; set; }
    public string SessionId { get; set; } = "";
    public string Src { get; set; } = "";
    public double? Lat { get; set; }
    public double? Lon { get; set; }
    public double? Acc { get; set; }
    public double? Spd { get; set; }
    public double? AccRms { get; set; }
    public double? BatLvl { get; set; }
    public string? NetType { get; set; }
    public double? ProbeRtt { get; set; }
    public double? ProbeLoss { get; set; }
    public int? WifiRssi { get; set; }
    public int? CellDbm { get; set; }
    public string? CellType { get; set; }
    /// <summary>Full decrypted payload (plain JSON, or "enc:" + Base64 AES-GCM when Storage:EncryptAtRest is on).</summary>
    public string Json { get; set; } = "";
}

public sealed class Packet
{
    public long Id { get; set; }
    public Guid? DeviceId { get; set; }
    public string? SessionId { get; set; }
    public long? Seq { get; set; }
    public long TsServer { get; set; }
    public string Status { get; set; } = "";
    public string? Reason { get; set; }
    public int WireBytes { get; set; }
    public string SourceHash { get; set; } = "";
    public string? Transport { get; set; }
    public string? CtB64 { get; set; }
}

public sealed class Alert
{
    public long Id { get; set; }
    public Guid DeviceId { get; set; }
    public long Ts { get; set; }
    public string Kind { get; set; } = "";
    public double? Value { get; set; }
    public string Message { get; set; } = "";
    public bool Acknowledged { get; set; }
}

public sealed class AppDb(DbContextOptions<AppDb> options) : DbContext(options)
{
    public DbSet<User> Users => Set<User>();
    public DbSet<Device> Devices => Set<Device>();
    public DbSet<Reading> Readings => Set<Reading>();
    public DbSet<Packet> Packets => Set<Packet>();
    public DbSet<Alert> Alerts => Set<Alert>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<User>().HasIndex(u => u.Username).IsUnique();
        b.Entity<Reading>().HasIndex(r => new { r.DeviceId, r.TsDevice });
        b.Entity<Packet>().HasIndex(p => new { p.DeviceId, p.TsServer });
        b.Entity<Alert>().HasIndex(a => new { a.DeviceId, a.Ts });
    }
}
