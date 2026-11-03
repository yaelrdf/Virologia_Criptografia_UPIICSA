// Caesar.cs — Algoritmo clásico César del sistema ABCO.
// Contiene el cifrado y descifrado sobre el alfabeto latino (26 letras) o español (27, con Ñ),
// la ofuscación de nombres de usuario que usa la consola de administrador y el ataque de
// fuerza bruta con la prueba χ² que alimenta el laboratorio de César.
using System.Text;

namespace Prisma.Core;

/// <summary>Cifrado César clásico: C = (P + k) mod n sobre un alfabeto; conserva mayúsculas/minúsculas.</summary>
/// <remarks>
/// Los caracteres que no pertenecen al alfabeto (espacios, dígitos, signos) pasan sin cambio.
/// Antes de desplazar, el texto se normaliza con <see cref="Normalize"/> para que las vocales con tilde
/// no queden fuera del alfabeto y delaten el texto claro.
/// </remarks>
public static class Caesar
{
    /// <summary>Alfabeto latino básico (n = 26).</summary>
    public const string Latin = "ABCDEFGHIJKLMNOPQRSTUVWXYZ";      // n = 26
    /// <summary>Alfabeto español con Ñ entre N y O (n = 27).</summary>
    public const string Spanish = "ABCDEFGHIJKLMNÑOPQRSTUVWXYZ";   // n = 27

    /// <summary>Devuelve el alfabeto que corresponde a la opción elegida.</summary>
    /// <param name="withEnye"><c>true</c> para el alfabeto español de 27 letras; <c>false</c> para el latino de 26.</param>
    /// <returns><see cref="Spanish"/> o <see cref="Latin"/>.</returns>
    public static string AlphabetFor(bool withEnye) => withEnye ? Spanish : Latin;

    /// <summary>Cifra: normaliza el texto y desplaza cada letra <paramref name="key"/> posiciones hacia adelante.</summary>
    /// <param name="text">Texto claro.</param>
    /// <param name="key">Desplazamiento; puede ser negativo o mayor que n (se reduce módulo n).</param>
    /// <param name="withEnye">Usar el alfabeto español de 27 letras.</param>
    /// <returns>Texto cifrado, con mayúsculas/minúsculas y símbolos conservados.</returns>
    public static string Encrypt(string text, int key, bool withEnye = false) => Shift(Normalize(text, withEnye), key, AlphabetFor(withEnye));
    /// <summary>Descifra: es el mismo desplazamiento con la clave negada, P = (C − k) mod n.</summary>
    /// <param name="text">Texto cifrado.</param>
    /// <param name="key">Desplazamiento usado al cifrar.</param>
    /// <param name="withEnye">Usar el alfabeto español de 27 letras.</param>
    /// <returns>Texto claro normalizado (sin tildes).</returns>
    public static string Decrypt(string text, int key, bool withEnye = false) => Shift(Normalize(text, withEnye), -key, AlphabetFor(withEnye));

    /// <summary>
    /// Quita tildes y diéresis (á → a, ü → u) para que todas las letras pasen por el alfabeto, como en el
    /// cifrado César clásico. La Ñ se conserva solo si el alfabeto la incluye; si no, pasa a N.
    /// </summary>
    /// <param name="text">Texto de entrada.</param>
    /// <param name="withEnye">Si es <c>true</c>, la Ñ/ñ se deja intacta porque forma parte del alfabeto.</param>
    /// <returns>Texto con las letras acentuadas reemplazadas por su letra base ASCII.</returns>
    public static string Normalize(string text, bool withEnye)
    {
        var sb = new StringBuilder(text.Length);
        foreach (char ch in text)
        {
            if (withEnye && ch is 'ñ' or 'Ñ') { sb.Append(ch); continue; }
            // La forma de descomposición canónica (FormD) separa la letra base de su marca diacrítica
            // ("á" → "a" + U+0301). Si el primer carácter es una letra ASCII, se toma solo esa base;
            // cualquier otro símbolo se deja tal cual.
            string d = ch.ToString().Normalize(NormalizationForm.FormD);
            char b = d[0];
            sb.Append(d.Length > 1 && char.IsLetter(b) && b < 128 ? b : ch);
        }
        return sb.ToString();
    }

    /// <summary>Desplaza cada letra del alfabeto <paramref name="key"/> posiciones (mod n), sin normalizar.</summary>
    /// <param name="text">Texto a desplazar.</param>
    /// <param name="key">Desplazamiento con signo.</param>
    /// <param name="alphabet">Alfabeto en mayúsculas; su longitud es el módulo n.</param>
    /// <returns>Texto desplazado; las minúsculas se comparan en mayúscula y se devuelven en minúscula.</returns>
    public static string Shift(string text, int key, string alphabet)
    {
        int n = alphabet.Length;
        var sb = new StringBuilder(text.Length);
        foreach (char ch in text)
        {
            char up = char.ToUpperInvariant(ch);
            int i = alphabet.IndexOf(up);
            if (i < 0) { sb.Append(ch); continue; }          // espacios, dígitos, tildes: sin cambio
            // ModMath.Mod da un residuo en [0, n) incluso con claves negativas (al descifrar).
            char r = alphabet[ModMath.Mod(i + key, n)];
            sb.Append(char.IsLower(ch) ? char.ToLowerInvariant(r) : r);
        }
        return sb.ToString();
    }

