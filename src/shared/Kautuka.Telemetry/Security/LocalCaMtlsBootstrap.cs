using System.Formats.Asn1;
using System.Net;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Security.Principal;

namespace Kautuka.Telemetry.Security;

/// <summary>
/// Manages the local stable CA for mTLS between a hosted service and Gateway.
///
/// On first start (CA files absent):
///   - Generates ECDSA P-256 CA keypair.
///   - Persists CA private key to {dataRoot}/.ca.key (chmod 600).
///   - Persists CA public cert DER to {dataRoot}/.ca.cer (chmod 600).
///   - Sets directory {dataRoot}/ to chmod 700.
///
/// On every start:
///   - Loads CA keypair from disk.
///   - Generates in-memory leaf server cert signed by CA (never written to disk).
///   - Generates each configured in-memory client cert signed by CA.
///   - Writes client certs as unprotected PKCS12 files under {dataRoot} (chmod 600).
///
/// CA validity: 10 years. Leaf cert validity: 30 days.
/// Gateway trusts the stable CA cert and survives host restarts without restart.
/// </summary>
public static class LocalCaMtlsBootstrap
{
    public static readonly Uri GatewayChannelWorkloadIdentity = new("spiffe://kautuka.local/workload/gateway/channel-control", UriKind.Absolute);
    private const string caKeyFile = ".ca.key";
    private const string caCertFile = ".ca.cer";

    private static readonly Oid serverAuthOid = new("1.3.6.1.5.5.7.3.1");
    private static readonly Oid clientAuthOid = new("1.3.6.1.5.5.7.3.2");

    public static (X509Certificate2 ServerCert, X509Certificate2 CaCert) EnsureAndLoad(
        string dataRoot,
        string serviceName,
        string clientPfxFile)
        => EnsureAndLoad(
            dataRoot,
            serviceName,
            [new LocalMtlsClientCertificate("Kautuka Gateway Client", clientPfxFile, GatewayChannelWorkloadIdentity)]);

    public static (X509Certificate2 ServerCert, X509Certificate2 CaCert) EnsureAndLoad(
        string dataRoot,
        string serviceName,
        IReadOnlyList<LocalMtlsClientCertificate> clientCertificates)
    {
        ArgumentNullException.ThrowIfNull(clientCertificates);
        if (clientCertificates.Count == 0)
            throw new ArgumentException("At least one client certificate must be configured.", nameof(clientCertificates));

        // Ensure the directory exists and is restricted before any file writes.
        Directory.CreateDirectory(dataRoot);
        RestrictPrivateDirectory(dataRoot);

        string caKeyPath = Path.Combine(dataRoot, caKeyFile);
        string caCertPath = Path.Combine(dataRoot, caCertFile);
        X509Certificate2 caCert = EnsureCa(caKeyPath, caCertPath, serviceName);
        X509Certificate2 serverCert = IssueCert(caCert, $"{serviceName} Server", serverAuthOid,
            san =>
            {
                san.AddDnsName(serviceName);
                san.AddDnsName("localhost");
                san.AddDnsName("host.containers.internal");
                san.AddIpAddress(IPAddress.Loopback);
            });

        var seenFiles = new HashSet<string>(StringComparer.Ordinal);
        var seenSubjects = new HashSet<string>(StringComparer.Ordinal);
        foreach (LocalMtlsClientCertificate client in clientCertificates)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(client.SubjectCommonName);
            ArgumentException.ThrowIfNullOrWhiteSpace(client.PfxFileName);
            if (Path.GetFileName(client.PfxFileName) != client.PfxFileName)
                throw new ArgumentException("Client certificate file names must not contain a path.", nameof(clientCertificates));
            if (!seenFiles.Add(client.PfxFileName) || !seenSubjects.Add(client.SubjectCommonName))
                throw new ArgumentException("Client certificate subjects and file names must be unique.", nameof(clientCertificates));

            string pfxPath = Path.Combine(dataRoot, client.PfxFileName);
            if (File.Exists(pfxPath))
            {
                SetOwnerOnly(pfxPath);
                using X509Certificate2 existing = X509CertificateLoader.LoadPkcs12FromFile(pfxPath, null);
                bool identityMatches = string.Equals(
                        existing.GetNameInfo(X509NameType.SimpleName, false),
                        client.SubjectCommonName,
                        StringComparison.Ordinal)
                    && (client.WorkloadIdentityUri is null
                        || HasUriSubjectAlternativeName(existing, client.WorkloadIdentityUri.AbsoluteUri));
                if (identityMatches
                    && existing.NotAfter.ToUniversalTime() > DateTime.UtcNow.AddDays(7)
                    && ValidateClientCert(existing, caCert))
                {
                    continue;
                }
                File.Copy(pfxPath, pfxPath + ".previous", overwrite: true);
            }

            using X509Certificate2 clientCert = IssueCert(caCert, client.SubjectCommonName, clientAuthOid,
                client.WorkloadIdentityUri is null ? null : san => san.AddUri(client.WorkloadIdentityUri));
            WriteClientPfx(clientCert, pfxPath);
        }

