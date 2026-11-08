using System.Collections.Concurrent;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using Microsoft.IdentityModel.Tokens;
using SignalTrace.Core;

namespace SignalTrace.Server.Services;

/// <summary>
/// Resuelve la carpeta de datos del servidor y crea los secretos en el primer arranque (llave de los JWT,
/// llave de firma del servidor, llave del cifrado en reposo).
/// </summary>
public sealed class ServerPaths
{
    /// <summary>Ruta absoluta de la carpeta de datos (Storage:DataDir, «data» por defecto).</summary>
    public string DataDir { get; }

    /// <summary>Calcula la ruta de la carpeta de datos (relativa a la raíz del contenido si no es absoluta) y la crea.</summary>
    public ServerPaths(IConfiguration cfg, IHostEnvironment env)
    {
        var dir = cfg["Storage:DataDir"] ?? "data";
        DataDir = Path.IsPathRooted(dir) ? dir : Path.Combine(env.ContentRootPath, dir);
        Directory.CreateDirectory(DataDir);
    }

    /// <summary>Ruta de un archivo dentro de la carpeta de datos; una ruta absoluta se respeta.</summary>
    public string File(string name) => Path.IsPathRooted(name) ? name : Path.Combine(DataDir, name);

    /// <summary>
    /// Lee un secreto de la configuración (en Base64); si no está, de un archivo de la carpeta de datos; si tampoco
    /// existe, lo genera al azar, lo guarda con permisos restringidos y lo devuelve.
    /// </summary>
    /// <param name="cfg">Configuración.</param>
    /// <param name="configKey">Llave de configuración, por ejemplo «Jwt:Key».</param>
    /// <param name="fileName">Archivo donde se guarda.</param>
    /// <param name="length">Longitud en bytes del secreto generado.</param>
    public byte[] SecretBytes(IConfiguration cfg, string configKey, string fileName, int length)
    {
        var configured = cfg[configKey];
        if (!string.IsNullOrWhiteSpace(configured)) return Convert.FromBase64String(configured);
        var path = File(fileName);
        if (System.IO.File.Exists(path)) return Convert.FromBase64String(System.IO.File.ReadAllText(path).Trim());
        var key = RandomNumberGenerator.GetBytes(length);
        System.IO.File.WriteAllText(path, Convert.ToBase64String(key));
        TryRestrict(path);
        return key;
    }

    /// <summary>En Linux y macOS deja el archivo legible y escribible solo por el usuario del servidor (permiso 600).</summary>
    public static void TryRestrict(string path)
    {
        if (!OperatingSystem.IsWindows())
            System.IO.File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }
}

/// <summary>
/// Almacenamiento de contraseñas con PBKDF2-HMAC-SHA256 (600 000 iteraciones, sal de 16 bytes, salida de 32 bytes).
/// El costo alto hace lento probar contraseñas por fuerza bruta si se roba la base de datos.
/// </summary>
/// <param name="cfg">Configuración (Auth:Pbkdf2Iterations).</param>
public sealed class PasswordHasher(IConfiguration cfg)
{
    /// <summary>Iteraciones que se usan para los hashes nuevos.</summary>
    public int Iterations { get; } = cfg.GetValue("Auth:Pbkdf2Iterations", 600_000);

    /// <summary>Calcula el hash de una contraseña nueva con una sal aleatoria.</summary>
    /// <returns>La sal, las iteraciones y el hash, que se guardan juntos.</returns>
    public (byte[] Salt, int Iterations, byte[] Hash) Hash(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        return (salt, Iterations, Derive(password, salt, Iterations));
    }

    /// <summary>Deriva 32 bytes con PBKDF2-HMAC-SHA256.</summary>
    public static byte[] Derive(string password, byte[] salt, int iterations)
        => Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, HashAlgorithmName.SHA256, 32);

    /// <summary>Verifica una contraseña comparando en tiempo constante, para no filtrar información por el tiempo de respuesta.</summary>
    public static bool Verify(string password, byte[] salt, int iterations, byte[] expected)
        => CryptographicOperations.FixedTimeEquals(Derive(password, salt, iterations), expected);
}

