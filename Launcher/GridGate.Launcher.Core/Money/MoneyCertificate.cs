using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace GridGate.Launcher.Core.Money;

/// <summary>
/// The money service's own certificate (money design §7.2), separate from the website's. Region programs pin its
/// fingerprint, so a self-signed certificate is enough. Kept as a PFX without a password in the owner-only security folder.
/// </summary>
public static class MoneyCertificate
{
    public const string FileName = "money.pfx";
    private static readonly TimeSpan RenewBefore = TimeSpan.FromDays(30);

    /// <summary>Loads the certificate, or makes a new one when the file is missing, unreadable or expires within 30 days.</summary>
    public static X509Certificate2 LoadOrCreate(string pfxPath, TimeProvider time)
    {
        var now = time.GetUtcNow();
        if (File.Exists(pfxPath))
        {
            try
            {
                var existing = X509CertificateLoader.LoadPkcs12FromFile(pfxPath, null);
                if (existing.HasPrivateKey && existing.NotAfter.ToUniversalTime() - now.UtcDateTime > RenewBefore)
                    return existing;
                existing.Dispose();
            }
            catch (CryptographicException)
            {
                // Unreadable: replaced below.
            }
        }

        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest("CN=GridGate money", key, HashAlgorithmName.SHA256);
        var san = new SubjectAlternativeNameBuilder();
        san.AddDnsName("localhost");
        san.AddIpAddress(IPAddress.Loopback);
        san.AddIpAddress(IPAddress.IPv6Loopback);
        request.CertificateExtensions.Add(san.Build());
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, critical: true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.1")], critical: false));
        using var made = request.CreateSelfSigned(now.AddHours(-1), now.AddYears(5));

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(pfxPath))!);
        var tmp = pfxPath + ".tmp";
        File.WriteAllBytes(tmp, made.Export(X509ContentType.Pfx));
        File.Move(tmp, pfxPath, overwrite: true);
        // Loaded back from the file: on Windows a key made in memory can't be used by the TLS stack.
        return X509CertificateLoader.LoadPkcs12FromFile(pfxPath, null);
    }

    /// <summary>The upper-case hex SHA-256 of the certificate's DER bytes: what region programs pin.</summary>
    public static string Fingerprint(X509Certificate2 c) => Convert.ToHexString(SHA256.HashData(c.RawData));
}
