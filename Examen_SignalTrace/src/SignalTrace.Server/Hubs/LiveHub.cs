using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using SignalTrace.Server.Analytics;
using SignalTrace.Server.Data;
using SignalTrace.Server.Services;

namespace SignalTrace.Server.Hubs;

/// <summary>
/// /hubs/live — user JWT. Groups: "users" (everybody), "admin" (packet traces), "device:{id}" (readings of one device).
/// Server → client: Reading, DeviceStatus, Alert, Spectrum, PacketTrace. Client → server: Subscribe, Unsubscribe.
/// </summary>
[Authorize(Policy = "user")]
public sealed class LiveHub(AppDb db) : Hub
{
    public override async Task OnConnectedAsync()
    {
        await Groups.AddToGroupAsync(Context.ConnectionId, "users");
        if (Context.User!.IsAdmin()) await Groups.AddToGroupAsync(Context.ConnectionId, "admin");
        await base.OnConnectedAsync();
    }

    public async Task Subscribe(string deviceId)
    {
        if (!Guid.TryParse(deviceId, out var id)) throw new HubException("deviceId inválido");
        var user = Context.User!;
        var ok = await db.Devices.AnyAsync(d => d.Id == id && (user.IsAdmin() || d.OwnerUserId == user.UserId()));
        if (!ok) throw new HubException("Sin acceso a ese dispositivo");
        await Groups.AddToGroupAsync(Context.ConnectionId, LiveBroadcaster.DeviceGroup(id));
    }

    public Task Unsubscribe(string deviceId)
        => Guid.TryParse(deviceId, out var id) ? Groups.RemoveFromGroupAsync(Context.ConnectionId, LiveBroadcaster.DeviceGroup(id)) : Task.CompletedTask;
}

public sealed record DeviceStatusDto(string DeviceId, string Name, string Kind, bool Online, long? LastSeen, double? Battery, int? LastRssi);

public sealed record AlertDto(long Id, string DeviceId, string DeviceName, long Ts, string Kind, double? Value, string Message, bool Acknowledged)
{
    public static AlertDto From(Alert a, string deviceName) => new(a.Id, a.DeviceId.ToString("D"), deviceName, a.Ts, a.Kind, a.Value, a.Message, a.Acknowledged);
}

public sealed class LiveBroadcaster(IHubContext<LiveHub> hub, IConfiguration cfg, AlertWebhook webhook)
{
    private readonly bool _console = cfg.GetValue("Console:Enabled", true);

    public static string DeviceGroup(Guid id) => "device:" + id.ToString("D");

    public Task Reading(Guid deviceId, ReadingDto r) => hub.Clients.Group(DeviceGroup(deviceId)).SendAsync("Reading", deviceId.ToString("D"), r);

    public Task DeviceStatus(Device d, bool online)
        => hub.Clients.Group("users").SendAsync("DeviceStatus", d.Id.ToString("D"),
            new DeviceStatusDto(d.Id.ToString("D"), d.Name, d.Kind, online, d.LastSeenAt, d.LastBattery, d.LastRssi));

    public async Task Alert(Alert a, Device d)
    {
        var dto = AlertDto.From(a, d.Name);
        await hub.Clients.Group("users").SendAsync("Alert", dto);
        webhook.Enqueue(dto);
    }

    public Task Spectrum(Guid deviceId, SpectrumResult s)
        => hub.Clients.Group(DeviceGroup(deviceId)).SendAsync("Spectrum", deviceId.ToString("D"), s.Freqs, s.Mags, s.DominantHz);

    public Task PacketTrace(PacketTraceDto t) => _console ? hub.Clients.Group("admin").SendAsync("PacketTrace", t) : Task.CompletedTask;
}