    /// <summary>Ofuscación (no cifrado) sobre los 95 caracteres ASCII imprimibles (32..126).</summary>
    /// <remarks>
    /// La consola de administrador la usa para no mostrar los nombres de usuario en claro. Con la clave
    /// por omisión (13) es un ROT13 extendido a todo el ASCII imprimible; cualquiera que conozca la clave
    /// la revierte, así que solo evita la lectura casual.
    /// </remarks>
    /// <param name="text">Texto a ofuscar.</param>
    /// <param name="key">Desplazamiento sobre los 95 imprimibles.</param>
    /// <returns>Texto ofuscado; los caracteres fuera de 32..126 no cambian.</returns>
    public static string Obfuscate(string text, int key = 13) => ShiftPrintable(text, key);
    /// <summary>Revierte <see cref="Obfuscate"/> desplazando con la clave negada.</summary>
    /// <param name="text">Texto ofuscado.</param>
    /// <param name="key">Clave usada al ofuscar.</param>
    /// <returns>Texto original.</returns>
    public static string Deobfuscate(string text, int key = 13) => ShiftPrintable(text, -key);

    /// <summary>César sobre el rango ASCII imprimible: c' = 32 + ((c − 32 + k) mod 95).</summary>
    private static string ShiftPrintable(string text, int key)
    {
        var sb = new StringBuilder(text.Length);
        foreach (char ch in text)
            sb.Append(ch is >= ' ' and <= '~' ? (char)(' ' + ModMath.Mod(ch - ' ' + key, 95)) : ch);
        return sb.ToString();
    }
}

/// <summary>Ataque a César: fuerza bruta de todas las claves + prueba χ² contra las frecuencias del español.</summary>
/// <remarks>
/// El espacio de claves es tan pequeño (n = 26 o 27) que basta probarlas todas. Para elegir la correcta
/// sin intervención humana, cada candidato se puntúa con la distancia χ² entre su histograma de letras y
/// el histograma esperado del español: el texto claro verdadero es el que más se parece al idioma.
/// </remarks>
public static class CaesarCracker
{
    // Frecuencias relativas (%) de letras en español.
    private static readonly Dictionary<char, double> Spanish = new()
    {
        ['A'] = 12.53, ['B'] = 1.42, ['C'] = 4.68, ['D'] = 5.86, ['E'] = 13.68, ['F'] = 0.69, ['G'] = 1.01,
        ['H'] = 0.70, ['I'] = 6.25, ['J'] = 0.44, ['K'] = 0.02, ['L'] = 4.97, ['M'] = 3.15, ['N'] = 6.71,
        ['Ñ'] = 0.31, ['O'] = 8.68, ['P'] = 2.51, ['Q'] = 0.88, ['R'] = 6.87, ['S'] = 7.98, ['T'] = 4.63,
        ['U'] = 3.93, ['V'] = 0.90, ['W'] = 0.01, ['X'] = 0.22, ['Y'] = 0.90, ['Z'] = 0.52,
    };

    /// <summary>Resultado de probar una clave.</summary>
    /// <param name="Key">Clave probada (0..n−1).</param>
    /// <param name="ChiSquare">Estadístico χ² del texto descifrado con esa clave (menor = más parecido al español).</param>
    /// <param name="Preview">Primeros 80 caracteres del texto descifrado.</param>
    public sealed record Candidate(int Key, double ChiSquare, string Preview);

    /// <summary>χ² = Σ (Oᵢ − Eᵢ)² / Eᵢ sobre las letras del alfabeto.</summary>
    /// <param name="text">Texto a evaluar (se compara en mayúsculas).</param>
    /// <param name="alphabet">Alfabeto cuyas letras se cuentan.</param>
    /// <returns>Estadístico χ²; <see cref="double.MaxValue"/> si el texto no tiene letras del alfabeto.</returns>
    public static double ChiSquare(string text, string alphabet)
    {
        var counts = new Dictionary<char, int>();
        int n = 0;
        foreach (char ch in text.ToUpperInvariant())
            if (alphabet.Contains(ch)) { counts[ch] = counts.GetValueOrDefault(ch) + 1; n++; }
        if (n == 0) return double.MaxValue;
        // Las frecuencias se renormalizan sobre el alfabeto elegido: sin Ñ, los porcentajes ya no suman 100.
        double total = alphabet.Sum(c => Spanish[c]), chi = 0;
        foreach (char c in alphabet)
        {
            // Eᵢ: cuántas veces se esperaría la letra en un texto español de n letras; Oᵢ: cuántas hay.
            double e = n * Spanish[c] / total;
            double o = counts.GetValueOrDefault(c);
            chi += (o - e) * (o - e) / e;
        }
        return chi;
    }

    /// <summary>Prueba las n claves y las ordena de menor a mayor χ² (la primera es la más probable).</summary>
    /// <param name="cipher">Texto cifrado con César.</param>
    /// <param name="withEnye">Usar el alfabeto español de 27 letras.</param>
    /// <returns>Los n candidatos ordenados por χ² ascendente.</returns>
    public static List<Candidate> Crack(string cipher, bool withEnye = false)
    {
        string alphabet = Caesar.AlphabetFor(withEnye);
        return Enumerable.Range(0, alphabet.Length)
            .Select(k =>
            {
                string plain = Caesar.Shift(cipher, -k, alphabet);
                return new Candidate(k, Math.Round(ChiSquare(plain, alphabet), 2), plain.Length > 80 ? plain[..80] + "…" : plain);
            })
            .OrderBy(c => c.ChiSquare)
            .ToList();
    }
}
