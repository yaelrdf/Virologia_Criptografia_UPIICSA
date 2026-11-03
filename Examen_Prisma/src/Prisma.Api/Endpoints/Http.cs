// Http.cs — Utilidades compartidas por las rutas de la API.
// Respuesta de error uniforme {"error"}, lectura del id de usuario desde el token, lectura de la
// portadora (archivo subido o imagen del catálogo) y formato hexadecimal recortado.
using System.Security.Claims;
using Prisma.Api.Services;
using Prisma.Core;

namespace Prisma.Api.Endpoints;

/// <summary>Utilidades comunes de las rutas.</summary>
public static class Http
{
    /// <summary>Tamaño máximo de un archivo de imagen subido (15 MB).</summary>
    public const long MaxUploadBytes = 15 * 1024 * 1024;
    /// <summary>Lado máximo de una portadora: las fotos grandes se reducen antes de ocultar.</summary>
    public const int MaxCoverSide = 1280;

    /// <summary>Respuesta de error con el formato del contrato: <c>{"error": "mensaje"}</c>.</summary>
    /// <param name="status">Código HTTP.</param>
    /// <param name="message">Mensaje en español para el usuario.</param>
    /// <returns>Resultado JSON con ese código.</returns>
    public static IResult Error(int status, string message) => Results.Json(new { error = message }, statusCode: status);

    /// <summary>Id del usuario autenticado, tomado del claim NameIdentifier del token.</summary>
    /// <param name="u">Usuario de la petición.</param>
    /// <returns>El id numérico.</returns>
    /// <remarks>Solo debe llamarse en rutas con <c>RequireAuthorization</c>, donde el claim siempre existe.</remarks>
    public static int UserId(this ClaimsPrincipal u) => int.Parse(u.FindFirstValue(ClaimTypes.NameIdentifier)!);

    /// <summary>Lee una imagen del formulario: archivo "image" o id "catalogId" del catálogo.</summary>
    /// <param name="form">Formulario multipart ya leído.</param>
    /// <param name="catalog">Catálogo de portadoras.</param>
    /// <param name="field">Nombre del campo de archivo.</param>
    /// <returns>
    /// La imagen y null, o null y la respuesta de error: 413 si el archivo supera 15 MB, 404 si el id del
    /// catálogo no existe, 400 si no se envió imagen o no se pudo decodificar.
    /// </returns>
    /// <remarks>Si llegan ambos campos, tiene prioridad el archivo subido.</remarks>
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
            // ImageIO.Decode lanza InvalidDataException con un mensaje apto para el usuario.
            return (null, Error(400, ex.Message));
        }
    }

    /// <summary>Bytes en hexadecimal, recortados a <paramref name="max"/> bytes con "…" al final.</summary>
    /// <param name="data">Bytes a mostrar.</param>
    /// <param name="max">Máximo de bytes que se convierten.</param>
    /// <returns>Cadena hexadecimal en mayúsculas.</returns>
    public static string Hex(byte[] data, int max = 48) =>
        data.Length <= max ? Convert.ToHexString(data) : Convert.ToHexString(data, 0, max) + "…";
}
