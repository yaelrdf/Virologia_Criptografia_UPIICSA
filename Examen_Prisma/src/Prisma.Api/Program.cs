// Program.cs — Punto de entrada de la API REST de ABCO (ASP.NET Core 10, minimal API).
// Configura SQLite, la autenticación JWT, el límite de intentos, CORS y los encabezados de proxy;
// crea la base y el administrador inicial, genera el catálogo de imágenes, aplica el bloqueo de
// sesiones con contraseña temporal y registra todas las rutas (ver docs/API.md). También sirve el
// cliente web desde wwwroot.
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
// Registrador temporal: la configuración JWT puede emitir advertencias antes de que exista el contenedor de servicios.
using var bootLog = LoggerFactory.Create(l => l.AddConsole());
var jwt = JwtSettings.From(cfg, bootLog.CreateLogger("Prisma"));

// Base de datos SQLite
// Se crea la carpeta del archivo (por defecto data/, relativa a la raíz del proyecto) antes de abrirlo.
var cs = cfg.GetConnectionString("Default") ?? "Data Source=data/prisma.db";
var dbFile = new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder(cs).DataSource;
Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(dbFile, builder.Environment.ContentRootPath))!);
builder.Services.AddDbContext<AppDb>(o => o.UseSqlite(cs));

// Servicios sin estado por petición como singletons; la auditoría usa el DbContext, por eso es scoped.
builder.Services.AddSingleton(jwt);
builder.Services.AddSingleton<TokenService>();
builder.Services.AddSingleton<TraceStore>();
builder.Services.AddSingleton<CatalogService>();
builder.Services.AddScoped<AuditService>();

// Validación de JWT: emisor, audiencia, firma HMAC-SHA256 y expiración.
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer(o =>
{
    // Sin mapeo de claims entrantes: los tipos llegan tal como TokenService los emitió (ClaimTypes.*),
    // y NameClaimType/RoleClaimType indican cuáles usar para el nombre y el rol (RequireRole).
    o.MapInboundClaims = false;
    o.TokenValidationParameters = new TokenValidationParameters
    {
        ValidIssuer = jwt.Issuer, ValidAudience = jwt.Issuer, IssuerSigningKey = jwt.SigningKey,
        NameClaimType = System.Security.Claims.ClaimTypes.Name, RoleClaimType = System.Security.Claims.ClaimTypes.Role,
    };
});
builder.Services.AddAuthorization();

// Límite de intentos en autenticación: 10 por minuto por IP.
// Ventana fija por dirección IP del cliente; frena los ataques de diccionario contra contraseñas
// e imágenes-llave. Al excederse responde 429.
builder.Services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = 429;
    o.AddPolicy("auth", ctx => RateLimitPartition.GetFixedWindowLimiter(
        ctx.Connection.RemoteIpAddress?.ToString() ?? "?",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 10, Window = TimeSpan.FromMinutes(1) }));
});

// CORS abierto: la autenticación va en la cabecera Authorization (no en cookies), así que otro origen no
// puede actuar a nombre del usuario sin su token. Se exponen las cabeceras que leen los clientes al ocultar
// y Content-Disposition para el nombre del archivo descargado.
builder.Services.AddCors(o => o.AddDefaultPolicy(p => p.AllowAnyOrigin().AllowAnyHeader().AllowAnyMethod()
    .WithExposedHeaders("X-Prisma-Psnr", "X-Prisma-Payload-Bytes", "X-Prisma-Capacity", "X-Prisma-Resized", "Content-Disposition")));
// Permite serializar Infinity/NaN en JSON (p. ej. un PSNR infinito si la imagen no cambió).
builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals);
// El formulario multipart admite la imagen máxima más 1 MB para los demás campos.
builder.Services.Configure<Microsoft.AspNetCore.Http.Features.FormOptions>(o => o.MultipartBodyLengthLimit = Http.MaxUploadBytes + 1024 * 1024);
// Detrás de un proxy, X-Forwarded-For da la IP real del cliente (la usa el límite de intentos) y
// X-Forwarded-Proto el esquema https. Se confía en cualquier proxy: el despliegue supone que la API
// solo es accesible a través del proxy del proveedor.
builder.Services.Configure<ForwardedHeadersOptions>(o =>
{
    o.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    o.KnownIPNetworks.Clear(); o.KnownProxies.Clear();   // detrás del proxy HTTPS del proveedor de nube
});

var app = builder.Build();

// Inicialización al arrancar: esquema de la base, administrador inicial y catálogo.
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDb>();
    db.Database.EnsureCreated();
    // EnsureCreated no migra: agrega la columna nueva a bases creadas por versiones anteriores.
    try { db.Database.ExecuteSqlRaw("ALTER TABLE Users ADD COLUMN MustChangePassword INTEGER NOT NULL DEFAULT 0"); }
    catch (Microsoft.Data.Sqlite.SqliteException) { /* ya existe */ }
    await SeedAdmin(db, cfg, app.Logger);
    scope.ServiceProvider.GetRequiredService<CatalogService>();   // genera el catálogo si no existe
}

// Orden del pipeline: los encabezados reenviados van primero para que el límite de intentos vea la IP
// real; la autenticación debe ir antes del middleware de contraseña temporal, que lee ctx.User.
app.UseForwardedHeaders();
app.UseDefaultFiles();
app.UseStaticFiles();
app.UseCors();
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();

// Con contraseña temporal solo se permite consultar el perfil y cambiar la contraseña.
// La marca viaja en el token (claim must_change_password); cualquier otra ruta /api responde 403 hasta
// que el usuario elija una contraseña nueva. /api/auth y /api/health quedan libres porque no usan la
// sesión, y los archivos estáticos del cliente web no pasan por /api.
app.Use(async (ctx, next) =>
{
    var path = ctx.Request.Path;
    if (ctx.User.HasClaim(TokenService.MustChangeClaim, "true") && path.StartsWithSegments("/api")
        && !path.StartsWithSegments("/api/account/password") && !path.StartsWithSegments("/api/account/me")
        && !path.StartsWithSegments("/api/auth") && !path.StartsWithSegments("/api/health"))
    {
        ctx.Response.StatusCode = 403;
        await ctx.Response.WriteAsJsonAsync(new { error = "Debes cambiar tu contraseña temporal antes de continuar." });
        return;
    }
    await next();
});

// GET /api/health — público. 200 {"status":"ok","service":"ABCO","time"}; lo usan los clientes y el despliegue para comprobar el servidor.
app.MapGet("/api/health", () => Results.Ok(new { status = "ok", service = "ABCO", time = DateTime.UtcNow }));
app.MapAuth();
app.MapAccount();
app.MapCatalog();
app.MapStego();
app.MapCaesar();
app.MapCompare();
app.MapAdmin();

app.Run();

// Crea el administrador inicial (Admin:Username, por defecto "admin", y Admin:Password) solo si no existe
// ningún usuario con rol admin. Sin una contraseña de al menos 8 caracteres no se crea y se advierte en el log.
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

/// <summary>Clase generada por las instrucciones de nivel superior.</summary>
/// <remarks>Se declara pública y parcial para poder referenciarla desde pruebas de integración (<c>WebApplicationFactory&lt;Program&gt;</c>).</remarks>
public partial class Program;
