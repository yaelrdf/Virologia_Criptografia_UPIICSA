using System.Collections.Concurrent;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using Microsoft.IdentityModel.Tokens;
using SignalTrace.Core;

namespace SignalTrace.Server.Services;

/// <summary>Resolves the data directory and creates secrets on first start (JWT key, server signing key).</summary>
public sealed class ServerPaths
{
    public string DataDir { get; }

    public ServerPaths(IConfiguration cfg, IHostEnvironment env)
    {
        var dir = cfg["Storage:DataDir"] ?? "data";
        DataDir = Path.IsPathRooted(dir) ? dir : Path.Combine(env.ContentRootPath, dir);
        Directory.CreateDirectory(DataDir);
    }

    public string File(string name) => Path.IsPathRooted(name) ? name : Path.Combine(DataDir, name);

    /// <summary>Reads a secret from configuration, else from a file in the data dir, else generates and stores it.</summary>
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

    public static void TryRestrict(string path)
    {
        if (!OperatingSystem.IsWindows())
            System.IO.File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }
}

/// <summary>PBKDF2-HMAC-SHA256 password storage (600 000 iterations, 16-byte salt, 32-byte output).</summary>
public sealed class PasswordHasher(IConfiguration cfg)
{
    public int Iterations { get; } = cfg.GetValue("Auth:Pbkdf2Iterations", 600_000);

    public (byte[] Salt, int Iterations, byte[] Hash) Hash(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        return (salt, Iterations, Derive(password, salt, Iterations));
    }

    public static byte[] Derive(string password, byte[] salt, int iterations)
        => Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, HashAlgorithmName.SHA256, 32);

    public static bool Verify(string password, byte[] salt, int iterations, byte[] expected)
        => CryptographicOperations.FixedTimeEquals(Derive(password, salt, iterations), expected);
}

/// <summary>Per-username failed login counter: after 5 failures the account waits 2^(n-5) s (max 5 min).</summary>
public sealed class LoginThrottle
{
    private readonly ConcurrentDictionary<string, (int Fails, DateTimeOffset Until)> _state = new(StringComparer.OrdinalIgnoreCase);

    public TimeSpan? RetryAfter(string username)
    {
        if (_state.TryGetValue(username, out var s) && s.Until > DateTimeOffset.UtcNow) return s.Until - DateTimeOffset.UtcNow;
        return null;
    }

    public void Fail(string username) => _state.AddOrUpdate(username, _ => (1, DateTimeOffset.MinValue), (_, s) =>
    {
        var fails = s.Fails + 1;
        var wait = fails >= 5 ? TimeSpan.FromSeconds(Math.Min(300, Math.Pow(2, fails - 5))) : TimeSpan.Zero;
        return (fails, DateTimeOffset.UtcNow + wait);
    });

    public void Success(string username) => _state.TryRemove(username, out _);
}

/// <summary>HS256 JWTs: user tokens (sub, role, 60 min) and device tokens (typ=device, did, 30 days).</summary>
public sealed class TokenService
{
    public const string TypeClaim = "typ";
    public const string DeviceClaim = "did";
    private readonly SymmetricSecurityKey _key;
    private readonly string _issuer, _audience;
    private readonly int _userMinutes, _deviceDays;

    public TokenService(IConfiguration cfg, ServerPaths paths)
    {
        _key = new SymmetricSecurityKey(paths.SecretBytes(cfg, "Jwt:Key", "jwt.key", 32));
        _issuer = cfg["Jwt:Issuer"] ?? "signaltrace";
        _audience = cfg["Jwt:Audience"] ?? "signaltrace";
        _userMinutes = cfg.GetValue("Jwt:UserMinutes", 60);
        _deviceDays = cfg.GetValue("Jwt:DeviceDays", 30);
    }

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

    public (string Token, long ExpiresAt) IssueUser(int userId, string username, string role)
        => Issue([new("sub", userId.ToString()), new("name", username), new("role", role), new(TypeClaim, "user")], TimeSpan.FromMinutes(_userMinutes));

    public string IssueDevice(Guid deviceId, int ownerId)
        => Issue([new("sub", ownerId.ToString()), new(TypeClaim, "device"), new(DeviceClaim, Bytes.DeviceIdText(deviceId))], TimeSpan.FromDays(_deviceDays)).Token;

    /// <summary>Validates a token outside the HTTP pipeline (MQTT bridge). Returns null when invalid.</summary>
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

    private (string Token, long ExpiresAt) Issue(Claim[] claims, TimeSpan life)
    {
        var expires = DateTime.UtcNow + life;
        var jwt = new JwtSecurityToken(_issuer, _audience, claims, DateTime.UtcNow, expires, new SigningCredentials(_key, SecurityAlgorithms.HmacSha256));
        return (new JwtSecurityTokenHandler().WriteToken(jwt), new DateTimeOffset(expires).ToUnixTimeMilliseconds());
    }
}

/// <summary>Long-term ECDSA P-256 server key (pinned by clients) plus an unrelated key used only by the MITM lab demo.</summary>
public sealed class ServerIdentity : IDisposable
{
    public ECDsa Key { get; }
    public byte[] PublicKey { get; }
    public string Fingerprint { get; }
    private readonly Lazy<ECDsa> _mallory = new(() => ECDsa.Create(ECCurve.NamedCurves.nistP256));
    public ECDsa MalloryKey => _mallory.Value;

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

    public void Dispose()
    {
        Key.Dispose();
        if (_mallory.IsValueCreated) _mallory.Value.Dispose();
    }
}

public static class ClaimsExtensions
{
    public static int UserId(this ClaimsPrincipal p) => int.Parse(p.FindFirstValue("sub") ?? "0");
    public static bool IsAdmin(this ClaimsPrincipal p) => p.IsInRole(Data.Roles.Admin);
    public static string? DeviceId(this ClaimsPrincipal p) => p.FindFirstValue(TokenService.DeviceClaim);
}
