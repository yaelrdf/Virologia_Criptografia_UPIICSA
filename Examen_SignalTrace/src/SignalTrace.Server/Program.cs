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
builder.Services.AddSingleton<AlertWebhook>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<AlertWebhook>());
builder.Services.AddHostedService<DeviceMonitor>();
builder.Services.AddHttpClient("webhook", c => c.Timeout = TimeSpan.FromSeconds(5));
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
    o.AddPolicy("login", c => RateLimitPartition.GetFixedWindowLimiter(Ip(c), _ => new() { PermitLimit = 20, Window = TimeSpan.FromMinutes(1) }));
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
app.UseRateLimiter();
app.UseAuthentication();
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
    if (await db.Users.AnyAsync()) return;
    // First start: create the admin. The password comes from Admin:Password or is printed once.
    var password = cfg["Admin:Password"];
    var generated = string.IsNullOrEmpty(password);
    if (generated) password = Convert.ToBase64String(RandomNumberGenerator.GetBytes(12)).Replace('/', 'x').Replace('+', 'y');
    var hasher = app.Services.GetRequiredService<PasswordHasher>();
    var (salt, it, hash) = hasher.Hash(password!);
    db.Users.Add(new User
    {
        Username = cfg["Admin:Username"] ?? "admin", Salt = salt, Iterations = it, PasswordHash = hash, Role = Roles.Admin,
        CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
    });
    await db.SaveChangesAsync();
    if (generated)
        Console.WriteLine($"\n==== Usuario inicial: {cfg["Admin:Username"] ?? "admin"} / contraseña: {password} (se muestra una sola vez) ====\n");
}

public partial class Program;
