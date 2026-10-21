using System.Runtime.InteropServices;
using SkiaSharp;

namespace Prisma.Core;

/// <summary>Imagen normalizada a RGB de 8 bits: Channels = [R,G,B, R,G,B, ...] por filas.</summary>
public sealed class RgbImage(int width, int height, byte[] channels)
{
    public int Width { get; } = width;
    public int Height { get; } = height;
    public byte[] Channels { get; } = channels;

    public RgbImage Clone() => new(Width, Height, (byte[])Channels.Clone());
}

public static class ImageIO
{
    /// <summary>Límite contra imágenes gigantes o "bombas de descompresión".</summary>
    public const long MaxDecodePixels = 40_000_000;

    public static RgbImage Decode(byte[] data)
    {
        using var codec = SKCodec.Create(new SKMemoryStream(data))
            ?? throw new InvalidDataException("El archivo no es una imagen válida (PNG, JPEG, WebP, BMP o GIF).");
        var (w, h) = (codec.Info.Width, codec.Info.Height);
        if ((long)w * h > MaxDecodePixels) throw new InvalidDataException("La imagen es demasiado grande.");
        // Sin espacio de color destino: se leen los valores tal como están en el archivo (sin conversión).
        var info = new SKImageInfo(w, h, SKColorType.Rgba8888, SKAlphaType.Unpremul);
        using var bmp = new SKBitmap(info);
        var result = codec.GetPixels(info, bmp.GetPixels());
        if (result is not (SKCodecResult.Success or SKCodecResult.IncompleteInput))
            throw new InvalidDataException($"No se pudo decodificar la imagen ({result}).");
        return FromBitmap(bmp);
    }

    public static byte[] EncodePng(RgbImage img)
    {
        using var bmp = ToBitmap(img);
        using var data = bmp.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    /// <summary>Reduce la imagen si su lado mayor excede maxSide (la salida estego siempre es un PNG nuevo).</summary>
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
