using Microsoft.EntityFrameworkCore;

namespace SignalTrace.Server.Data;

/// <summary>Cuenta de una persona que usa la interfaz web.</summary>
public sealed class User
{
    /// <summary>Llave primaria.</summary>
    public int Id { get; set; }
    /// <summary>Nombre de usuario único.</summary>
    public string Username { get; set; } = "";
    /// <summary>Sal aleatoria de PBKDF2.</summary>
    public byte[] Salt { get; set; } = [];
    /// <summary>Iteraciones de PBKDF2 con las que se calculó el hash (permite subirlas sin romper cuentas viejas).</summary>
    public int Iterations { get; set; }
    /// <summary>Hash PBKDF2-HMAC-SHA256 de la contraseña.</summary>
    public byte[] PasswordHash { get; set; } = [];
    /// <summary>Rol: administrador u observador.</summary>
    public string Role { get; set; } = Roles.Viewer;
    /// <summary>Fecha de alta en ms Unix.</summary>
    public long CreatedAt { get; set; }
}

/// <summary>Roles de usuario.</summary>
public static class Roles
{
    /// <summary>Puede ver todo, administrar usuarios y usar la consola y el laboratorio.</summary>
    public const string Admin = "admin";
    /// <summary>Solo ve sus propios dispositivos.</summary>
    public const string Viewer = "viewer";
    /// <summary>Indica si el texto es un rol válido.</summary>
    public static bool IsValid(string? r) => r is Admin or Viewer;
}

/// <summary>Dispositivo registrado que envía telemetría.</summary>
public sealed class Device
{
    /// <summary>Identificador del dispositivo (también va en el AAD de cada paquete).</summary>
    public Guid Id { get; set; }
    /// <summary>Usuario dueño del dispositivo.</summary>
    public int OwnerUserId { get; set; }
    /// <summary>Nombre visible.</summary>
    public string Name { get; set; } = "";
    /// <summary>Tipo: web, android o sim.</summary>
    public string Kind { get; set; } = "web";
    /// <summary>Fecha de registro en ms Unix.</summary>
    public long CreatedAt { get; set; }
    /// <summary>Último paquete aceptado, en ms Unix.</summary>
    public long? LastSeenAt { get; set; }
    /// <summary>Si fue revocado; un dispositivo revocado ya no puede abrir sesiones.</summary>
    public bool Revoked { get; set; }
    /// <summary>Último nivel de batería conocido (0 a 1).</summary>
    public double? LastBattery { get; set; }
    /// <summary>Última intensidad de señal conocida (dBm).</summary>
    public int? LastRssi { get; set; }
    /// <summary>Intervalo medio entre paquetes (ms); sirve para detectar cuando deja de enviar.</summary>
    public double? IntervalMs { get; set; }
}

/// <summary>
/// Lectura aceptada y descifrada. Los valores más consultados se copian a columnas propias para poder
/// filtrarlos y graficarlos sin leer el JSON completo.
/// </summary>
public sealed class Reading
{
    /// <summary>Llave primaria.</summary>
    public long Id { get; set; }
    /// <summary>Dispositivo que envió la lectura.</summary>
    public Guid DeviceId { get; set; }
    /// <summary>Marca de tiempo del dispositivo (ms Unix).</summary>
    public long TsDevice { get; set; }
    /// <summary>Momento en que la recibió el servidor (ms Unix).</summary>
    public long TsServer { get; set; }
    /// <summary>Número de secuencia del paquete.</summary>
    public long Seq { get; set; }
    /// <summary>Sesión en la que llegó.</summary>
    public string SessionId { get; set; } = "";
    /// <summary>Origen (web, android, sim).</summary>
    public string Src { get; set; } = "";
    /// <summary>RMS de la aceleración.</summary>
    public double? AccRms { get; set; }
    /// <summary>Norma del giroscopio.</summary>
    public double? GyrNorm { get; set; }
    /// <summary>Iluminancia.</summary>
    public double? Lux { get; set; }
    /// <summary>Presión barométrica.</summary>
    public double? Hpa { get; set; }
    /// <summary>Nivel de batería.</summary>
    public double? BatLvl { get; set; }
    /// <summary>Tipo de red.</summary>
    public string? NetType { get; set; }
    /// <summary>RTT del sondeo de enlace.</summary>
    public double? ProbeRtt { get; set; }
    /// <summary>Pérdida del sondeo de enlace.</summary>
    public double? ProbeLoss { get; set; }
    /// <summary>RSSI del Wi-Fi.</summary>
    public int? WifiRssi { get; set; }
    /// <summary>Potencia de la señal celular.</summary>
    public int? CellDbm { get; set; }
    /// <summary>Tecnología celular.</summary>
    public string? CellType { get; set; }
    /// <summary>Carga descifrada completa (JSON en claro, o "enc:" + Base64 AES-GCM cuando Storage:EncryptAtRest está activo).</summary>
    public string Json { get; set; } = "";
}

/// <summary>
/// Registro de cada paquete recibido, aceptado o rechazado, con el motivo del rechazo. Es lo que muestran
/// la consola y el historial de paquetes.
/// </summary>
public sealed class Packet
{
    /// <summary>Llave primaria.</summary>
    public long Id { get; set; }
    /// <summary>Dispositivo, si se pudo identificar.</summary>
    public Guid? DeviceId { get; set; }
    /// <summary>Sesión indicada en el sobre.</summary>
    public string? SessionId { get; set; }
    /// <summary>Número de secuencia indicado en el sobre.</summary>
    public long? Seq { get; set; }
    /// <summary>Momento de recepción (ms Unix).</summary>
    public long TsServer { get; set; }
    /// <summary>Resultado: aceptado o rechazado.</summary>
    public string Status { get; set; } = "";
    /// <summary>Motivo del rechazo (etiqueta inválida, reenvío, fuera de ventana, …).</summary>
    public string? Reason { get; set; }
    /// <summary>Tamaño en bytes del sobre tal como se transmitió.</summary>
    public int WireBytes { get; set; }
    /// <summary>Hash del origen (IP) para agrupar sin guardar la dirección en claro.</summary>
    public string SourceHash { get; set; } = "";
    /// <summary>Transporte: http o mqtt.</summary>
    public string? Transport { get; set; }
    /// <summary>Texto cifrado en Base64, para la consola.</summary>
    public string? CtB64 { get; set; }
}

/// <summary>Contexto de Entity Framework Core sobre SQLite con las cuatro tablas del sistema.</summary>
/// <param name="options">Opciones con la cadena de conexión.</param>
public sealed class AppDb(DbContextOptions<AppDb> options) : DbContext(options)
{
    /// <summary>Usuarios.</summary>
    public DbSet<User> Users => Set<User>();
    /// <summary>Dispositivos.</summary>
    public DbSet<Device> Devices => Set<Device>();
    /// <summary>Lecturas aceptadas.</summary>
    public DbSet<Reading> Readings => Set<Reading>();
    /// <summary>Bitácora de paquetes.</summary>
    public DbSet<Packet> Packets => Set<Packet>();

    /// <summary>Define los índices: nombre de usuario único y búsquedas por dispositivo y tiempo.</summary>
    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<User>().HasIndex(u => u.Username).IsUnique();
        b.Entity<Reading>().HasIndex(r => new { r.DeviceId, r.TsDevice });
        b.Entity<Packet>().HasIndex(p => new { p.DeviceId, p.TsServer });
    }
}
