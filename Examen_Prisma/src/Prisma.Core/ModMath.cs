// ModMath.cs — Aritmética modular y matrices 2×2 sobre Z_256 para la capa Hill de ABCO.
// Aquí están el inverso modular (Euclides extendido), el determinante y la matriz inversa que
// permiten descifrar, y el tipo Matrix2 con el producto matriz-vector usado en cada bloque.
namespace Prisma.Core;

/// <summary>Aritmética modular en Z_256 (matemáticas discretas).</summary>
public static class ModMath
{
    /// <summary>Inverso multiplicativo de x módulo m con el algoritmo de Euclides extendido.</summary>
    /// <param name="x">Número a invertir (puede ser negativo; se reduce módulo m).</param>
    /// <param name="m">Módulo (256 en ABCO).</param>
    /// <returns>El único y en [0, m) con x·y ≡ 1 (mod m).</returns>
    /// <exception cref="ArgumentException">Si mcd(x, m) ≠ 1; en Z_256, si x es par.</exception>
    /// <remarks>
    /// Se mantienen los restos r y solo el coeficiente de Bézout s de x, con el invariante rᵢ ≡ sᵢ·x (mod m).
    /// Cuando el resto llega a 0, el anterior es mcd(x, m); si vale 1, s₀·x ≡ 1 y s₀ es el inverso.
    /// </remarks>
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

    /// <summary>Residuo no negativo de x módulo m, en [0, m).</summary>
    /// <param name="x">Entero con signo.</param>
    /// <param name="m">Módulo positivo.</param>
    /// <returns>x mod m en [0, m).</returns>
    /// <remarks>En C#, <c>%</c> conserva el signo del dividendo (−3 % 256 = −3); sumar m y reducir otra vez lo corrige.</remarks>
    public static int Mod(int x, int m = 256) => ((x % m) + m) % m;

    /// <summary>Determinante de una matriz 2x2 [a b; c d] módulo 256.</summary>
    /// <param name="A">Matriz sobre Z_256.</param>
    /// <returns>det(A) = a·d − b·c reducido a [0, 256).</returns>
    public static int Det(Matrix2 A) => Mod(A.A * A.D - A.B * A.C);

    /// <summary>A^-1 = det(A)^-1 · adj(A) (mod 256). Requiere det impar.</summary>
    /// <param name="A">Matriz invertible sobre Z_256.</param>
    /// <returns>La matriz inversa, con A·A⁻¹ = I (mod 256).</returns>
    /// <exception cref="ArgumentException">Si det(A) es par y la matriz no tiene inversa.</exception>
    /// <remarks>
    /// Una matriz es invertible módulo m si y solo si su determinante es una unidad de Z_m. Como 256 = 2⁸,
    /// las unidades son exactamente los números impares. La adjunta de [a b; c d] es [d −b; −c a].
    /// </remarks>
    public static Matrix2 Inverse(Matrix2 A)
    {
        int di = InvMod(Det(A));
        return new Matrix2(Mod(A.D * di), Mod(-A.B * di), Mod(-A.C * di), Mod(A.A * di));
    }
}

/// <summary>Matriz 2x2 sobre Z_256: [[A, B], [C, D]].</summary>
/// <param name="A">Fila 1, columna 1.</param>
/// <param name="B">Fila 1, columna 2.</param>
/// <param name="C">Fila 2, columna 1.</param>
/// <param name="D">Fila 2, columna 2.</param>
public readonly record struct Matrix2(int A, int B, int C, int D)
{
    /// <summary>Producto matriz-vector (x, y) ↦ A·(x, y)ᵀ mod 256.</summary>
    /// <param name="x">Primer componente del bloque.</param>
    /// <param name="y">Segundo componente del bloque.</param>
    /// <returns>El bloque transformado, con cada componente en [0, 256).</returns>
    /// <remarks><c>&amp; 0xFF</c> equivale a mod 256 y, en complemento a dos, también da un resultado no negativo.</remarks>
    public (int, int) Mul(int x, int y) => ((A * x + B * y) & 0xFF, (C * x + D * y) & 0xFF);
    /// <summary>Representación legible para la traza: <c>[[a, b], [c, d]]</c>.</summary>
    public override string ToString() => $"[[{A}, {B}], [{C}, {D}]]";
}
