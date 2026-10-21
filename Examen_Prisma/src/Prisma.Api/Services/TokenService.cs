using System.Security.Claims;
using System.Text;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using Prisma.Api.Data;

namespace Prisma.Api.Services;

public sealed class TokenService(JwtSettings settings)
{
    public string Issue(User u) => new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
    {
        Issuer = settings.Issuer,
        Audience = settings.Issuer,
        Subject = new ClaimsIdentity([
            new Claim(ClaimTypes.NameIdentifier, u.Id.ToString()),
            new Claim(ClaimTypes.Name, u.Username),
            new Claim(ClaimTypes.Role, u.Role)]),
        Expires = DateTime.UtcNow.AddHours(settings.Hours),
        SigningCredentials = new SigningCredentials(settings.SigningKey, SecurityAlgorithms.HmacSha256),
    });
}

public sealed class JwtSettings
{
    public required string Issuer { get; init; }
    public required SymmetricSecurityKey SigningKey { get; init; }
    public int Hours { get; init; } = 8;

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
