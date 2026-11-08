// TokenService.cs — Emisión de tokens JWT y configuración de su firma.
// El token identifica al usuario (id, nombre, rol) y, si tiene contraseña temporal, lleva la marca
// que el middleware de Program.cs usa para bloquear las demás rutas con 403.
using System.Security.Claims;
using System.Text;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using Prisma.Api.Data;

namespace Prisma.Api.Services;

/// <summary>Emite los JWT de sesión firmados con HMAC-SHA256.</summary>
/// <param name="settings">Emisor, clave de firma y vigencia.</param>
public sealed class TokenService(JwtSettings settings)
{
    /// <summary>Marca de sesión con contraseña temporal: solo puede cambiar la contraseña.</summary>
    public const string MustChangeClaim = "must_change_password";

    /// <summary>Crea un token para el usuario.</summary>
    /// <param name="u">Usuario autenticado.</param>
    /// <returns>JWT compacto con claims de id, nombre, rol y, si aplica, la marca de contraseña temporal.</returns>
    /// <remarks>
    /// El token no tiene estado en el servidor: es válido hasta que expira. Por eso, tras cambiar la
    /// contraseña, la API entrega un token nuevo sin la marca en lugar de modificar el anterior.
    /// </remarks>
    public string Issue(User u) => new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
    {
        Issuer = settings.Issuer,
        Audience = settings.Issuer,
        Subject = new ClaimsIdentity([
            new Claim(ClaimTypes.NameIdentifier, u.Id.ToString()),
            new Claim(ClaimTypes.Name, u.Username),
            new Claim(ClaimTypes.Role, u.Role),
            .. (u.MustChangePassword ? new[] { new Claim(MustChangeClaim, "true") } : [])]),
        Expires = DateTime.UtcNow.AddHours(settings.Hours),
        SigningCredentials = new SigningCredentials(settings.SigningKey, SecurityAlgorithms.HmacSha256),
    });
}

/// <summary>Configuración de los JWT, leída de la sección <c>Jwt</c>.</summary>
public sealed class JwtSettings
{
    /// <summary>Emisor; también se usa como audiencia.</summary>
    public required string Issuer { get; init; }
    /// <summary>Clave simétrica que firma y valida los tokens.</summary>
    public required SymmetricSecurityKey SigningKey { get; init; }
    /// <summary>Vigencia del token en horas (8 por defecto).</summary>
    public int Hours { get; init; } = 8;

    /// <summary>Lee <c>Jwt:Key</c>, <c>Jwt:Issuer</c> y <c>Jwt:Hours</c> de la configuración.</summary>
    /// <param name="cfg">Configuración de la aplicación (appsettings y variables de entorno, p. ej. <c>Jwt__Key</c>).</param>
    /// <param name="log">Registrador para advertir si falta la clave.</param>
    /// <returns>La configuración lista para registrar como singleton.</returns>
    /// <remarks>
    /// HMAC-SHA256 necesita una clave de al menos 256 bits (32 bytes). Si no se configura una válida, se
    /// genera una aleatoria de 48 bytes: el servidor funciona, pero los tokens emitidos dejan de valer al
    /// reiniciar. En producción debe definirse <c>Jwt__Key</c>.
    /// </remarks>
    public static JwtSettings From(IConfiguration cfg, ILogger log)
    {
        string? key = cfg["Jwt:Key"];
        if (string.IsNullOrWhiteSpace(key) || Encoding.UTF8.GetByteCount(key) < 32)
        {
            log.LogWarning("Jwt:Key no está configurada (≥ 32 bytes). Se usa una clave aleatoria: las sesiones se invalidan al reiniciar.");
            key = Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(48));
        }
        return new JwtSettings
        {
            Issuer = cfg["Jwt:Issuer"] ?? "prisma-l",
            SigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(key)),
            Hours = cfg.GetValue("Jwt:Hours", 8),
        };
    }
}
