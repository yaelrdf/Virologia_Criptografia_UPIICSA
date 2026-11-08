using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using SignalTrace.Core;

namespace SignalTrace.Server.Services;

/// <summary>
/// HTTPS para la red local (Https:Enabled). Los teléfonos necesitan un contexto seguro para usar Web Crypto y los
/// sensores de movimiento, así que el servidor también escucha en Https:Port con un certificado ECDSA P-256
/// autofirmado que se crea en el primer arranque (data/lan-cert.pfx). Su SHA-256 viaja en el QR de vinculación:
/// la aplicación Android fija exactamente ese certificado y el navegador muestra una advertencia una sola vez.
/// </summary>
public sealed class LanHttps
{
    /// <summary>Si el HTTPS de la red local está activo.</summary>
    public bool Enabled { get; }
    /// <summary>Puerto HTTPS (5443 por defecto).</summary>
    public int Port { get; }
    /// <summary>Certificado que presenta Kestrel en ese puerto.</summary>
    public X509Certificate2? Certificate { get; }
    /// <summary>SHA-256 del certificado (DER) en hexadecimal minúsculo.</summary>
    public string? CertSha256 { get; }

    /// <summary>Dirección configurada a mano para anunciar, si la hay.</summary>
    private readonly string? _advertise;

    /// <summary>Lee la configuración y, si está activo, carga o crea el certificado.</summary>
    /// <param name="cfg">Configuración (Https:Enabled, Https:Port, Https:AdvertiseUrl, Https:CertificatePath).</param>
    /// <param name="paths">Rutas de la carpeta de datos.</param>
    public LanHttps(IConfiguration cfg, ServerPaths paths)
    {
        Enabled = cfg.GetValue("Https:Enabled", false);
        Port = cfg.GetValue("Https:Port", 5443);
        _advertise = cfg["Https:AdvertiseUrl"] is { Length: > 0 } a ? a.TrimEnd('/') : null;
        if (!Enabled) return;
        Certificate = LoadOrCreate(paths.File(cfg["Https:CertificatePath"] ?? "lan-cert.pfx"));
        CertSha256 = Bytes.ToHex(SHA256.HashData(Certificate.RawData));
    }

    /// <summary>
    /// La dirección que puede alcanzar un teléfono de la misma red: Https:AdvertiseUrl (por ejemplo, la IP del equipo
    /// cuando el servidor corre en un contenedor) o, si no, https://{primera IPv4 de la red local}:{puerto}.
    /// </summary>
    public string? LanUrl => !Enabled ? null : _advertise ?? (LanAddresses().FirstOrDefault() is { } ip ? $"https://{ip}:{Port}" : null);

    /// <summary>Direcciones IPv4 del equipo en interfaces activas, sin la de loopback.</summary>
    public static IEnumerable<IPAddress> LanAddresses() =>
        NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
            // Primero las interfaces con puerta de enlace (la red local real, no los puentes de Docker o virtuales).
            .OrderByDescending(n => n.GetIPProperties().GatewayAddresses.Any(g => !g.Address.Equals(IPAddress.Any)))
            .SelectMany(n => n.GetIPProperties().UnicastAddresses)
            .Select(a => a.Address)
            .Where(a => a.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(a));

    /// <summary>
    /// Carga el certificado del archivo o, si no existe, crea uno autofirmado válido por 2 años con nombres
    /// alternativos para localhost, el nombre del equipo y sus IP, y lo guarda con permisos restringidos.
    /// </summary>
    /// <param name="path">Ruta del archivo PFX.</param>
    private static X509Certificate2 LoadOrCreate(string path)
    {
        if (File.Exists(path)) return X509CertificateLoader.LoadPkcs12FromFile(path, null);
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var req = new CertificateRequest("CN=SignalTrace LAN", key, HashAlgorithmName.SHA256);
        var san = new SubjectAlternativeNameBuilder();
        san.AddDnsName("localhost");
        san.AddDnsName(Environment.MachineName);
        san.AddIpAddress(IPAddress.Loopback);
        foreach (var ip in LanAddresses()) san.AddIpAddress(ip);
        req.CertificateExtensions.Add(san.Build());
        // Certificado final (no de autoridad), solo para firmar, con el uso «autenticación de servidor TLS» (OID 1.3.6.1.5.5.7.3.1).
        req.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        req.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
        req.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.1")], false));
        // Empieza un día antes para tolerar relojes desfasados.
        using var cert = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(2));
        File.WriteAllBytes(path, cert.Export(X509ContentType.Pfx));
        ServerPaths.TryRestrict(path);
        return X509CertificateLoader.LoadPkcs12FromFile(path, null);
    }
}
