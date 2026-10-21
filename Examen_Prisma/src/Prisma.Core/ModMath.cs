namespace Prisma.Core;

/// <summary>Aritmética modular en Z_256 (matemáticas discretas).</summary>
public static class ModMath
{
    /// <summary>Inverso multiplicativo de x módulo m con el algoritmo de Euclides extendido.</summary>
    public static int InvMod(int x, int m = 256)
    {
        int r0 = Mod(x, m), r1 = m, s0 = 1, s1 = 0;
        while (r1 != 0)
        {
            int q = r0 / r1;
            (r0, r1) = (r1, r0 - q * r1);
            (s0, s1) = (s1, s0 - q * s1);
        }
        if (r0 != 1) throw new ArgumentException($"{x} no es invertible módulo {m} (mcd = {r0})");
        return Mod(s0, m);
    }

    public static int Mod(int x, int m = 256) => ((x % m) + m) % m;

    /// <summary>Determinante de una matriz 2x2 [a b; c d] módulo 256.</summary>
    public static int Det(Matrix2 A) => Mod(A.A * A.D - A.B * A.C);

    /// <summary>A^-1 = det(A)^-1 · adj(A) (mod 256). Requiere det impar.</summary>
    public static Matrix2 Inverse(Matrix2 A)
    {
        int di = InvMod(Det(A));
        return new Matrix2(Mod(A.D * di), Mod(-A.B * di), Mod(-A.C * di), Mod(A.A * di));
    }
}

/// <summary>Matriz 2x2 sobre Z_256: [[A, B], [C, D]].</summary>
public readonly record struct Matrix2(int A, int B, int C, int D)
{
    public (int, int) Mul(int x, int y) => ((A * x + B * y) & 0xFF, (C * x + D * y) & 0xFF);
    public override string ToString() => $"[[{A}, {B}], [{C}, {D}]]";
}
