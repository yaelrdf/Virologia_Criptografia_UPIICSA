using Microsoft.EntityFrameworkCore;
using SignalTrace.Server.Data;
using SignalTrace.Server.Hubs;

namespace SignalTrace.Server.Services;

/// <summary>
/// Tarea de fondo que revisa cada 5 segundos los dispositivos: marca como desconectado al que no envía un paquete
/// dentro de max(15 s, 3 × su intervalo) y avisa al tablero. También aplica la política de retención una vez por hora.
/// </summary>
/// <param name="scopes">Fábrica de ámbitos para obtener la base de datos en cada revisión.</param>
/// <param name="clock">Reloj del servidor.</param>
/// <param name="cfg">Configuración (Retention:Days).</param>
/// <param name="log">Registro de eventos.</param>
public sealed class DeviceMonitor(IServiceScopeFactory scopes, TimeProvider clock, IConfiguration cfg, ILogger<DeviceMonitor> log) : BackgroundService
{
    /// <summary>Dispositivos que estaban en línea en la revisión anterior; sirve para avisar solo cuando cambia el estado.</summary>
    private readonly HashSet<Guid> _online = [];
    /// <summary>Última vez que se borraron datos vencidos.</summary>
    private DateTimeOffset _lastRetention = DateTimeOffset.MinValue;

    /// <summary>
    /// Un dispositivo está en línea si su último paquete llegó hace menos de max(15 s, 3 × intervalo medio).
    /// </summary>
    /// <param name="d">Dispositivo.</param>
    /// <param name="now">Hora actual en ms.</param>
    public static bool IsOnline(Device d, long now)
        => d.LastSeenAt is { } seen && now - seen <= Math.Max(15_000, 3 * (d.IntervalMs ?? 1000));

    /// <summary>Ciclo principal: una revisión cada 5 s hasta que se apague el servidor. Un fallo se registra y no detiene el ciclo.</summary>
    protected override async Task ExecuteAsync(CancellationToken stop)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));
        while (await timer.WaitForNextTickAsync(stop))
        {
            try { await Tick(stop); }
            catch (Exception e) when (e is not OperationCanceledException) { log.LogWarning(e, "Device monitor tick failed"); }
        }
    }

    /// <summary>Una revisión: detecta dispositivos que se acaban de desconectar y, si toca, borra datos antiguos.</summary>
    private async Task Tick(CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDb>();
        var live = scope.ServiceProvider.GetRequiredService<LiveBroadcaster>();
        var now = clock.GetUtcNow().ToUnixTimeMilliseconds();
        foreach (var d in await db.Devices.Where(d => !d.Revoked && d.LastSeenAt != null).ToListAsync(ct))
        {
            var online = IsOnline(d, now);
            if (online) { _online.Add(d.Id); continue; }
            // Solo avisa la transición de en línea a desconectado, no en cada revisión.
            if (!_online.Remove(d.Id)) continue;
            await live.DeviceStatus(d, online: false);
        }

        // Retención: borra lecturas y paquetes más antiguos que Retention:Days (30 días por defecto).
        if (clock.GetUtcNow() - _lastRetention > TimeSpan.FromHours(1))
        {
            _lastRetention = clock.GetUtcNow();
            var cutoff = now - (long)TimeSpan.FromDays(cfg.GetValue("Retention:Days", 30)).TotalMilliseconds;
            await db.Readings.Where(r => r.TsServer < cutoff).ExecuteDeleteAsync(ct);
            await db.Packets.Where(p => p.TsServer < cutoff).ExecuteDeleteAsync(ct);
        }
    }
}
