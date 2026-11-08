// Punto de entrada del servidor SignalTrace (ASP.NET Core).
// Configura en este orden: HTTPS para la red local, servicios (identidad del servidor, tokens, sesiones,
// analítica, base de datos), autenticación JWT con sus políticas, límites de solicitudes, SignalR y OpenAPI.
// Después crea la base de datos y el administrador inicial, publica los archivos estáticos de la interfaz web
// y registra todos los endpoints de la API.
using System.Security.Cryptography;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Scalar.AspNetCore;
using SignalTrace.Core;
using SignalTrace.Server.Analytics;
using SignalTrace.Server.Data;
using SignalTrace.Server.Endpoints;
using SignalTrace.Server.Hubs;
using SignalTrace.Server.Mqtt;
using SignalTrace.Server.Services;

var builder = WebApplication.CreateBuilder(args);
var cfg = builder.Configuration;

// HTTPS en la red local (los teléfonos necesitan un contexto seguro para leer sensores y usar Web Crypto):
// se conservan las direcciones HTTP configuradas y se agrega https://0.0.0.0:{Https:Port}.
var lanHttps = new LanHttps(cfg, new ServerPaths(cfg, builder.Environment));
builder.Services.AddSingleton(lanHttps);
if (lanHttps.Enabled)
{
    // Toma la URL configurada; si no hay, la arma con los puertos HTTP del contenedor; si tampoco, usa el puerto 5000.
    var urls = cfg["urls"] ?? (cfg["http_ports"] is { Length: > 0 } ports
        ? string.Join(';', ports.Split(';').Select(p => $"http://*:{p}"))
        : "http://localhost:5000");
    builder.WebHost.UseUrls($"{urls};https://0.0.0.0:{lanHttps.Port}");
    builder.WebHost.ConfigureKestrel(k => k.ConfigureHttpsDefaults(h => h.ServerCertificate = lanHttps.Certificate));
}

// Las respuestas JSON omiten los campos nulos, igual que el contrato del protocolo.
builder.Services.ConfigureHttpJsonOptions(o =>
{
    o.SerializerOptions.DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull;
});
// Servicios de una sola instancia que viven todo el tiempo que corre el servidor.
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<ServerPaths>();
builder.Services.AddSingleton<ServerIdentity>();
builder.Services.AddSingleton<TokenService>();
builder.Services.AddSingleton<PasswordHasher>();
builder.Services.AddSingleton<LoginThrottle>();
builder.Services.AddSingleton<SessionStore>();
builder.Services.AddSingleton<AnalyticsEngine>();
builder.Services.AddSingleton<TraceStore>();
builder.Services.AddSingleton<AtRestProtector>();
builder.Services.AddSingleton<EnrollmentCodes>();
// Tarea de fondo que marca dispositivos inactivos y borra datos vencidos.
builder.Services.AddHostedService<DeviceMonitor>();
// Servicios por solicitud (usan la base de datos, que también es por solicitud).
builder.Services.AddScoped<LiveBroadcaster>();
builder.Services.AddScoped<TelemetryPipeline>();
// El puente MQTT solo arranca si está habilitado en la configuración.
if (cfg.GetValue("Mqtt:Enabled", false)) builder.Services.AddHostedService<MqttBridge>();

// Base de datos SQLite dentro de la carpeta de datos del servidor.
builder.Services.AddDbContext<AppDb>((sp, o) =>
    o.UseSqlite($"Data Source={sp.GetRequiredService<ServerPaths>().File(cfg["Storage:Database"] ?? "signaltrace.db")}"));

// Autenticación con JWT. La validación usa los mismos parámetros con los que TokenService firma los tokens.
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer();
builder.Services.AddOptions<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme).Configure<TokenService>((o, tokens) =>
{
    // Conserva los nombres de los claims tal como vienen en el token (sin traducirlos a los de Microsoft).
    o.MapInboundClaims = false;
    o.TokenValidationParameters = tokens.ValidationParameters;
    o.Events = new JwtBearerEvents
    {
        // SignalR sobre WebSockets no puede mandar encabezados, así que envía el token como ?access_token=.
        OnMessageReceived = ctx =>
        {
            var t = ctx.Request.Query["access_token"];
            if (!string.IsNullOrEmpty(t) && ctx.HttpContext.Request.Path.StartsWithSegments("/hubs")) ctx.Token = t;
            return Task.CompletedTask;
        },
        // Sin token válido: responde 401 con el formato de error de la API en lugar del encabezado estándar.
        OnChallenge = async ctx =>
        {
            ctx.HandleResponse();
            ctx.Response.StatusCode = 401;
            await ctx.Response.WriteAsJsonAsync(new ErrorResponse("unauthorized", "Token ausente, inválido o expirado"));
        },
        // Token válido pero sin el rol o tipo requerido: 403.
        OnForbidden = async ctx =>
        {
            ctx.Response.StatusCode = 403;
            await ctx.Response.WriteAsJsonAsync(new ErrorResponse("forbidden", "Permisos insuficientes"));
        },
    };
});
// Políticas de autorización: «user» para cualquier persona con sesión, «admin» para administradores
// y «device» para los tokens de dispositivo (que llevan el claim con el id del dispositivo).
builder.Services.AddAuthorizationBuilder()
    .AddPolicy("user", p => p.RequireClaim(TokenService.TypeClaim, "user"))
    .AddPolicy("admin", p => p.RequireClaim(TokenService.TypeClaim, "user").RequireRole(Roles.Admin))
    .AddPolicy("device", p => p.RequireClaim(TokenService.TypeClaim, "device").RequireClaim(TokenService.DeviceClaim));

