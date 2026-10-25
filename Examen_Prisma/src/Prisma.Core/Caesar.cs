using System.Text;

namespace Prisma.Core;

/// <summary>Cifrado César clásico: C = (P + k) mod n sobre un alfabeto; conserva mayúsculas/minúsculas.</summary>
public static class Caesar
{
    public const string Latin = "ABCDEFGHIJKLMNOPQRSTUVWXYZ";      // n = 26
    public const string Spanish = "ABCDEFGHIJKLMNÑOPQRSTUVWXYZ";   // n = 27

    public static string AlphabetFor(bool withEnye) => withEnye ? Spanish : Latin;

    public static string Encrypt(string text, int key, bool withEnye = false) => Shift(Normalize(text, withEnye), key, AlphabetFor(withEnye));
    public static string Decrypt(string text, int key, bool withEnye = false) => Shift(Normalize(text, withEnye), -key, AlphabetFor(withEnye));

    /// <summary>
    /// Quita tildes y diéresis (á → a, ü → u) para que todas las letras pasen por el alfabeto, como en el
    /// cifrado César clásico. La Ñ se conserva solo si el alfabeto la incluye; si no, pasa a N.
    /// </summary>
    public static string Normalize(string text, bool withEnye)
    {
        var sb = new StringBuilder(text.Length);
        foreach (char ch in text)
        {
            if (withEnye && ch is 'ñ' or 'Ñ') { sb.Append(ch); continue; }
            string d = ch.ToString().Normalize(NormalizationForm.FormD);
            char b = d[0];
            sb.Append(d.Length > 1 && char.IsLetter(b) && b < 128 ? b : ch);
        }
        return sb.ToString();
    }

    public static string Shift(string text, int key, string alphabet)
    {
        int n = alphabet.Length;
        var sb = new StringBuilder(text.Length);
        foreach (char ch in text)
        {
            char up = char.ToUpperInvariant(ch);
            int i = alphabet.IndexOf(up);
            if (i < 0) { sb.Append(ch); continue; }          // espacios, dígitos, tildes: sin cambio
            char r = alphabet[ModMath.Mod(i + key, n)];
            sb.Append(char.IsLower(ch) ? char.ToLowerInvariant(r) : r);
        }
        return sb.ToString();
    }

    /// <summary>Ofuscación (no cifrado) sobre los 95 caracteres ASCII imprimibles (32..126).</summary>
    public static string Obfuscate(string text, int key = 13) => ShiftPrintable(text, key);
    public static string Deobfuscate(string text, int key = 13) => ShiftPrintable(text, -key);

    private static string ShiftPrintable(string text, int key)
    {
        var sb = new StringBuilder(text.Length);
        foreach (char ch in text)
            sb.Append(ch is >= ' ' and <= '~' ? (char)(' ' + ModMath.Mod(ch - ' ' + key, 95)) : ch);
        return sb.ToString();
    }
}

/// <summary>Ataque a César: fuerza bruta de todas las claves + prueba χ² contra las frecuencias del español.</summary>
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

    public sealed record Candidate(int Key, double ChiSquare, string Preview);

    /// <summary>χ² = Σ (Oᵢ − Eᵢ)² / Eᵢ sobre las letras del alfabeto.</summary>
    public static double ChiSquare(string text, string alphabet)
    {
        var counts = new Dictionary<char, int>();
        int n = 0;
        foreach (char ch in text.ToUpperInvariant())
            if (alphabet.Contains(ch)) { counts[ch] = counts.GetValueOrDefault(ch) + 1; n++; }
        if (n == 0) return double.MaxValue;
        double total = alphabet.Sum(c => Spanish[c]), chi = 0;
        foreach (char c in alphabet)
        {
            double e = n * Spanish[c] / total;
            double o = counts.GetValueOrDefault(c);
            chi += (o - e) * (o - e) / e;
        }
        return chi;
    }

    /// <summary>Prueba las n claves y las ordena de menor a mayor χ² (la primera es la más probable).</summary>
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
