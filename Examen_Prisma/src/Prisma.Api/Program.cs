using System.Text.Json.Serialization;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Prisma.Api.Data;
using Prisma.Api.Endpoints;
using Prisma.Api.Services;

var builder = WebApplication.CreateBuilder(args);
var cfg = builder.Configuration;
using var bootLog = LoggerFactory.Create(l => l.AddConsole());
var jwt = JwtSettings.From(cfg, bootLog.CreateLogger("Prisma"));

// Base de datos SQLite
var cs = cfg.GetConnectionString("Default") ?? "Data Source=data/prisma.db";
var dbFile = new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder(cs).DataSource;
Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(dbFile, builder.Environment.ContentRootPath))!);
builder.Services.AddDbContext<AppDb>(o => o.UseSqlite(cs));

builder.Services.AddSingleton(jwt);
builder.Services.AddSingleton<TokenService>();
builder.Services.AddSingleton<TraceStore>();
builder.Services.AddSingleton<CatalogService>();
builder.Services.AddScoped<AuditService>();

builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer(o =>
{
    o.MapInboundClaims = false;
    o.TokenValidationParameters = new TokenValidationParameters
    {
        ValidIssuer = jwt.Issuer, ValidAudience = jwt.Issuer, IssuerSigningKey = jwt.SigningKey,
        NameClaimType = System.Security.Claims.ClaimTypes.Name, RoleClaimType = System.Security.Claims.ClaimTypes.Role,
    };
});
builder.Services.AddAuthorization();

// Límite de intentos en autenticación: 10 por minuto por IP.
builder.Services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = 429;
    o.AddPolicy("auth", ctx => RateLimitPartition.GetFixedWindowLimiter(
        ctx.Connection.RemoteIpAddress?.ToString() ?? "?",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 10, Window = TimeSpan.FromMinutes(1) }));
});

builder.Services.AddCors(o => o.AddDefaultPolicy(p => p.AllowAnyOrigin().AllowAnyHeader().AllowAnyMethod()
    .WithExposedHeaders("X-Prisma-Psnr", "X-Prisma-Payload-Bytes", "X-Prisma-Capacity", "X-Prisma-Resized", "Content-Disposition")));
builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals);
builder.Services.Configure<Microsoft.AspNetCore.Http.Features.FormOptions>(o => o.MultipartBodyLengthLimit = Http.MaxUploadBytes + 1024 * 1024);
builder.Services.Configure<ForwardedHeadersOptions>(o =>
{
    o.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    o.KnownIPNetworks.Clear(); o.KnownProxies.Clear();   // detrás del proxy HTTPS del proveedor de nube
});

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDb>();
    db.Database.EnsureCreated();
    await SeedAdmin(db, cfg, app.Logger);
    scope.ServiceProvider.GetRequiredService<CatalogService>();   // genera el catálogo si no existe
}

app.UseForwardedHeaders();
app.UseDefaultFiles();
app.UseStaticFiles();
app.UseCors();
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();

app.MapGet("/api/health", () => Results.Ok(new { status = "ok", service = "PRISMA-L", time = DateTime.UtcNow }));
app.MapAuth();
app.MapAccount();
app.MapCatalog();
app.MapStego();
app.MapCaesar();
app.MapCompare();
app.MapAdmin();

app.Run();

static async Task SeedAdmin(AppDb db, IConfiguration cfg, ILogger log)
{
    string user = cfg["Admin:Username"] ?? "admin", pass = cfg["Admin:Password"] ?? "";
    if (await db.Users.AnyAsync(u => u.Role == Roles.Admin)) return;
    if (pass.Length < 8) { log.LogWarning("No hay administrador: define Admin__Password (≥ 8 caracteres) para crearlo."); return; }
    var (hash, salt) = Passwords.Hash(pass);
    db.Users.Add(new User { Username = user, PwdHash = hash, PwdSalt = salt, Role = Roles.Admin });
    await db.SaveChangesAsync();
    log.LogInformation("Administrador '{User}' creado.", user);
}

public partial class Program;
