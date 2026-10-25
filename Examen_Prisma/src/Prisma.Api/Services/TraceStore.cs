using Prisma.Core;

namespace Prisma.Api.Services;

/// <summary>Guarda en memoria la traza y el mapa de diferencias de la última operación ABCO (modo demo).</summary>
public sealed class TraceStore(IConfiguration cfg, ILogger<TraceStore> log)
{
    private readonly object _gate = new();
    private PrismaTrace? _last;
    private byte[]? _diffPng;
    private string? _lastUser;

    public bool Enabled { get; } = cfg.GetValue("Demo:Trace", true);

    public PrismaTrace? New(string operation) => Enabled ? new PrismaTrace { Operation = operation } : null;

    public void Save(PrismaTrace? trace, string user, byte[]? diffPng = null)
    {
        if (trace is null) return;
        lock (_gate) { _last = trace; _lastUser = user; if (diffPng is not null) _diffPng = diffPng; }
        // Consola de administrador en el backend: la traza se imprime en el log del servidor.
        log.LogInformation("Usuario {User}\n{Trace}", Prisma.Core.Caesar.Obfuscate(user), trace.ToString());
    }

    public (PrismaTrace? Trace, string? User) Last { get { lock (_gate) return (_last, _lastUser); } }
    public byte[]? LastDiffPng { get { lock (_gate) return _diffPng; } }
}