/// <summary>
/// Contador de inicios de sesión fallidos por nombre de usuario: a partir del 5.º fallo la cuenta espera
/// 2^(n−5) segundos antes de otro intento (máximo 5 minutos). Un inicio correcto reinicia el contador.
/// </summary>
public sealed class LoginThrottle
{
    /// <summary>Fallos acumulados y hora hasta la que se bloquea cada usuario (sin distinguir mayúsculas).</summary>
    private readonly ConcurrentDictionary<string, (int Fails, DateTimeOffset Until)> _state = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Tiempo que falta para poder intentar de nuevo, o null si no hay bloqueo.</summary>
    public TimeSpan? RetryAfter(string username)
    {
        if (_state.TryGetValue(username, out var s) && s.Until > DateTimeOffset.UtcNow) return s.Until - DateTimeOffset.UtcNow;
        return null;
    }

    /// <summary>Registra un fallo y calcula la espera con retroceso exponencial.</summary>
    public void Fail(string username) => _state.AddOrUpdate(username, _ => (1, DateTimeOffset.MinValue), (_, s) =>
    {
        var fails = s.Fails + 1;
        var wait = fails >= 5 ? TimeSpan.FromSeconds(Math.Min(300, Math.Pow(2, fails - 5))) : TimeSpan.Zero;
        return (fails, DateTimeOffset.UtcNow + wait);
    });

    /// <summary>Borra el contador después de un inicio de sesión correcto.</summary>
    public void Success(string username) => _state.TryRemove(username, out _);
}

/// <summary>
/// JWT firmados con HS256. Hay dos tipos: el de usuario (sub, name, role; 60 min) y el de dispositivo
/// (typ=device, did; 30 días). El claim «typ» impide usar uno en lugar del otro.
/// </summary>
public sealed class TokenService
{
    /// <summary>Nombre del claim que distingue el tipo de token (user o device).</summary>
    public const string TypeClaim = "typ";
    /// <summary>Nombre del claim con el id del dispositivo.</summary>
    public const string DeviceClaim = "did";
    /// <summary>Llave HMAC de 32 bytes.</summary>
    private readonly SymmetricSecurityKey _key;
    /// <summary>Emisor y audiencia que se escriben y se exigen en cada token.</summary>
    private readonly string _issuer, _audience;
    /// <summary>Vigencia de los tokens de usuario (minutos) y de dispositivo (días).</summary>
    private readonly int _userMinutes, _deviceDays;

    /// <summary>Lee la configuración Jwt:* y obtiene la llave (Jwt:Key o el archivo jwt.key).</summary>
    public TokenService(IConfiguration cfg, ServerPaths paths)
    {
        _key = new SymmetricSecurityKey(paths.SecretBytes(cfg, "Jwt:Key", "jwt.key", 32));
        _issuer = cfg["Jwt:Issuer"] ?? "signaltrace";
        _audience = cfg["Jwt:Audience"] ?? "signaltrace";
        _userMinutes = cfg.GetValue("Jwt:UserMinutes", 60);
        _deviceDays = cfg.GetValue("Jwt:DeviceDays", 30);
    }

    /// <summary>
    /// Parámetros de validación: emisor, audiencia, llave, solo HS256 (evita ataques de cambio de algoritmo)
    /// y 30 s de tolerancia de reloj.
    /// </summary>
    public TokenValidationParameters ValidationParameters => new()
    {
        ValidIssuer = _issuer,
        ValidAudience = _audience,
        IssuerSigningKey = _key,
        ValidAlgorithms = [SecurityAlgorithms.HmacSha256],
        NameClaimType = "name",
        RoleClaimType = "role",
        ClockSkew = TimeSpan.FromSeconds(30),
    };

    /// <summary>Emite el token de un usuario que inició sesión.</summary>
    /// <returns>El token y su vencimiento en ms Unix.</returns>
    public (string Token, long ExpiresAt) IssueUser(int userId, string username, string role)
        => Issue([new("sub", userId.ToString()), new("name", username), new("role", role), new(TypeClaim, "user")], TimeSpan.FromMinutes(_userMinutes));

