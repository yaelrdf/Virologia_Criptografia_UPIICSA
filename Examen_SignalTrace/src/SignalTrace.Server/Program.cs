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

// LAN HTTPS (phones need a secure context): keep the configured HTTP address(es) and add https://0.0.0.0:{Https:Port}.
var lanHttps = new LanHttps(cfg, new ServerPaths(cfg, builder.Environment));
builder.Services.AddSingleton(lanHttps);
if (lanHttps.Enabled)
{
    var urls = cfg["urls"] ?? (cfg["http_ports"] is { Length: > 0 } ports
        ? string.Join(';', ports.Split(';').Select(p => $"http://*:{p}"))
        : "http://localhost:5000");
    builder.WebHost.UseUrls($"{urls};https://0.0.0.0:{lanHttps.Port}");
    builder.WebHost.ConfigureKestrel(k => k.ConfigureHttpsDefaults(h => h.ServerCertificate = lanHttps.Certificate));
}

builder.Services.ConfigureHttpJsonOptions(o =>
{
    o.SerializerOptions.DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull;
});
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
builder.Services.AddHostedService<DeviceMonitor>();
builder.Services.AddScoped<LiveBroadcaster>();
builder.Services.AddScoped<TelemetryPipeline>();
if (cfg.GetValue("Mqtt:Enabled", false)) builder.Services.AddHostedService<MqttBridge>();

builder.Services.AddDbContext<AppDb>((sp, o) =>
    o.UseSqlite($"Data Source={sp.GetRequiredService<ServerPaths>().File(cfg["Storage:Database"] ?? "signaltrace.db")}"));

builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer();
builder.Services.AddOptions<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme).Configure<TokenService>((o, tokens) =>
{
    o.MapInboundClaims = false;
    o.TokenValidationParameters = tokens.ValidationParameters;
    o.Events = new JwtBearerEvents
    {
        // SignalR over WebSockets sends the token as ?access_token=.
        OnMessageReceived = ctx =>
        {
            var t = ctx.Request.Query["access_token"];
            if (!string.IsNullOrEmpty(t) && ctx.HttpContext.Request.Path.StartsWithSegments("/hubs")) ctx.Token = t;
            return Task.CompletedTask;
        },
        OnChallenge = async ctx =>
        {
            ctx.HandleResponse();
            ctx.Response.StatusCode = 401;
            await ctx.Response.WriteAsJsonAsync(new ErrorResponse("unauthorized", "Token ausente, inválido o expirado"));
        },
        OnForbidden = async ctx =>
        {
            ctx.Response.StatusCode = 403;
            await ctx.Response.WriteAsJsonAsync(new ErrorResponse("forbidden", "Permisos insuficientes"));
        },
    };
});
builder.Services.AddAuthorizationBuilder()
    .AddPolicy("user", p => p.RequireClaim(TokenService.TypeClaim, "user"))
    .AddPolicy("admin", p => p.RequireClaim(TokenService.TypeClaim, "user").RequireRole(Roles.Admin))
    .AddPolicy("device", p => p.RequireClaim(TokenService.TypeClaim, "device").RequireClaim(TokenService.DeviceClaim));

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

builder.Services.AddSignalR().AddJsonProtocol(o =>
    o.PayloadSerializerOptions.DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull);
builder.Services.AddOpenApi();

var app = builder.Build();

await Bootstrap(app);

app.UseDefaultFiles();
app.UseStaticFiles(new StaticFileOptions
{
    OnPrepareResponse = c =>
    {
        // The web client must always fetch the latest JS/HTML; the service worker caches the shell itself.
        c.Context.Response.Headers.CacheControl = "no-cache";
    },
});
app.UseAuthentication();
// After authentication so the telemetry limiter can partition by device (did claim) instead of by IP.
app.UseRateLimiter();
app.UseAuthorization();

app.MapOpenApi();
app.MapScalarApiReference(o => o.WithTitle("SignalTrace API"));
app.MapCore();
app.MapData();
app.MapEnrollment();
app.MapLab();
app.MapHub<LiveHub>("/hubs/live");

app.Run();

static async Task Bootstrap(WebApplication app)
{
    using var scope = app.Services.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<AppDb>();
    var cfg = app.Configuration;
    await db.Database.EnsureCreatedAsync();
    var identity = app.Services.GetRequiredService<ServerIdentity>();
    app.Logger.LogInformation("Server key fingerprint (SHA-256): {Fingerprint}", identity.Fingerprint);
    if (await db.Users.AnyAsync(u => u.Role == Roles.Admin)) return;
    // First start: create the admin with the fixed password from configuration (Admin:Password, default in appsettings.json).
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

public partial class Program;