        string[] publishedFiles =
        [
            caCertFile,
            .. clientCertificates.Select(static item => item.PfxFileName)
        ];
        PublishGatewayReadableArtifacts(dataRoot, publishedFiles);
        return (serverCert, caCert);
    }

    public static void PublishGatewayReadableArtifacts(string dataRoot, params string[] fileNames)
    {
        ArgumentNullException.ThrowIfNull(fileNames);
        if (OperatingSystem.IsWindows() || string.IsNullOrWhiteSpace(dataRoot) || fileNames.Length == 0)
        {
            return;
        }

        if (Directory.Exists(dataRoot))
        {
            File.SetUnixFileMode(
                dataRoot,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
                | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.SetGroup);
        }

        foreach (string fileName in fileNames)
        {
            if (string.IsNullOrWhiteSpace(fileName) || Path.GetFileName(fileName) != fileName)
            {
                continue;
            }

            string path = Path.Combine(dataRoot, fileName);
            if (File.Exists(path))
            {
                File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead);
            }
        }
    }

    public static X509Certificate2 EnsureDelegationSigningCertificate(
        string dataRoot,
        string serviceName,
        string pfxFileName,
        string publicCertificateFileName)
    {
        Directory.CreateDirectory(dataRoot);
        RestrictPrivateDirectory(dataRoot);
        X509Certificate2 caCert = EnsureCa(Path.Combine(dataRoot, caKeyFile), Path.Combine(dataRoot, caCertFile), serviceName);
        try
        {
            string pfxPath = Path.Combine(dataRoot, pfxFileName);
            string publicPath = Path.Combine(dataRoot, publicCertificateFileName);
            if (File.Exists(pfxPath))
            {
                X509Certificate2 existing = X509CertificateLoader.LoadPkcs12FromFile(pfxPath, null,
                    X509KeyStorageFlags.Exportable | X509KeyStorageFlags.PersistKeySet);
                if (existing.NotAfter.ToUniversalTime() > DateTime.UtcNow.AddDays(7)
                    && ValidateClientCert(existing, caCert)
                    && string.Equals(existing.GetNameInfo(X509NameType.SimpleName, false), "Kautuka Gateway Channel Delegation", StringComparison.Ordinal))
                {
                    if (!File.Exists(publicPath)) WriteAtomic(publicPath, existing.Export(X509ContentType.Cert));
                    return existing;
                }
                existing.Dispose();
            }

            if (File.Exists(publicPath))
                File.Copy(publicPath, publicPath + ".previous", overwrite: true);
            using X509Certificate2 signingCert = IssueCert(caCert, "Kautuka Gateway Channel Delegation", clientAuthOid,
                san => san.AddUri(new Uri("spiffe://kautuka.local/delegation/gateway/channel", UriKind.Absolute)));
            WriteClientPfx(signingCert, pfxPath);
            WriteAtomic(publicPath, signingCert.Export(X509ContentType.Cert));
            return X509CertificateLoader.LoadPkcs12(signingCert.Export(X509ContentType.Pkcs12), null,
                X509KeyStorageFlags.Exportable | X509KeyStorageFlags.PersistKeySet);
        }
        finally
        {
            caCert.Dispose();
        }
    }

    public static X509Certificate2 RenewGatewayClientCertificate(
        string dataRoot,
        string serviceName,
        string clientPfxFile)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(serviceName);
        ArgumentException.ThrowIfNullOrWhiteSpace(clientPfxFile);
        X509Certificate2 caCert = EnsureCa(
            Path.Combine(dataRoot, caKeyFile),
            Path.Combine(dataRoot, caCertFile),
            serviceName);
        try
        {
            X509Certificate2 clientCert = IssueCert(caCert, "Kautuka Gateway Client", clientAuthOid,
                san => san.AddUri(GatewayChannelWorkloadIdentity));
            WriteClientPfx(clientCert, Path.Combine(dataRoot, clientPfxFile));
            return clientCert;
        }
        finally
        {
            caCert.Dispose();
        }
    }

    private static X509Certificate2 EnsureCa(string caKeyPath, string caCertPath, string serviceName)
    {
        if (File.Exists(caKeyPath) && File.Exists(caCertPath))
        {
            SetOwnerOnly(caKeyPath);
            SetOwnerOnly(caCertPath);
            byte[] keyBytes = File.ReadAllBytes(caKeyPath);
            byte[] certBytes = File.ReadAllBytes(caCertPath);

            using var ecdsa = ECDsa.Create();
            ecdsa.ImportECPrivateKey(keyBytes, out _);

            using var cert = X509CertificateLoader.LoadCertificate(certBytes);
            return cert.CopyWithPrivateKey(ecdsa);
        }

        return GenerateCa(caKeyPath, caCertPath, serviceName);
    }

    private static X509Certificate2 GenerateCa(string caKeyPath, string caCertPath, string serviceName)
    {
        using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);

        var req = new CertificateRequest(
            $"CN=Kautuka {serviceName} CA",
            ecdsa,
            HashAlgorithmName.SHA256);

        req.CertificateExtensions.Add(new X509BasicConstraintsExtension(
            certificateAuthority: true, hasPathLengthConstraint: true, pathLengthConstraint: 0, critical: true));
        req.CertificateExtensions.Add(new X509KeyUsageExtension(
            X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, critical: true));
        req.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(req.PublicKey, critical: false));

        DateTimeOffset now = DateTimeOffset.UtcNow;
        X509Certificate2 caCert = req.CreateSelfSigned(now.AddSeconds(-5), now.AddYears(10));

        // Persist CA private key (PKCS8 EC private key DER)
        byte[] keyBytes = ecdsa.ExportECPrivateKey();
        WriteAtomic(caKeyPath, keyBytes);

        // Persist CA public cert (DER)
        byte[] certBytes = caCert.Export(X509ContentType.Cert);
        WriteAtomic(caCertPath, certBytes);

        return caCert;
    }

    private static X509Certificate2 IssueCert(
        X509Certificate2 ca,
        string subjectCn,
        Oid ekuOid,
        Action<SubjectAlternativeNameBuilder>? sanBuilder)
    {
        using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);

        var req = new CertificateRequest(
            $"CN={subjectCn}",
            ecdsa,
            HashAlgorithmName.SHA256);

        req.CertificateExtensions.Add(new X509BasicConstraintsExtension(
            certificateAuthority: false, hasPathLengthConstraint: false, pathLengthConstraint: 0, critical: true));
        req.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
            new OidCollection { ekuOid }, critical: true));
        req.CertificateExtensions.Add(new X509KeyUsageExtension(
            X509KeyUsageFlags.DigitalSignature,
            critical: true));
        req.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(req.PublicKey, critical: false));

        if (sanBuilder is not null)
        {
            var sanExt = new SubjectAlternativeNameBuilder();
            sanBuilder(sanExt);
            req.CertificateExtensions.Add(sanExt.Build());
        }

        DateTimeOffset now = DateTimeOffset.UtcNow;
        byte[] serial = new byte[8];
        RandomNumberGenerator.Fill(serial);

        X509Certificate2 signed = req.Create(ca, now.AddSeconds(-5), now.AddDays(30), serial);
        var ephemeralCert = signed.CopyWithPrivateKey(ecdsa);
        signed.Dispose();

        if (OperatingSystem.IsWindows())
        {
            byte[] pfxBytes = ephemeralCert.Export(X509ContentType.Pkcs12);
            var persistentCert = X509CertificateLoader.LoadPkcs12(
                pfxBytes,
                (string?)null,
                X509KeyStorageFlags.Exportable | X509KeyStorageFlags.PersistKeySet);
            ephemeralCert.Dispose();
            return persistentCert;
        }

        return ephemeralCert;
    }

    private static void WriteClientPfx(X509Certificate2 clientCert, string pfxPath)
    {
        // Export with no password — the file itself is chmod 600 + dir is 700.
        byte[] pfxBytes = clientCert.Export(X509ContentType.Pkcs12);
        WriteAtomic(pfxPath, pfxBytes);
    }

    private static void WriteAtomic(string path, ReadOnlySpan<byte> bytes)
    {
        string temporaryPath = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                4096,
                FileOptions.WriteThrough))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }
            SetOwnerOnly(temporaryPath);
            File.Move(temporaryPath, path, overwrite: true);
            SetOwnerOnly(path);
        }
        finally
        {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
        }
    }

    private static void RestrictPrivateDirectory(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            var security = new DirectorySecurity();
            security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            security.AddAccessRule(new FileSystemAccessRule(
                WindowsIdentity.GetCurrent().User!,
                FileSystemRights.Modify | FileSystemRights.ReadAndExecute | FileSystemRights.DeleteSubdirectoriesAndFiles,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                PropagationFlags.None,
                AccessControlType.Allow));
            new DirectoryInfo(path).SetAccessControl(security);
            return;
        }

        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    private static void SetOwnerOnly(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            var security = new FileSecurity();
            security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            security.AddAccessRule(new FileSystemAccessRule(
                WindowsIdentity.GetCurrent().User!,
                FileSystemRights.Read | FileSystemRights.Write,
                InheritanceFlags.None,
                PropagationFlags.None,
                AccessControlType.Allow));
            new FileInfo(path).SetAccessControl(security);
            return;
        }

        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

    /// <summary>
    /// Validates a client certificate against the pinned in-memory CA.
    /// Used as Kestrel's ClientCertificateValidation callback.
    /// </summary>
    public static bool ValidateClientCert(X509Certificate2 clientCert, X509Certificate2 caCert)
    {
        ArgumentNullException.ThrowIfNull(clientCert);
        ArgumentNullException.ThrowIfNull(caCert);
        using var chain = new X509Chain();
        chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        chain.ChainPolicy.CustomTrustStore.Add(caCert);
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        // AllowUnknownCertificateAuthority lets the chain build against this process-local
        // CA. Build still returns true for a certificate that does not chain to that CA, so
        // the CA certificate itself must appear in the chain.
        chain.ChainPolicy.VerificationFlags = X509VerificationFlags.AllowUnknownCertificateAuthority;
        if (!chain.Build(clientCert))
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

    public static bool ValidateExactClientCert(
        X509Certificate2 clientCert,
        X509Certificate2 caCert,
        string expectedCommonName,
        Uri expectedWorkloadIdentityUri)
    {
        ArgumentNullException.ThrowIfNull(clientCert);
        ArgumentNullException.ThrowIfNull(caCert);
        ArgumentNullException.ThrowIfNull(expectedWorkloadIdentityUri);
        DateTimeOffset now = DateTimeOffset.UtcNow;
        if (now < clientCert.NotBefore.ToUniversalTime() || now > clientCert.NotAfter.ToUniversalTime()) return false;
        if (!string.Equals(clientCert.GetNameInfo(X509NameType.SimpleName, false), expectedCommonName, StringComparison.Ordinal)) return false;
        if (!HasEnhancedKeyUsage(clientCert, clientAuthOid.Value!)) return false;
        if (!HasUriSubjectAlternativeName(clientCert, expectedWorkloadIdentityUri.AbsoluteUri)) return false;
        return ValidateClientCert(clientCert, caCert);
    }

    public static bool ValidateExactServerCert(
        X509Certificate2 serverCert,
        X509Certificate2 caCert,
        string expectedCommonName,
        string expectedDnsName)
    {
        ArgumentNullException.ThrowIfNull(serverCert);
        ArgumentNullException.ThrowIfNull(caCert);
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedCommonName);
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedDnsName);
        DateTimeOffset now = DateTimeOffset.UtcNow;
        return now >= serverCert.NotBefore.ToUniversalTime()
            && now <= serverCert.NotAfter.ToUniversalTime()
            && string.Equals(serverCert.GetNameInfo(X509NameType.SimpleName, false), expectedCommonName, StringComparison.Ordinal)
            && HasEnhancedKeyUsage(serverCert, serverAuthOid.Value!)
            && HasGeneralName(serverCert, 2, expectedDnsName)
            && ValidateClientCert(serverCert, caCert);
    }

    private static bool HasEnhancedKeyUsage(X509Certificate2 certificate, string expectedOid) =>
        certificate.Extensions.OfType<X509EnhancedKeyUsageExtension>()
            .Any(extension => extension.EnhancedKeyUsages.Cast<Oid>().Any(oid => string.Equals(oid.Value, expectedOid, StringComparison.Ordinal)));

    private static bool HasUriSubjectAlternativeName(X509Certificate2 certificate, string expectedUri)
        => HasGeneralName(certificate, 6, expectedUri);

    private static bool HasGeneralName(X509Certificate2 certificate, int tagValue, string expectedValue)
    {
        X509Extension? san = certificate.Extensions["2.5.29.17"];
        if (san is null) return false;
        try
        {
            var reader = new AsnReader(san.RawData, AsnEncodingRules.DER);
            AsnReader names = reader.ReadSequence();
            var expectedTag = new Asn1Tag(TagClass.ContextSpecific, tagValue);
            while (names.HasData)
            {
                Asn1Tag tag = names.PeekTag();
                if (tag.HasSameClassAndValue(expectedTag))
                {
                    string value = names.ReadCharacterString(UniversalTagNumber.IA5String, expectedTag);
                    if (string.Equals(value, expectedValue, StringComparison.OrdinalIgnoreCase)) return true;
                }
                else
                {
                    _ = names.ReadEncodedValue();
                }
            }
            reader.ThrowIfNotEmpty();
            return false;
        }
        catch (AsnContentException)
        {
            return false;
        }
    }
}

public sealed record LocalMtlsClientCertificate(string SubjectCommonName, string PfxFileName, Uri? WorkloadIdentityUri = null);
