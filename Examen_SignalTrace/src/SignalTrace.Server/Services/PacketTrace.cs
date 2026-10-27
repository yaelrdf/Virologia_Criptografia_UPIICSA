using System.Collections.Concurrent;
using System.Text;
using SignalTrace.Core;

namespace SignalTrace.Server.Services;

public sealed record TraceStage(string Name, string Hex, double Entropy);

/// <summary>What the admin console shows for one packet (PLAN §11.2). Keys appear only with Lab:ExposeKeys.</summary>
public sealed class PacketTraceDto
{
    private static long _next;
    public long Id { get; } = Interlocked.Increment(ref _next);
    public long TsServer { get; set; }
    public string Transport { get; set; } = "http";
    public string Status { get; set; } = "";
    public string Code { get; set; } = "";
    public string? Message { get; set; }
    public string? Warning { get; set; }
    public string? Sid { get; set; }
    public string? Did { get; set; }
    public ulong Seq { get; set; }
    public ulong? LastSeq { get; set; }
    public long Ts { get; set; }
    public string? Suite { get; set; }
    public int WireBytes { get; set; }
    /// <summary>The HTTP body exactly as received: what a passive listener sees.</summary>
    public string Wire { get; set; } = "";

    public string? PlaintextJson { get; set; }
    public double? PlaintextEntropy { get; set; }
    public object? Ycm1Params { get; set; }
    public List<TraceStage> Ycm1Stages { get; set; } = [];
    public double? Ycm1Entropy { get; set; }
    public string? Nonce { get; set; }
    public string? Aad { get; set; }
    public string? Ciphertext { get; set; }
    public string? Tag { get; set; }
    public double? CiphertextEntropy { get; set; }
    public Dictionary<string, double> Micros { get; set; } = [];
    public Dictionary<string, string>? Keys { get; set; }

    public static void Fill(PacketTraceDto t, PacketTraceData d, bool exposePlaintext, SessionKeys? keys)
    {
        if (d.Plaintext.Length > 0)
        {
            t.PlaintextEntropy = Entropy.Shannon(d.Plaintext);
            if (exposePlaintext) t.PlaintextJson = Encoding.UTF8.GetString(d.Plaintext);
        }
        if (d.Ycm1Params is { } p)
            t.Ycm1Params = new { seed = p.Seed, rounds = p.Rounds.Select(r => new { k = r.K, m = r.M, rot = r.Rot }) };
        t.Ycm1Stages = exposePlaintext
            ? d.Ycm1.Stages.Select(s => new TraceStage(s.Name, Bytes.ToHex(s.State), Entropy.Shannon(s.State))).ToList()
            : [];
        if (d.Ycm1Output.Length > 0) t.Ycm1Entropy = Entropy.Shannon(d.Ycm1Output);
        if (d.Nonce.Length > 0) t.Nonce = Bytes.ToHex(d.Nonce);
        if (d.Aad.Length > 0) t.Aad = Bytes.ToHex(d.Aad);
        if (d.Ciphertext.Length > 0)
        {
            t.Ciphertext = Bytes.ToHex(d.Ciphertext);
            t.CiphertextEntropy = Entropy.Shannon(d.Ciphertext);
        }
        if (d.Tag.Length > 0) t.Tag = Bytes.ToHex(d.Tag);
        foreach (var (k, v) in d.Micros) t.Micros[k] = Math.Round(v, 1);
        if (keys is not null)
            t.Keys = new() { ["kAead"] = Bytes.ToHex(keys.KAead), ["kSig"] = Bytes.ToHex(keys.KSig), ["ivSalt"] = Bytes.ToHex(keys.IvSalt) };
    }
}

/// <summary>Ring buffer with the last packet traces, so the console can show history on load.</summary>
public sealed class TraceStore
{
    private readonly ConcurrentQueue<PacketTraceDto> _q = new();
    private const int Max = 100;

    public void Add(PacketTraceDto t)
    {
        _q.Enqueue(t);
        while (_q.Count > Max && _q.TryDequeue(out _)) { }
    }

    public IEnumerable<PacketTraceDto> Recent(int n) => _q.Reverse().Take(n);
}
