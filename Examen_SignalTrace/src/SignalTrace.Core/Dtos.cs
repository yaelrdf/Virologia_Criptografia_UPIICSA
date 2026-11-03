using System.Text.Json;
using System.Text.Json.Serialization;

namespace SignalTrace.Core;

// Contrato de la API (PLAN §7). Los campos binarios van en Base64 estándar con relleno «=»; los tiempos son
// milisegundos desde la época Unix. Cada record se serializa en camelCase (por ejemplo, ExpiresAt → expiresAt).

/// <summary>Cuerpo de cualquier respuesta de error.</summary>
/// <param name="Code">Código estable para el programa (por ejemplo <c>"bad_credentials"</c>).</param>
/// <param name="Message">Mensaje legible para la persona usuaria.</param>
public sealed record ErrorResponse(string Code, string Message);

/// <summary>Credenciales de <c>POST /api/auth/login</c>.</summary>
public sealed record LoginRequest(string Username, string Password);
/// <summary>Respuesta de un inicio de sesión correcto: el JWT, cuándo vence, el rol y el nombre de usuario.</summary>
public sealed record LoginResponse(string Token, long ExpiresAt, string Role, string Username);

/// <summary>Alta de un usuario (solo administradores).</summary>
public sealed record CreateUserRequest(string Username, string Password, string Role);
/// <summary>Cambio de rol de un usuario.</summary>
public sealed record ChangeRoleRequest(string Role);
/// <summary>Cambio de la contraseña propia: la actual y la nueva.</summary>
public sealed record ChangePasswordRequest(string? Current, string? New);
/// <summary>Contraseña temporal generada al restablecer la de otro usuario.</summary>
public sealed record ResetPasswordResponse(string Username, string Password);

/// <summary>Llave pública de largo plazo del servidor, su huella y las suites AEAD disponibles.</summary>
public sealed record ServerKeyResponse(string PublicKey, string Fingerprint, string[] Suites);

/// <summary>Registro de un dispositivo nuevo con su nombre y su tipo (web, android o sim).</summary>
public sealed record RegisterDeviceRequest(string Name, string Kind);
/// <summary>Identificador y token secreto del dispositivo; el token solo se entrega una vez.</summary>
public sealed record RegisterDeviceResponse(string DeviceId, string DeviceToken);

/// <summary>Primer mensaje del handshake: llave efímera y nonce del cliente, más la suite que propone.</summary>
public sealed record HandshakeRequest(string DeviceId, string ClientPublicKey, string ClientNonce, string? Suite = null);
/// <summary>Respuesta del handshake: sesión, llave efímera y nonce del servidor, firma del transcript, vencimiento y suite elegida.</summary>
public sealed record HandshakeResponse(string SessionId, string ServerPublicKey, string ServerNonce, string Signature, long ExpiresAt, string Suite);

/// <summary>Confirmación de un paquete aceptado con su número de secuencia.</summary>
public sealed record TelemetryAck(ulong Ack);

/// <summary>Opciones JSON compartidas por el servidor, el simulador y las pruebas.</summary>
public static class Json
{
    /// <summary>JSON compacto en camelCase que omite los valores nulos (PLAN §8: un campo ausente se omite, nunca se manda como null).</summary>
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };
}

// Carga de telemetría v2: el banco de sensores. Todos los bloques salvo T y Src son opcionales; un dispositivo
// solo manda los sensores que su usuario decidió exponer. No hay datos de ubicación (ni GPS, ni SSID, ni BSSID,
// ni identificadores de celda).

