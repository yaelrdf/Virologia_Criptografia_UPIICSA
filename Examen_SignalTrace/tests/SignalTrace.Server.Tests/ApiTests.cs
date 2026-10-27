using Microsoft.Extensions.DependencyInjection;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;
using SignalTrace.Core;

namespace SignalTrace.Server.Tests;

public class ApiTests(StFactoryFixture fx) : IClassFixture<StFactoryFixture>
{
    private HttpClient Client() => fx.Factory.CreateClient();

    private async Task<TestDevice> ReadyDevice(string suite = Suites.AesGcm)
    {
        var http = Client();
        var dev = await TestDevice.Create(http, await TestDevice.Login(http));
        await dev.Handshake(suite);
        return dev;
    }

    [Fact]
    public async Task Login_WrongPassword_401_Right_200()
    {
        var http = Client();
        var bad = await http.PostAsJsonAsync("/api/auth/login", new LoginRequest("admin", "nope-nope"));
        Assert.Equal(HttpStatusCode.Unauthorized, bad.StatusCode);
        Assert.Equal("unauthorized", await TestDevice.Code(bad));
        var ok = await http.PostAsJsonAsync("/api/auth/login", new LoginRequest("admin", StFactory.AdminPassword));
        var body = await ok.Content.ReadFromJsonAsync<LoginResponse>();
        Assert.Equal("admin", body!.Role);
    }

