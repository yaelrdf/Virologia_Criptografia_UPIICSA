using System.Text.Json;
using System.Text.Json.Serialization;

namespace SignalTrace.Core;

// API contract (PLAN §7). Binary fields are standard Base64 with padding; times are epoch milliseconds.

public sealed record ErrorResponse(string Code, string Message);

public sealed record LoginRequest(string Username, string Password);
public sealed record LoginResponse(string Token, long ExpiresAt, string Role, string Username);

public sealed record CreateUserRequest(string Username, string Password, string Role);

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

// Telemetry payload v1 (PLAN §8). Every field except T and Src is optional.

public sealed class TelemetryPayload
{
    public long T { get; set; }
    public string Src { get; set; } = "";
    public GpsData? Gps { get; set; }
    public MotionData? Mot { get; set; }
    public BatteryData? Bat { get; set; }
    public NetData? Net { get; set; }
    public WifiData? Wifi { get; set; }
    public CellData? Cell { get; set; }
    public AppData? App { get; set; }
}

public sealed class GpsData
{
    public double Lat { get; set; }
    public double Lon { get; set; }
    public double? Acc { get; set; }
    public double? Alt { get; set; }
    public double? Spd { get; set; }
    public double? Hdg { get; set; }
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

public sealed class BatteryData
{
    public double Lvl { get; set; }
    public bool? Chg { get; set; }
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

public sealed class WifiData
{
    public int? Rssi { get; set; }
    public int? Freq { get; set; }
    public int? Link { get; set; }
    public string? Ssid { get; set; }
    public string? Bssid { get; set; }
}

public sealed class CellData
{
    public string? Type { get; set; }
    public int? Dbm { get; set; }
    public int? Level { get; set; }
    public int? Rsrp { get; set; }
    public int? Rsrq { get; set; }
    public double? Snr { get; set; }
    public int? Mcc { get; set; }
    public int? Mnc { get; set; }
    public long? Cid { get; set; }
    public int? Tac { get; set; }
}

public sealed class AppData
{
    public bool? Vis { get; set; }
    public string? Ver { get; set; }
}