    /// <summary>Emite el token de un dispositivo; «sub» es su dueño y «did» el dispositivo.</summary>
    public string IssueDevice(Guid deviceId, int ownerId)
        => Issue([new("sub", ownerId.ToString()), new(TypeClaim, "device"), new(DeviceClaim, Bytes.DeviceIdText(deviceId))], TimeSpan.FromDays(_deviceDays)).Token;

    /// <summary>Valida un token fuera de la canalización HTTP (puente MQTT). Devuelve null si no es válido.</summary>
    public ClaimsPrincipal? Validate(string token)
    {
        try
        {
            var handler = new JwtSecurityTokenHandler { MapInboundClaims = false };
            return handler.ValidateToken(token, ValidationParameters, out _);
        }
        catch (Exception e) when (e is SecurityTokenException or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>Firma un token con los claims y la vigencia indicados.</summary>
    private (string Token, long ExpiresAt) Issue(Claim[] claims, TimeSpan life)
    {
        var expires = DateTime.UtcNow + life;
        var jwt = new JwtSecurityToken(_issuer, _audience, claims, DateTime.UtcNow, expires, new SigningCredentials(_key, SecurityAlgorithms.HmacSha256));
        return (new JwtSecurityTokenHandler().WriteToken(jwt), new DateTimeOffset(expires).ToUnixTimeMilliseconds());
    }
}

/// <summary>
/// Llave ECDSA P-256 de largo plazo del servidor (la que fijan los clientes), más una llave sin relación que solo
/// usa la demostración de intermediario (MITM) del laboratorio.
/// </summary>
public sealed class ServerIdentity : IDisposable
{
    /// <summary>Llave privada de firma.</summary>
    public ECDsa Key { get; }
    /// <summary>Llave pública como punto de 65 bytes.</summary>
    public byte[] PublicKey { get; }
    /// <summary>Huella SHA-256 de la llave pública en hexadecimal.</summary>
    public string Fingerprint { get; }
    /// <summary>Llave del atacante; se crea solo si se usa.</summary>
    private readonly Lazy<ECDsa> _attacker = new(() => ECDsa.Create(ECCurve.NamedCurves.nistP256));
    /// <summary>Llave con la que firma el intermediario simulado; los clientes deben rechazar su firma.</summary>
    public ECDsa AttackerKey => _attacker.Value;

    /// <summary>Carga la llave de firma desde el archivo PEM o genera una nueva y la guarda con permisos restringidos.</summary>
    public ServerIdentity(IConfiguration cfg, ServerPaths paths, ILogger<ServerIdentity> log)
    {
        var path = paths.File(cfg["Server:SigningKeyPath"] ?? "server-signing-key.pem");
        Key = ECDsa.Create();
        if (File.Exists(path))
        {
            Key.ImportFromPem(File.ReadAllText(path));
        }
        else
        {
            Key.GenerateKey(ECCurve.NamedCurves.nistP256);
            File.WriteAllText(path, Key.ExportPkcs8PrivateKeyPem());
            ServerPaths.TryRestrict(path);
            log.LogInformation("Generated new long-term server signing key at {Path}", path);
        }
        PublicKey = EcKeys.ExportPublic(Key);
        Fingerprint = EcKeys.Fingerprint(PublicKey);
    }

    /// <summary>Libera las llaves.</summary>
    public void Dispose()
    {
        Key.Dispose();
        if (_attacker.IsValueCreated) _attacker.Value.Dispose();
    }
}

/// <summary>Lectura cómoda de los claims del token.</summary>
public static class ClaimsExtensions
{
    /// <summary>Id del usuario (claim «sub»); 0 si falta.</summary>
    public static int UserId(this ClaimsPrincipal p) => int.Parse(p.FindFirstValue("sub") ?? "0");
    /// <summary>Si el usuario es administrador.</summary>
    public static bool IsAdmin(this ClaimsPrincipal p) => p.IsInRole(Data.Roles.Admin);
    /// <summary>Id del dispositivo (claim «did»), solo presente en tokens de dispositivo.</summary>
    public static string? DeviceId(this ClaimsPrincipal p) => p.FindFirstValue(TokenService.DeviceClaim);
}
