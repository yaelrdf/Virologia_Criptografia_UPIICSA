// PrismaStego.cs — Esteganografía del sistema ABCO.
// Contiene la capa esteganográfica de ABCO (sal + trama cifrada y autenticada, escrita en el bit
// menos significativo siguiendo un recorrido secreto) y la esteganografía clásica (bits en orden,
// sin clave), que sirve como referencia y como algoritmo clásico del proyecto.
using System.Buffers.Binary;
using System.Security.Cryptography;

namespace Prisma.Core;

/// <summary>Error genérico: no distingue "frase incorrecta", "imagen alterada" ni "no hay mensaje".</summary>
/// <remarks>
/// Usar un único mensaje evita que un atacante obtenga un oráculo: si el sistema dijera cuál de los
/// tres casos ocurrió, podría saber si una imagen contiene algo o afinar ataques contra la frase.
/// </remarks>
/// <param name="message">Mensaje que se mostrará al usuario.</param>
public sealed class StegoException(string message) : Exception(message)
{
    /// <summary>Texto del error genérico de ABCO.</summary>
    public const string Generic = "No se encontró un mensaje válido para esa frase (la frase es incorrecta, la imagen fue alterada o no contiene mensaje).";
}

/// <summary>
/// Capa esteganográfica de ABCO.
/// Sal (16 B) en el bit menos significativo de los canales 0..127; trama len(4) ‖ C ‖ HMAC[0..7] en el
/// bit menos significativo de los canales de un recorrido Fisher–Yates sembrado con la clave.
/// </summary>
/// <remarks>
/// La sal va en una posición fija porque hace falta para derivar las claves (y con ellas el recorrido);
/// no es secreta. La longitud (4 B, big-endian) va en claro dentro de la trama, pero solo es legible
/// siguiendo el recorrido. El HMAC truncado a 8 B autentica el cifrado (cifrar y luego autenticar):
/// detecta frases incorrectas y alteraciones antes de descifrar.
/// </remarks>
public static class PrismaStego
{
    /// <summary>Tamaños de la trama: sal (16 B = 128 canales), etiqueta HMAC (8 B) y longitud (4 B).</summary>
    public const int SaltBytes = 16, SaltChannels = SaltBytes * 8, TagBytes = 8, LenBytes = 4;
    /// <summary>Máximo de canales que se aceptan al revelar (≈ 2560×2560).</summary>
    /// <remarks>Limita la memoria y el tiempo del recorrido (un <c>int</c> por canal) ante imágenes enormes enviadas a la API.</remarks>
    public const int MaxChannels = 3 * 6_600_000;

    /// <summary>Bytes de mensaje que caben en una imagen con ese número de canales.</summary>
    /// <param name="channels">Número de canales de la portadora (ancho × alto × 3).</param>
    /// <returns>Capacidad útil en bytes (par y no negativa), descontando sal, longitud y etiqueta.</returns>
    public static int Capacity(int channels)
    {
        // Cada canal guarda 1 bit: (canales − 128 de la sal) / 8 bytes de trama, menos longitud y etiqueta.
        int frameBytes = (channels - SaltChannels) / 8 - LenBytes - TagBytes;
        return Math.Max(0, frameBytes - frameBytes % 2);   // el cifrado ocupa un número par de bytes
    }

    /// <summary>Oculta un mensaje en una copia de la portadora con ABCO.</summary>
    /// <param name="cover">Portadora; no se modifica.</param>
    /// <param name="payload">Mensaje en bytes.</param>
    /// <param name="passphrase">Frase de la que se derivan las claves.</param>
    /// <param name="trace">Traza opcional para la consola admin (claves, bloques, recorrido y PSNR).</param>
    /// <returns>Imagen estego; debe guardarse sin pérdida (PNG) para conservar los bits.</returns>
    /// <exception cref="InvalidOperationException">Si el mensaje excede <see cref="Capacity"/>.</exception>
    public static RgbImage Embed(RgbImage cover, byte[] payload, string passphrase, PrismaTrace? trace = null)
    {
        var img = cover.Clone();
        var ch = img.Channels;
        if (payload.Length > Capacity(ch.Length))
            throw new InvalidOperationException($"El mensaje ({payload.Length} B) no cabe en la imagen (capacidad {Capacity(ch.Length)} B).");

        // Sal nueva en cada ocultamiento (CSPRNG): la misma frase y el mismo mensaje dan imágenes distintas.
        byte[] salt = RandomNumberGenerator.GetBytes(SaltBytes);
        var k = PrismaKeys.Derive(passphrase, salt);
        trace?.SetKeys(k, salt);
        byte[] c = PrismaCipher.Encrypt(payload, k, trace);
        // Trama: longitud real del mensaje (sin relleno) ‖ cifrado ‖ primeros 8 B de HMAC-SHA256(KMac, cifrado).
        byte[] frame = new byte[LenBytes + c.Length + TagBytes];
        BinaryPrimitives.WriteInt32BigEndian(frame, payload.Length);
        c.CopyTo(frame, LenBytes);
        HMACSHA256.HashData(k.KMac, c).AsSpan(0, TagBytes).CopyTo(frame.AsSpan(LenBytes + c.Length));

        // La sal se escribe en orden en los primeros 128 canales; la trama, en el orden del recorrido.
        for (int i = 0; i < SaltChannels; i++) SetLsb(ch, i, Bit(salt, i));
        int[] path = FisherYates.Path(ch.Length, k.Seed, SaltChannels);
        for (int j = 0; j < frame.Length * 8; j++) SetLsb(ch, path[j], Bit(frame, j));

        if (trace is not null)
        {
            trace.FirstPositions = [.. path.Take(20)];
            trace.PayloadBytes = payload.Length;
            trace.CapacityBytes = Capacity(ch.Length);
            trace.Psnr = Metrics.Psnr(cover.Channels, ch);
        }
        return img;
    }

