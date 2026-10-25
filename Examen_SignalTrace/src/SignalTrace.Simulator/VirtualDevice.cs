using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using MQTTnet;
using SignalTrace.Core;

namespace SignalTrace.Simulator;

public sealed record DeviceCredentials(string DeviceId, string DeviceToken, string Name);

public sealed class Stats
{
    public long Sent, Ok, Handshakes, NetworkErrors;
    public readonly ConcurrentDictionary<string, long> Rejected = new();
    public double LatencyMsSum;

    public void Reject(string code) => Rejected.AddOrUpdate(code, 1, (_, n) => n + 1);
}

/// <summary>
/// One simulated device: realistic synthetic telemetry (route, 2–7 Hz vibration, RSSI and RTT with drift and
/// optional anomalies, draining battery) sealed with the real protocol and sent over HTTP or MQTT, in series.
/// </summary>
public sealed class VirtualDevice(int index, DeviceCredentials cred, Options opt, HttpClient http, byte[] pinnedKey, Stats stats)
{
    private readonly Random _rnd = new(1000 + index);
    private SessionKeys? _keys;
    private string? _sid;
    private ulong _seq;
    private long _packets;
    private double _lat = 19.3955 + 0.002 * Math.Sin(index), _lon = -99.0915 + 0.002 * Math.Cos(index), _hdg = index * 37 % 360;
    private double _bat = 0.95 - 0.03 * (index % 10), _rssi = -55 - index % 15, _rtt = 35 + index % 20;
    private readonly double _vibHz = 2 + index % 6;
    private double _t;
    private IMqttClient? _mqtt;
    private TaskCompletionSource<JsonElement>? _ack;

    private double Gauss() => Math.Sqrt(-2 * Math.Log(1 - _rnd.NextDouble())) * Math.Cos(2 * Math.PI * _rnd.NextDouble());

    public async Task RunAsync(CancellationToken stop)
    {
        var period = TimeSpan.FromSeconds(1 / opt.Rate);
        if (opt.Transport == "mqtt") await ConnectMqtt(stop);
        using var timer = new PeriodicTimer(period);
        do
        {
            try { await SendOne(stop); }
            catch (OperationCanceledException) { break; }
            catch (HttpRequestException) { Interlocked.Increment(ref stats.NetworkErrors); await Task.Delay(1000, stop).ContinueWith(_ => { }); }
        } while (await timer.WaitForNextTickAsync(stop).AsTask().ContinueWith(t => !t.IsCanceled && t.Result));
        if (_mqtt is not null) await _mqtt.DisconnectAsync();
    }

    private async Task Handshake(CancellationToken ct)
    {
        using var hs = new ClientHandshake(cred.DeviceId, opt.Suite);
        using var req = new HttpRequestMessage(HttpMethod.Post, "/api/handshake") { Content = JsonContent.Create(hs.Request(), options: Json.Options) };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", cred.DeviceToken);
        using var res = await http.SendAsync(req, ct);
        res.EnsureSuccessStatusCode();
        var body = (await res.Content.ReadFromJsonAsync<HandshakeResponse>(Json.Options, ct))!;
        _keys = hs.Complete(body, pinnedKey); // throws if the signature does not match the pinned key
        _sid = body.SessionId;
        _seq = 0;
        Interlocked.Increment(ref stats.Handshakes);
    }

    private async Task SendOne(CancellationToken ct)
    {
        if (_keys is null) await Handshake(ct);
        var ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var payload = NextPayload(ts);
        var plain = JsonSerializer.SerializeToUtf8Bytes(payload, Json.Options);
        var seq = ++_seq;
        var ct2 = PacketCrypto.Seal(_keys!, cred.DeviceId, seq, ts, plain);
        var env = new TelemetryEnvelope(1, _sid!, cred.DeviceId, seq, ts, Convert.ToBase64String(ct2));
        var body = JsonSerializer.SerializeToUtf8Bytes(env, Json.Options);
        Interlocked.Increment(ref stats.Sent);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var (ok, code) = opt.Transport == "mqtt" ? await PublishMqtt(body, ct) : await PostHttp(body, ct);
        lock (stats) stats.LatencyMsSum += sw.Elapsed.TotalMilliseconds;
        if (ok) { Interlocked.Increment(ref stats.Ok); return; }
        stats.Reject(code);
        if (code == "unknown_session") _keys = null; // server restarted: redo the handshake on the next tick
        if (code == "unauthorized") throw new OperationCanceledException($"{cred.Name}: token revocado");
    }

