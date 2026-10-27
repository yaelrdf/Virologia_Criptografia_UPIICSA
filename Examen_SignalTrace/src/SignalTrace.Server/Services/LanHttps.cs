using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using SignalTrace.Core;

namespace SignalTrace.Server.Services;

/// <summary>
/// HTTPS for the local network (Https:Enabled). Phones need a secure context for Web Crypto and the motion sensors, so
/// the server also listens on Https:Port with a self-signed ECDSA P-256 certificate created on first start
/// (data/lan-cert.pfx). Its SHA-256 travels in the enrollment QR: the Android app pins that exact certificate and the
/// browser shows a one-time warning.
/// </summary>
public sealed class LanHttps
{
    public bool Enabled { get; }
    public int Port { get; }
    public X509Certificate2? Certificate { get; }
    /// <summary>SHA-256 of the certificate (DER), lowercase hex.</summary>
    public string? CertSha256 { get; }

    public LanHttps(IConfiguration cfg, ServerPaths paths)
    {
        Enabled = cfg.GetValue("Https:Enabled", false);
        Port = cfg.GetValue("Https:Port", 5443);
        if (!Enabled) return;
        Certificate = LoadOrCreate(paths.File(cfg["Https:CertificatePath"] ?? "lan-cert.pfx"));
        CertSha256 = Bytes.ToHex(SHA256.HashData(Certificate.RawData));
    }

    /// <summary>https://{first LAN IPv4}:{port}, the address a phone on the same network can reach.</summary>
    public string? LanUrl => Enabled && LanAddresses().FirstOrDefault() is { } ip ? $"https://{ip}:{Port}" : null;

    public static IEnumerable<IPAddress> LanAddresses() =>
        NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
            // Interfaces with a default gateway first (the real LAN, not docker/virtual bridges).
            .OrderByDescending(n => n.GetIPProperties().GatewayAddresses.Any(g => !g.Address.Equals(IPAddress.Any)))
            .SelectMany(n => n.GetIPProperties().UnicastAddresses)
            .Select(a => a.Address)
            .Where(a => a.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(a));

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
        req.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        req.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
        req.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.1")], false));
        using var cert = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(2));
        File.WriteAllBytes(path, cert.Export(X509ContentType.Pfx));
        ServerPaths.TryRestrict(path);
        return X509CertificateLoader.LoadPkcs12FromFile(path, null);
    }
}
