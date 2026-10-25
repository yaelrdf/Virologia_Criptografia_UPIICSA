using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using SignalTrace.Core;
using SignalTrace.Simulator;

// SignalTrace simulator: N virtual devices with the real protocol (handshake + SigMix + AEAD), over HTTP or MQTT.
// Usage: dotnet run --project src/SignalTrace.Simulator -- --server http://localhost:5090 --user admin --password X
//        [--fingerprint HEX] [--devices 10] [--rate 1] [--anomalies] [--transport http|mqtt] [--duration 60] [--suite chacha20-poly1305]

var opt = Options.Parse(args);
if (opt.ShowHelp)
{
    Console.WriteLine(Options.Help);
    return 0;
}

using var http = new HttpClient { BaseAddress = new Uri(opt.Server), Timeout = TimeSpan.FromSeconds(15) };

// 1) Server key and pinning: abort if the fingerprint given with --fingerprint does not match.
var key = (await http.GetFromJsonAsync<ServerKeyResponse>("/api/server-key", Json.Options))!;
var pinned = Convert.FromBase64String(key.PublicKey);
var fp = EcKeys.Fingerprint(pinned);
if (fp != key.Fingerprint) throw new InvalidOperationException("Inconsistent fingerprint from server.");
if (opt.Fingerprint is { } expected && !string.Equals(expected.Replace(" ", ""), fp, StringComparison.OrdinalIgnoreCase))
{
    Console.Error.WriteLine($"ERROR: la huella del servidor {fp} no coincide con --fingerprint. ¿MITM?");
    return 2;
}
Console.WriteLine($"Servidor {opt.Server} · huella {fp[..16]}… {(opt.Fingerprint is null ? "(TOFU: no se pasó --fingerprint)" : "(verificada)")}");

// 2) Devices: reuse the saved ones, register what is missing with the user's credentials.
var saved = File.Exists(opt.StateFile) ? JsonSerializer.Deserialize<List<DeviceCredentials>>(File.ReadAllText(opt.StateFile)) ?? [] : [];
if (saved.Count < opt.Devices)
{
    if (opt.User is null || opt.Password is null)
    {
        Console.Error.WriteLine("Faltan --user y --password para registrar dispositivos.");
        return 1;
    }
    var login = await http.PostAsJsonAsync("/api/auth/login", new LoginRequest(opt.User, opt.Password));
    if (!login.IsSuccessStatusCode)
    {
        Console.Error.WriteLine($"Login falló: {(int)login.StatusCode} {await login.Content.ReadAsStringAsync()}");
        return 1;
    }
    var token = (await login.Content.ReadFromJsonAsync<LoginResponse>(Json.Options))!.Token;
    for (var i = saved.Count; i < opt.Devices; i++)
    {
        var name = $"sim-{i + 1:00}";
        using var req = new HttpRequestMessage(HttpMethod.Post, "/api/devices") { Content = JsonContent.Create(new RegisterDeviceRequest(name, "sim")) };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var res = await http.SendAsync(req);
        res.EnsureSuccessStatusCode();
        var reg = (await res.Content.ReadFromJsonAsync<RegisterDeviceResponse>(Json.Options))!;
        saved.Add(new DeviceCredentials(reg.DeviceId, reg.DeviceToken, name));
    }
    File.WriteAllText(opt.StateFile, JsonSerializer.Serialize(saved));
}

// 3) Run.
var stats = new Stats();
using var cts = opt.Duration > 0 ? new CancellationTokenSource(TimeSpan.FromSeconds(opt.Duration)) : new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
var tasks = saved.Take(opt.Devices).Select((c, i) => new VirtualDevice(i, c, opt, http, pinned, stats).RunAsync(cts.Token)).ToList();
Console.WriteLine($"{tasks.Count} dispositivos · {opt.Rate} Hz · {opt.Transport} · {opt.Suite}{(opt.Anomalies ? " · con anomalías" : "")}{(opt.Gaps ? " · con huecos" : "")}. Ctrl+C para terminar.");

