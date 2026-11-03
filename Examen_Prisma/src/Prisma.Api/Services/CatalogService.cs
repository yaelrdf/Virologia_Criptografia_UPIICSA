// CatalogService.cs — Catálogo de portadoras predefinidas, generado por código.
// Dibuja seis imágenes de 640×480 con SkiaSharp (atardecer, océano, montañas, galaxia, mosaico y
// aurora), las guarda como PNG en data/catalog la primera vez y las sirve desde memoria. Los clientes
// las usan como portadoras (catalogId) y tools/Prisma.Metrics como imágenes de prueba.
#pragma warning disable CS0618 // SKPath.MoveTo/LineTo: suficientes para dibujar el catálogo
using Prisma.Core;
using SkiaSharp;

namespace Prisma.Api.Services;

/// <summary>Elemento del catálogo tal como lo devuelve GET /api/catalog.</summary>
/// <param name="Id">Identificador usado en <c>catalogId</c> y en la ruta /api/catalog/{id}.</param>
/// <param name="Name">Nombre visible.</param>
/// <param name="Width">Ancho en píxeles.</param>
/// <param name="Height">Alto en píxeles.</param>
/// <param name="PrismaCapacity">Bytes de mensaje que caben con ABCO.</param>
public sealed record CatalogItem(string Id, string Name, int Width, int Height, int PrismaCapacity);

/// <summary>
/// Catálogo de imágenes predefinidas. Se generan por código la primera vez (sin derechos de autor de terceros)
/// y llevan ruido suave para que se parezcan a una foto, lo que también favorece el ocultamiento.
/// </summary>
/// <remarks>
/// Las semillas de <see cref="Random"/> dependen solo del id, así que el catálogo es reproducible: borrar
/// la carpeta y reiniciar vuelve a generar las mismas imágenes (con la misma versión de SkiaSharp). Es singleton y solo lee su diccionario tras el
/// constructor, por lo que no necesita sincronización.
/// </remarks>
public sealed class CatalogService
{
    /// <summary>Tamaño fijo de las portadoras del catálogo.</summary>
    private const int W = 640, H = 480;
    /// <summary>Diseños disponibles: id, nombre visible y función de dibujo.</summary>
    private static readonly (string Id, string Name, Action<SKCanvas, Random> Draw)[] Designs =
    [
        ("atardecer", "Atardecer", DrawSunset),
        ("oceano", "Océano", DrawOcean),
        ("montanas", "Montañas", DrawMountains),
        ("galaxia", "Galaxia", DrawGalaxy),
        ("mosaico", "Mosaico", DrawMosaic),
        ("aurora", "Aurora", DrawAurora),
    ];

    /// <summary>Carpeta donde se guardan los PNG.</summary>
    private readonly string _dir;
    /// <summary>Bytes PNG de cada imagen, por id.</summary>
    private readonly Dictionary<string, byte[]> _png = [];
    /// <summary>Elementos del catálogo en el orden de <see cref="Designs"/>.</summary>
    public IReadOnlyList<CatalogItem> Items { get; }

    /// <summary>Carga el catálogo y genera las imágenes que falten.</summary>
    /// <param name="cfg">Configuración; <c>Catalog:Directory</c> cambia la carpeta (por defecto data/catalog).</param>
    /// <param name="env">Entorno, para resolver la ruta relativa a la raíz del proyecto.</param>
    public CatalogService(IConfiguration cfg, IWebHostEnvironment env)
    {
        _dir = Path.GetFullPath(Path.Combine(env.ContentRootPath, cfg["Catalog:Directory"] ?? "data/catalog"));
        Directory.CreateDirectory(_dir);
        var items = new List<CatalogItem>();
        foreach (var (id, name, draw) in Designs)
        {
            string file = Path.Combine(_dir, id + ".png");
            if (!File.Exists(file)) File.WriteAllBytes(file, Render(draw, id));
            _png[id] = File.ReadAllBytes(file);
            items.Add(new CatalogItem(id, name, W, H, PrismaStego.Capacity(W * H * 3)));
        }
        Items = items;
    }