/// <summary>Contenido en claro de un paquete de telemetría. Los nombres cortos reducen el tamaño cifrado.</summary>
public sealed class TelemetryPayload
{
    /// <summary>Momento de la muestra en milisegundos Unix.</summary>
    public long T { get; set; }
    /// <summary>Origen de los datos: <c>"web"</c>, <c>"android"</c> o <c>"sim"</c>.</summary>
    public string Src { get; set; } = "";
    /// <summary>Acelerómetro (con gravedad) en m/s², más la ventana de magnitudes que se usa para el espectro.</summary>
    public MotionData? Mot { get; set; }
    /// <summary>Aceleración lineal (sin gravedad), m/s².</summary>
    public Vec3? Lin { get; set; }
    /// <summary>Vector de gravedad, m/s².</summary>
    public Vec3? Grv { get; set; }
    /// <summary>Giroscopio, rad/s.</summary>
    public Vec3? Gyr { get; set; }
    /// <summary>Campo magnético, µT.</summary>
    public Vec3? Mfl { get; set; }
    /// <summary>Orientación del dispositivo.</summary>
    public OrientationData? Ori { get; set; }
    /// <summary>Sensores de entorno (luz, proximidad, presión, temperatura, humedad).</summary>
    public EnvData? Env { get; set; }
    /// <summary>Podómetro.</summary>
    public StepData? Stp { get; set; }
    /// <summary>Estado de la batería.</summary>
    public BatteryData? Bat { get; set; }
    /// <summary>Tipo de red y sondeo de enlace.</summary>
    public NetData? Net { get; set; }
    /// <summary>Calidad del enlace Wi-Fi (solo Android).</summary>
    public WifiData? Wifi { get; set; }
    /// <summary>Intensidad de la señal celular (solo Android).</summary>
    public CellData? Cell { get; set; }
    /// <summary>Estado de la aplicación que envía.</summary>
    public AppData? App { get; set; }
}

/// <summary>Vector de tres ejes.</summary>
public sealed class Vec3
{
    /// <summary>Componente X.</summary>
    public double X { get; set; }
    /// <summary>Componente Y.</summary>
    public double Y { get; set; }
    /// <summary>Componente Z.</summary>
    public double Z { get; set; }
    /// <summary>Norma euclidiana √(x² + y² + z²).</summary>
    public double Norm() => Math.Sqrt(X * X + Y * Y + Z * Z);
}

/// <summary>Resumen del acelerómetro durante la ventana de muestreo.</summary>
public sealed class MotionData
{
    /// <summary>Último valor del eje X (m/s²).</summary>
    public double? Ax { get; set; }
    /// <summary>Último valor del eje Y (m/s²).</summary>
    public double? Ay { get; set; }
    /// <summary>Último valor del eje Z (m/s²).</summary>
    public double? Az { get; set; }
    /// <summary>Valor cuadrático medio de la magnitud en la ventana.</summary>
    public double? Rms { get; set; }
    /// <summary>Magnitud máxima en la ventana.</summary>
    public double? Peak { get; set; }
    /// <summary>Frecuencia de muestreo en Hz, necesaria para convertir el espectro a hercios.</summary>
    public double? Fs { get; set; }
    /// <summary>Magnitudes de la ventana; el servidor calcula su FFT.</summary>
    public double[]? Mag { get; set; }
}

/// <summary>Orientación del dispositivo en grados: azimut [0,360), cabeceo [-180,180], alabeo [-90,90] (alpha/beta/gamma del W3C).</summary>
public sealed class OrientationData
{
    /// <summary>Azimut.</summary>
    public double Az { get; set; }
    /// <summary>Cabeceo (pitch).</summary>
    public double Pi { get; set; }
    /// <summary>Alabeo (roll).</summary>
    public double Ro { get; set; }
}

/// <summary>Sensores de entorno: iluminancia (lx), proximidad (cm), presión (hPa), temperatura (°C) y humedad relativa (%).</summary>
public sealed class EnvData
{
    /// <summary>Iluminancia en lux.</summary>
    public double? Lux { get; set; }
    /// <summary>Distancia de proximidad en centímetros.</summary>
    public double? Prx { get; set; }
    /// <summary>Presión barométrica en hectopascales.</summary>
    public double? Hpa { get; set; }
    /// <summary>Temperatura ambiente en grados Celsius.</summary>
    public double? Tmp { get; set; }
    /// <summary>Humedad relativa en porcentaje.</summary>
    public double? Hum { get; set; }
}

