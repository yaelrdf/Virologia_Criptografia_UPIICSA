using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using SignalTrace.Core;

namespace SignalTrace.Server.Tests;

/// <summary>
/// Servidor en el mismo proceso de las pruebas, con su propia carpeta de datos temporal y PBKDF2 barato
/// (1000 iteraciones, solo en pruebas). Habilita el laboratorio y la demostración de intermediario.
/// </summary>
public sealed class StFactory : WebApplicationFactory<Program>
{
    /// <summary>Contraseña del administrador inicial en las pruebas.</summary>
    public const string AdminPassword = "AdminPass#2026";
    /// <summary>Carpeta temporal única para esta instancia; se borra al terminar.</summary>
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "st-tests-" + Guid.NewGuid().ToString("N"));
    /// <summary>Configuración adicional de una prueba concreta.</summary>
    private readonly Dictionary<string, string?> _extra;

    /// <summary>Crea el servidor de prueba con configuración opcional que reemplaza la predeterminada.</summary>
    public StFactory(Dictionary<string, string?>? extra = null) => _extra = extra ?? [];

    /// <summary>Aplica la configuración de pruebas al servidor.</summary>
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        var settings = new Dictionary<string, string?>
        {
            ["Storage:DataDir"] = _dir,
            ["Auth:Pbkdf2Iterations"] = "1000",
            ["Admin:Password"] = AdminPassword,
            ["Mqtt:Enabled"] = "false",
            ["Lab:Enabled"] = "true",
            ["Lab:EnableMitmDemo"] = "true",
            ["RateLimits:LoginPerMinute"] = "10000",
        };
        foreach (var (k, v) in _extra) settings[k] = v;
        foreach (var (k, v) in settings) builder.UseSetting(k, v);
    }

    /// <summary>Detiene el servidor y borra su carpeta de datos.</summary>
    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }
}

/// <summary>
/// Un dispositivo que se comporta como los clientes reales: inicio de sesión → registro → fijar la llave →
/// handshake → paquetes sellados. Permite además enviar sobres alterados para probar los rechazos.
/// </summary>
public sealed class TestDevice
{
    /// <summary>Cliente HTTP conectado al servidor de prueba.</summary>
    public required HttpClient Http { get; init; }
    /// <summary>Id del dispositivo registrado.</summary>
    public required string DeviceId { get; init; }
    /// <summary>Token del dispositivo.</summary>
    public required string DeviceToken { get; init; }
    /// <summary>Llave pública fijada del servidor.</summary>
    public required byte[] PinnedKey { get; init; }
    /// <summary>Llaves de la sesión actual.</summary>
    public SessionKeys? Keys { get; private set; }
    /// <summary>Id de la sesión actual.</summary>
    public string? SessionId { get; private set; }
    /// <summary>Último número de secuencia usado.</summary>
    public ulong Seq { get; set; }

    /// <summary>Inicia sesión y devuelve el JWT del usuario (por defecto, el administrador).</summary>
    public static async Task<string> Login(HttpClient http, string user = "admin", string password = StFactory.AdminPassword)
    {
        var r = await http.PostAsJsonAsync("/api/auth/login", new LoginRequest(user, password));
        r.EnsureSuccessStatusCode();
        return (await r.Content.ReadFromJsonAsync<LoginResponse>())!.Token;
    }

    /// <summary>Registra un dispositivo con el token del usuario y fija la llave pública del servidor.</summary>
    public static async Task<TestDevice> Create(HttpClient http, string userToken, string kind = "sim")
    {
        var req = new HttpRequestMessage(HttpMethod.Post, "/api/devices") { Content = JsonContent.Create(new RegisterDeviceRequest("test-" + kind, kind)) };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", userToken);
        var r = await http.SendAsync(req);
        r.EnsureSuccessStatusCode();
        var reg = (await r.Content.ReadFromJsonAsync<RegisterDeviceResponse>())!;
        var key = (await http.GetFromJsonAsync<ServerKeyResponse>("/api/server-key"))!;
        return new TestDevice { Http = http, DeviceId = reg.DeviceId, DeviceToken = reg.DeviceToken, PinnedKey = Convert.FromBase64String(key.PublicKey) };
    }

    /// <summary>Envía una solicitud de handshake tal cual; con <paramref name="mitm"/> pide la firma del intermediario simulado.</summary>
    public async Task<HttpResponseMessage> RawHandshake(HandshakeRequest body, bool mitm = false)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, "/api/handshake" + (mitm ? "?mitm=true" : "")) { Content = JsonContent.Create(body) };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", DeviceToken);
        return await Http.SendAsync(req);
    }

    /// <summary>Hace un handshake completo y guarda las llaves y la sesión.</summary>
    public async Task Handshake(string suite = Suites.AesGcm)
    {
        using var hs = new ClientHandshake(DeviceId, suite);
        var r = await RawHandshake(hs.Request());
        r.EnsureSuccessStatusCode();
        var resp = (await r.Content.ReadFromJsonAsync<HandshakeResponse>())!;
        Keys = hs.Complete(resp, PinnedKey);
        SessionId = resp.SessionId;
        Seq = 0;
    }

    /// <summary>JSON mínimo de telemetría con campos adicionales opcionales.</summary>
    public static string Payload(long ts, string extra = "") => $"{{\"t\":{ts},\"src\":\"sim\"{extra}}}";

    /// <summary>
    /// Sella un JSON en un sobre. «$T» dentro del JSON se reemplaza por la marca de tiempo; la secuencia y el tiempo
    /// se pueden forzar para probar reenvíos y desfases.
    /// </summary>
    public TelemetryEnvelope Seal(string json, ulong? seq = null, long? ts = null)
    {
        var s = seq ?? ++Seq;
        var t = ts ?? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        json = json.Replace("$T", t.ToString());
        var ct = PacketCrypto.Seal(Keys!, DeviceId, s, t, Encoding.UTF8.GetBytes(json));
        return new TelemetryEnvelope(1, SessionId!, DeviceId, s, t, Convert.ToBase64String(ct));
    }

    /// <summary>Envía un sobre (con el token del dispositivo u otro).</summary>
    public Task<HttpResponseMessage> Send(TelemetryEnvelope env, string? token = null)
        => SendRaw(JsonSerializer.Serialize(env, Json.Options), token);

    /// <summary>Envía un cuerpo arbitrario a /api/telemetry.</summary>
    public async Task<HttpResponseMessage> SendRaw(string body, string? token = null)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, "/api/telemetry") { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token ?? DeviceToken);
        return await Http.SendAsync(req);
    }

    /// <summary>Lee el campo «code» de una respuesta de error, o null si no hay cuerpo.</summary>
    public static async Task<string?> Code(HttpResponseMessage r)
    {
        var text = await r.Content.ReadAsStringAsync();
        if (string.IsNullOrEmpty(text)) return null;
        using var doc = JsonDocument.Parse(text);
        return doc.RootElement.TryGetProperty("code", out var c) ? c.GetString() : null;
    }
}
