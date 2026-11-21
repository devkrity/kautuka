using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace Kautuka.Telemetry.Security;

/// <summary>Per-install, disjoint certificate inventories for the Gateway CONNECT listener.</summary>
[SupportedOSPlatform("linux")]
public sealed class GatewayConnectCertificateMaterial
{
    private const string ServerDnsName = "kautuka-connect.internal";
    private const string ClientName = "Kautuka Gateway CONNECT Client";
    private const string ServerName = "Kautuka Gateway CONNECT Listener";
    private const int LeafValidityDays = 365;
    private const UnixFileMode OwnerDirectory = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
    private const UnixFileMode OwnerFile = UnixFileMode.UserRead | UnixFileMode.UserWrite;
    // Envoy's mapped UID can read each file bind. The host directory remains owner-only.
    private const UnixFileMode EnvoyFile = UnixFileMode.UserRead | UnixFileMode.GroupRead | UnixFileMode.OtherRead;
    private static readonly Uri clientIdentity = new("spiffe://kautuka.internal/gateway", UriKind.Absolute);

    private GatewayConnectCertificateMaterial(string root)
    {
        GatewayDirectory = Path.Combine(root, "gateway");
        EnvoyDirectory = Path.Combine(root, "envoy");
    }

    public string GatewayDirectory { get; }
    public string EnvoyDirectory { get; }

    public static GatewayConnectCertificateMaterial Ensure(string path)
    {
        string root = Path.GetFullPath(path);
        if (!Directory.Exists(root))
        {
            string parent = Path.GetDirectoryName(root) ?? throw new InvalidOperationException("CONNECT certificates require an installation directory.");
            Directory.CreateDirectory(parent, OwnerDirectory);
            string staging = root + "." + Guid.NewGuid().ToString("N");
            Directory.CreateDirectory(staging, OwnerDirectory);
            try
            {
                var staged = new GatewayConnectCertificateMaterial(staging);
                staged.Provision();
                staged.Validate();
                try { Directory.Move(staging, root); }
                catch (IOException) when (Directory.Exists(root)) { }
            }
            finally
            {
                if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
            }
        }

        RequirePrivateEntry(root, OwnerDirectory);
        var installed = new GatewayConnectCertificateMaterial(root);
        installed.Validate();
        return installed;
    }

    private void Provision()
    {
        Directory.CreateDirectory(GatewayDirectory, OwnerDirectory);
        Directory.CreateDirectory(EnvoyDirectory, OwnerDirectory);

        using X509Certificate2 authority = LocalCaMtlsBootstrap.CreateCertificateAuthority("Gateway CONNECT");
        using X509Certificate2 server = LocalCaMtlsBootstrap.IssueCert(authority, ServerName, new Oid("1.3.6.1.5.5.7.3.1"),
            static san => san.AddDnsName(ServerDnsName), LeafValidityDays);
        using X509Certificate2 client = LocalCaMtlsBootstrap.IssueCert(authority, ClientName, new Oid("1.3.6.1.5.5.7.3.2"),
            static san => san.AddUri(clientIdentity), LeafValidityDays);
        using ECDsa serverKey = server.GetECDsaPrivateKey() ?? throw new CryptographicException("CONNECT listener private key is missing.");
        string authorityPem = authority.ExportCertificatePem();
        WriteFile(Path.Combine(GatewayDirectory, "ca.crt"), Encoding.ASCII.GetBytes(authorityPem), OwnerFile);
        WriteFile(Path.Combine(EnvoyDirectory, "gateway-ca.crt"), Encoding.ASCII.GetBytes(authorityPem), EnvoyFile);
        WriteFile(Path.Combine(EnvoyDirectory, "server.crt"), Encoding.ASCII.GetBytes(server.ExportCertificatePem()), EnvoyFile);
        WriteFile(Path.Combine(EnvoyDirectory, "server.key"), Encoding.ASCII.GetBytes(serverKey.ExportPkcs8PrivateKeyPem()), EnvoyFile);
        WriteFile(Path.Combine(GatewayDirectory, "client.pfx"), client.Export(X509ContentType.Pkcs12), OwnerFile);
    }

    private void Validate()
    {
        string authorityPath = Path.Combine(GatewayDirectory, "ca.crt");
        string clientPath = Path.Combine(GatewayDirectory, "client.pfx");
        string envoyAuthorityPath = Path.Combine(EnvoyDirectory, "gateway-ca.crt");
        string serverPath = Path.Combine(EnvoyDirectory, "server.crt");
        string serverKeyPath = Path.Combine(EnvoyDirectory, "server.key");
        RequireInventory(GatewayDirectory, OwnerFile, authorityPath, clientPath);
        RequireInventory(EnvoyDirectory, EnvoyFile, envoyAuthorityPath, serverPath, serverKeyPath);

        using X509Certificate2 authority = X509CertificateLoader.LoadCertificateFromFile(authorityPath);
        using X509Certificate2 envoyAuthority = X509CertificateLoader.LoadCertificateFromFile(envoyAuthorityPath);
        using X509Certificate2 gateway = X509CertificateLoader.LoadPkcs12FromFile(clientPath, null, X509KeyStorageFlags.EphemeralKeySet);
        using X509Certificate2 envoy = X509Certificate2.CreateFromPemFile(serverPath, serverKeyPath);
        if (!authority.RawDataMemory.Span.SequenceEqual(envoyAuthority.RawDataMemory.Span)
            || !gateway.HasPrivateKey || !envoy.HasPrivateKey
            || !LocalCaMtlsBootstrap.ValidateExactClientCert(gateway, authority, ClientName, clientIdentity)
            || !LocalCaMtlsBootstrap.ValidateExactServerCert(envoy, authority, ServerName, ServerDnsName))
        {
            throw new CryptographicException("The installation's Gateway CONNECT certificate inventory is invalid.");
        }
    }

    private static void RequireInventory(string directory, UnixFileMode fileMode, params string[] expected)
    {
        RequirePrivateEntry(directory, OwnerDirectory);
        if (!expected.ToHashSet(StringComparer.Ordinal).SetEquals(Directory.EnumerateFileSystemEntries(directory)))
            throw new IOException("Unexpected entries in a Gateway CONNECT certificate inventory.");

        foreach (string file in expected) RequirePrivateEntry(file, fileMode);
    }

    private static void RequirePrivateEntry(string path, UnixFileMode mode)
    {
        FileAttributes attributes = File.GetAttributes(path);
        if ((attributes & FileAttributes.ReparsePoint) != 0
            || ((attributes & FileAttributes.Directory) != 0) != (mode == OwnerDirectory)
            || File.GetUnixFileMode(path) != mode)
        {
            throw new IOException("Gateway CONNECT certificate inventory has unexpected permissions or file type.");
        }
    }

    private static void WriteFile(string path, byte[] contents, UnixFileMode publishedMode)
    {
        try
        {
            using var stream = new FileStream(path, new FileStreamOptions
            {
                Mode = FileMode.CreateNew,
                Access = FileAccess.Write,
                UnixCreateMode = OwnerFile,
            });
            stream.Write(contents);
            stream.Flush(flushToDisk: true);
            File.SetUnixFileMode(path, publishedMode);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(contents);
        }
    }
}
