namespace Prisma.Core;

/// <summary>Traza paso a paso de una operación PRISMA-L, para la consola de administrador (modo demo).</summary>
public sealed class PrismaTrace
{
    public const int MaxBlocks = 6;

    public string Operation { get; set; } = "";
    public DateTime Utc { get; set; } = DateTime.UtcNow;
    public string SaltHex { get; set; } = "";
    public string Matrix { get; set; } = "";
    public int Det { get; set; }
    public string Inverse { get; set; } = "";
    public int S0 { get; set; }
    public int Mult { get; set; }
    public int Inc { get; set; }
    public string C0 { get; set; } = "";
    public List<BlockStep> Blocks { get; } = [];
    public List<int> FirstPositions { get; set; } = [];
    public int PayloadBytes { get; set; }
    public int CapacityBytes { get; set; }
    public double? Psnr { get; set; }

    public void SetKeys(PrismaKeys k, byte[] salt)
    {
        SaltHex = Convert.ToHexString(salt);
        Matrix = k.A.ToString(); Det = ModMath.Det(k.A); Inverse = k.AInv.ToString();
        S0 = k.S0; Mult = k.Mult; Inc = k.Inc; C0 = $"({k.C0.Item1}, {k.C0.Item2})";
    }

    public void AddBlock(int i, int p0, int p1, int x0, int x1, int y0, int y1, int s, int c0, int c1)
    {
        if (Blocks.Count < MaxBlocks) Blocks.Add(new BlockStep(i, $"({p0}, {p1})", $"({x0}, {x1})", $"({y0}, {y1})", s, $"({c0}, {c1})"));
    }

    public override string ToString()
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"PRISMA-L · {Operation}");
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

public sealed record BlockStep(int Index, string P, string X, string Y, int Shift, string C);