/// <summary>Pasos contados desde que empezó la transmisión.</summary>
public sealed class StepData
{
    /// <summary>Número de pasos.</summary>
    public long N { get; set; }
}

/// <summary>Estado de la batería.</summary>
public sealed class BatteryData
{
    /// <summary>Nivel de carga de 0 a 1.</summary>
    public double Lvl { get; set; }
    /// <summary>Si está cargando.</summary>
    public bool? Chg { get; set; }
    /// <summary>Temperatura de la batería (°C); esta y las tres siguientes solo las manda Android.</summary>
    public double? Temp { get; set; }
    /// <summary>Voltaje (V).</summary>
    public double? Volt { get; set; }
    /// <summary>Salud reportada por el sistema (por ejemplo <c>"good"</c>).</summary>
    public string? Health { get; set; }
    /// <summary>Fuente de energía conectada (ac, usb, wireless).</summary>
    public string? Plug { get; set; }
}

/// <summary>Información de la red que usa el dispositivo.</summary>
public sealed class NetData
{
    /// <summary>Tipo de conexión (wifi, cellular, …).</summary>
    public string? Type { get; set; }
    /// <summary>Tipo efectivo estimado por el navegador (4g, 3g, …).</summary>
    public string? Eff { get; set; }
    /// <summary>Tiempo de ida y vuelta estimado por el navegador (ms).</summary>
    public double? Rtt { get; set; }
    /// <summary>Ancho de banda de bajada estimado (Mbps).</summary>
    public double? Down { get; set; }
    /// <summary>Resultado del sondeo activo contra el servidor.</summary>
    public ProbeData? Probe { get; set; }
}

/// <summary>Medición activa del enlace con el servidor.</summary>
public sealed class ProbeData
{
    /// <summary>Tiempo medio de ida y vuelta (ms).</summary>
    public double? Rtt { get; set; }
    /// <summary>Variación del RTT entre muestras (jitter, ms).</summary>
    public double? Jit { get; set; }
    /// <summary>Fracción de sondeos perdidos (0 a 1).</summary>
    public double? Loss { get; set; }
    /// <summary>Rendimiento medido (kbit/s).</summary>
    public double? Kbps { get; set; }
}

/// <summary>Calidad del enlace Wi-Fi: RSSI (dBm), frecuencia (MHz) y velocidades de enlace, transmisión y recepción (Mbps). Sin SSID ni BSSID.</summary>
public sealed class WifiData
{
    /// <summary>Intensidad de la señal recibida en dBm.</summary>
    public int? Rssi { get; set; }
    /// <summary>Frecuencia del canal en MHz.</summary>
    public int? Freq { get; set; }
    /// <summary>Velocidad del enlace en Mbps.</summary>
    public int? Link { get; set; }
    /// <summary>Velocidad de transmisión en Mbps.</summary>
    public int? Tx { get; set; }
    /// <summary>Velocidad de recepción en Mbps.</summary>
    public int? Rx { get; set; }
}

/// <summary>Intensidad de la señal celular. Sin identificadores de celda (MCC, MNC, CID, TAC).</summary>
public sealed class CellData
{
    /// <summary>Tecnología (lte, nr, wcdma, gsm).</summary>
    public string? Type { get; set; }
    /// <summary>Potencia de la señal en dBm.</summary>
    public int? Dbm { get; set; }
    /// <summary>Nivel de 0 a 4 que muestra el sistema.</summary>
    public int? Level { get; set; }
    /// <summary>Potencia recibida de la señal de referencia (LTE/NR, dBm).</summary>
    public int? Rsrp { get; set; }
    /// <summary>Calidad recibida de la señal de referencia (dB).</summary>
    public int? Rsrq { get; set; }
    /// <summary>Relación señal/ruido (dB).</summary>
    public double? Snr { get; set; }
}

/// <summary>Datos de la aplicación que envía.</summary>
public sealed class AppData
{
    /// <summary>Si la aplicación estaba visible en pantalla.</summary>
    public bool? Vis { get; set; }
    /// <summary>Versión de la aplicación.</summary>
    public string? Ver { get; set; }
}