    [Fact]
    public async Task Telemetry_Success_Then_ReadingVisible()
    {
        var dev = await ReadyDevice();
        var r = await dev.Send(dev.Seal(TestDevice.Payload(0, ",\"gps\":{\"lat\":19.43,\"lon\":-99.13},\"bat\":{\"lvl\":0.8}").Replace("\"t\":0", "\"t\":$T")));
        Assert.Equal(HttpStatusCode.Accepted, r.StatusCode);
        Assert.Equal(1UL, (await r.Content.ReadFromJsonAsync<TelemetryAck>())!.Ack);

        var http = dev.Http;
        var req = new HttpRequestMessage(HttpMethod.Get, $"/api/readings?deviceId={dev.DeviceId}");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await TestDevice.Login(http));
        var list = await (await http.SendAsync(req)).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(19.43, list[0].GetProperty("lat").GetDouble());
    }

    [Fact]
    public async Task Tampered_Ciphertext_422_AuthFailed()
    {
        var dev = await ReadyDevice();
        var env = dev.Seal("{\"t\":$T,\"src\":\"sim\"}");
        var ct = Convert.FromBase64String(env.Ct);
        ct[3] ^= 0x80;
        var r = await dev.Send(env with { Ct = Convert.ToBase64String(ct) });
        Assert.Equal((HttpStatusCode)422, r.StatusCode);
        Assert.Equal("auth_failed", await TestDevice.Code(r));
        // The rejected packet did not burn the sequence number.
        Assert.Equal(HttpStatusCode.Accepted, (await dev.Send(env)).StatusCode);
    }

    [Fact]
    public async Task Replay_409()
    {
        var dev = await ReadyDevice();
        var env = dev.Seal("{\"t\":$T,\"src\":\"sim\"}");
        Assert.Equal(HttpStatusCode.Accepted, (await dev.Send(env)).StatusCode);
        var again = await dev.Send(env);
        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
        Assert.Equal("replay", await TestDevice.Code(again));
    }

    [Fact]
    public async Task UnknownSession_404()
    {
        var dev = await ReadyDevice();
        var env = dev.Seal("{\"t\":$T,\"src\":\"sim\"}") with { Sid = "0000000000000000" };
        var r = await dev.Send(env);
        Assert.Equal(HttpStatusCode.NotFound, r.StatusCode);
        Assert.Equal("unknown_session", await TestDevice.Code(r));
    }

    [Fact]
    public async Task OtherDevicesToken_401()
    {
        var a = await ReadyDevice();
        var b = await ReadyDevice();
        var r = await a.Send(a.Seal("{\"t\":$T,\"src\":\"sim\"}"), token: b.DeviceToken);
        Assert.Equal(HttpStatusCode.Unauthorized, r.StatusCode);
        Assert.Equal("unauthorized", await TestDevice.Code(r));
    }

    [Fact]
    public async Task InvalidPayload_422_And_SeqNotConsumed()
    {
        var dev = await ReadyDevice();
        var bad = dev.Seal("{\"t\":$T,\"src\":\"sim\",\"gps\":{\"lat\":200,\"lon\":0}}", seq: 1);
        var r = await dev.Send(bad);
        Assert.Equal((HttpStatusCode)422, r.StatusCode);
        Assert.Equal("invalid_payload", await TestDevice.Code(r));
        Assert.Equal(HttpStatusCode.Accepted, (await dev.Send(dev.Seal("{\"t\":$T,\"src\":\"sim\"}", seq: 1))).StatusCode);
        // t of the payload must equal ts of the envelope (both are authenticated).
        var mismatch = dev.Seal("{\"t\":5,\"src\":\"sim\"}", seq: 2);
        Assert.Equal("invalid_payload", await TestDevice.Code(await dev.Send(mismatch)));
    }

    [Fact]
    public async Task Malformed_400_And_NoToken_401()
    {
        var dev = await ReadyDevice();
        var r = await dev.SendRaw("{not json");
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        Assert.Equal("malformed", await TestDevice.Code(r));
        var anon = await Client().PostAsync("/api/telemetry", JsonContent.Create(new { v = 1 }));
        Assert.Equal(HttpStatusCode.Unauthorized, anon.StatusCode);
        Assert.Equal("unauthorized", await TestDevice.Code(anon));
    }

    [Fact]
    public async Task Roles_ViewerCannotCreateUsers_DeviceTokenCannotReadData()
    {
        var http = Client();
        var admin = await TestDevice.Login(http);
        var name = "viewer" + RandomNumberGenerator.GetHexString(4).ToLowerInvariant();
        var create = new HttpRequestMessage(HttpMethod.Post, "/api/users") { Content = JsonContent.Create(new CreateUserRequest(name, "viewer-pass-1", "viewer")) };
        create.Headers.Authorization = new AuthenticationHeaderValue("Bearer", admin);
        Assert.Equal(HttpStatusCode.Created, (await http.SendAsync(create)).StatusCode);

        var viewer = await TestDevice.Login(http, name, "viewer-pass-1");
        var again = new HttpRequestMessage(HttpMethod.Post, "/api/users") { Content = JsonContent.Create(new CreateUserRequest("x" + name, "viewer-pass-1", "viewer")) };
        again.Headers.Authorization = new AuthenticationHeaderValue("Bearer", viewer);
        Assert.Equal(HttpStatusCode.Forbidden, (await http.SendAsync(again)).StatusCode);

        var dev = await TestDevice.Create(http, admin);
        var read = new HttpRequestMessage(HttpMethod.Get, "/api/devices");
        read.Headers.Authorization = new AuthenticationHeaderValue("Bearer", dev.DeviceToken);
        Assert.Equal(HttpStatusCode.Forbidden, (await http.SendAsync(read)).StatusCode);

        // A viewer does not see the admin's devices.
        var vlist = new HttpRequestMessage(HttpMethod.Get, $"/api/readings?deviceId={dev.DeviceId}");
        vlist.Headers.Authorization = new AuthenticationHeaderValue("Bearer", viewer);
        Assert.Equal(HttpStatusCode.Forbidden, (await http.SendAsync(vlist)).StatusCode);
    }

    [Fact]
    public async Task Mitm_Handshake_RejectedByClient()
    {
        var http = Client();
        var dev = await TestDevice.Create(http, await TestDevice.Login(http));
        using var hs = new ClientHandshake(dev.DeviceId);
        var r = await dev.RawHandshake(hs.Request(), mitm: true);
        var resp = (await r.Content.ReadFromJsonAsync<HandshakeResponse>())!;
        Assert.Throws<CryptographicException>(() => hs.Complete(resp, dev.PinnedKey));
    }

    [Fact]
    public async Task Handshake_RejectsBadKeyLength()
    {
        var http = Client();
        var dev = await TestDevice.Create(http, await TestDevice.Login(http));
        var r = await dev.RawHandshake(new HandshakeRequest(dev.DeviceId, Convert.ToBase64String(new byte[10]), Convert.ToBase64String(new byte[16])));
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
    }

    [Fact]
    public async Task ChaCha20Suite_Works()
    {
        if (!Suites.IsSupported(Suites.ChaCha20Poly1305)) return;
        var dev = await ReadyDevice(Suites.ChaCha20Poly1305);
        Assert.Equal(Suites.ChaCha20Poly1305, dev.Keys!.Suite);
        Assert.Equal(HttpStatusCode.Accepted, (await dev.Send(dev.Seal("{\"t\":$T,\"src\":\"sim\"}"))).StatusCode);
    }

    [Fact]
    public async Task Enrollment_Code_Redeem_Once()
    {
        var http = Client();
        var req = new HttpRequestMessage(HttpMethod.Post, "/api/enroll");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await TestDevice.Login(http));
        var enroll = await (await http.SendAsync(req)).Content.ReadFromJsonAsync<JsonElement>();
        var code = enroll.GetProperty("code").GetString()!;
        Assert.Contains(code, enroll.GetProperty("qr").GetString());
        var first = await http.PostAsJsonAsync("/api/enroll/redeem", new { code, name = "phone", kind = "android" });
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        var second = await http.PostAsJsonAsync("/api/enroll/redeem", new { code, name = "phone", kind = "android" });
        Assert.Equal(HttpStatusCode.Unauthorized, second.StatusCode);
    }

    [Fact]
    public async Task Lab_Ycm1Attack_RecoversSecondMessage()
    {
        var http = Client();
        var req = new HttpRequestMessage(HttpMethod.Post, "/api/lab/ycm1-attack") { Content = JsonContent.Create(new { }) };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await TestDevice.Login(http));
        var r = await (await http.SendAsync(req)).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("{\"gps\":{\"lat\":19.5046,\"lon\":-99.1468}}", r.GetProperty("recoveredPlaintext").GetString());
    }

    [Fact]
    public async Task Lab_ResetSessions_GivesUnknownSession()
    {
        var dev = await ReadyDevice();
        var req = new HttpRequestMessage(HttpMethod.Post, "/api/lab/reset-sessions");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await TestDevice.Login(dev.Http));
        (await dev.Http.SendAsync(req)).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.NotFound, (await dev.Send(dev.Seal("{\"t\":$T,\"src\":\"sim\"}"))).StatusCode);
        await dev.Handshake();
        Assert.Equal(HttpStatusCode.Accepted, (await dev.Send(dev.Seal("{\"t\":$T,\"src\":\"sim\"}"))).StatusCode);
    }

    [Fact]
    public async Task SignalR_Authenticated_ReceivesReadingAndTrace()
    {
        var dev = await ReadyDevice();
        var token = await TestDevice.Login(dev.Http);
        var conn = new HubConnectionBuilder()
            .WithUrl(new Uri(fx.Factory.Server.BaseAddress, "/hubs/live"), o =>
            {
                o.HttpMessageHandlerFactory = _ => fx.Factory.Server.CreateHandler();
                o.Transports = HttpTransportType.LongPolling;
                o.AccessTokenProvider = () => Task.FromResult<string?>(token);
            })
            .Build();
        var reading = new TaskCompletionSource<JsonElement>();
        var trace = new TaskCompletionSource<JsonElement>();
        conn.On<string, JsonElement>("Reading", (_, r) => reading.TrySetResult(r));
        conn.On<JsonElement>("PacketTrace", t => { if (t.GetProperty("status").GetString() == "ok") trace.TrySetResult(t); });
        await conn.StartAsync();
        await conn.InvokeAsync("Subscribe", dev.DeviceId);
        Assert.Equal(HttpStatusCode.Accepted, (await dev.Send(dev.Seal("{\"t\":$T,\"src\":\"sim\",\"bat\":{\"lvl\":0.5}}"))).StatusCode);
        var r = await reading.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(0.5, r.GetProperty("batLvl").GetDouble());
        var t = await trace.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(9, t.GetProperty("ycm1Stages").GetArrayLength() - 1); // padded + 3 rounds × 3 stages
        await conn.DisposeAsync();

        var anonymous = new HubConnectionBuilder()
            .WithUrl(new Uri(fx.Factory.Server.BaseAddress, "/hubs/live"), o => { o.HttpMessageHandlerFactory = _ => fx.Factory.Server.CreateHandler(); o.Transports = HttpTransportType.LongPolling; })
            .Build();
        await Assert.ThrowsAnyAsync<Exception>(() => anonymous.StartAsync());
    }
}

