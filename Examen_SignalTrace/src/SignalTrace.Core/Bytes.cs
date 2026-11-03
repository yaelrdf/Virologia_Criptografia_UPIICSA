using System.Buffers.Binary;
using System.Text;

namespace SignalTrace.Core;

/// <summary>
/// Utilidades pequeñas de bytes que comparten todos los componentes: conversión a y desde hexadecimal,
/// concatenación de arreglos y codificación de enteros en orden big-endian (el byte más significativo primero).
/// </summary>
public static class Bytes
{
    /// <summary>Convierte un bloque de bytes a texto hexadecimal en minúsculas (dos caracteres por byte).</summary>
    /// <param name="data">Bytes que se van a mostrar.</param>
    /// <returns>Cadena hexadecimal, por ejemplo <c>"0a1b"</c>.</returns>
    public static string ToHex(ReadOnlySpan<byte> data) => Convert.ToHexStringLower(data);

    /// <summary>Convierte texto hexadecimal (mayúsculas o minúsculas) de vuelta a bytes.</summary>
    /// <param name="hex">Cadena con un número par de dígitos hexadecimales.</param>
    /// <returns>Los bytes representados por la cadena.</returns>
    public static byte[] FromHex(string hex) => Convert.FromHexString(hex);

    /// <summary>Codifica una cadena en UTF-8, la codificación que usan el protocolo y las pruebas.</summary>
    /// <param name="s">Texto a codificar.</param>
    /// <returns>Los bytes UTF-8 del texto.</returns>
    public static byte[] Utf8(string s) => Encoding.UTF8.GetBytes(s);

    /// <summary>
    /// Escribe un entero sin signo de 64 bits en 8 bytes big-endian. Se usa para el número de secuencia
    /// y la marca de tiempo dentro del nonce y de los datos asociados (AAD).
    /// </summary>
    /// <param name="value">Valor a codificar.</param>
    /// <returns>Arreglo de 8 bytes con el valor en orden de red.</returns>
    public static byte[] U64Be(ulong value)
    {
        var b = new byte[8];
        BinaryPrimitives.WriteUInt64BigEndian(b, value);
        return b;
    }

    /// <summary>
    /// Une varios arreglos de bytes en uno solo, en el orden recibido. Primero suma las longitudes
    /// para reservar el arreglo final una sola vez y después copia cada parte en su posición.
    /// </summary>
    /// <param name="parts">Arreglos que se van a concatenar.</param>
    /// <returns>Un arreglo nuevo con todas las partes seguidas.</returns>
    public static byte[] Concat(params byte[][] parts)
    {
        // Longitud total del resultado.
        var total = 0;
        foreach (var p in parts) total += p.Length;
        var result = new byte[total];
        // Copia cada parte a continuación de la anterior.
        var offset = 0;
        foreach (var p in parts)
        {
            Buffer.BlockCopy(p, 0, result, offset, p.Length);
            offset += p.Length;
        }
        return result;
    }

    /// <summary>
    /// Texto del GUID de un dispositivo en minúsculas y con guiones (36 caracteres). Es la forma canónica
    /// que se usa en todas las concatenaciones (transcript del handshake, AAD), para que el servidor y los
    /// clientes produzcan exactamente los mismos bytes.
    /// </summary>
    /// <param name="id">Identificador del dispositivo.</param>
    /// <returns>El GUID en formato <c>"D"</c>, por ejemplo <c>"3f2504e0-4f89-11d3-9a0c-0305e82c3301"</c>.</returns>
    public static string DeviceIdText(Guid id) => id.ToString("D");
}
