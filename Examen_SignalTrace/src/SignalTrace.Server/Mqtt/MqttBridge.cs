using System.Buffers;
using System.Text;
using System.Text.Json;
using MQTTnet;
using MQTTnet.Protocol;
using MQTTnet.Server;
using SignalTrace.Server.Services;

namespace SignalTrace.Server.Mqtt;

/// <summary>
/// Embedded MQTT broker (Mqtt:Enabled). Devices connect with username = deviceId and password = device JWT,
/// publish the very same encrypted envelope to st/{deviceId}/up and receive the result on st/{deviceId}/ack.
/// Every message goes through the same <see cref="TelemetryPipeline"/> as HTTP.
/// </summary>
public sealed class MqttBridge(IServiceScopeFactory scopes, TokenService tokens, IConfiguration cfg, ILogger<MqttBridge> log) : IHostedService, IAsyncDisposable
{
    private MqttServer? _server;
    private const string DeviceItem = "did";

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

    private static Task OnSubscribe(InterceptingSubscriptionEventArgs e)
    {
        // A device may only listen to its own ack topic.
        var did = e.SessionItems[DeviceItem] as string;
        if (e.TopicFilter.Topic != $"st/{did}/ack") e.ProcessSubscription = false;
        return Task.CompletedTask;
    }

    private async Task OnPublish(InterceptingPublishEventArgs e)
    {
        if (e.ClientId == "signaltrace-server") return;
        e.ProcessPublish = false; // the broker never relays device uplinks to other clients
        var did = e.SessionItems[DeviceItem] as string;
        var topic = e.ApplicationMessage.Topic;
        if (did is null || topic != $"st/{did}/up") return;
        var body = e.ApplicationMessage.Payload.ToArray();
        using var scope = scopes.CreateScope();
        var pipeline = scope.ServiceProvider.GetRequiredService<TelemetryPipeline>();
        var r = await pipeline.ProcessAsync(body, did, "mqtt:" + e.ClientId, "mqtt", e.CancellationToken);
        var ack = JsonSerializer.Serialize(r.Ok ? (object)new { ack = r.Ack } : new { code = r.Code, message = r.Message, status = r.Status });
        var msg = new MqttApplicationMessageBuilder().WithTopic($"st/{did}/ack").WithPayload(Encoding.UTF8.GetBytes(ack)).Build();
        await _server!.InjectApplicationMessage(new InjectedMqttApplicationMessage(msg) { SenderClientId = "signaltrace-server" });
    }

    public async Task StopAsync(CancellationToken ct)
    {
        if (_server is not null) await _server.StopAsync();
    }

    public ValueTask DisposeAsync()
    {
        _server?.Dispose();
        return ValueTask.CompletedTask;
    }
}