    /// <summary>Bytes PNG de una imagen del catálogo.</summary>
    /// <param name="id">Identificador.</param>
    /// <returns>El PNG, o null si el id no existe.</returns>
    public byte[]? Png(string id) => _png.GetValueOrDefault(id);

    /// <summary>Dibuja un diseño, le agrega ruido y lo codifica como PNG.</summary>
    /// <param name="draw">Función de dibujo.</param>
    /// <param name="id">Id del diseño, del que salen las semillas.</param>
    /// <returns>PNG final.</returns>
    /// <remarks>
    /// El ruido uniforme de ±5 por canal hace que los bits menos significativos se vean naturalmente
    /// aleatorios, como en una foto real. En una imagen sintética lisa, cualquier cambio de esos bits
    /// resaltaría en el estegoanálisis.
    /// </remarks>
    private static byte[] Render(Action<SKCanvas, Random> draw, string id)
    {
        using var bmp = new SKBitmap(new SKImageInfo(W, H, SKColorType.Rgba8888, SKAlphaType.Opaque));
        using (var canvas = new SKCanvas(bmp)) draw(canvas, new Random(id.Sum(ch => ch) * 7919));
        using var data = bmp.Encode(SKEncodedImageFormat.Png, 100);
        var img = ImageIO.Decode(data.ToArray());
        var rng = new Random(id.Length * 31 + id[0]);
        for (int i = 0; i < img.Channels.Length; i++)
            img.Channels[i] = (byte)Math.Clamp(img.Channels[i] + rng.Next(-5, 6), 0, 255);
        return ImageIO.EncodePng(img);
    }

    /// <summary>Pincel de relleno con suavizado, con sombreador (degradado) o color sólido.</summary>
    private static SKPaint Fill(SKShader? shader = null, SKColor? color = null) =>
        new() { IsAntialias = true, Shader = shader, Color = color ?? SKColors.White };

    /// <summary>Rellena todo el lienzo con un degradado vertical entre los colores dados.</summary>
    private static void Vertical(SKCanvas c, params SKColor[] colors)
    {
        using var p = Fill(SKShader.CreateLinearGradient(new SKPoint(0, 0), new SKPoint(0, H), colors, SKShaderTileMode.Clamp));
        c.DrawRect(0, 0, W, H, p);
    }

    /// <summary>Cielo degradado, sol con halo radial, mar oscuro y reflejos horizontales.</summary>
    private static void DrawSunset(SKCanvas c, Random r)
    {
        Vertical(c, new SKColor(40, 24, 90), new SKColor(220, 80, 90), new SKColor(255, 170, 60));
        using var sun = Fill(SKShader.CreateRadialGradient(new SKPoint(320, 330), 90, [new SKColor(255, 240, 160), new SKColor(255, 170, 60, 0)], SKShaderTileMode.Clamp));
        c.DrawCircle(320, 330, 90, sun);
        using var sea = Fill(SKShader.CreateLinearGradient(new SKPoint(0, 360), new SKPoint(0, H), [new SKColor(70, 40, 90), new SKColor(20, 15, 40)], SKShaderTileMode.Clamp));
        c.DrawRect(0, 360, W, H - 360, sea);
        using var glint = Fill(color: new SKColor(255, 200, 120, 90));
        for (int i = 0; i < 40; i++) c.DrawRect(320 - r.Next(10, 90), 365 + i * 3, r.Next(20, 180), 1.5f, glint);
    }

