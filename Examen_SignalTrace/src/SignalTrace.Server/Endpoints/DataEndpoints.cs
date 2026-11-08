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

/// <summary>
/// Endpoints de consulta: lecturas descifradas, rejilla de tiempo con interpolación, historial de paquetes,
/// estadísticas y exportación a CSV. Un observador solo puede consultar sus propios dispositivos.
/// </summary>
public static class DataEndpoints
{
    /// <summary>Cultura invariante, para que los decimales del CSV siempre usen punto.</summary>
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    /// <summary>Registra las rutas de consulta bajo <c>/api</c>.</summary>
    public static void MapData(this WebApplication app)
    {
        var api = app.MapGroup("/api").RequireAuthorization("user");

        // Historial de un dispositivo. maxPoints reduce los rangos largos (una de cada k lecturas) para que 24 h quepan en una gráfica.
        api.MapGet("/readings", async (Guid deviceId, long? from, long? to, int? limit, int? maxPoints, AppDb db, ClaimsPrincipal me, AtRestProtector atRest) =>
        {
            if (!await CanSee(db, me, deviceId)) return Http.Error(403, "forbidden", "Sin acceso a ese dispositivo");
            var rows = await Query(db, deviceId, from, to, limit ?? 500);
            // Puntuaciones z sobre la serie completa (antes de reducirla), igual que las calcula la canalización en vivo.
            var z = AnalyticsEngine.RollingZ(rows);
            var picked = Enumerable.Range(0, rows.Count);
            if (maxPoints is > 0 and var maxP && rows.Count > maxP)
            {
                var stride = (int)Math.Ceiling(rows.Count / (double)maxP);
                // Conserva una de cada k lecturas, la última y todas las anomalías, para que la reducción no las oculte.
                picked = picked.Where(i => i % stride == 0 || i == rows.Count - 1 || z[i]?.Values.Any(v => Math.Abs(v) > AnalyticsEngine.ZThreshold) == true);
            }
            return Results.Ok(picked.Select(i => ReadingDto.From(rows[i], Parse(atRest, rows[i].Json), null, z[i])));
        });

        // Rejilla de tiempo regular para un campo: muestras reales, puntos interpolados (marcados) y huecos (null).
        // También devuelve la media móvil simple y la exponencial de los valores.
        api.MapGet("/readings/grid", async (Guid deviceId, string field, long? from, long? to, long? stepMs, AppDb db, ClaimsPrincipal me) =>
        {
            if (!await CanSee(db, me, deviceId)) return Http.Error(403, "forbidden", "Sin acceso a ese dispositivo");
            var selector = FieldSelector(field);
            if (selector is null) return Http.Error(400, "malformed", "field: rssi | rtt | loss | rms | battery | gyr | lux | hpa");
            var device = await db.Devices.FindAsync(deviceId);
            var rows = await Query(db, deviceId, from, to, 2000);
            // Solo las lecturas que traen ese campo.
            var samples = rows.Select(r => (r.TsDevice, V: selector(r))).Where(x => x.V is not null).Select(x => (x.TsDevice, x.V!.Value)).ToList();
            // Paso de la rejilla: el pedido o el intervalo medio del dispositivo, entre 200 ms y 1 min.
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

        // Bitácora de paquetes (solo administradores), filtrable por dispositivo y estado.
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

        // Últimas trazas de paquetes guardadas en memoria para la consola.
        app.MapGet("/api/console/traces", (int? limit, TraceStore traces) => Results.Ok(traces.Recent(Math.Clamp(limit ?? 30, 1, 100))))
            .RequireAuthorization("admin");

        // Totales de dispositivos, lecturas y paquetes por estado, limitados a lo que el usuario puede ver.
        api.MapGet("/stats", async (AppDb db, ClaimsPrincipal me) =>
        {
            var visible = (await VisibleDevices(db, me)).Keys.ToList();
            var byStatus = await db.Packets.Where(p => p.DeviceId != null && visible.Contains(p.DeviceId.Value))
                .GroupBy(p => p.Status).Select(g => new { status = g.Key, count = g.Count() }).ToListAsync();
            return Results.Ok(new { devices = visible.Count, readings = await db.Readings.CountAsync(r => visible.Contains(r.DeviceId)), packets = byStatus });
        });

        // ---- Exportación
        // Lecturas de un dispositivo en CSV, con una columna por cada valor resumido.
        api.MapGet("/export/readings.csv", async (Guid deviceId, long? from, long? to, AppDb db, ClaimsPrincipal me) =>
        {
            if (!await CanSee(db, me, deviceId)) return Http.Error(403, "forbidden", "Sin acceso a ese dispositivo");
            var rows = await Query(db, deviceId, from, to, 100_000);
            var sb = new StringBuilder("ts_device,ts_server,seq,src,acc_rms,gyr_norm,lux,hpa,bat_lvl,net_type,probe_rtt,probe_loss,wifi_rssi,cell_dbm,cell_type\n");
            foreach (var r in rows)
                sb.AppendJoin(',', r.TsDevice, r.TsServer, r.Seq, r.Src, N(r.AccRms), N(r.GyrNorm), N(r.Lux), N(r.Hpa), N(r.BatLvl), r.NetType, N(r.ProbeRtt), N(r.ProbeLoss), r.WifiRssi, r.CellDbm, r.CellType).Append('\n');
            return Results.File(Encoding.UTF8.GetBytes(sb.ToString()), "text/csv", $"readings-{deviceId:D}.csv");
        });
    }

    /// <summary>Formatea un número opcional para el CSV (hasta 6 decimales, vacío si no hay valor).</summary>
    private static string N(double? v) => v?.ToString("0.######", Inv) ?? "";

    /// <summary>Devuelve la función que extrae el campo pedido de una lectura, o null si el nombre no existe.</summary>
    /// <param name="field">rssi, rtt, loss, rms, battery, gyr, lux o hpa.</param>
    private static Func<Reading, double?>? FieldSelector(string field) => field switch
    {
        "rssi" => r => r.WifiRssi ?? r.CellDbm,
        "rtt" => r => r.ProbeRtt,
        "loss" => r => r.ProbeLoss,
        "rms" => r => r.AccRms,
        "battery" => r => r.BatLvl,
        "gyr" => r => r.GyrNorm,
        "lux" => r => r.Lux,
        "hpa" => r => r.Hpa,
        _ => null,
    };

    /// <summary>Descifra (si estaba cifrado en reposo) y deserializa el JSON guardado; null si no se puede leer.</summary>
    private static TelemetryPayload? Parse(AtRestProtector atRest, string stored)
    {
        try { return JsonSerializer.Deserialize<TelemetryPayload>(atRest.Unprotect(stored), Json.Options); }
        catch (Exception) { return null; }
    }

    /// <summary>
    /// Consulta las lecturas más recientes de un dispositivo dentro del rango y las devuelve en orden cronológico.
    /// </summary>
    /// <param name="db">Base de datos.</param>
    /// <param name="deviceId">Dispositivo.</param>
    /// <param name="from">Inicio del rango (ms), opcional.</param>
    /// <param name="to">Fin del rango (ms), opcional.</param>
    /// <param name="limit">Máximo de lecturas (de 1 a 100 000).</param>
    private static async Task<List<Reading>> Query(AppDb db, Guid deviceId, long? from, long? to, int limit)
    {
        var q = db.Readings.Where(r => r.DeviceId == deviceId);
        if (from is { } f) q = q.Where(r => r.TsDevice >= f);
        if (to is { } t) q = q.Where(r => r.TsDevice <= t);
        // Toma las últimas N (orden descendente) y después las invierte para graficarlas de izquierda a derecha.
        var latest = await q.OrderByDescending(r => r.TsDevice).Take(Math.Clamp(limit, 1, 100_000)).ToListAsync();
        latest.Reverse();
        return latest;
    }

    /// <summary>Indica si el usuario puede ver el dispositivo: es administrador o es su dueño.</summary>
    public static async Task<bool> CanSee(AppDb db, ClaimsPrincipal me, Guid deviceId)
        => await db.Devices.AnyAsync(d => d.Id == deviceId && (me.IsAdmin() || d.OwnerUserId == me.UserId()));

    /// <summary>Dispositivos visibles para el usuario, con su nombre.</summary>
    private static async Task<Dictionary<Guid, string>> VisibleDevices(AppDb db, ClaimsPrincipal me)
        => await db.Devices.Where(d => me.IsAdmin() || d.OwnerUserId == me.UserId()).ToDictionaryAsync(d => d.Id, d => d.Name);
}
