using System.Net.Http.Json;
using System.Threading.Channels;
using Microsoft.EntityFrameworkCore;
using SignalTrace.Server.Analytics;
using SignalTrace.Server.Data;
using SignalTrace.Server.Hubs;

namespace SignalTrace.Server.Services;

/// <summary>
/// Marks devices offline when no packet arrives within max(15 s, 3 × interval), raises an alert and broadcasts the status.
/// Also applies the retention policy once per hour.
/// </summary>
public sealed class DeviceMonitor(IServiceScopeFactory scopes, AnalyticsEngine analytics, TimeProvider clock, IConfiguration cfg, ILogger<DeviceMonitor> log) : BackgroundService
{
    private readonly HashSet<Guid> _online = [];
    private DateTimeOffset _lastRetention = DateTimeOffset.MinValue;

    public static bool IsOnline(Device d, long now)
        => d.LastSeenAt is { } seen && now - seen <= Math.Max(15_000, 3 * (d.IntervalMs ?? 1000));

    protected override async Task ExecuteAsync(CancellationToken stop)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));
        while (await timer.WaitForNextTickAsync(stop))
        {
            try { await Tick(stop); }
            catch (Exception e) when (e is not OperationCanceledException) { log.LogWarning(e, "Device monitor tick failed"); }
        }
    }

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
            if (!_online.Remove(d.Id)) continue;
            await live.DeviceStatus(d, online: false);
            if (analytics.Raise(d.Id, "offline", null, $"{d.Name} fuera de línea", out var alert))
            {
                db.Alerts.Add(alert);
                await db.SaveChangesAsync(ct);
                await live.Alert(alert, d);
            }
        }

        if (clock.GetUtcNow() - _lastRetention > TimeSpan.FromHours(1))
        {
            _lastRetention = clock.GetUtcNow();
            var cutoff = now - (long)TimeSpan.FromDays(cfg.GetValue("Retention:Days", 30)).TotalMilliseconds;
            await db.Readings.Where(r => r.TsServer < cutoff).ExecuteDeleteAsync(ct);
            await db.Packets.Where(p => p.TsServer < cutoff).ExecuteDeleteAsync(ct);
        }
    }
}

/// <summary>Optional webhook (Alerts:WebhookUrl): each alert is POSTed as JSON, best effort, off the request path.</summary>
public sealed class AlertWebhook(IConfiguration cfg, IHttpClientFactory http, ILogger<AlertWebhook> log) : BackgroundService
{
    private readonly Channel<AlertDto> _queue = Channel.CreateBounded<AlertDto>(new BoundedChannelOptions(200) { FullMode = BoundedChannelFullMode.DropOldest });
    private readonly string? _url = string.IsNullOrWhiteSpace(cfg["Alerts:WebhookUrl"]) ? null : cfg["Alerts:WebhookUrl"];

    public void Enqueue(AlertDto a)
    {
        if (_url is not null) _queue.Writer.TryWrite(a);
    }

    protected override async Task ExecuteAsync(CancellationToken stop)
    {
        if (_url is null) return;
        var client = http.CreateClient("webhook");
        await foreach (var a in _queue.Reader.ReadAllAsync(stop))
        {
            try { await client.PostAsJsonAsync(_url, new { source = "SignalTrace", alert = a }, stop); }
            catch (Exception e) when (e is not OperationCanceledException) { log.LogWarning("Webhook failed: {Message}", e.Message); }
        }
    }
}
