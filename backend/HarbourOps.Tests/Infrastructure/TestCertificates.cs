using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace HarbourOps.Tests.Infrastructure;

/// <summary>Throwaway certificates generated in code (no committed key material), like the host's Data Protection certificate.</summary>
public static class TestCertificates
{
    /// <summary>A new self-signed certificate with an RSA private key, as base64 PKCS#12 without a password.</summary>
    public static string Pkcs12Base64(int keySize = 2048)
    {
        using var rsa = RSA.Create(keySize);
        var request = new CertificateRequest("CN=HarbourOps test data protection", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));
        return Convert.ToBase64String(certificate.Export(X509ContentType.Pkcs12));
    }
}
