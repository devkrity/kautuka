using System.Security.Cryptography.X509Certificates;

using Kautuka.CredentialsHost.Hosting;
using Kautuka.CredentialsHost.Security;
using Kautuka.Gateway.Infrastructure.Credentials;
using Kautuka.Manager.Protos;
using Kautuka.Telemetry.Security;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace Kautuka.IntegrationTests;

public sealed class ClientIdentityPublicationTests
{
    [Fact]
    public void PublicationContainsOnlyTheSelectedIdentityAndDoesNotReplaceUnchangedFiles()
    {
        WithRoot(root =>
        {
            var (server, ca) = LocalCaMtlsBootstrap.EnsureAndLoad(root, "Manager",
                [new(ManagerClientIdentities.GatewayCommonName, ManagerClientIdentities.GatewayCertificateFileName),
                 new(ManagerClientIdentities.UpdateServiceCommonName, ManagerClientIdentities.UpdateServiceCertificateFileName)]);
            using (server)
            using (ca)
            {
                string published = LocalCaMtlsBootstrap.GatewayClientDirectory(root);
                string intermediate = Path.GetDirectoryName(published)!;
                Directory.CreateDirectory(intermediate);
                if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(intermediate, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
                LocalCaMtlsBootstrap.PublishGatewayClientIdentity(root, ManagerClientIdentities.GatewayCertificateFileName);
                Assert.Equal(new[] { ".ca.cer", ManagerClientIdentities.GatewayCertificateFileName },
                    Directory.GetFiles(published).Select(Path.GetFileName).Order(StringComparer.Ordinal));
                string pfx = Path.Combine(published, ManagerClientIdentities.GatewayCertificateFileName);
                DateTime marker = DateTime.UtcNow.AddDays(-2);
                File.SetLastWriteTimeUtc(pfx, marker);
                DateTime observed = File.GetLastWriteTimeUtc(pfx);
                string sentinel = Path.Combine(published, "directory-mount-sentinel");
                File.WriteAllText(sentinel, "directory remains mounted");
                LocalCaMtlsBootstrap.PublishGatewayClientIdentity(root, ManagerClientIdentities.GatewayCertificateFileName);
                Assert.Equal(observed, File.GetLastWriteTimeUtc(pfx));
                Assert.True(File.Exists(sentinel));
                using X509Certificate2 identity = X509CertificateLoader.LoadPkcs12FromFile(pfx, null);
                Assert.Equal(ManagerClientIdentities.GatewayCommonName, identity.GetNameInfo(X509NameType.SimpleName, false));
                Assert.True(LocalCaMtlsBootstrap.ValidateClientCert(identity, ca));
                if (!OperatingSystem.IsWindows())
                {
                    UnixFileMode traversable = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
                        | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.SetGroup;
                    Assert.Equal(traversable, File.GetUnixFileMode(intermediate));
                    Assert.Equal(traversable, File.GetUnixFileMode(published));
                    Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead, File.GetUnixFileMode(pfx));
                    Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(Path.Combine(root, ".ca.key")));
                }
            }
        });
    }

    [Fact]
    public void CredentialRenewalPublishesBeforeActivationAndGatewayReloadsTheNewClient()
    {
        WithRoot(root =>
        {
            var layout = new CredentialStorageLayout(Path.Combine(root, "credentialsHost"));
            var (server, ca) = LocalCaMtlsBootstrap.EnsureAndLoad(layout.TransportRoot, "Credentials", ".credentials-client.pfx");
            using (server)
            using (ca)
            {
                LocalCaMtlsBootstrap.PublishGatewayClientIdentity(layout.TransportRoot, ".credentials-client.pfx");
                var configuration = new ConfigurationBuilder().AddInMemoryCollection(
                    new Dictionary<string, string?> { ["Kautuka:ContainerStateRoot"] = root }).Build();
                using var gateway = new CredentialsMtlsCerts(configuration, NullLogger<CredentialsMtlsCerts>.Instance);
                string original = gateway.ClientCert.Thumbprint;
                var state = new CredentialClientIdentityState(original, DateTimeOffset.UtcNow.AddDays(1));
                var renewal = new CredentialClientCertificateRenewalService(layout, state, TimeProvider.System,
                    NullLogger<CredentialClientCertificateRenewalService>.Instance);
                renewal.RenewIfNeeded();
                Assert.NotEqual(original, state.Current.Thumbprint);
                Assert.Equal(state.Current.Thumbprint, gateway.ClientCert.Thumbprint);
                Assert.True(LocalCaMtlsBootstrap.ValidateClientCert(gateway.ClientCert, ca));
                renewal.Dispose();
            }
        });
    }

    [Fact]
    public void FailedPublicationLeavesThePreviouslyAcceptedClientActive()
    {
        WithRoot(root =>
        {
            var layout = new CredentialStorageLayout(Path.Combine(root, "credentialsHost"));
            var (server, ca) = LocalCaMtlsBootstrap.EnsureAndLoad(layout.TransportRoot, "Credentials", ".credentials-client.pfx");
            using (server)
            using (ca)
            using (X509Certificate2 original = X509CertificateLoader.LoadPkcs12FromFile(Path.Combine(layout.TransportRoot, ".credentials-client.pfx"), null))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(layout.GatewayClientRoot)!);
                File.WriteAllText(layout.GatewayClientRoot, "cannot publish a directory over this file");
                var state = new CredentialClientIdentityState(original.Thumbprint, DateTimeOffset.UtcNow.AddDays(1));
                using var renewal = new CredentialClientCertificateRenewalService(layout, state, TimeProvider.System,
                    NullLogger<CredentialClientCertificateRenewalService>.Instance);
                renewal.RenewIfNeeded();
                using X509Certificate2 attempted = X509CertificateLoader.LoadPkcs12FromFile(Path.Combine(layout.TransportRoot, ".credentials-client.pfx"), null);
                Assert.NotEqual(original.Thumbprint, attempted.Thumbprint);
                Assert.Equal(original.Thumbprint, state.Current.Thumbprint);
                Assert.False(Directory.Exists(layout.GatewayClientRoot));
            }
        });
    }

    private static void WithRoot(Action<string> test)
    {
        string root = Path.Combine(Path.GetTempPath(), "kautuka-client-publication-" + Guid.NewGuid().ToString("N"));
        try { test(root); }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }
}
