using System.Formats.Asn1;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Kautuka.NetworkAccessController;

/// <summary>
/// Dedicated Certificate Authority and SAN validator for the Windows Envoy TCP+mTLS listener (MF4 / C4).
/// Server certificate carries <c>DNS:kautuka-decision.internal</c> only (never an IP SAN).
/// Client certificate carries <c>DNS:nac-envoy</c> and is verified for both CA chain and exact SAN.
/// </summary>
public static class EnvoyMtlsCertificateAuthority
{
    public const string DefaultServerDnsSan = "kautuka-decision.internal";
    public const string DefaultClientDnsSan = "nac-envoy";

    private static readonly Oid serverAuthOid = new("1.3.6.1.5.5.7.3.1");
    private static readonly Oid clientAuthOid = new("1.3.6.1.5.5.7.3.2");

    public static (X509Certificate2 CaCert, X509Certificate2 ServerCert, X509Certificate2 ClientCert) CreateDedicatedBundle(
        string serverDnsSan = DefaultServerDnsSan,
        string clientDnsSan = DefaultClientDnsSan)
    {
        X509Certificate2 caCert = CreateCa("Kautuka NAC Dedicated Envoy CA");
        X509Certificate2 serverCert = IssueServerCertificate(caCert, serverDnsSan);
        X509Certificate2 clientCert = IssueClientCertificate(caCert, clientDnsSan);
        return (caCert, serverCert, clientCert);
    }

