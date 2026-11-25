using System.Runtime.InteropServices;
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
    private static readonly TimeSpan RenewalWindow = TimeSpan.FromDays(30);
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
    public DateTimeOffset RenewAfterUtc { get; private set; }

    public static GatewayConnectCertificateMaterial Ensure(string path, TimeProvider? timeProvider = null)
    {
        string root = Path.GetFullPath(path);
        DateTimeOffset now = (timeProvider ?? TimeProvider.System).GetUtcNow();
        if (!Directory.Exists(root))
        {
            string parent = Path.GetDirectoryName(root) ?? throw new InvalidOperationException("CONNECT certificates require an installation directory.");
            Directory.CreateDirectory(parent, OwnerDirectory);
            string staging = root + "." + Guid.NewGuid().ToString("N");
            Directory.CreateDirectory(staging, OwnerDirectory);
            try
            {
                var staged = new GatewayConnectCertificateMaterial(staging);
                staged.Provision(now);
                staged.Validate(now);
                try { Directory.Move(staging, root); }
                catch (IOException) when (Directory.Exists(root)) { }
                FlushDirectory(parent);
            }
            finally
            {
                if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
            }
        }

        RequirePrivateEntry(root, OwnerDirectory);
        string selector = Path.Combine(root, "current");
        string active = root;
        if (File.Exists(selector) || Directory.Exists(selector) || new FileInfo(selector).LinkTarget is not null)
        {
            RequirePrivateEntry(selector, OwnerFile);
            string generation = File.ReadAllText(selector);
            if (!Guid.TryParseExact(generation, "N", out Guid id) || generation != id.ToString("N"))
                throw new IOException("The Gateway CONNECT certificate selector is invalid.");
            active = Path.Combine(root, generation);
            RequirePrivateEntry(active, OwnerDirectory);
        }

        var installed = new GatewayConnectCertificateMaterial(active);
        installed.Validate(now, allowExpiredInventory: true);
        if (now < installed.RenewAfterUtc) return installed;

        string nextGeneration = Guid.NewGuid().ToString("N");
        string nextDirectory = Path.Combine(root, nextGeneration);
        string nextSelector = selector + "." + nextGeneration;
        Directory.CreateDirectory(nextDirectory, OwnerDirectory);
        var renewed = new GatewayConnectCertificateMaterial(nextDirectory);
        try
        {
            renewed.Provision(now);
            renewed.Validate(now);
            WriteFile(nextSelector, Encoding.ASCII.GetBytes(nextGeneration), OwnerFile);
            FlushDirectory(root);
            File.Move(nextSelector, selector, overwrite: true);
        }
        catch
        {
            File.Delete(nextSelector);
            Directory.Delete(nextDirectory, recursive: true);
            throw;
        }
        FlushDirectory(root);
        return renewed;
    }

    private void Provision(DateTimeOffset now)
    {
        Directory.CreateDirectory(GatewayDirectory, OwnerDirectory);
        Directory.CreateDirectory(EnvoyDirectory, OwnerDirectory);

        using X509Certificate2 authority = LocalCaMtlsBootstrap.CreateCertificateAuthority("Gateway CONNECT", now);
        using X509Certificate2 server = LocalCaMtlsBootstrap.IssueCert(authority, ServerName, new Oid("1.3.6.1.5.5.7.3.1"),
            static san => san.AddDnsName(ServerDnsName), LeafValidityDays, now);
        using X509Certificate2 client = LocalCaMtlsBootstrap.IssueCert(authority, ClientName, new Oid("1.3.6.1.5.5.7.3.2"),
            static san => san.AddUri(clientIdentity), LeafValidityDays, now);
        using ECDsa serverKey = server.GetECDsaPrivateKey() ?? throw new CryptographicException("CONNECT listener private key is missing.");
        string authorityPem = authority.ExportCertificatePem();
        WriteFile(Path.Combine(GatewayDirectory, "ca.crt"), Encoding.ASCII.GetBytes(authorityPem), OwnerFile);
        WriteFile(Path.Combine(EnvoyDirectory, "gateway-ca.crt"), Encoding.ASCII.GetBytes(authorityPem), EnvoyFile);
        WriteFile(Path.Combine(EnvoyDirectory, "server.crt"), Encoding.ASCII.GetBytes(server.ExportCertificatePem()), EnvoyFile);
        WriteFile(Path.Combine(EnvoyDirectory, "server.key"), Encoding.ASCII.GetBytes(serverKey.ExportPkcs8PrivateKeyPem()), EnvoyFile);
        WriteFile(Path.Combine(GatewayDirectory, "client.pfx"), client.Export(X509ContentType.Pkcs12), OwnerFile);
        FlushDirectory(GatewayDirectory);
        FlushDirectory(EnvoyDirectory);
        FlushDirectory(Path.GetDirectoryName(GatewayDirectory)!);
    }

    private void Validate(DateTimeOffset now, bool allowExpiredInventory = false)
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
        DateTimeOffset expires = new[] { authority, gateway, envoy }.Min(static cert => cert.NotAfter.ToUniversalTime());
        // Check the full chain and workload identity before replacing an expired pair.
        DateTimeOffset verificationTime = allowExpiredInventory && now >= expires ? expires.AddSeconds(-1) : now;
        if (!authority.RawDataMemory.Span.SequenceEqual(envoyAuthority.RawDataMemory.Span)
            || !gateway.HasPrivateKey || !envoy.HasPrivateKey
            || !LocalCaMtlsBootstrap.ValidateExactClientCert(gateway, authority, ClientName, clientIdentity, verificationTime)
            || !LocalCaMtlsBootstrap.ValidateExactServerCert(envoy, authority, ServerName, ServerDnsName, verificationTime))
        {
            throw new CryptographicException("The installation's Gateway CONNECT certificate inventory is invalid.");
        }
        RenewAfterUtc = expires - RenewalWindow;
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
            File.SetUnixFileMode(path, publishedMode);
            stream.Flush(flushToDisk: true);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(contents);
        }
    }

    private static void FlushDirectory(string path)
    {
        int descriptor = open(path, 0x10000 | 0x80000); // O_RDONLY | O_DIRECTORY | O_CLOEXEC
        if (descriptor < 0)
            throw new IOException("Could not open the Gateway CONNECT certificate directory.", Marshal.GetLastPInvokeError());
        try
        {
            if (fsync(descriptor) != 0)
                throw new IOException("Could not persist the Gateway CONNECT certificate directory.", Marshal.GetLastPInvokeError());
        }
        finally { _ = close(descriptor); }
    }

    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    [DllImport("libc", SetLastError = true)]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Globalization", "CA2101", Justification = "Linux libc paths use UTF-8.")]
    private static extern int open([MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags);

    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    [DllImport("libc", SetLastError = true)]
    private static extern int fsync(int descriptor);

    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    [DllImport("libc", SetLastError = true)]
    private static extern int close(int descriptor);
}