    /// <summary>Revela el mensaje oculto con ABCO.</summary>
    /// <param name="img">Imagen estego (decodificada sin pérdida).</param>
    /// <param name="passphrase">Frase usada al ocultar.</param>
    /// <param name="trace">Traza opcional para la consola admin.</param>
    /// <returns>El mensaje original, sin el byte de relleno.</returns>
    /// <exception cref="StegoException">Error genérico: frase incorrecta, imagen alterada, sin mensaje o imagen demasiado pequeña.</exception>
    /// <exception cref="InvalidDataException">Si la imagen supera <see cref="MaxChannels"/>.</exception>
    public static byte[] Reveal(RgbImage img, string passphrase, PrismaTrace? trace = null)
    {
        var ch = img.Channels;
        // Mínimo: la sal más una trama con un bloque de 2 bytes.
        if (ch.Length < SaltChannels + (LenBytes + TagBytes + 2) * 8) throw new StegoException(StegoException.Generic);
        if (ch.Length > MaxChannels) throw new InvalidDataException("La imagen es demasiado grande para revelar (máx. ≈ 2560×2560).");

        byte[] salt = ReadBits(ch, i => i, SaltBytes);
        var k = PrismaKeys.Derive(passphrase, salt);
        trace?.SetKeys(k, salt);
        int[] path = FisherYates.Path(ch.Length, k.Seed, SaltChannels);

        byte[] lenBytes = ReadBits(ch, j => path[j], LenBytes);
        int len = BinaryPrimitives.ReadInt32BigEndian(lenBytes);
        // Con una frase incorrecta el recorrido es otro y la longitud leída es basura: se valida antes de
        // usarla para no leer fuera de la imagen ni reservar memoria desmedida.
        if (len < 0 || len > Capacity(ch.Length)) throw new StegoException(StegoException.Generic);
        int clen = len + len % 2;   // longitud del cifrado: la del mensaje redondeada a par
        byte[] rest = ReadBits(ch, j => path[LenBytes * 8 + j], clen + TagBytes);
        byte[] c = rest[..clen], tag = rest[clen..];
        byte[] expected = HMACSHA256.HashData(k.KMac, c).AsSpan(0, TagBytes).ToArray();
        // Comparación en tiempo constante: el tiempo de respuesta no revela cuántos bytes de la etiqueta coinciden.
        // Si la etiqueta no coincide no se descifra nada y se responde con el mismo error genérico.
        if (!CryptographicOperations.FixedTimeEquals(tag, expected)) throw new StegoException(StegoException.Generic);

        if (trace is not null)
        {
            trace.FirstPositions = [.. path.Take(20)];
            trace.PayloadBytes = len;
            trace.CapacityBytes = Capacity(ch.Length);
        }
        return PrismaCipher.Decrypt(c, k, trace)[..len];
    }

    /// <summary>Bit i de un arreglo de bytes, contando desde el bit más significativo de cada byte.</summary>
    /// <param name="data">Bytes de origen.</param>
    /// <param name="i">Índice global del bit (byte i/8, posición i mod 8).</param>
    /// <returns>0 o 1.</returns>
    public static int Bit(byte[] data, int i) => (data[i >> 3] >> (7 - (i & 7))) & 1;   // bit más significativo primero
    /// <summary>Reemplaza el bit menos significativo del canal <paramref name="index"/> por <paramref name="bit"/>.</summary>
    /// <param name="ch">Canales de la imagen.</param>
    /// <param name="index">Índice del canal.</param>
    /// <param name="bit">Bit a escribir (0 o 1).</param>
    public static void SetLsb(byte[] ch, int index, int bit) => ch[index] = (byte)((ch[index] & 0xFE) | bit);