    public static X509Certificate2 CreateCa(string commonName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(commonName);
        using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var req = new CertificateRequest($"CN={commonName}", ecdsa, HashAlgorithmName.SHA256);
        req.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, true, 0, true));
        req.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
        req.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(req.PublicKey, false));

        DateTimeOffset now = DateTimeOffset.UtcNow;
        using X509Certificate2 ephemeral = req.CreateSelfSigned(now.AddMinutes(-5), now.AddYears(10));
        return ExportReloadable(ephemeral);
    }

    public static X509Certificate2 IssueServerCertificate(
        X509Certificate2 caCert,
        string dnsSan = DefaultServerDnsSan,
        bool includeForbiddenIpSan = false)
    {
        ArgumentNullException.ThrowIfNull(caCert);
        ArgumentException.ThrowIfNullOrWhiteSpace(dnsSan);

        using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var req = new CertificateRequest($"CN={dnsSan}", ecdsa, HashAlgorithmName.SHA256);
        req.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        req.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection { serverAuthOid }, true));
        req.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
        req.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(req.PublicKey, false));

        var san = new SubjectAlternativeNameBuilder();
        san.AddDnsName(dnsSan);
        if (includeForbiddenIpSan)
        {
            san.AddIpAddress(System.Net.IPAddress.Loopback);
        }

        req.CertificateExtensions.Add(san.Build());

        DateTimeOffset now = DateTimeOffset.UtcNow;
        Span<byte> serial = stackalloc byte[8];
        RandomNumberGenerator.Fill(serial);
        using X509Certificate2 signed = req.Create(caCert, now.AddMinutes(-5), now.AddDays(30), serial);
        using X509Certificate2 withKey = signed.CopyWithPrivateKey(ecdsa);
        return ExportReloadable(withKey);
    }

    public static X509Certificate2 IssueClientCertificate(
        X509Certificate2 caCert,
        string clientDnsSan = DefaultClientDnsSan)
    {
        ArgumentNullException.ThrowIfNull(caCert);
        ArgumentException.ThrowIfNullOrWhiteSpace(clientDnsSan);

        using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var req = new CertificateRequest($"CN={clientDnsSan}", ecdsa, HashAlgorithmName.SHA256);
        req.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        req.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection { clientAuthOid }, true));
        req.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
        req.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(req.PublicKey, false));

        var san = new SubjectAlternativeNameBuilder();
        san.AddDnsName(clientDnsSan);
        req.CertificateExtensions.Add(san.Build());

        DateTimeOffset now = DateTimeOffset.UtcNow;
        Span<byte> serial = stackalloc byte[8];
        RandomNumberGenerator.Fill(serial);
        using X509Certificate2 signed = req.Create(caCert, now.AddMinutes(-5), now.AddDays(30), serial);
        using X509Certificate2 withKey = signed.CopyWithPrivateKey(ecdsa);
        return ExportReloadable(withKey);
    }

    public static bool ValidateServerCertificateConfiguration(
        X509Certificate2? serverCert,
        X509Certificate2? caCert,
        string expectedServerSan = DefaultServerDnsSan)
    {
        if (serverCert is null || caCert is null || !serverCert.HasPrivateKey || string.IsNullOrWhiteSpace(expectedServerSan))
        {
            return false;
        }

        if (!ValidateCertificateChain(serverCert, caCert))
        {
            return false;
        }

        if (HasIpSubjectAlternativeName(serverCert))
        {
            return false;
        }

        return HasDnsSubjectAlternativeName(serverCert, expectedServerSan);
    }

    public static bool ValidateEnvoyClientCertificate(
        X509Certificate2? clientCert,
        X509Certificate2? caCert,
        string expectedClientSan)
    {
        if (clientCert is null || caCert is null || string.IsNullOrWhiteSpace(expectedClientSan))
        {
            return false;
        }

        DateTimeOffset now = DateTimeOffset.UtcNow;
        if (now < clientCert.NotBefore.ToUniversalTime() || now > clientCert.NotAfter.ToUniversalTime())
        {
            return false;
        }

        if (!HasDnsSubjectAlternativeName(clientCert, expectedClientSan))
        {
            return false;
        }

        return ValidateCertificateChain(clientCert, caCert);
    }

    public static bool ValidateCertificateChain(X509Certificate2 cert, X509Certificate2 caCert)
    {
        ArgumentNullException.ThrowIfNull(cert);
        ArgumentNullException.ThrowIfNull(caCert);

        using var chain = new X509Chain();
        chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        chain.ChainPolicy.CustomTrustStore.Add(caCert);
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        chain.ChainPolicy.VerificationFlags = X509VerificationFlags.AllowUnknownCertificateAuthority;
        if (!chain.Build(cert))
        {
            return false;
        }

        foreach (X509ChainElement element in chain.ChainElements)
        {
            if (string.Equals(element.Certificate.Thumbprint, caCert.Thumbprint, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    public static bool HasDnsSubjectAlternativeName(X509Certificate2 certificate, string expectedDnsName)
    {
        ArgumentNullException.ThrowIfNull(certificate);
        X509Extension? san = certificate.Extensions["2.5.29.17"];
        if (san is null)
        {
            return false;
        }

        try
        {
            var reader = new AsnReader(san.RawData, AsnEncodingRules.DER);
            AsnReader names = reader.ReadSequence();
            var dnsTag = new Asn1Tag(TagClass.ContextSpecific, 2);
            while (names.HasData)
            {
                Asn1Tag tag = names.PeekTag();
                if (tag.HasSameClassAndValue(dnsTag))
                {
                    string value = names.ReadCharacterString(UniversalTagNumber.IA5String, dnsTag);
                    if (string.Equals(value, expectedDnsName, StringComparison.Ordinal))
                    {
                        return true;
                    }
                }
                else
                {
                    _ = names.ReadEncodedValue();
                }
            }

            return false;
        }
        catch (AsnContentException)
        {
            return false;
        }
    }

    public static bool HasIpSubjectAlternativeName(X509Certificate2 certificate)
    {
        ArgumentNullException.ThrowIfNull(certificate);
        X509Extension? san = certificate.Extensions["2.5.29.17"];
        if (san is null)
        {
            return false;
        }

        try
        {
            var reader = new AsnReader(san.RawData, AsnEncodingRules.DER);
            AsnReader names = reader.ReadSequence();
            var ipTag = new Asn1Tag(TagClass.ContextSpecific, 7);
            while (names.HasData)
            {
                Asn1Tag tag = names.PeekTag();
                if (tag.HasSameClassAndValue(ipTag))
                {
                    return true;
                }

                _ = names.ReadEncodedValue();
            }

            return false;
        }
        catch (AsnContentException)
        {
            return true;
        }
    }

    private static X509Certificate2 ExportReloadable(X509Certificate2 cert)
    {
        byte[] pfx = cert.Export(X509ContentType.Pkcs12);
        X509KeyStorageFlags flags = OperatingSystem.IsWindows()
            ? X509KeyStorageFlags.Exportable | X509KeyStorageFlags.PersistKeySet
            : X509KeyStorageFlags.Exportable;
        return X509CertificateLoader.LoadPkcs12(pfx, null, flags);
    }
}
