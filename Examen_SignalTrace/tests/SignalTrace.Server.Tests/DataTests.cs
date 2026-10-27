using System.Net.Http.Json;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace SignalTrace.Server.Tests;

/// <summary>Dashboard data endpoints: interpolated grid, sensor-bank columns and CSV export.</summary>
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
    public async Task SensorBank_Columns_And_CsvExport()
    {
        var http = fx.Factory.CreateClient();
        var admin = await TestDevice.Login(http);
        var dev = await TestDevice.Create(http, admin);
        await dev.Handshake();
        const string sensors = ",\"gyr\":{\"x\":0.3,\"y\":0,\"z\":0.4},\"mfl\":{\"x\":20,\"y\":-5,\"z\":-40},\"env\":{\"lux\":150,\"prx\":5,\"hpa\":780.5,\"tmp\":24,\"hum\":40},\"stp\":{\"n\":12},\"wifi\":{\"rssi\":-61,\"tx\":433,\"rx\":390}";
        Assert.Equal(HttpStatusCode.Accepted, (await dev.Send(dev.Seal("{\"t\":$T,\"src\":\"sim\"" + sensors + "}"))).StatusCode);
        var bad = dev.Seal("{\"t\":$T,\"src\":\"sim\",\"env\":{\"hum\":140}}");
        Assert.Equal((HttpStatusCode)422, (await dev.Send(bad)).StatusCode);

        var r = (await Get(http, admin, $"/api/readings?deviceId={dev.DeviceId}"))[0];
        Assert.Equal(0.5, r.GetProperty("gyrNorm").GetDouble(), 6);
        Assert.Equal(780.5, r.GetProperty("hpa").GetDouble());
        Assert.Equal(12, r.GetProperty("p").GetProperty("stp").GetProperty("n").GetInt32());
        var grid = await Get(http, admin, $"/api/readings/grid?deviceId={dev.DeviceId}&field=lux");
        Assert.Equal(150, grid.GetProperty("points")[0].GetProperty("v").GetDouble());

        var req = new HttpRequestMessage(HttpMethod.Get, $"/api/export/readings.csv?deviceId={dev.DeviceId}");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", admin);
        var csv = await (await http.SendAsync(req)).Content.ReadAsStringAsync();
        Assert.StartsWith("ts_device,ts_server,seq,src,acc_rms,gyr_norm,lux,hpa", csv);
        Assert.DoesNotContain("lat", csv);
    }
}