public class ReplayWindowTests
{
    [Fact]
    public async Task Window_AcceptsOutOfOrder_RejectsDuplicates()
    {
        using var f = new StFactory(new() { ["Telemetry:ReplayWindow"] = "true" });
        var http = f.CreateClient();
        var dev = await TestDevice.Create(http, await TestDevice.Login(http));
        await dev.Handshake();
        var p = "{\"t\":$T,\"src\":\"sim\"}";
        Assert.Equal(HttpStatusCode.Accepted, (await dev.Send(dev.Seal(p, seq: 5))).StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, (await dev.Send(dev.Seal(p, seq: 3))).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await dev.Send(dev.Seal(p, seq: 3))).StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, (await dev.Send(dev.Seal(p, seq: 70))).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await dev.Send(dev.Seal(p, seq: 5))).StatusCode); // older than 64
    }

    [Fact]
    public async Task EncryptAtRest_StoresCiphertext_ApiReturnsPlain()
    {
        using var f = new StFactory(new() { ["Storage:EncryptAtRest"] = "true" });
        var http = f.CreateClient();
        var admin = await TestDevice.Login(http);
        var dev = await TestDevice.Create(http, admin);
        await dev.Handshake();
        Assert.Equal(HttpStatusCode.Accepted, (await dev.Send(dev.Seal("{\"t\":$T,\"src\":\"sim\",\"bat\":{\"lvl\":0.42,\"chg\":true}}"))).StatusCode);
        using (var scope = f.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<Data.AppDb>();
            Assert.StartsWith("enc:", db.Readings.Single().Json);
        }
        var req = new HttpRequestMessage(HttpMethod.Get, $"/api/readings?deviceId={dev.DeviceId}");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", admin);
        var list = await (await http.SendAsync(req)).Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(list[0].GetProperty("batChg").GetBoolean());
    }
}

public sealed class StFactoryFixture : IDisposable
{
    public StFactory Factory { get; } = new();
    public void Dispose() => Factory.Dispose();
}
