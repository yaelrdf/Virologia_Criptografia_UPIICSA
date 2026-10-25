using System.Net.Http.Json;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace SignalTrace.Server.Tests;

/// <summary>Dashboard data endpoints: interpolated grid, heatmap, exports and alerts.</summary>
public class DataTests(StFactoryFixture fx) : IClassFixture<StFactoryFixture>
{
    private static async Task<JsonElement> Get(HttpClient http, string token, string url)
    {
        var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var res = await http.SendAsync(req);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        return await res.Content.ReadFromJsonAsync<JsonElement>();
    }

    [Fact]
    public async Task Grid_FlagsInterpolatedPoints_AndLeavesLongGaps()
    {
        var http = fx.Factory.CreateClient();
        var admin = await TestDevice.Login(http);
        var dev = await TestDevice.Create(http, admin);
        await dev.Handshake();
        var t0 = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - 60_000;
        // RSSI = −50 − t/1000 at t = 0, 1, 2, 5 s (3 s pause → interpolated) and 20 s (15 s pause → gap).
        foreach (var s in new[] { 0, 1, 2, 5, 20 })
        {
            var ts = t0 + s * 1000;
            var env = dev.Seal($"{{\"t\":$T,\"src\":\"sim\",\"wifi\":{{\"rssi\":{-50 - s}}}}}", ts: ts);
            Assert.Equal(HttpStatusCode.Accepted, (await dev.Send(env)).StatusCode);
        }
        var g = await Get(http, admin, $"/api/readings/grid?deviceId={dev.DeviceId}&field=rssi&stepMs=1000");
        var pts = g.GetProperty("points").EnumerateArray().ToArray();
        Assert.Equal(-53, pts[3].GetProperty("v").GetDouble(), 6);      // exact on the line
        Assert.True(pts[3].GetProperty("interpolated").GetBoolean());
        Assert.False(pts[5].GetProperty("interpolated").GetBoolean());
        Assert.False(pts[10].TryGetProperty("v", out _)); // inside the 15 s gap: no value (nulls are omitted)
        Assert.Equal(2, g.GetProperty("interpolatedCount").GetInt32());
        Assert.True(g.GetProperty("gapCount").GetInt32() > 0);
    }

    [Fact]
    public async Task Heatmap_Exports_And_LowSignalAlert()
    {
        var http = fx.Factory.CreateClient();
        var admin = await TestDevice.Login(http);
        var dev = await TestDevice.Create(http, admin);
        await dev.Handshake();
        Assert.Equal(HttpStatusCode.Accepted, (await dev.Send(dev.Seal("{\"t\":$T,\"src\":\"sim\",\"gps\":{\"lat\":19.3955,\"lon\":-99.0915},\"wifi\":{\"rssi\":-88}}"))).StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, (await dev.Send(dev.Seal("{\"t\":$T,\"src\":\"sim\",\"gps\":{\"lat\":19.39551,\"lon\":-99.09151},\"wifi\":{\"rssi\":-60}}"))).StatusCode);

        var h = await Get(http, admin, $"/api/heatmap?deviceId={dev.DeviceId}&cellM=25");
        var cell = Assert.Single(h.GetProperty("cells").EnumerateArray());
        Assert.Equal(-74, cell.GetProperty("mean").GetDouble());

        var alerts = await Get(http, admin, $"/api/alerts?deviceId={dev.DeviceId}");
        Assert.Contains(alerts.EnumerateArray(), a => a.GetProperty("kind").GetString() == "low_signal");

        foreach (var (path, needle) in new[] { ("readings.csv", "ts_device,ts_server"), ("track.geojson", "LineString"), ("track.kml", "<coordinates>") })
        {
            var req = new HttpRequestMessage(HttpMethod.Get, $"/api/export/{path}?deviceId={dev.DeviceId}");
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", admin);
            var body = await (await http.SendAsync(req)).Content.ReadAsStringAsync();
            Assert.Contains(needle, body);
        }
    }
}
