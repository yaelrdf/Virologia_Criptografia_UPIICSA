using System.Text.Json;
using System.Text.Json.Serialization;

namespace SignalTrace.Core;

// API contract (PLAN §7). Binary fields are standard Base64 with padding; times are epoch milliseconds.

public sealed record ErrorResponse(string Code, string Message);

public sealed record LoginRequest(string Username, string Password);
public sealed record LoginResponse(string Token, long ExpiresAt, string Role, string Username);

public sealed record CreateUserRequest(string Username, string Password, string Role);
public sealed record ChangeRoleRequest(string Role);
public sealed record ChangePasswordRequest(string? Current, string? New);
public sealed record ResetPasswordResponse(string Username, string Password);

public sealed record ServerKeyResponse(string PublicKey, string Fingerprint, string[] Suites);

public sealed record RegisterDeviceRequest(string Name, string Kind);
public sealed record RegisterDeviceResponse(string DeviceId, string DeviceToken);

public sealed record HandshakeRequest(string DeviceId, string ClientPublicKey, string ClientNonce, string? Suite = null);
public sealed record HandshakeResponse(string SessionId, string ServerPublicKey, string ServerNonce, string Signature, long ExpiresAt, string Suite);

public sealed record TelemetryAck(ulong Ack);

public static class Json
{
    /// <summary>Compact camelCase JSON, nulls omitted (PLAN §8: absent fields are omitted, never null).</summary>
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };
}

// Telemetry payload v2: the sensor bank. Every block except T and Src is optional; a device only sends the
// sensors its user chose to expose. There is no location data (no GPS, SSID, BSSID or cell identifiers).

public sealed class TelemetryPayload
{
    public long T { get; set; }
    public string Src { get; set; } = "";
    /// <summary>Accelerometer (with gravity), m/s², plus the magnitude window used for the spectrum.</summary>
    public MotionData? Mot { get; set; }
    /// <summary>Linear acceleration (without gravity), m/s².</summary>
    public Vec3? Lin { get; set; }
    /// <summary>Gravity vector, m/s².</summary>
    public Vec3? Grv { get; set; }
    /// <summary>Gyroscope, rad/s.</summary>
    public Vec3? Gyr { get; set; }
    /// <summary>Magnetic field, µT.</summary>
    public Vec3? Mfl { get; set; }
    public OrientationData? Ori { get; set; }
    public EnvData? Env { get; set; }
    public StepData? Stp { get; set; }
    public BatteryData? Bat { get; set; }
    public NetData? Net { get; set; }
    public WifiData? Wifi { get; set; }
    public CellData? Cell { get; set; }
    public AppData? App { get; set; }
}

public sealed class Vec3
{
    public double X { get; set; }
    public double Y { get; set; }
    public double Z { get; set; }
    public double Norm() => Math.Sqrt(X * X + Y * Y + Z * Z);
}

public sealed class MotionData
{
    public double? Ax { get; set; }
    public double? Ay { get; set; }
    public double? Az { get; set; }
    public double? Rms { get; set; }
    public double? Peak { get; set; }
    public double? Fs { get; set; }
    public double[]? Mag { get; set; }
}

/// <summary>Device orientation in degrees: azimuth [0,360), pitch [-180,180], roll [-90,90] (W3C alpha/beta/gamma).</summary>
public sealed class OrientationData
{
    public double Az { get; set; }
    public double Pi { get; set; }
    public double Ro { get; set; }
}

/// <summary>Environment sensors: illuminance (lx), proximity (cm), pressure (hPa), temperature (°C), relative humidity (%).</summary>
public sealed class EnvData
{
    public double? Lux { get; set; }
    public double? Prx { get; set; }
    public double? Hpa { get; set; }
    public double? Tmp { get; set; }
    public double? Hum { get; set; }
}

/// <summary>Steps counted since the transmission started.</summary>
public sealed class StepData
{
    public long N { get; set; }
}

public sealed class BatteryData
{
    public double Lvl { get; set; }
    public bool? Chg { get; set; }
    /// <summary>Battery temperature (°C), voltage (V), health and power source (Android only).</summary>
    public double? Temp { get; set; }
    public double? Volt { get; set; }
    public string? Health { get; set; }
    public string? Plug { get; set; }
}

public sealed class NetData
{
    public string? Type { get; set; }
    public string? Eff { get; set; }
    public double? Rtt { get; set; }
    public double? Down { get; set; }
    public ProbeData? Probe { get; set; }
}

public sealed class ProbeData
{
    public double? Rtt { get; set; }
    public double? Jit { get; set; }
    public double? Loss { get; set; }
    public double? Kbps { get; set; }
}

/// <summary>Wi-Fi link quality: RSSI (dBm), frequency (MHz), link/tx/rx speeds (Mbps). No SSID/BSSID.</summary>
public sealed class WifiData
{
    public int? Rssi { get; set; }
    public int? Freq { get; set; }
    public int? Link { get; set; }
    public int? Tx { get; set; }
    public int? Rx { get; set; }
}

/// <summary>Cellular signal strength. No cell identifiers (MCC/MNC/CID/TAC).</summary>
public sealed class CellData
{
    public string? Type { get; set; }
    public int? Dbm { get; set; }
    public int? Level { get; set; }
    public int? Rsrp { get; set; }
    public int? Rsrq { get; set; }
    public double? Snr { get; set; }
}

public sealed class AppData
{
    public bool? Vis { get; set; }
    public string? Ver { get; set; }
}
