using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using SignalTrace.Core;

namespace SignalTrace.Server.Tests;

/// <summary>In-process server with its own temp data dir and cheap PBKDF2 (tests only).</summary>
public sealed class StFactory : WebApplicationFactory<Program>
{
    public const string AdminPassword = "AdminPass#2026";
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "st-tests-" + Guid.NewGuid().ToString("N"));
    private readonly Dictionary<string, string?> _extra;

    public StFactory(Dictionary<string, string?>? extra = null) => _extra = extra ?? [];

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

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }
}

/// <summary>A device as the real clients do it: login → register → pin key → handshake → sealed packets.</summary>
public sealed class TestDevice
{
    public required HttpClient Http { get; init; }
    public required string DeviceId { get; init; }
    public required string DeviceToken { get; init; }
    public required byte[] PinnedKey { get; init; }
    public SessionKeys? Keys { get; private set; }
    public string? SessionId { get; private set; }
    public ulong Seq { get; set; }

    public static async Task<string> Login(HttpClient http, string user = "admin", string password = StFactory.AdminPassword)
    {
        var r = await http.PostAsJsonAsync("/api/auth/login", new LoginRequest(user, password));
        r.EnsureSuccessStatusCode();
        return (await r.Content.ReadFromJsonAsync<LoginResponse>())!.Token;
    }

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

    public async Task<HttpResponseMessage> RawHandshake(HandshakeRequest body, bool mitm = false)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, "/api/handshake" + (mitm ? "?mitm=true" : "")) { Content = JsonContent.Create(body) };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", DeviceToken);
        return await Http.SendAsync(req);
    }

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

    public static string Payload(long ts, string extra = "") => $"{{\"t\":{ts},\"src\":\"sim\"{extra}}}";

    public TelemetryEnvelope Seal(string json, ulong? seq = null, long? ts = null)
    {
        var s = seq ?? ++Seq;
        var t = ts ?? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        json = json.Replace("$T", t.ToString());
        var ct = PacketCrypto.Seal(Keys!, DeviceId, s, t, Encoding.UTF8.GetBytes(json));
        return new TelemetryEnvelope(1, SessionId!, DeviceId, s, t, Convert.ToBase64String(ct));
    }

    public Task<HttpResponseMessage> Send(TelemetryEnvelope env, string? token = null)
        => SendRaw(JsonSerializer.Serialize(env, Json.Options), token);

    public async Task<HttpResponseMessage> SendRaw(string body, string? token = null)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, "/api/telemetry") { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token ?? DeviceToken);
        return await Http.SendAsync(req);
    }

    public static async Task<string?> Code(HttpResponseMessage r)
    {
        var text = await r.Content.ReadAsStringAsync();
        if (string.IsNullOrEmpty(text)) return null;
        using var doc = JsonDocument.Parse(text);
        return doc.RootElement.TryGetProperty("code", out var c) ? c.GetString() : null;
    }
}