    /// <summary>Degradado azul con nueve olas senoidales semitransparentes.</summary>
    private static void DrawOcean(SKCanvas c, Random r)
    {
        Vertical(c, new SKColor(150, 210, 240), new SKColor(20, 90, 150), new SKColor(5, 30, 70));
        for (int k = 0; k < 9; k++)
        {
            using var path = new SKPath();
            // Cada ola baja 40 px, crece en amplitud y tiene una fase aleatoria.
            float y0 = 140 + k * 40, amp = 6 + k * 2, ph = (float)r.NextDouble() * 6;
            path.MoveTo(0, y0);
            for (int x = 0; x <= W; x += 8) path.LineTo(x, y0 + amp * MathF.Sin(x / 40f + ph));
            using var stroke = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 3, Color = new SKColor(255, 255, 255, (byte)(40 + k * 10)) };
            c.DrawPath(path, stroke);
        }
    }

    /// <summary>Cuatro cordilleras de picos aleatorios, más oscuras cuanto más cerca.</summary>
    private static void DrawMountains(SKCanvas c, Random r)
    {
        Vertical(c, new SKColor(255, 220, 180), new SKColor(140, 180, 220));
        SKColor[] layers = [new(110, 120, 160), new(70, 90, 130), new(40, 60, 90), new(20, 35, 55)];
        for (int l = 0; l < layers.Length; l++)
        {
            using var path = new SKPath();
            float baseY = 220 + l * 60;
            path.MoveTo(0, H);
            for (int x = 0; x <= W; x += 40) path.LineTo(x, baseY - r.Next(20, 120 - l * 15));
            path.LineTo(W, H); path.Close();
            using var p = Fill(color: layers[l]);
            c.DrawPath(path, p);
        }
    }

    /// <summary>Fondo radial y 900 estrellas distribuidas en una espiral achatada.</summary>
    private static void DrawGalaxy(SKCanvas c, Random r)
    {
        using var bg = Fill(SKShader.CreateRadialGradient(new SKPoint(320, 240), 420, [new SKColor(90, 40, 120), new SKColor(10, 8, 30)], SKShaderTileMode.Clamp));
        c.DrawRect(0, 0, W, H, bg);
        for (int i = 0; i < 900; i++)
        {
            // Radio con √u para repartir las estrellas por área; el ángulo crece con el radio (brazos en espiral)
            // y el factor 0.55 en y inclina el disco.
            double a = r.NextDouble() * Math.PI * 6, d = Math.Sqrt(r.NextDouble()) * 260;
            float x = 320 + (float)(Math.Cos(a + d / 60) * d), y = 240 + (float)(Math.Sin(a + d / 60) * d * 0.55);
            using var star = Fill(color: new SKColor(255, (byte)r.Next(200, 256), (byte)r.Next(180, 256), (byte)r.Next(80, 256)));
            c.DrawCircle(x, y, (float)(r.NextDouble() * 1.6 + 0.3), star);
        }
    }

    /// <summary>Cuadrícula de mosaicos redondeados de 40 px con colores de una paleta fija.</summary>
    private static void DrawMosaic(SKCanvas c, Random r)
    {
        SKColor[] palette = [new(231, 111, 81), new(244, 162, 97), new(233, 196, 106), new(42, 157, 143), new(38, 70, 83), new(241, 250, 238)];
        for (int y = 0; y < H; y += 40)
            for (int x = 0; x < W; x += 40)
            {
                using var p = Fill(color: palette[r.Next(palette.Length)]);
                c.DrawRoundRect(x + 2, y + 2, 36, 36, 6, 6, p);
            }
    }

    /// <summary>Cielo nocturno con cinco franjas senoidales difuminadas en verde y azul.</summary>
    private static void DrawAurora(SKCanvas c, Random r)
    {
        Vertical(c, new SKColor(5, 10, 30), new SKColor(10, 30, 50));
        for (int k = 0; k < 5; k++)
        {
            using var path = new SKPath();
            float y0 = 120 + k * 35, ph = (float)r.NextDouble() * 6;
            path.MoveTo(0, y0);
            for (int x = 0; x <= W; x += 10) path.LineTo(x, y0 + 40 * MathF.Sin(x / 90f + ph));
            using var p = new SKPaint
            {
                IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 30,
                Color = k % 2 == 0 ? new SKColor(80, 255, 170, 70) : new SKColor(90, 180, 255, 60),
                MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, 14),
            };
            c.DrawPath(path, p);
        }
    }
}
