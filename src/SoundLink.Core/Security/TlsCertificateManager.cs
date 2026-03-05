using System;
using System.IO;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace SoundLink.Core.Security;

/// <summary>
/// Manages a self-signed TLS certificate for secure communication.
/// Certificate is generated on first use and persisted to disk.
/// Also manages pairing PINs.
/// </summary>
public sealed class TlsCertificateManager
{
    private const string CertFileName = "soundlink.pfx";
    private readonly string _certPath;
    private X509Certificate2? _certificate;

    public X509Certificate2 Certificate => _certificate ?? throw new InvalidOperationException("Certificate not loaded.");

    public TlsCertificateManager(string? dataDirectory = null)
    {
        var dir = dataDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "SoundLink");
        Directory.CreateDirectory(dir);
        _certPath = Path.Combine(dir, CertFileName);
    }

    /// <summary>
    /// Loads or creates the TLS certificate.
    /// </summary>
    public X509Certificate2 GetOrCreateCertificate()
    {
        if (_certificate != null) return _certificate;

        if (File.Exists(_certPath))
        {
            try
            {
                _certificate = new X509Certificate2(_certPath);
                if (_certificate.NotAfter > DateTime.UtcNow)
                    return _certificate;
                // Expired — regenerate
                _certificate.Dispose();
                _certificate = null;
            }
            catch
            {
                // Corrupt — regenerate
            }
        }

        _certificate = GenerateSelfSignedCert();
        File.WriteAllBytes(_certPath, _certificate.Export(X509ContentType.Pfx));
        return _certificate;
    }

    private static X509Certificate2 GenerateSelfSignedCert()
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest(
            "CN=SoundLink Server",
            rsa,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);

        // Add SAN for localhost
        var sanBuilder = new SubjectAlternativeNameBuilder();
        sanBuilder.AddDnsName("localhost");
        sanBuilder.AddDnsName(Environment.MachineName);
        request.CertificateExtensions.Add(sanBuilder.Build());

        var cert = request.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddDays(-1),
            DateTimeOffset.UtcNow.AddYears(5));

        // On Windows, we need to export and re-import to get a usable cert with private key
        return new X509Certificate2(
            cert.Export(X509ContentType.Pfx),
            (string?)null,
            X509KeyStorageFlags.Exportable | X509KeyStorageFlags.PersistKeySet);
    }

    /// <summary>
    /// Generates a random 6-digit PIN for device pairing.
    /// </summary>
    public static string GeneratePairingPin()
    {
        return RandomNumberGenerator.GetInt32(100000, 999999).ToString();
    }
}
