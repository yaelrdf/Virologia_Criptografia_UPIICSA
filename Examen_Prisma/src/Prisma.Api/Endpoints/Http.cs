using System.Security.Claims;
using Prisma.Api.Services;
using Prisma.Core;

namespace Prisma.Api.Endpoints;

/// <summary>Utilidades comunes de las rutas.</summary>
public static class Http
{
    public const long MaxUploadBytes = 15 * 1024 * 1024;
    /// <summary>Lado máximo de una portadora: las fotos grandes se reducen antes de ocultar.</summary>
    public const int MaxCoverSide = 1280;

    public static IResult Error(int status, string message) => Results.Json(new { error = message }, statusCode: status);

    public static int UserId(this ClaimsPrincipal u) => int.Parse(u.FindFirstValue(ClaimTypes.NameIdentifier)!);

    /// <summary>Lee una imagen del formulario: archivo "image" o id "catalogId" del catálogo.</summary>
    public static async Task<(RgbImage? Image, IResult? Error)> ReadImageAsync(IFormCollection form, CatalogService catalog, string field = "image")
    {
        try
        {
            var file = form.Files.GetFile(field);
            if (file is { Length: > 0 })
            {
                if (file.Length > MaxUploadBytes) return (null, Error(413, "La imagen supera 15 MB."));
                using var ms = new MemoryStream();
                await file.CopyToAsync(ms);
                return (ImageIO.Decode(ms.ToArray()), null);
            }
            string? id = form["catalogId"];
            if (!string.IsNullOrEmpty(id))
                return catalog.Png(id) is { } png ? (ImageIO.Decode(png), null) : (null, Error(404, "La imagen del catálogo no existe."));
            return (null, Error(400, "Falta la imagen: envía un archivo o elige una del catálogo."));
        }
        catch (InvalidDataException ex)
        {
            return (null, Error(400, ex.Message));
        }
    }

    public static string Hex(byte[] data, int max = 48) =>
        data.Length <= max ? Convert.ToHexString(data) : Convert.ToHexString(data, 0, max) + "…";
}