    private async Task<(bool, string)> PostHttp(byte[] body, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, "/api/telemetry") { Content = new ByteArrayContent(body) };
        req.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", cred.DeviceToken);
        using var res = await http.SendAsync(req, ct);
        if ((int)res.StatusCode == 202) return (true, "ok");
        var err = await res.Content.ReadFromJsonAsync<ErrorResponse>(Json.Options, ct).ConfigureAwait(false);
        return (false, err?.Code ?? ((int)res.StatusCode).ToString());
    }

    private async Task ConnectMqtt(CancellationToken ct)
    {
        _mqtt = new MqttClientFactory().CreateMqttClient();
        _mqtt.ApplicationMessageReceivedAsync += e =>
        {
            _ack?.TrySetResult(JsonDocument.Parse(Encoding.UTF8.GetString(e.ApplicationMessage.Payload)).RootElement.Clone());
            return Task.CompletedTask;
        };
        var host = new Uri(opt.Server).Host;
        var options = new MqttClientOptionsBuilder().WithTcpServer(host, opt.MqttPort).WithClientId("sim-" + cred.DeviceId[..8])
            .WithCredentials(cred.DeviceId, cred.DeviceToken).Build();
        await _mqtt.ConnectAsync(options, ct);
        await _mqtt.SubscribeAsync($"st/{cred.DeviceId}/ack", cancellationToken: ct);
    }

    private async Task<(bool, string)> PublishMqtt(byte[] body, CancellationToken ct)
    {
        _ack = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        await _mqtt!.PublishAsync(new MqttApplicationMessageBuilder().WithTopic($"st/{cred.DeviceId}/up").WithPayload(body).Build(), ct);
        var ack = await _ack.Task.WaitAsync(TimeSpan.FromSeconds(10), ct);
        return ack.TryGetProperty("ack", out _) ? (true, "ok") : (false, ack.GetProperty("code").GetString() ?? "?");
    }

    /// <summary>Builds the next reading (PLAN §8). Anomalies: occasional RSSI drops, RTT spikes and strong vibration.</summary>
    private TelemetryPayload NextPayload(long ts)
    {
        var dt = 1 / opt.Rate;
        _packets++;
        _hdg = (_hdg + 15 * Gauss() + 360) % 360;
        var spd = Math.Max(0, 1.4 + 0.4 * Gauss());
        var d = spd * dt;
        _lat += d * Math.Cos(_hdg * Math.PI / 180) / 111_320;
        _lon += d * Math.Sin(_hdg * Math.PI / 180) / (111_320 * Math.Cos(_lat * Math.PI / 180));
        _bat = Math.Max(0.02, _bat - 0.0005 * dt * (1 + index % 3));
        _rssi = Math.Clamp(_rssi + 0.8 * Gauss() + 0.05 * (-60 - _rssi), -98, -35);
        _rtt = Math.Max(5, _rtt + 2 * Gauss() + 0.1 * (40 - _rtt));
        var anomaly = opt.Anomalies && _rnd.NextDouble() < 0.03;
        var rssi = anomaly ? _rssi - 30 : _rssi;
        var rtt = anomaly && _rnd.NextDouble() < 0.5 ? _rtt + 600 : _rtt;
        var vibAmp = anomaly ? 3.0 : 0.4;

        // 32 Hz acceleration window: gravity + sinusoidal vibration + noise.
        const int fs = 32, n = 64;
        var mag = new double[n];
        for (var i = 0; i < n; i++)
        {
            _t += 1.0 / fs;
            mag[i] = Math.Round(9.81 + vibAmp * Math.Sin(2 * Math.PI * _vibHz * _t) + 0.05 * Gauss(), 3);
        }
        var mean = mag.Average();
        var rms = Math.Sqrt(mag.Sum(v => (v - mean) * (v - mean)) / n);
        return new TelemetryPayload
        {
            T = ts,
            Src = "sim",
            Gps = new GpsData { Lat = Math.Round(_lat, 6), Lon = Math.Round(_lon, 6), Acc = Math.Round(4 + 2 * _rnd.NextDouble(), 1), Spd = Math.Round(spd, 2), Hdg = Math.Round(_hdg) },
            Mot = new MotionData { Ax = 0.02, Ay = -0.01, Az = Math.Round(mean, 3), Rms = Math.Round(rms, 4), Peak = Math.Round(mag.Max(v => Math.Abs(v - mean)), 4), Fs = fs, Mag = _packets % 5 == 0 ? mag : null },
            Bat = new BatteryData { Lvl = Math.Round(_bat, 3), Chg = false },
            Net = new NetData { Type = "wifi", Probe = new ProbeData { Rtt = Math.Round(rtt, 1), Jit = Math.Round(Math.Abs(2 * Gauss()), 1), Loss = anomaly ? 0.2 : 0 } },
            Wifi = new WifiData { Rssi = (int)Math.Round(rssi), Freq = index % 2 == 0 ? 5180 : 2437, Link = 433 },
            App = new AppData { Vis = true, Ver = "sim-1.0" },
        };
    }
}
