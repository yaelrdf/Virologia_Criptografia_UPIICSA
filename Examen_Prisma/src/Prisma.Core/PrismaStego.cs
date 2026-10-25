using System.Buffers.Binary;
using System.Security.Cryptography;

namespace Prisma.Core;

/// <summary>Error genérico: no distingue "frase incorrecta", "imagen alterada" ni "no hay mensaje".</summary>
public sealed class StegoException(string message) : Exception(message)
{
    public const string Generic = "No se encontró un mensaje válido para esa frase (la frase es incorrecta, la imagen fue alterada o no contiene mensaje).";
}

/// <summary>
/// Capa esteganográfica de ABCO.
/// Sal (16 B) en los LSB de los canales 0..127; trama len(4) ‖ C ‖ HMAC[0..7] en los LSB
/// de un recorrido Fisher–Yates sembrado con la clave.
/// </summary>
public static class PrismaStego
{
    public const int SaltBytes = 16, SaltChannels = SaltBytes * 8, TagBytes = 8, LenBytes = 4;
    /// <summary>Máximo de canales que se aceptan al revelar (≈ 2560×2560).</summary>
    public const int MaxChannels = 3 * 6_600_000;

    /// <summary>Bytes de mensaje que caben en una imagen con ese número de canales.</summary>
    public static int Capacity(int channels)
    {
        int frameBytes = (channels - SaltChannels) / 8 - LenBytes - TagBytes;
        return Math.Max(0, frameBytes - frameBytes % 2);   // el cifrado ocupa un número par de bytes
    }

    public static RgbImage Embed(RgbImage cover, byte[] payload, string passphrase, PrismaTrace? trace = null)
    {
        var img = cover.Clone();
        var ch = img.Channels;
        if (payload.Length > Capacity(ch.Length))
            throw new InvalidOperationException($"El mensaje ({payload.Length} B) no cabe en la imagen (capacidad {Capacity(ch.Length)} B).");

        byte[] salt = RandomNumberGenerator.GetBytes(SaltBytes);
        var k = PrismaKeys.Derive(passphrase, salt);
        trace?.SetKeys(k, salt);
        byte[] c = PrismaCipher.Encrypt(payload, k, trace);
        byte[] frame = new byte[LenBytes + c.Length + TagBytes];
        BinaryPrimitives.WriteInt32BigEndian(frame, payload.Length);
        c.CopyTo(frame, LenBytes);
        HMACSHA256.HashData(k.KMac, c).AsSpan(0, TagBytes).CopyTo(frame.AsSpan(LenBytes + c.Length));

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

    public static byte[] Reveal(RgbImage img, string passphrase, PrismaTrace? trace = null)
    {
        var ch = img.Channels;
        if (ch.Length < SaltChannels + (LenBytes + TagBytes + 2) * 8) throw new StegoException(StegoException.Generic);
        if (ch.Length > MaxChannels) throw new InvalidDataException("La imagen es demasiado grande para revelar (máx. ≈ 2560×2560).");

        byte[] salt = ReadBits(ch, i => i, SaltBytes);
        var k = PrismaKeys.Derive(passphrase, salt);
        trace?.SetKeys(k, salt);
        int[] path = FisherYates.Path(ch.Length, k.Seed, SaltChannels);

        byte[] lenBytes = ReadBits(ch, j => path[j], LenBytes);
        int len = BinaryPrimitives.ReadInt32BigEndian(lenBytes);
        if (len < 0 || len > Capacity(ch.Length)) throw new StegoException(StegoException.Generic);
        int clen = len + len % 2;
        byte[] rest = ReadBits(ch, j => path[LenBytes * 8 + j], clen + TagBytes);
        byte[] c = rest[..clen], tag = rest[clen..];
        byte[] expected = HMACSHA256.HashData(k.KMac, c).AsSpan(0, TagBytes).ToArray();
        if (!CryptographicOperations.FixedTimeEquals(tag, expected)) throw new StegoException(StegoException.Generic);

        if (trace is not null)
        {
            trace.FirstPositions = [.. path.Take(20)];
            trace.PayloadBytes = len;
            trace.CapacityBytes = Capacity(ch.Length);
        }
        return PrismaCipher.Decrypt(c, k, trace)[..len];
    }

    public static int Bit(byte[] data, int i) => (data[i >> 3] >> (7 - (i & 7))) & 1;   // MSB primero
    public static void SetLsb(byte[] ch, int index, int bit) => ch[index] = (byte)((ch[index] & 0xFE) | bit);

    public static byte[] ReadBits(byte[] ch, Func<int, int> position, int nBytes)
    {
        var r = new byte[nBytes];
        for (int j = 0; j < nBytes * 8; j++)
            r[j >> 3] |= (byte)((ch[position(j)] & 1) << (7 - (j & 7)));
        return r;
    }
}

/// <summary>Esteganografía clásica: bits menos significativos en orden y sin clave. Cualquiera puede leerla.</summary>
public static class LsbClassic
{
    public static int Capacity(int channels) => Math.Max(0, channels / 8 - 4);

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

    public static byte[] Extract(RgbImage img)
    {
        var ch = img.Channels;
        if (ch.Length < 32) throw new StegoException("La imagen es demasiado pequeña.");
        int len = BinaryPrimitives.ReadInt32BigEndian(PrismaStego.ReadBits(ch, j => j, 4));
        if (len < 0 || len > Capacity(ch.Length))
            throw new StegoException("La lectura secuencial de los bits menos significativos no produce un mensaje legible (no hay mensaje clásico o está protegido).");
        return PrismaStego.ReadBits(ch, j => 32 + j, len);
    }

    /// <summary>Lo que "ve" un atacante que lee los LSB en orden: los primeros n bytes, sin interpretar.</summary>
    public static byte[] RawSequential(RgbImage img, int nBytes) =>
        PrismaStego.ReadBits(img.Channels, j => j, Math.Min(nBytes, img.Channels.Length / 8));
}
