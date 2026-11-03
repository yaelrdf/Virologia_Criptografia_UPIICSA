// TraceStore.cs — Almacén en memoria de la última traza ABCO para la consola de administrador.
// Las rutas que usan ABCO crean una traza con New(), la llenan los algoritmos de Prisma.Core y se
// guarda con Save(); la consola la lee en GET /api/admin/trace/last y /api/admin/diffmap/last.
using Prisma.Core;

namespace Prisma.Api.Services;

/// <summary>Guarda en memoria la traza y el mapa de diferencias de la última operación ABCO (modo demo).</summary>
/// <remarks>
/// Es un singleton compartido por todas las peticiones, así que el acceso se serializa con un candado.
/// Solo se conserva la última operación y nada se escribe en la base de datos.
/// </remarks>
/// <param name="cfg">Configuración; <c>Demo:Trace</c> (por defecto true) activa la traza.</param>
/// <param name="log">Registrador donde se imprime cada traza.</param>
public sealed class TraceStore(IConfiguration cfg, ILogger<TraceStore> log)
{
    private readonly object _gate = new();
    private PrismaTrace? _last;
    private byte[]? _diffPng;
    private string? _lastUser;

    /// <summary>Si la traza está activa (<c>Demo:Trace</c>).</summary>
    public bool Enabled { get; } = cfg.GetValue("Demo:Trace", true);

    /// <summary>Crea una traza vacía para una operación.</summary>
    /// <param name="operation">Descripción que aparecerá en la consola.</param>
    /// <returns>La traza, o null si está desactivada: así los algoritmos omiten todo el registro (<c>trace?.</c>).</returns>
    public PrismaTrace? New(string operation) => Enabled ? new PrismaTrace { Operation = operation } : null;

    /// <summary>Guarda la traza como la última operación y la imprime en el log del servidor.</summary>
    /// <param name="trace">Traza a guardar; si es null (traza desactivada o modo clásico) no hace nada.</param>
    /// <param name="user">Usuario que hizo la operación; en el log aparece ofuscado con César.</param>
    /// <param name="diffPng">Mapa de diferencias en PNG; si es null se conserva el anterior (p. ej. al revelar).</param>
    public void Save(PrismaTrace? trace, string user, byte[]? diffPng = null)
    {
        if (trace is null) return;
        lock (_gate) { _last = trace; _lastUser = user; if (diffPng is not null) _diffPng = diffPng; }
        // Consola de administrador en el backend: la traza se imprime en el log del servidor.
        log.LogInformation("Usuario {User}\n{Trace}", Prisma.Core.Caesar.Obfuscate(user), trace.ToString());
    }

    /// <summary>Última traza y el usuario (sin ofuscar) que la produjo.</summary>
    public (PrismaTrace? Trace, string? User) Last { get { lock (_gate) return (_last, _lastUser); } }
    /// <summary>Último mapa de diferencias en PNG, o null si aún no se ha ocultado nada.</summary>
    public byte[]? LastDiffPng { get { lock (_gate) return _diffPng; } }
}
