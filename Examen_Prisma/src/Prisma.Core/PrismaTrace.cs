// PrismaTrace.cs — Traza paso a paso de una operación ABCO.
// La llenan PrismaKeys/PrismaCipher/PrismaStego cuando reciben una instancia (si es null no se
// registra nada); la API la guarda en TraceStore, la imprime en el log del servidor y la muestra
// en la consola de administrador para explicar el algoritmo en la demostración.
namespace Prisma.Core;

/// <summary>Traza paso a paso de una operación ABCO, para la consola de administrador (modo demo).</summary>
/// <remarks>
/// Expone valores secretos (matriz, desplazamientos, recorrido); por eso solo la ve el rol admin y se
/// puede desactivar con <c>Demo:Trace = false</c>. Las propiedades se serializan tal cual a JSON.
/// </remarks>
public sealed class PrismaTrace
{
    /// <summary>Número máximo de bloques que se registran, para que la traza sea legible.</summary>
    public const int MaxBlocks = 6;

    /// <summary>Descripción de la operación (p. ej. "ocultar mensaje", "revelar imagen-llave (login 2FA)").</summary>
    public string Operation { get; set; } = "";
    /// <summary>Momento (UTC) en que se creó la traza.</summary>
    public DateTime Utc { get; set; } = DateTime.UtcNow;
    /// <summary>Sal de 16 bytes en hexadecimal.</summary>
    public string SaltHex { get; set; } = "";
    /// <summary>Matriz de Hill A en texto.</summary>
    public string Matrix { get; set; } = "";
    /// <summary>det(A) mod 256 (siempre impar).</summary>
    public int Det { get; set; }
    /// <summary>Matriz inversa A⁻¹ en texto.</summary>
    public string Inverse { get; set; } = "";
    /// <summary>Desplazamiento César inicial s₀.</summary>
    public int S0 { get; set; }
    /// <summary>Multiplicador a del César dinámico.</summary>
    public int Mult { get; set; }
    /// <summary>Incremento c del César dinámico.</summary>
    public int Inc { get; set; }
    /// <summary>Bloque inicial C₀ del encadenamiento, como "(x, y)".</summary>
    public string C0 { get; set; } = "";
    /// <summary>Primeros bloques procesados (hasta <see cref="MaxBlocks"/>).</summary>
    public List<BlockStep> Blocks { get; } = [];
    /// <summary>Primeras posiciones (canales) del recorrido Fisher–Yates.</summary>
    public List<int> FirstPositions { get; set; } = [];
    /// <summary>Tamaño del mensaje en bytes.</summary>
    public int PayloadBytes { get; set; }
    /// <summary>Capacidad ABCO de la portadora en bytes.</summary>
    public int CapacityBytes { get; set; }
    /// <summary>PSNR de la imagen estego (solo al ocultar).</summary>
    public double? Psnr { get; set; }

    /// <summary>Copia en la traza la sal y las claves derivadas.</summary>
    /// <param name="k">Claves de ABCO.</param>
    /// <param name="salt">Sal usada en la derivación.</param>
    public void SetKeys(PrismaKeys k, byte[] salt)
    {
        SaltHex = Convert.ToHexString(salt);
        Matrix = k.A.ToString(); Det = ModMath.Det(k.A); Inverse = k.AInv.ToString();
        S0 = k.S0; Mult = k.Mult; Inc = k.Inc; C0 = $"({k.C0.Item1}, {k.C0.Item2})";
    }

    /// <summary>Registra los valores intermedios de un bloque; ignora los bloques después de <see cref="MaxBlocks"/>.</summary>
    /// <param name="i">Índice del bloque.</param>
    /// <param name="p0">Primer byte del texto claro P.</param>
    /// <param name="p1">Segundo byte del texto claro P.</param>
    /// <param name="x0">Primer componente de X = P ⊕ Cᵢ₋₁.</param>
    /// <param name="x1">Segundo componente de X.</param>
    /// <param name="y0">Primer componente de Y = A·X.</param>
    /// <param name="y1">Segundo componente de Y.</param>
    /// <param name="s">Desplazamiento sᵢ del bloque.</param>
    /// <param name="c0">Primer byte cifrado.</param>
    /// <param name="c1">Segundo byte cifrado.</param>
    public void AddBlock(int i, int p0, int p1, int x0, int x1, int y0, int y1, int s, int c0, int c1)
    {
        if (Blocks.Count < MaxBlocks) Blocks.Add(new BlockStep(i, $"({p0}, {p1})", $"({x0}, {x1})", $"({y0}, {y1})", s, $"({c0}, {c1})"));
    }

    /// <summary>Texto multilínea de la traza, tal como se imprime en el log del servidor y en la consola admin.</summary>
    /// <returns>La traza formateada.</returns>
    public override string ToString()
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"ABCO · {Operation}");
        sb.AppendLine($"  sal = {SaltHex}");
        sb.AppendLine($"  A = {Matrix}  det = {Det}  A⁻¹ = {Inverse}");
        sb.AppendLine($"  César dinámico: s₀ = {S0}, sᵢ = ({Mult}·sᵢ₋₁ + {Inc}) mod 256;  C₀ = {C0}");
        foreach (var b in Blocks)
            sb.AppendLine($"  bloque {b.Index}: P = {b.P} → X = P⊕Cᵢ₋₁ = {b.X} → Y = A·X = {b.Y} → s = {b.Shift} → C = {b.C}");
        if (FirstPositions.Count > 0) sb.AppendLine($"  recorrido[0..{FirstPositions.Count - 1}] = {string.Join(", ", FirstPositions)}");
        sb.Append($"  carga = {PayloadBytes} B de {CapacityBytes} B");
        if (Psnr is { } p) sb.Append($"  PSNR = {p:F2} dB");
        return sb.ToString();
    }
}

/// <summary>Valores intermedios de un bloque de ABCO, ya formateados para mostrarse.</summary>
/// <param name="Index">Índice del bloque.</param>
/// <param name="P">Bloque de texto claro.</param>
/// <param name="X">P ⊕ Cᵢ₋₁.</param>
/// <param name="Y">A·X mod 256.</param>
/// <param name="Shift">Desplazamiento César sᵢ.</param>
/// <param name="C">Bloque cifrado Y + sᵢ.</param>
public sealed record BlockStep(int Index, string P, string X, string Y, int Shift, string C);
