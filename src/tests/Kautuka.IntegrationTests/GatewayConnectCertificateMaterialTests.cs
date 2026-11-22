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

    [Theory]
    [InlineData(0)]
    [InlineData(31)]
    public void RenewalPublishesBothInventoriesAndRetainsThePreviousPair(int daysAfterRenewal)
    {
        if (!OperatingSystem.IsLinux())
        {
            Assert.Skip("CONNECT material uses Linux file permissions.");
            return;
        }

        VerifyRenewal(daysAfterRenewal);
    }

    [SupportedOSPlatform("linux")]
    private static void VerifyRenewal(int daysAfterRenewal)
    {
        DirectoryInfo root = Directory.CreateTempSubdirectory("kautuka-connect-renewal-");
        try
        {
            string installPath = Path.Combine(root.FullName, "installation");
            GatewayConnectCertificateMaterial original = GatewayConnectCertificateMaterial.Ensure(installPath);
            var beforeRenewal = new FixedClock(original.RenewAfterUtc.AddMinutes(-1));
            Assert.Equal(original.GatewayDirectory, GatewayConnectCertificateMaterial.Ensure(installPath, beforeRenewal).GatewayDirectory);

            var renewal = new FixedClock(original.RenewAfterUtc.AddDays(daysAfterRenewal));
            GatewayConnectCertificateMaterial renewed = GatewayConnectCertificateMaterial.Ensure(installPath, renewal);
            Assert.NotEqual(original.GatewayDirectory, renewed.GatewayDirectory);
            Assert.NotEqual(original.EnvoyDirectory, renewed.EnvoyDirectory);
            GatewayConnectCertificateMaterial loaded = GatewayConnectCertificateMaterial.Ensure(installPath, renewal);
            Assert.Equal(renewed.GatewayDirectory, loaded.GatewayDirectory);
            Assert.Equal(renewed.EnvoyDirectory, loaded.EnvoyDirectory);
            Assert.True(loaded.RenewAfterUtc > renewal.GetUtcNow());

            using X509Certificate2 originalCa = X509CertificateLoader.LoadCertificateFromFile(Path.Combine(original.GatewayDirectory, "ca.crt"));
            using X509Certificate2 renewedCa = X509CertificateLoader.LoadCertificateFromFile(Path.Combine(renewed.GatewayDirectory, "ca.crt"));
            using X509Certificate2 client = X509CertificateLoader.LoadPkcs12FromFile(
                Path.Combine(renewed.GatewayDirectory, "client.pfx"), null, X509KeyStorageFlags.EphemeralKeySet);
            var clientIdentity = new Uri("spiffe://kautuka.internal/gateway");
            Assert.True(LocalCaMtlsBootstrap.ValidateExactClientCert(client, renewedCa, "Kautuka Gateway CONNECT Client", clientIdentity, renewal.GetUtcNow()));
            Assert.False(LocalCaMtlsBootstrap.ValidateExactClientCert(client, originalCa, "Kautuka Gateway CONNECT Client", clientIdentity, renewal.GetUtcNow()));

            string selector = Path.Combine(installPath, "current");
            File.Delete(selector);
            File.CreateSymbolicLink(selector, Path.Combine(root.FullName, "missing-generation"));
            Assert.Throws<IOException>(() => GatewayConnectCertificateMaterial.Ensure(installPath, renewal));
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
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
