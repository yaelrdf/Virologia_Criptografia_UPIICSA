// RgbImage.cs — Representación de imágenes y entrada/salida con SkiaSharp.
// Todos los algoritmos de esteganografía trabajan sobre RgbImage (canales RGB de 8 bits, sin alfa).
// ImageIO decodifica cualquier formato común sin alterar los valores de los píxeles y codifica
// siempre a PNG, porque un formato con pérdida destruiría los bits ocultos.
using System.Runtime.InteropServices;
using SkiaSharp;

namespace Prisma.Core;

/// <summary>Imagen normalizada a RGB de 8 bits: Channels = [R,G,B, R,G,B, ...] por filas.</summary>
/// <param name="width">Ancho en píxeles.</param>
/// <param name="height">Alto en píxeles.</param>
/// <param name="channels">Canales en orden por filas; longitud = ancho × alto × 3.</param>
public sealed class RgbImage(int width, int height, byte[] channels)
{
    /// <summary>Ancho en píxeles.</summary>
    public int Width { get; } = width;
    /// <summary>Alto en píxeles.</summary>
    public int Height { get; } = height;
    /// <summary>Canales R, G, B de cada píxel; cada uno guarda un bit de la trama en su bit menos significativo.</summary>
    public byte[] Channels { get; } = channels;

    /// <summary>Copia profunda: los algoritmos nunca modifican la portadora original.</summary>
    /// <returns>Una imagen nueva con su propio arreglo de canales.</returns>
    public RgbImage Clone() => new(Width, Height, (byte[])Channels.Clone());
}

/// <summary>Decodificación y codificación de imágenes con SkiaSharp.</summary>
public static class ImageIO
{
    /// <summary>Límite contra imágenes gigantes o "bombas de descompresión".</summary>
    /// <remarks>Se comprueba con las dimensiones del encabezado, antes de reservar memoria para los píxeles.</remarks>
    public const long MaxDecodePixels = 40_000_000;

    /// <summary>Decodifica PNG, JPEG, WebP, BMP o GIF a <see cref="RgbImage"/>.</summary>
    /// <param name="data">Bytes del archivo.</param>
    /// <returns>La imagen con los valores de píxel tal como están en el archivo; el canal alfa se descarta.</returns>
    /// <exception cref="InvalidDataException">Si no es una imagen válida, es demasiado grande o falla la decodificación.</exception>
    public static RgbImage Decode(byte[] data)
    {
        using var codec = SKCodec.Create(new SKMemoryStream(data))
            ?? throw new InvalidDataException("El archivo no es una imagen válida (PNG, JPEG, WebP, BMP o GIF).");
        var (w, h) = (codec.Info.Width, codec.Info.Height);
        if ((long)w * h > MaxDecodePixels) throw new InvalidDataException("La imagen es demasiado grande.");
        // Sin espacio de color destino: se leen los valores tal como están en el archivo (sin conversión).
        // Alfa no premultiplicado: premultiplicar alteraría los valores RGB (y sus bits menos significativos)
        // de los píxeles semitransparentes.
        var info = new SKImageInfo(w, h, SKColorType.Rgba8888, SKAlphaType.Unpremul);
        using var bmp = new SKBitmap(info);
        var result = codec.GetPixels(info, bmp.GetPixels());
        // IncompleteInput se tolera: el archivo está truncado, pero los píxeles leídos sirven.
        if (result is not (SKCodecResult.Success or SKCodecResult.IncompleteInput))
            throw new InvalidDataException($"No se pudo decodificar la imagen ({result}).");
        return FromBitmap(bmp);
    }

    /// <summary>Codifica a PNG (sin pérdida), el único formato que conserva los bits ocultos.</summary>
    /// <param name="img">Imagen a codificar.</param>
    /// <returns>Bytes del archivo PNG, opaco.</returns>
    public static byte[] EncodePng(RgbImage img)
    {
        using var bmp = ToBitmap(img);
        using var data = bmp.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    /// <summary>Reduce la imagen si su lado mayor excede maxSide (la salida estego siempre es un PNG nuevo).</summary>
    /// <param name="img">Imagen original.</param>
    /// <param name="maxSide">Lado máximo permitido en píxeles.</param>
    /// <returns>
    /// La misma instancia si ya cabe (la API usa <c>ReferenceEquals</c> para saber si se redujo), o una
    /// copia escalada con la misma proporción y remuestreo bicúbico Mitchell.
    /// </returns>
    /// <remarks>Solo debe aplicarse a portadoras antes de ocultar: reducir una imagen estego destruye el mensaje.</remarks>
    public static RgbImage FitWithin(RgbImage img, int maxSide)
    {
        int side = Math.Max(img.Width, img.Height);
        if (side <= maxSide) return img;
        double f = (double)maxSide / side;
        int w = Math.Max(1, (int)Math.Round(img.Width * f)), h = Math.Max(1, (int)Math.Round(img.Height * f));
        using var src = ToBitmap(img);
        using var dst = src.Resize(new SKImageInfo(w, h, SKColorType.Rgba8888, SKAlphaType.Opaque), new SKSamplingOptions(SKCubicResampler.Mitchell));
        return FromBitmap(dst);
    }

    /// <summary>Convierte un mapa de bits RGBA de Skia en canales RGB compactos.</summary>
    /// <remarks>Cada fila puede tener relleno (<c>RowBytes</c> ≥ ancho × 4), así que se indexa por fila.</remarks>
    private static RgbImage FromBitmap(SKBitmap bmp)
    {
        int w = bmp.Width, h = bmp.Height, rowBytes = bmp.RowBytes;
        var raw = new byte[rowBytes * h];
        Marshal.Copy(bmp.GetPixels(), raw, 0, raw.Length);
        var ch = new byte[w * h * 3];
        for (int y = 0, o = 0; y < h; y++)
        {
            int row = y * rowBytes;
            for (int x = 0; x < w; x++, o += 3)
            {
                int p = row + x * 4;
                ch[o] = raw[p]; ch[o + 1] = raw[p + 1]; ch[o + 2] = raw[p + 2];   // el alfa se descarta
            }
        }
        return new RgbImage(w, h, ch);
    }

    /// <summary>Convierte canales RGB en un mapa de bits RGBA opaco (alfa = 255) de Skia.</summary>
    /// <remarks>Con alfa opaco, codificar a PNG conserva exactamente los valores RGB.</remarks>
    private static SKBitmap ToBitmap(RgbImage img)
    {
        var bmp = new SKBitmap(new SKImageInfo(img.Width, img.Height, SKColorType.Rgba8888, SKAlphaType.Opaque));
        int rowBytes = bmp.RowBytes;
        var raw = new byte[rowBytes * img.Height];
        var ch = img.Channels;
        for (int y = 0, o = 0; y < img.Height; y++)
        {
            int row = y * rowBytes;
            for (int x = 0; x < img.Width; x++, o += 3)
            {
                int p = row + x * 4;
                raw[p] = ch[o]; raw[p + 1] = ch[o + 1]; raw[p + 2] = ch[o + 2]; raw[p + 3] = 255;
            }
        }
        Marshal.Copy(raw, 0, bmp.GetPixels(), raw.Length);
        return bmp;
    }
}