// Límites de solicitudes por ventana fija: inicio de sesión y ping por IP, telemetría por dispositivo.
builder.Services.AddRateLimiter(o =>
{
    o.OnRejected = async (ctx, ct) =>
    {
        ctx.HttpContext.Response.StatusCode = 429;
        await ctx.HttpContext.Response.WriteAsJsonAsync(new ErrorResponse("rate_limited", "Demasiadas solicitudes"), ct);
    };
    static string Ip(HttpContext c) => c.Connection.RemoteIpAddress?.ToString() ?? "unknown";
    var loginPerMinute = cfg.GetValue("RateLimits:LoginPerMinute", 20);
    o.AddPolicy("login", c => RateLimitPartition.GetFixedWindowLimiter(Ip(c), _ => new() { PermitLimit = loginPerMinute, Window = TimeSpan.FromMinutes(1) }));
    o.AddPolicy("ping", c => RateLimitPartition.GetFixedWindowLimiter(Ip(c), _ => new() { PermitLimit = 60, Window = TimeSpan.FromSeconds(10) }));
    o.AddPolicy("telemetry", c => RateLimitPartition.GetFixedWindowLimiter(c.User.DeviceId() ?? Ip(c), _ => new() { PermitLimit = 100, Window = TimeSpan.FromSeconds(10) }));
});

// SignalR para el tablero en vivo y documentación OpenAPI de la API.
builder.Services.AddSignalR().AddJsonProtocol(o =>
    o.PayloadSerializerOptions.DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull);
builder.Services.AddOpenApi();

var app = builder.Build();

await Bootstrap(app);

// Archivos de la interfaz web (wwwroot); index.html es la página predeterminada.
app.UseDefaultFiles();
app.UseStaticFiles(new StaticFileOptions
{
    OnPrepareResponse = c =>
    {
        // El cliente web siempre debe pedir la versión más reciente de JS y HTML; el service worker guarda en caché solo la estructura.
        c.Context.Response.Headers.CacheControl = "no-cache";
    },
});
app.UseAuthentication();
// Va después de la autenticación para que el límite de telemetría agrupe por dispositivo (claim did) y no por IP.
app.UseRateLimiter();
app.UseAuthorization();

// Rutas: documento OpenAPI, visor Scalar, endpoints de la API y el hub en vivo.
app.MapOpenApi();
app.MapScalarApiReference(o => o.WithTitle("SignalTrace API"));
app.MapCore();
app.MapData();
app.MapEnrollment();
app.MapLab();
app.MapHub<LiveHub>("/hubs/live");

app.Run();

// Prepara el servidor al arrancar: crea la base de datos si no existe, muestra la huella de la llave del
// servidor en el registro y, en el primer arranque, crea la cuenta de administrador.
static async Task Bootstrap(WebApplication app)
{
    using var scope = app.Services.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<AppDb>();
    var cfg = app.Configuration;
    await db.Database.EnsureCreatedAsync();
    var identity = app.Services.GetRequiredService<ServerIdentity>();
    app.Logger.LogInformation("Server key fingerprint (SHA-256): {Fingerprint}", identity.Fingerprint);
    if (await db.Users.AnyAsync(u => u.Role == Roles.Admin)) return;
    // Primer arranque: crea el administrador con la contraseña fija de la configuración (Admin:Password, con valor por defecto en appsettings.json).
    var username = cfg["Admin:Username"] ?? "admin";
    var password = cfg["Admin:Password"];
    if (string.IsNullOrEmpty(password)) throw new InvalidOperationException("Admin:Password no está configurada.");
    var (salt, it, hash) = app.Services.GetRequiredService<PasswordHasher>().Hash(password);
    db.Users.Add(new User
    {
        Username = username, Salt = salt, Iterations = it, PasswordHash = hash, Role = Roles.Admin,
        CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
    });
    await db.SaveChangesAsync();
    app.Logger.LogWarning("Created initial admin '{User}' with the configured password; change it from Usuarios.", username);
}

/// <summary>Clase parcial pública para que las pruebas de integración puedan levantar el servidor con WebApplicationFactory.</summary>
public partial class Program;
