using System.Globalization;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SignalTrace.Core;
using SignalTrace.Server.Analytics;
using SignalTrace.Server.Data;
using SignalTrace.Server.Hubs;
using SignalTrace.Server.Services;

namespace SignalTrace.Server.Endpoints;

/// <summary>Decrypted readings, time grid with interpolation, alerts, packet history, exports and coverage heatmap.</summary>
public static class DataEndpoints
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public static void MapData(this WebApplication app)
    {
        var api = app.MapGroup("/api").RequireAuthorization("user");

        api.MapGet("/readings", async (Guid deviceId, long? from, long? to, int? limit, AppDb db, ClaimsPrincipal me, AtRestProtector atRest) =>
        {
            if (!await CanSee(db, me, deviceId)) return Http.Error(403, "forbidden", "Sin acceso a ese dispositivo");
            var rows = await Query(db, deviceId, from, to, limit ?? 500);
            return Results.Ok(rows.Select(r => ReadingDto.From(r, Parse(atRest, r.Json), null)));
        });

        // Regular time grid for one field: real samples, interpolated points (flagged) and gaps (null).
        api.MapGet("/readings/grid", async (Guid deviceId, string field, long? from, long? to, long? stepMs, AppDb db, ClaimsPrincipal me) =>
        {
            if (!await CanSee(db, me, deviceId)) return Http.Error(403, "forbidden", "Sin acceso a ese dispositivo");
            var selector = FieldSelector(field);
            if (selector is null) return Http.Error(400, "malformed", "field: rssi | rtt | loss | rms | battery | speed");
            var device = await db.Devices.FindAsync(deviceId);
            var rows = await Query(db, deviceId, from, to, 2000);
            var samples = rows.Select(r => (r.TsDevice, V: selector(r))).Where(x => x.V is not null).Select(x => (x.TsDevice, x.V!.Value)).ToList();
            var step = stepMs ?? (long)Math.Clamp(device?.IntervalMs ?? 1000, 200, 60_000);
            var grid = Numeric.ResampleLinear(samples, step);
            var values = grid.Select(g => g.Value ?? double.NaN).ToList();
            var filled = grid.Where(g => g.Value is not null).Select(g => g.Value!.Value).ToList();
            return Results.Ok(new
            {
                field, stepMs = step,
                points = grid.Select(g => new { t = g.T, v = g.Value, interpolated = g.Interpolated }),
                sma = filled.Count > 0 ? Numeric.Sma(filled, 10) : [],
                ema = filled.Count > 0 ? Numeric.Ema(filled, AnalyticsEngine.Alpha) : [],
                interpolatedCount = grid.Count(g => g.Interpolated),
                gapCount = grid.Count(g => g.Value is null),
            });
        });

        api.MapGet("/alerts", async (Guid? deviceId, int? limit, AppDb db, ClaimsPrincipal me) =>
        {
            var visible = await VisibleDevices(db, me);
            var q = db.Alerts.Where(a => visible.Keys.Contains(a.DeviceId));
            if (deviceId is { } id) q = q.Where(a => a.DeviceId == id);
            var list = await q.OrderByDescending(a => a.Ts).Take(Math.Clamp(limit ?? 100, 1, 1000)).ToListAsync();
            return Results.Ok(list.Select(a => AlertDto.From(a, visible[a.DeviceId])));
        });

        api.MapPost("/alerts/{id:long}/ack", async (long id, AppDb db, ClaimsPrincipal me) =>
        {
            var a = await db.Alerts.FindAsync(id);
            if (a is null || !await CanSee(db, me, a.DeviceId)) return Http.Error(404, "not_found", "Alerta inexistente");
            a.Acknowledged = true;
            await db.SaveChangesAsync();
            return Results.NoContent();
        });

        app.MapGet("/api/packets", async (Guid? deviceId, int? limit, string? status, AppDb db) =>
        {
            var q = db.Packets.AsQueryable();
            if (deviceId is { } id) q = q.Where(p => p.DeviceId == id);
            if (!string.IsNullOrEmpty(status)) q = q.Where(p => p.Status == status);
            var list = await q.OrderByDescending(p => p.Id).Take(Math.Clamp(limit ?? 100, 1, 1000))
                .Select(p => new { p.Id, deviceId = p.DeviceId, p.SessionId, p.Seq, p.TsServer, p.Status, p.Reason, p.WireBytes, p.SourceHash, p.Transport })
                .ToListAsync();
            return Results.Ok(list);
        }).RequireAuthorization("admin");

        app.MapGet("/api/console/traces", (int? limit, TraceStore traces) => Results.Ok(traces.Recent(Math.Clamp(limit ?? 30, 1, 100))))
            .RequireAuthorization("admin");

        api.MapGet("/stats", async (AppDb db, ClaimsPrincipal me) =>
        {
            var visible = (await VisibleDevices(db, me)).Keys.ToList();
            var byStatus = await db.Packets.Where(p => p.DeviceId != null && visible.Contains(p.DeviceId.Value))
                .GroupBy(p => p.Status).Select(g => new { status = g.Key, count = g.Count() }).ToListAsync();
            return Results.Ok(new { devices = visible.Count, readings = await db.Readings.CountAsync(r => visible.Contains(r.DeviceId)), packets = byStatus });
        });

        // ---- Exports
        api.MapGet("/export/readings.csv", async (Guid deviceId, long? from, long? to, AppDb db, ClaimsPrincipal me) =>
        {
            if (!await CanSee(db, me, deviceId)) return Http.Error(403, "forbidden", "Sin acceso a ese dispositivo");
            var rows = await Query(db, deviceId, from, to, 100_000);
            var sb = new StringBuilder("ts_device,ts_server,seq,src,lat,lon,acc,spd,acc_rms,bat_lvl,net_type,probe_rtt,probe_loss,wifi_rssi,cell_dbm,cell_type\n");
            foreach (var r in rows)
                sb.AppendJoin(',', r.TsDevice, r.TsServer, r.Seq, r.Src, N(r.Lat), N(r.Lon), N(r.Acc), N(r.Spd), N(r.AccRms), N(r.BatLvl), r.NetType, N(r.ProbeRtt), N(r.ProbeLoss), r.WifiRssi, r.CellDbm, r.CellType).Append('\n');
            return Results.File(Encoding.UTF8.GetBytes(sb.ToString()), "text/csv", $"readings-{deviceId:D}.csv");
        });

        api.MapGet("/export/track.geojson", async (Guid deviceId, long? from, long? to, AppDb db, ClaimsPrincipal me) =>
        {
            if (!await CanSee(db, me, deviceId)) return Http.Error(403, "forbidden", "Sin acceso a ese dispositivo");
            var fixes = (await Query(db, deviceId, from, to, 100_000)).Where(r => r.Lat is not null).ToList();
            var features = new List<object>
            {
                new { type = "Feature", properties = new { name = "ruta", points = fixes.Count },
                      geometry = new { type = "LineString", coordinates = fixes.Select(r => new[] { r.Lon!.Value, r.Lat!.Value }) } },
            };
            features.AddRange(fixes.Select(r => (object)new
            {
                type = "Feature",
                properties = new { ts = r.TsDevice, rssi = r.WifiRssi ?? r.CellDbm, src = r.Src },
                geometry = new { type = "Point", coordinates = new[] { r.Lon!.Value, r.Lat!.Value } },
            }));
            var json = JsonSerializer.Serialize(new { type = "FeatureCollection", features });
            return Results.File(Encoding.UTF8.GetBytes(json), "application/geo+json", $"track-{deviceId:D}.geojson");
        });

        api.MapGet("/export/track.kml", async (Guid deviceId, long? from, long? to, AppDb db, ClaimsPrincipal me) =>
        {
            if (!await CanSee(db, me, deviceId)) return Http.Error(403, "forbidden", "Sin acceso a ese dispositivo");
            var fixes = (await Query(db, deviceId, from, to, 100_000)).Where(r => r.Lat is not null).ToList();
            var coords = string.Join(' ', fixes.Select(r => $"{N(r.Lon)},{N(r.Lat)},0"));
            var kml = $"""
                <?xml version="1.0" encoding="UTF-8"?>
                <kml xmlns="http://www.opengis.net/kml/2.2"><Document><name>SignalTrace {deviceId:D}</name>
                <Placemark><name>Ruta</name><LineString><tessellate>1</tessellate><coordinates>{coords}</coordinates></LineString></Placemark>
                </Document></kml>
                """;
            return Results.File(Encoding.UTF8.GetBytes(kml), "application/vnd.google-earth.kml+xml", $"track-{deviceId:D}.kml");
        });

        // Coverage heatmap: mean signal per grid cell (cellM metres), computed on the server.
        api.MapGet("/heatmap", async (Guid deviceId, double? cellM, AppDb db, ClaimsPrincipal me) =>
        {
            if (!await CanSee(db, me, deviceId)) return Http.Error(403, "forbidden", "Sin acceso a ese dispositivo");
            var size = Math.Clamp(cellM ?? 25, 5, 1000);
            var rows = (await Query(db, deviceId, null, null, 100_000)).Where(r => r.Lat is not null && (r.WifiRssi ?? r.CellDbm) is not null).ToList();
            var cells = Heatmap.Build(rows.Select(r => (r.Lat!.Value, r.Lon!.Value, (double)(r.WifiRssi ?? r.CellDbm)!.Value)), size);
            return Results.Ok(new { cellM = size, cells });
        });
    }

    private static string N(double? v) => v?.ToString("0.######", Inv) ?? "";

    private static Func<Reading, double?>? FieldSelector(string field) => field switch
    {
        "rssi" => r => r.WifiRssi ?? r.CellDbm,
        "rtt" => r => r.ProbeRtt,
        "loss" => r => r.ProbeLoss,
        "rms" => r => r.AccRms,
        "battery" => r => r.BatLvl,
        "speed" => r => r.Spd,
        _ => null,
    };

    private static TelemetryPayload? Parse(AtRestProtector atRest, string stored)
    {
        try { return JsonSerializer.Deserialize<TelemetryPayload>(atRest.Unprotect(stored), Json.Options); }
        catch (Exception) { return null; }
    }

    private static async Task<List<Reading>> Query(AppDb db, Guid deviceId, long? from, long? to, int limit)
    {
        var q = db.Readings.Where(r => r.DeviceId == deviceId);
        if (from is { } f) q = q.Where(r => r.TsDevice >= f);
        if (to is { } t) q = q.Where(r => r.TsDevice <= t);
        var latest = await q.OrderByDescending(r => r.TsDevice).Take(Math.Clamp(limit, 1, 100_000)).ToListAsync();
        latest.Reverse();
        return latest;
    }

    public static async Task<bool> CanSee(AppDb db, ClaimsPrincipal me, Guid deviceId)
        => await db.Devices.AnyAsync(d => d.Id == deviceId && (me.IsAdmin() || d.OwnerUserId == me.UserId()));

    private static async Task<Dictionary<Guid, string>> VisibleDevices(AppDb db, ClaimsPrincipal me)
        => await db.Devices.Where(d => me.IsAdmin() || d.OwnerUserId == me.UserId()).ToDictionaryAsync(d => d.Id, d => d.Name);
}

public static class Heatmap
{
    public sealed record Cell(double Lat, double Lon, double Mean, int Count);

    /// <summary>Equirectangular grid: Δlat = size / R, Δlon = size / (R cos φ). Mean signal per cell.</summary>
    public static List<Cell> Build(IEnumerable<(double Lat, double Lon, double V)> points, double sizeM)
    {
        var list = points.ToList();
        if (list.Count == 0) return [];
        var lat0 = list.Average(p => p.Lat) * Math.PI / 180;
        var dLat = sizeM / Numeric.EarthRadiusM * 180 / Math.PI;
        var dLon = dLat / Math.Max(0.01, Math.Cos(lat0));
        return list.GroupBy(p => (Math.Floor(p.Lat / dLat), Math.Floor(p.Lon / dLon)))
            .Select(g => new Cell((g.Key.Item1 + 0.5) * dLat, (g.Key.Item2 + 0.5) * dLon, Math.Round(g.Average(p => p.V), 1), g.Count()))
            .ToList();
    }
}
