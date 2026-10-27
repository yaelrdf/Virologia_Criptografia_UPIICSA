using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using SignalTrace.Core;

namespace SignalTrace.Server.Tests;

/// <summary>User management: roles, admin password reset and changing one's own password.</summary>
public class UserTests(StFactoryFixture fx) : IClassFixture<StFactoryFixture>
{
    private static async Task<HttpResponseMessage> Send(HttpClient http, HttpMethod method, string url, string token, object? body = null)
    {
        var req = new HttpRequestMessage(method, url) { Content = body is null ? null : JsonContent.Create(body) };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await http.SendAsync(req);
    }

    private static async Task<int> CreateUser(HttpClient http, string admin, string name, string password, string role = "viewer")
    {
        var r = await Send(http, HttpMethod.Post, "/api/users", admin, new CreateUserRequest(name, password, role));
        Assert.Equal(HttpStatusCode.Created, r.StatusCode);
        return (await r.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt32();
    }

    [Fact]
    public async Task Admin_ChangesRole_ViewerBecomesAdmin()
    {
        var http = fx.Factory.CreateClient();
        var admin = await TestDevice.Login(http);
        var id = await CreateUser(http, admin, "rolero", "viewer-pass-1");
        var viewer = await TestDevice.Login(http, "rolero", "viewer-pass-1");
        Assert.Equal(HttpStatusCode.Forbidden, (await Send(http, HttpMethod.Get, "/api/users", viewer)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Send(http, HttpMethod.Put, $"/api/users/{id}/role", viewer, new { role = "admin" })).StatusCode);

        var r = await Send(http, HttpMethod.Put, $"/api/users/{id}/role", admin, new { role = "admin" });
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Send(http, HttpMethod.Put, $"/api/users/{id}/role", admin, new { role = "root" })).StatusCode);
        // The role is read at login: a new token carries it.
        var promoted = await TestDevice.Login(http, "rolero", "viewer-pass-1");
        Assert.Equal(HttpStatusCode.OK, (await Send(http, HttpMethod.Get, "/api/users", promoted)).StatusCode);
    }

    [Fact]
    public async Task LastAdmin_CannotBeDemoted()
    {
        using var factory = new StFactory();
        var http = factory.CreateClient();
        var admin = await TestDevice.Login(http);
        var users = await (await Send(http, HttpMethod.Get, "/api/users", admin)).Content.ReadFromJsonAsync<JsonElement>();
        var adminId = users.EnumerateArray().Single(u => u.GetProperty("username").GetString() == "admin").GetProperty("id").GetInt32();
        var r = await Send(http, HttpMethod.Put, $"/api/users/{adminId}/role", admin, new { role = "viewer" });
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        Assert.Equal("last_admin", await TestDevice.Code(r));
    }

    [Fact]
    public async Task Admin_ResetsPassword_OldStopsWorking_NewWorks()
    {
        var http = fx.Factory.CreateClient();
        var admin = await TestDevice.Login(http);
        var id = await CreateUser(http, admin, "olvidadizo", "old-password");
        var r = await Send(http, HttpMethod.Post, $"/api/users/{id}/reset-password", admin);
        var reset = (await r.Content.ReadFromJsonAsync<ResetPasswordResponse>())!;
        Assert.Equal(16, reset.Password.Length);
        var oldLogin = await http.PostAsJsonAsync("/api/auth/login", new LoginRequest("olvidadizo", "old-password"));
        Assert.Equal(HttpStatusCode.Unauthorized, oldLogin.StatusCode);
        await TestDevice.Login(http, "olvidadizo", reset.Password);
    }

    [Fact]
    public async Task User_ChangesOwnPassword_RequiresCurrent()
    {
        var http = fx.Factory.CreateClient();
        var admin = await TestDevice.Login(http);
        await CreateUser(http, admin, "cambiante", "first-password");
        var me = await TestDevice.Login(http, "cambiante", "first-password");

        var wrong = await Send(http, HttpMethod.Post, "/api/auth/change-password", me, new { current = "nope-nope", @new = "second-password" });
        Assert.Equal(HttpStatusCode.BadRequest, wrong.StatusCode);
        Assert.Equal("wrong_password", await TestDevice.Code(wrong));
        var tooShort = await Send(http, HttpMethod.Post, "/api/auth/change-password", me, new { current = "first-password", @new = "short" });
        Assert.Equal(HttpStatusCode.BadRequest, tooShort.StatusCode);

        var ok = await Send(http, HttpMethod.Post, "/api/auth/change-password", me, new { current = "first-password", @new = "second-password" });
        Assert.Equal(HttpStatusCode.NoContent, ok.StatusCode);
        await TestDevice.Login(http, "cambiante", "second-password");
    }
}
