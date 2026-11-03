using System.Buffers;
using System.Text;
using System.Text.Json;
using MQTTnet;
using MQTTnet.Protocol;
using MQTTnet.Server;
using SignalTrace.Server.Services;

namespace SignalTrace.Server.Mqtt;

/// <summary>
/// Broker MQTT integrado (se activa con Mqtt:Enabled). Los dispositivos se conectan con usuario = deviceId y
/// contraseña = JWT del dispositivo, publican exactamente el mismo sobre cifrado en st/{deviceId}/up y reciben
/// el resultado en st/{deviceId}/ack. Cada mensaje pasa por la misma <see cref="TelemetryPipeline"/> que HTTP.
/// </summary>
/// <param name="scopes">Fábrica de ámbitos, para crear una canalización (y su base de datos) por mensaje.</param>
/// <param name="tokens">Servicio que valida el JWT del dispositivo.</param>
/// <param name="cfg">Configuración (puerto MQTT).</param>
/// <param name="log">Registro de eventos.</param>
public sealed class MqttBridge(IServiceScopeFactory scopes, TokenService tokens, IConfiguration cfg, ILogger<MqttBridge> log) : IHostedService, IAsyncDisposable
{
    /// <summary>Servidor MQTT en ejecución.</summary>
    private MqttServer? _server;
    /// <summary>Llave con la que se guarda el id del dispositivo en los datos de la sesión MQTT.</summary>
    private const string DeviceItem = "did";

    /// <summary>Arranca el broker en el puerto configurado (1883 por defecto) y engancha los manejadores.</summary>
    public async Task StartAsync(CancellationToken ct)
    {
        var port = cfg.GetValue("Mqtt:Port", 1883);
        var options = new MqttServerOptionsBuilder().WithDefaultEndpoint().WithDefaultEndpointPort(port).Build();
        _server = new MqttServerFactory().CreateMqttServer(options);
        _server.ValidatingConnectionAsync += OnConnect;
        _server.InterceptingPublishAsync += OnPublish;
        _server.InterceptingSubscriptionAsync += OnSubscribe;
        await _server.StartAsync();
        log.LogInformation("MQTT bridge listening on port {Port} (topic st/{{deviceId}}/up)", port);
    }

    /// <summary>
    /// Valida la conexión: la contraseña debe ser un JWT de dispositivo válido cuyo id coincida con el usuario MQTT.
    /// Si es correcta, recuerda el id del dispositivo en la sesión.
    /// </summary>
    private Task OnConnect(ValidatingConnectionEventArgs e)
    {
        var principal = tokens.Validate(e.Password ?? "");
        var did = principal?.DeviceId();
        if (principal?.FindFirst(TokenService.TypeClaim)?.Value != "device" || did is null || did != e.UserName)
        {
            e.ReasonCode = MqttConnectReasonCode.BadUserNameOrPassword;
            return Task.CompletedTask;
        }
        e.SessionItems[DeviceItem] = did;
        return Task.CompletedTask;
    }

    /// <summary>Limita las suscripciones.</summary>
    private static Task OnSubscribe(InterceptingSubscriptionEventArgs e)
    {
        // Un dispositivo solo puede escuchar su propio tema de confirmaciones.
        var did = e.SessionItems[DeviceItem] as string;
        if (e.TopicFilter.Topic != $"st/{did}/ack") e.ProcessSubscription = false;
        return Task.CompletedTask;
    }

    /// <summary>
    /// Procesa cada publicación de un dispositivo: la pasa por la canalización de telemetría y publica
    /// el resultado (ack o error) en su tema de confirmaciones.
    /// </summary>
    private async Task OnPublish(InterceptingPublishEventArgs e)
    {
        // Las confirmaciones que inyecta el propio servidor sí se entregan.
        if (e.ClientId == "signaltrace-server") return;
        e.ProcessPublish = false; // el broker nunca reenvía los paquetes de un dispositivo a otros clientes
        var did = e.SessionItems[DeviceItem] as string;
        var topic = e.ApplicationMessage.Topic;
        // Solo acepta publicaciones en el tema de subida del propio dispositivo.
        if (did is null || topic != $"st/{did}/up") return;
        var body = e.ApplicationMessage.Payload.ToArray();
        using var scope = scopes.CreateScope();
        var pipeline = scope.ServiceProvider.GetRequiredService<TelemetryPipeline>();
        var r = await pipeline.ProcessAsync(body, did, "mqtt:" + e.ClientId, "mqtt", e.CancellationToken);
        var ack = JsonSerializer.Serialize(r.Ok ? (object)new { ack = r.Ack } : new { code = r.Code, message = r.Message, status = r.Status });
        var msg = new MqttApplicationMessageBuilder().WithTopic($"st/{did}/ack").WithPayload(Encoding.UTF8.GetBytes(ack)).Build();
        await _server!.InjectApplicationMessage(new InjectedMqttApplicationMessage(msg) { SenderClientId = "signaltrace-server" });
    }

    /// <summary>Detiene el broker al apagar el servidor.</summary>
    public async Task StopAsync(CancellationToken ct)
    {
        if (_server is not null) await _server.StopAsync();
    }

    /// <summary>Libera el broker.</summary>
    public ValueTask DisposeAsync()
    {
        _server?.Dispose();
        return ValueTask.CompletedTask;
    }
}
