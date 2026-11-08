using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using SignalTrace.Core;
using SignalTrace.Simulator;

// Simulador de SignalTrace: N dispositivos virtuales que usan el protocolo real (handshake + YCM1 + AEAD), por HTTP o MQTT.
// Sirve para llenar el tablero con datos y para pruebas de carga y de resistencia.
// Uso: dotnet run --project src/SignalTrace.Simulator -- --server http://localhost:5090 --user admin --password X
//      [--fingerprint HEX] [--devices 10] [--rate 1] [--anomalies] [--transport http|mqtt] [--duration 60] [--suite chacha20-poly1305]

var opt = Options.Parse(args);
if (opt.ShowHelp)
{
    Console.WriteLine(Options.Help);
    return 0;
}

using var http = new HttpClient { BaseAddress = new Uri(opt.Server), Timeout = TimeSpan.FromSeconds(15) };

// 1) Llave del servidor y fijado (pinning): termina si la huella pasada con --fingerprint no coincide.
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

// 2) Dispositivos: reutiliza los guardados y registra los que falten con las credenciales del usuario.
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
    // Guarda los tokens para no registrar dispositivos nuevos en cada ejecución.
    File.WriteAllText(opt.StateFile, JsonSerializer.Serialize(saved));
}

// 3) Ejecución: una tarea por dispositivo hasta que se acabe --duration o se pulse Ctrl+C.
var stats = new Stats();
using var cts = opt.Duration > 0 ? new CancellationTokenSource(TimeSpan.FromSeconds(opt.Duration)) : new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
var tasks = saved.Take(opt.Devices).Select((c, i) => new VirtualDevice(i, c, opt, http, pinned, stats).RunAsync(cts.Token)).ToList();
Console.WriteLine($"{tasks.Count} dispositivos · {opt.Rate} Hz · {opt.Transport} · {opt.Suite}{(opt.Anomalies ? " · con anomalías" : "")}{(opt.Gaps ? " · con huecos" : "")}. Ctrl+C para terminar.");

// Imprime un resumen cada 5 segundos mientras corre.
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
// Código de salida 3 si hubo rechazos distintos de sesión desconocida (que es normal tras un reinicio) o errores de red.
return stats.Rejected.Keys.Any(k => k is not ("unknown_session")) || stats.NetworkErrors > 0 ? 3 : 0;

// Escribe una línea con los contadores acumulados.
void Print()
{
    var secs = (DateTime.UtcNow - started).TotalSeconds;
    var rejected = string.Join(", ", stats.Rejected.Select(kv => $"{kv.Key}={kv.Value}"));
    var avg = stats.Sent > 0 ? stats.LatencyMsSum / stats.Sent : 0;
    Console.WriteLine($"[{secs,6:F0} s] enviados {stats.Sent} · aceptados {stats.Ok} · rechazados {(rejected.Length > 0 ? rejected : "0")} · red {stats.NetworkErrors} · handshakes {stats.Handshakes} · latencia media {avg:F1} ms");
}

/// <summary>Opciones de la línea de comandos del simulador.</summary>
public sealed class Options
{
    /// <summary>URL base del servidor.</summary>
    public string Server { get; private set; } = "http://localhost:5090";
    /// <summary>Usuario para registrar dispositivos.</summary>
    public string? User { get; private set; }
    /// <summary>Contraseña; también se puede pasar con la variable de entorno ST_PASSWORD.</summary>
    public string? Password { get; private set; } = Environment.GetEnvironmentVariable("ST_PASSWORD");
    /// <summary>Huella esperada de la llave del servidor; si falta, se confía en el primer contacto (TOFU).</summary>
    public string? Fingerprint { get; private set; }
    /// <summary>Número de dispositivos virtuales.</summary>
    public int Devices { get; private set; } = 3;
    /// <summary>Paquetes por segundo por dispositivo.</summary>
    public double Rate { get; private set; } = 1;
    /// <summary>Si se inyectan anomalías.</summary>
    public bool Anomalies { get; private set; }
    /// <summary>Si se simulan pausas periódicas en el envío.</summary>
    public bool Gaps { get; private set; }
    /// <summary>Transporte: http o mqtt.</summary>
    public string Transport { get; private set; } = "http";
    /// <summary>Puerto del broker MQTT.</summary>
    public int MqttPort { get; private set; } = 1883;
    /// <summary>Duración en segundos (0 = sin límite).</summary>
    public int Duration { get; private set; }
    /// <summary>Suite AEAD que proponen los dispositivos.</summary>
    public string Suite { get; private set; } = Suites.AesGcm;
    /// <summary>Archivo donde se guardan los tokens de los dispositivos.</summary>
    public string StateFile { get; private set; } = "sim-devices.json";
    /// <summary>Si se pidió la ayuda.</summary>
    public bool ShowHelp { get; private set; }

    /// <summary>Texto de ayuda.</summary>
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

    /// <summary>Interpreta los argumentos y valida el transporte y la frecuencia.</summary>
    /// <exception cref="ArgumentException">Si una opción no existe, le falta el valor o está fuera de rango.</exception>
    public static Options Parse(string[] args)
    {
        var o = new Options();
        for (var i = 0; i < args.Length; i++)
        {
            // Devuelve el argumento que sigue a la opción actual.
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
