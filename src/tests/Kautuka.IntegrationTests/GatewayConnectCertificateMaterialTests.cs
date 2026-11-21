using System.Runtime.Versioning;
using System.Security.Cryptography.X509Certificates;

using Kautuka.Telemetry.Security;

namespace Kautuka.IntegrationTests;

public sealed class GatewayConnectCertificateMaterialTests
{
    [Fact]
    public void InstallationUsesSeparateInventoriesAndDoesNotShareTrustWithAnotherInstallation()
    {
        if (!OperatingSystem.IsLinux())
        {
            Assert.Skip("CONNECT material uses Linux file permissions.");
            return;
        }

        VerifyMaterial();
    }

    [SupportedOSPlatform("linux")]
    private static void VerifyMaterial()
    {
        DirectoryInfo root = Directory.CreateTempSubdirectory("kautuka-connect-certificates-");
        try
        {
            string installPath = Path.Combine(root.FullName, "installation-a");
            GatewayConnectCertificateMaterial first = GatewayConnectCertificateMaterial.Ensure(installPath);
            GatewayConnectCertificateMaterial second = GatewayConnectCertificateMaterial.Ensure(Path.Combine(root.FullName, "installation-b"));
            string clientPath = Path.Combine(first.GatewayDirectory, "client.pfx");
            string authorityPath = Path.Combine(first.GatewayDirectory, "ca.crt");
            using X509Certificate2 client = X509CertificateLoader.LoadPkcs12FromFile(clientPath, null, X509KeyStorageFlags.EphemeralKeySet);
            using X509Certificate2 authority = X509CertificateLoader.LoadCertificateFromFile(authorityPath);
            using X509Certificate2 otherAuthority = X509CertificateLoader.LoadCertificateFromFile(Path.Combine(second.GatewayDirectory, "ca.crt"));

            Assert.Equal(2, Directory.GetFileSystemEntries(first.GatewayDirectory).Length);
            Assert.Equal(3, Directory.GetFileSystemEntries(first.EnvoyDirectory).Length);
            Assert.Equal(2, Directory.GetFileSystemEntries(installPath).Length);
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute,
                File.GetUnixFileMode(first.EnvoyDirectory));
            Assert.True(LocalCaMtlsBootstrap.ValidateClientCert(client, authority));
            Assert.False(LocalCaMtlsBootstrap.ValidateClientCert(client, otherAuthority));

            _ = GatewayConnectCertificateMaterial.Ensure(installPath);
            using X509Certificate2 reloaded = X509CertificateLoader.LoadPkcs12FromFile(clientPath, null, X509KeyStorageFlags.EphemeralKeySet);
            Assert.Equal(client.Thumbprint, reloaded.Thumbprint);

            File.SetUnixFileMode(clientPath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead);
            Assert.Throws<IOException>(() => GatewayConnectCertificateMaterial.Ensure(installPath));
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }
}
