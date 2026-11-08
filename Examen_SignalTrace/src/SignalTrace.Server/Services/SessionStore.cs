using System.Collections.Concurrent;
using SignalTrace.Core;

namespace SignalTrace.Server.Services;

/// <summary>
/// Una sesión criptográfica abierta por un handshake. Vive solo en memoria: las llaves nunca se guardan en disco,
/// así que reiniciar el servidor obliga a los clientes a negociar llaves nuevas.
/// </summary>
/// <param name="id">Identificador de la sesión.</param>
/// <param name="deviceId">Dispositivo dueño de la sesión.</param>
/// <param name="keys">Llaves derivadas.</param>
/// <param name="now">Momento de creación.</param>
public sealed class CryptoSession(string id, string deviceId, SessionKeys keys, DateTimeOffset now)
{
    /// <summary>Identificador de la sesión.</summary>
    public string Id { get; } = id;
    /// <summary>Dispositivo dueño.</summary>
    public string DeviceId { get; } = deviceId;
    /// <summary>Llaves de la sesión.</summary>
    public SessionKeys Keys { get; } = keys;
    /// <summary>Momento de creación.</summary>
    public DateTimeOffset CreatedAt { get; } = now;
    /// <summary>Último uso; la sesión vence por inactividad (vencimiento deslizante).</summary>
    public DateTimeOffset LastUsed { get; set; } = now;
    /// <summary>Mayor número de secuencia aceptado.</summary>
    public ulong LastSeq { get; set; }
    /// <summary>Bit i encendido = el paquete (LastSeq − i) ya se aceptó (ventana deslizante de 64 bits, opcional).</summary>
    public ulong Window { get; set; }
    /// <summary>Paquetes aceptados en la sesión.</summary>
    public long Accepted { get; set; }
    /// <summary>Candado para procesar en orden los paquetes de la misma sesión.</summary>
    public object Gate { get; } = new();
}

/// <summary>
/// Regla contra el reenvío (replay). Modo estricto: la secuencia debe superar a la última aceptada.
/// Modo ventana: mapa de bits de 64 posiciones al estilo del RFC 4303 (IPsec), que tolera paquetes desordenados.
/// </summary>
public static class ReplayGuard
{
    /// <summary>Indica si un número de secuencia todavía no se ha usado en la sesión.</summary>
    /// <param name="s">Sesión.</param>
    /// <param name="seq">Secuencia del paquete.</param>
    /// <param name="window">Si se permite la ventana de 64 paquetes.</param>
    public static bool IsFresh(CryptoSession s, ulong seq, bool window)
    {
        // La secuencia empieza en 1; el 0 nunca es válido.
        if (seq == 0) return false;
        if (seq > s.LastSeq) return true;
        if (!window) return false;
        // Dentro de la ventana, es nuevo si su bit todavía está apagado.
        var age = s.LastSeq - seq;
        return age < 64 && (s.Window & (1UL << (int)age)) == 0;
    }

    /// <summary>
    /// Marca la secuencia como usada. Se llama solo después de que el paquete se autenticó y validó
    /// (si no, un atacante podría «quemar» números de secuencia con paquetes falsos).
    /// </summary>
    public static void Accept(CryptoSession s, ulong seq)
    {
        if (seq > s.LastSeq)
        {
            // Desplaza la ventana para que el bit 0 sea la secuencia nueva.
            var shift = seq - s.LastSeq;
            s.Window = shift >= 64 ? 1UL : (s.Window << (int)shift) | 1UL;
            s.LastSeq = seq;
        }
        else
        {
            s.Window |= 1UL << (int)(s.LastSeq - seq);
        }
        s.Accepted++;
    }
}

/// <summary>Almacén en memoria de las sesiones abiertas, con vencimiento por inactividad.</summary>
/// <param name="cfg">Configuración (Telemetry:SessionHours, 24 h por defecto).</param>
/// <param name="clock">Reloj del servidor.</param>
public sealed class SessionStore(IConfiguration cfg, TimeProvider clock)
{
    /// <summary>Sesiones indexadas por su id.</summary>
    private readonly ConcurrentDictionary<string, CryptoSession> _sessions = new();
    /// <summary>Tiempo máximo de inactividad.</summary>
    private readonly TimeSpan _life = TimeSpan.FromHours(cfg.GetValue("Telemetry:SessionHours", 24.0));

    /// <summary>Tiempo máximo de inactividad de una sesión.</summary>
    public TimeSpan Lifetime => _life;
    /// <summary>Número de sesiones abiertas.</summary>
    public int Count => _sessions.Count;

    /// <summary>Guarda una sesión nueva y aprovecha para limpiar las vencidas.</summary>
    public CryptoSession Add(string id, string deviceId, SessionKeys keys)
    {
        var s = new CryptoSession(id, deviceId, keys, clock.GetUtcNow());
        _sessions[id] = s;
        Sweep();
        return s;
    }

    /// <summary>Busca una sesión; si venció la elimina y devuelve null; si no, renueva su último uso.</summary>
    public CryptoSession? Get(string id)
    {
        if (!_sessions.TryGetValue(id, out var s)) return null;
        var now = clock.GetUtcNow();
        if (now - s.LastUsed > _life)
        {
            _sessions.TryRemove(id, out _);
            return null;
        }
        s.LastUsed = now; // vencimiento deslizante
        return s;
    }

    /// <summary>Cierra todas las sesiones de un dispositivo (al revocarlo).</summary>
    public void RemoveDevice(string deviceId)
    {
        foreach (var (k, v) in _sessions)
            if (v.DeviceId == deviceId) _sessions.TryRemove(k, out _);
    }

    /// <summary>Elimina todas las sesiones (el laboratorio lo usa para simular un reinicio del servidor).</summary>
    public void Clear() => _sessions.Clear();

    /// <summary>Elimina las sesiones vencidas.</summary>
    private void Sweep()
    {
        var now = clock.GetUtcNow();
        foreach (var (k, v) in _sessions)
            if (now - v.LastUsed > _life) _sessions.TryRemove(k, out _);
    }
}
