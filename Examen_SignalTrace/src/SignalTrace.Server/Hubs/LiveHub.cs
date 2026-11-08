using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using SignalTrace.Server.Analytics;
using SignalTrace.Server.Data;
using SignalTrace.Server.Services;

namespace SignalTrace.Server.Hubs;

/// <summary>
/// Hub de SignalR en <c>/hubs/live</c>; requiere el JWT de un usuario. Grupos: «users» (todas las personas
/// conectadas), «admin» (trazas de paquetes para la consola) y «device:{id}» (lecturas de un dispositivo).
/// Mensajes del servidor al cliente: Reading, DeviceStatus, Spectrum y PacketTrace.
/// Métodos que llama el cliente: Subscribe y Unsubscribe.
/// </summary>
/// <param name="db">Base de datos, para comprobar el acceso a cada dispositivo.</param>
[Authorize(Policy = "user")]
public sealed class LiveHub(AppDb db) : Hub
{
    /// <summary>Al conectarse, agrega la conexión al grupo general y, si es administrador, al de la consola.</summary>
    public override async Task OnConnectedAsync()
    {
        await Groups.AddToGroupAsync(Context.ConnectionId, "users");
        if (Context.User!.IsAdmin()) await Groups.AddToGroupAsync(Context.ConnectionId, "admin");
        await base.OnConnectedAsync();
    }

    /// <summary>
    /// Suscribe la conexión a las lecturas de un dispositivo, siempre que el usuario sea administrador o su dueño.
    /// </summary>
    /// <param name="deviceId">GUID del dispositivo en texto.</param>
    /// <exception cref="HubException">Si el id no es válido o el usuario no tiene acceso.</exception>
    public async Task Subscribe(string deviceId)
    {
        if (!Guid.TryParse(deviceId, out var id)) throw new HubException("deviceId inválido");
        var user = Context.User!;
        var ok = await db.Devices.AnyAsync(d => d.Id == id && (user.IsAdmin() || d.OwnerUserId == user.UserId()));
        if (!ok) throw new HubException("Sin acceso a ese dispositivo");
        await Groups.AddToGroupAsync(Context.ConnectionId, LiveBroadcaster.DeviceGroup(id));
    }

    /// <summary>Deja de recibir las lecturas del dispositivo. Un id inválido se ignora.</summary>
    /// <param name="deviceId">GUID del dispositivo en texto.</param>
    public Task Unsubscribe(string deviceId)
        => Guid.TryParse(deviceId, out var id) ? Groups.RemoveFromGroupAsync(Context.ConnectionId, LiveBroadcaster.DeviceGroup(id)) : Task.CompletedTask;
}

/// <summary>Estado resumido de un dispositivo que se envía cuando se conecta, se desconecta o cambia.</summary>
public sealed record DeviceStatusDto(string DeviceId, string Name, string Kind, bool Online, long? LastSeen, double? Battery, int? LastRssi);

/// <summary>Envía los mensajes del hub desde fuera de él (la canalización de telemetría y las tareas de fondo).</summary>
/// <param name="hub">Contexto del hub para enviar a grupos.</param>
/// <param name="cfg">Configuración; <c>Console:Enabled</c> decide si se emiten trazas de paquetes.</param>
public sealed class LiveBroadcaster(IHubContext<LiveHub> hub, IConfiguration cfg)
{
    /// <summary>Si la consola del administrador está habilitada.</summary>
    private readonly bool _console = cfg.GetValue("Console:Enabled", true);

    /// <summary>Nombre del grupo de un dispositivo.</summary>
    public static string DeviceGroup(Guid id) => "device:" + id.ToString("D");

    /// <summary>Envía una lectura nueva a quienes siguen ese dispositivo.</summary>
    public Task Reading(Guid deviceId, ReadingDto r) => hub.Clients.Group(DeviceGroup(deviceId)).SendAsync("Reading", deviceId.ToString("D"), r);

    /// <summary>Avisa a todos los usuarios que un dispositivo cambió de estado (en línea o desconectado).</summary>
    public Task DeviceStatus(Device d, bool online)
        => hub.Clients.Group("users").SendAsync("DeviceStatus", d.Id.ToString("D"),
            new DeviceStatusDto(d.Id.ToString("D"), d.Name, d.Kind, online, d.LastSeenAt, d.LastBattery, d.LastRssi));

    /// <summary>Envía el espectro del acelerómetro a quienes siguen ese dispositivo.</summary>
    public Task Spectrum(Guid deviceId, SpectrumResult s)
        => hub.Clients.Group(DeviceGroup(deviceId)).SendAsync("Spectrum", deviceId.ToString("D"), s.Freqs, s.Mags, s.DominantHz);

    /// <summary>Envía la traza de un paquete a los administradores, solo si la consola está habilitada.</summary>
    public Task PacketTrace(PacketTraceDto t) => _console ? hub.Clients.Group("admin").SendAsync("PacketTrace", t) : Task.CompletedTask;
}