    /// <summary>Lee <paramref name="nBytes"/> bytes del bit menos significativo de los canales indicados por <paramref name="position"/>.</summary>
    /// <param name="ch">Canales de la imagen.</param>
    /// <param name="position">Función que da el canal del j-ésimo bit (identidad para lectura en orden, o el recorrido).</param>
    /// <param name="nBytes">Bytes a leer.</param>
    /// <returns>Los bytes reconstruidos, con el bit más significativo primero (el mismo orden que <see cref="Bit"/>).</returns>
    public static byte[] ReadBits(byte[] ch, Func<int, int> position, int nBytes)
    {
        var r = new byte[nBytes];
        for (int j = 0; j < nBytes * 8; j++)
            r[j >> 3] |= (byte)((ch[position(j)] & 1) << (7 - (j & 7)));
        return r;
    }
}

/// <summary>Esteganografía clásica: bits menos significativos en orden y sin clave. Cualquiera puede leerla.</summary>
/// <remarks>
/// Trama: longitud (4 B, big-endian) ‖ mensaje, escrita desde el canal 0 sin cifrar. Se incluye como
/// algoritmo clásico y como punto de comparación: muestra por qué ABCO cifra, autentica y dispersa los bits.
/// </remarks>
public static class LsbClassic
{
    /// <summary>Bytes de mensaje que caben: un bit por canal, menos los 4 B de la longitud.</summary>
    /// <param name="channels">Número de canales de la portadora.</param>
    /// <returns>Capacidad en bytes (no negativa).</returns>
    public static int Capacity(int channels) => Math.Max(0, channels / 8 - 4);

    /// <summary>Oculta el mensaje en orden en una copia de la portadora.</summary>
    /// <param name="cover">Portadora; no se modifica.</param>
    /// <param name="payload">Mensaje en bytes.</param>
    /// <returns>Imagen estego.</returns>
    /// <exception cref="InvalidOperationException">Si el mensaje no cabe.</exception>
    public static RgbImage Embed(RgbImage cover, byte[] payload)
    {
        var img = cover.Clone();
        if (payload.Length > Capacity(img.Channels.Length))
            throw new InvalidOperationException($"El mensaje ({payload.Length} B) no cabe en la imagen (capacidad {Capacity(img.Channels.Length)} B).");
        byte[] frame = new byte[4 + payload.Length];
        BinaryPrimitives.WriteInt32BigEndian(frame, payload.Length);
        payload.CopyTo(frame, 4);
        for (int j = 0; j < frame.Length * 8; j++) PrismaStego.SetLsb(img.Channels, j, PrismaStego.Bit(frame, j));
        return img;
    }

    /// <summary>Extrae un mensaje oculto con la esteganografía clásica.</summary>
    /// <param name="img">Imagen estego.</param>
    /// <returns>El mensaje en bytes.</returns>
    /// <exception cref="StegoException">Si la imagen es muy pequeña o la longitud leída no es válida (no hay mensaje clásico).</exception>
    public static byte[] Extract(RgbImage img)
    {
        var ch = img.Channels;
        if (ch.Length < 32) throw new StegoException("La imagen es demasiado pequeña.");
        int len = BinaryPrimitives.ReadInt32BigEndian(PrismaStego.ReadBits(ch, j => j, 4));
        // Sin HMAC, la única validación posible es que la longitud sea coherente con la imagen.
        if (len < 0 || len > Capacity(ch.Length))
            throw new StegoException("La lectura secuencial de los bits menos significativos no produce un mensaje legible (no hay mensaje clásico o está protegido).");
        return PrismaStego.ReadBits(ch, j => 32 + j, len);
    }

    /// <summary>Lo que "ve" un atacante que lee los bits menos significativos en orden: los primeros n bytes, sin interpretar.</summary>
    /// <param name="img">Imagen a inspeccionar.</param>
    /// <param name="nBytes">Bytes a leer (se limita a lo que cabe en la imagen).</param>
    /// <returns>Bytes crudos; en una imagen ABCO son la sal y bits dispersos sin sentido.</returns>
    public static byte[] RawSequential(RgbImage img, int nBytes) =>
        PrismaStego.ReadBits(img.Channels, j => j, Math.Min(nBytes, img.Channels.Length / 8));
}
