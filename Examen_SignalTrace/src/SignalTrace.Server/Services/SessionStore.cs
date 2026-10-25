using System.Collections.Concurrent;
using SignalTrace.Core;

namespace SignalTrace.Server.Services;

/// <summary>A cryptographic session. Lives only in memory: keys are never persisted.</summary>
public sealed class CryptoSession(string id, string deviceId, SessionKeys keys, DateTimeOffset now)
{
    public string Id { get; } = id;
    public string DeviceId { get; } = deviceId;
    public SessionKeys Keys { get; } = keys;
    public DateTimeOffset CreatedAt { get; } = now;
    public DateTimeOffset LastUsed { get; set; } = now;
    public ulong LastSeq { get; set; }
    /// <summary>Bit i set = packet (LastSeq - i) already accepted (64-bit sliding window, optional).</summary>
    public ulong Window { get; set; }
    public long Accepted { get; set; }
    public object Gate { get; } = new();
}

/// <summary>Anti-replay rule. Strict mode: seq must exceed the last accepted one. Window mode: RFC 4303-style 64-bit bitmap.</summary>
public static class ReplayGuard
{
    public static bool IsFresh(CryptoSession s, ulong seq, bool window)
    {
        if (seq == 0) return false;
        if (seq > s.LastSeq) return true;
        if (!window) return false;
        var age = s.LastSeq - seq;
        return age < 64 && (s.Window & (1UL << (int)age)) == 0;
    }

    /// <summary>Called only after the packet authenticated and validated (otherwise an attacker could burn sequence numbers).</summary>
    public static void Accept(CryptoSession s, ulong seq)
    {
        if (seq > s.LastSeq)
        {
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

public sealed class SessionStore(IConfiguration cfg, TimeProvider clock)
{
    private readonly ConcurrentDictionary<string, CryptoSession> _sessions = new();
    private readonly TimeSpan _life = TimeSpan.FromHours(cfg.GetValue("Telemetry:SessionHours", 24.0));

    public TimeSpan Lifetime => _life;
    public int Count => _sessions.Count;

    public CryptoSession Add(string id, string deviceId, SessionKeys keys)
    {
        var s = new CryptoSession(id, deviceId, keys, clock.GetUtcNow());
        _sessions[id] = s;
        Sweep();
        return s;
    }

    public CryptoSession? Get(string id)
    {
        if (!_sessions.TryGetValue(id, out var s)) return null;
        var now = clock.GetUtcNow();
        if (now - s.LastUsed > _life)
        {
            _sessions.TryRemove(id, out _);
            return null;
        }
        s.LastUsed = now; // sliding expiration
        return s;
    }

    public void RemoveDevice(string deviceId)
    {
        foreach (var (k, v) in _sessions)
            if (v.DeviceId == deviceId) _sessions.TryRemove(k, out _);
    }

    /// <summary>Drops every session (used by the lab to simulate a server restart).</summary>
    public void Clear() => _sessions.Clear();

    private void Sweep()
    {
        var now = clock.GetUtcNow();
        foreach (var (k, v) in _sessions)
            if (now - v.LastUsed > _life) _sessions.TryRemove(k, out _);
    }
}
