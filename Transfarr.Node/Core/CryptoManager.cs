using System;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Logging;

namespace Transfarr.Node.Core;

/// <summary>
/// Manages cryptographic material for the Node, particularly the ephemeral
/// self-signed certificate used for P2P End-to-End Encryption.
/// </summary>
public class CryptoManager
{
    private readonly ILogger<CryptoManager> logger;

    public X509Certificate2 NodeCertificate { get; }
    public string CertificateThumbprint { get; }

    public CryptoManager(ILogger<CryptoManager> logger)
    {
        this.logger = logger;
        this.NodeCertificate = GenerateEphemeralCertificate();
        this.CertificateThumbprint = this.NodeCertificate.GetCertHashString();
        
        logger.LogInformation("Generated ephemeral P2P certificate. Thumbprint: {Thumbprint}", this.CertificateThumbprint);
    }

    private X509Certificate2 GenerateEphemeralCertificate()
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("cn=TransfarrP2PPeer", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);

        // Enhance certificate with basic constraints and key usages appropriate for a TLS endpoint
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, false));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, false));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection
        {
            new Oid("1.3.6.1.5.5.7.3.1"), // Server Authentication
            new Oid("1.3.6.1.5.5.7.3.2")  // Client Authentication
        }, false));

        // Create the certificate valid from yesterday (to avoid timezone clock skew issues) to 30 days ahead
        var cert = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30));

        // Export and re-import to ensure private key is accessible by SslStream (Windows SChannel does not support EphemeralKeySet for Server authentication)
        var export = cert.Export(X509ContentType.Pfx);
        return X509CertificateLoader.LoadPkcs12(export, null, X509KeyStorageFlags.Exportable);
    }

    /// <summary>
    /// Utility method to determine if an IP address is belonging to a private network block.
    /// In IPv4, this checks against 10.0.0.0/8, 172.16.0.0/12, 192.168.0.0/16, and Loopback.
    /// In IPv6, it checks against Loopback, Unique Local Address (fc00::/7) and Link Local.
    /// </summary>
    public static bool IsPrivateOrLocalIp(System.Net.IPAddress ip)
    {
        if (System.Net.IPAddress.IsLoopback(ip)) return true;

        var bytes = ip.GetAddressBytes();
        
        if (ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
        {
            // 10.x.x.x
            if (bytes[0] == 10) return true;
            // 172.16.x.x to 172.31.x.x
            if (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31) return true;
            // 192.168.x.x
            if (bytes[0] == 192 && bytes[1] == 168) return true;
            // APIPA / Link Local
            if (bytes[0] == 169 && bytes[1] == 254) return true;
        }
        else if (ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6)
        {
            if (ip.IsIPv6LinkLocal || ip.IsIPv6SiteLocal) return true;
            
            // Unique Local Address fc00::/7 (fc00...fdff)
            if (bytes[0] == 0xfc || bytes[0] == 0xfd) return true;
        }

        return false;
    }
}