var started = DateTime.UtcNow;
var report = Task.Run(async () =>
{
    while (!cts.IsCancellationRequested)
    {
        await Task.Delay(5000, cts.Token).ContinueWith(_ => { });
        Print();
    }
});
await Task.WhenAll(tasks);
await report;
Print();
return stats.Rejected.Keys.Any(k => k is not ("unknown_session")) || stats.NetworkErrors > 0 ? 3 : 0;

void Print()
{
    var secs = (DateTime.UtcNow - started).TotalSeconds;
    var rejected = string.Join(", ", stats.Rejected.Select(kv => $"{kv.Key}={kv.Value}"));
    var avg = stats.Sent > 0 ? stats.LatencyMsSum / stats.Sent : 0;
    Console.WriteLine($"[{secs,6:F0} s] enviados {stats.Sent} · aceptados {stats.Ok} · rechazados {(rejected.Length > 0 ? rejected : "0")} · red {stats.NetworkErrors} · handshakes {stats.Handshakes} · latencia media {avg:F1} ms");
}

public sealed class Options
{
    public string Server { get; private set; } = "http://localhost:5090";
    public string? User { get; private set; }
    public string? Password { get; private set; } = Environment.GetEnvironmentVariable("ST_PASSWORD");
    public string? Fingerprint { get; private set; }
    public int Devices { get; private set; } = 3;
    public double Rate { get; private set; } = 1;
    public bool Anomalies { get; private set; }
    public bool Gaps { get; private set; }
    public string Transport { get; private set; } = "http";
    public int MqttPort { get; private set; } = 1883;
    public int Duration { get; private set; }
    public string Suite { get; private set; } = Suites.AesGcm;
    public string StateFile { get; private set; } = "sim-devices.json";
    public bool ShowHelp { get; private set; }

    public const string Help = """
        SignalTrace simulador
          --server URL        servidor (http://localhost:5090)
          --user U --password P   credenciales para registrar dispositivos (o ST_PASSWORD)
          --fingerprint HEX   huella SHA-256 esperada de la llave del servidor (pinning)
          --devices N         dispositivos virtuales (3)
          --rate HZ           paquetes por segundo por dispositivo (1)
          --anomalies         inyecta caídas de señal, picos de RTT y vibración fuerte
          --gaps              pausas periódicas como pantalla bloqueada (4 s interpolables, 12 s hueco)
          --transport T       http | mqtt (http)
          --mqtt-port P       puerto del broker MQTT (1883)
          --suite S           aes-256-gcm | chacha20-poly1305
          --duration S        segundos (0 = hasta Ctrl+C)
          --state FILE        archivo con los tokens de los dispositivos (sim-devices.json)
        """;

    public static Options Parse(string[] args)
    {
        var o = new Options();
        for (var i = 0; i < args.Length; i++)
        {
            string Next() => i + 1 < args.Length ? args[++i] : throw new ArgumentException($"Falta el valor de {args[i]}");
            switch (args[i])
            {
                case "--server": o.Server = Next().TrimEnd('/'); break;
                case "--user": o.User = Next(); break;
                case "--password": o.Password = Next(); break;
                case "--fingerprint": o.Fingerprint = Next(); break;
                case "--devices": o.Devices = int.Parse(Next()); break;
                case "--rate": o.Rate = double.Parse(Next(), System.Globalization.CultureInfo.InvariantCulture); break;
                case "--anomalies": o.Anomalies = true; break;
                case "--gaps": o.Gaps = true; break;
                case "--transport": o.Transport = Next(); break;
                case "--mqtt-port": o.MqttPort = int.Parse(Next()); break;
                case "--duration": o.Duration = int.Parse(Next()); break;
                case "--suite": o.Suite = Next(); break;
                case "--state": o.StateFile = Next(); break;
                case "-h" or "--help": o.ShowHelp = true; break;
                default: throw new ArgumentException($"Opción desconocida: {args[i]}\n{Help}");
            }
        }
        if (o.Transport is not ("http" or "mqtt")) throw new ArgumentException("--transport debe ser http o mqtt");
        if (o.Rate is <= 0 or > 50) throw new ArgumentException("--rate fuera de rango (0, 50]");
        return o;
    }
}
