using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Security.Principal;

using Kautuka.Manager.Protos;
using Kautuka.Telemetry.Security;

namespace Kautuka.IntegrationTests;

public sealed class ManagerClientCertificateIdentityTests
{
    [Fact]
    public void LocalCaCanBeReloadedAfterFirstStart()
    {
        string root = Path.Combine(Path.GetTempPath(), "kautuka-local-ca-reload-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var first = LocalCaMtlsBootstrap.EnsureAndLoad(root, "Manager", ".manager-client.pfx");
            first.ServerCert.Dispose();
            first.CaCert.Dispose();
            string caKeyPath = Path.Combine(root, ".ca.key");
            if (OperatingSystem.IsWindows())
            {
                var info = new FileInfo(caKeyPath);
                FileSecurity loosened = info.GetAccessControl();
                loosened.AddAccessRule(new FileSystemAccessRule(
                    new SecurityIdentifier(WellKnownSidType.WorldSid, null),
                    FileSystemRights.Read,
                    AccessControlType.Allow));
                info.SetAccessControl(loosened);
            }
            else
            {
                File.SetUnixFileMode(
                    caKeyPath,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead);
            }

            var second = LocalCaMtlsBootstrap.EnsureAndLoad(root, "Manager", ".manager-client.pfx");
            using (second.ServerCert)
            using (second.CaCert)
            {
                Assert.True(second.ServerCert.HasPrivateKey);
                Assert.True(second.CaCert.HasPrivateKey);
            }

            if (OperatingSystem.IsWindows())
            {
                FileSecurity security = new FileInfo(caKeyPath).GetAccessControl();
                Assert.True(security.AreAccessRulesProtected);
                AuthorizationRuleCollection rules = security.GetAccessRules(includeExplicit: true, includeInherited: false, typeof(SecurityIdentifier));
                Assert.NotEmpty(rules);
                foreach (FileSystemAccessRule rule in rules)
                {
                    Assert.Equal(WindowsIdentity.GetCurrent().User, (SecurityIdentifier)rule.IdentityReference);
                }
            }
            else
            {
                Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(caKeyPath));
            }
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void ManagerIssuesDistinctPurposeBoundClientCertificates()
    {
        string root = Path.Combine(Path.GetTempPath(), "kautuka-manager-client-cert-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var certificates = new[]
            {
                new LocalMtlsClientCertificate(ManagerClientIdentities.GatewayCommonName, ManagerClientIdentities.GatewayCertificateFileName),
                new LocalMtlsClientCertificate(ManagerClientIdentities.UpdateServiceCommonName, ManagerClientIdentities.UpdateServiceCertificateFileName),
            };
            var (server, ca) = LocalCaMtlsBootstrap.EnsureAndLoad(root, "Manager", certificates);
            using (server)
            using (ca)
            using (X509Certificate2 gateway = X509CertificateLoader.LoadPkcs12FromFile(
                Path.Combine(root, ManagerClientIdentities.GatewayCertificateFileName), null))
            using (X509Certificate2 updateService = X509CertificateLoader.LoadPkcs12FromFile(
                Path.Combine(root, ManagerClientIdentities.UpdateServiceCertificateFileName), null))
            {
                Assert.Equal(ManagerClientIdentities.GatewayCommonName, gateway.GetNameInfo(X509NameType.SimpleName, false));
                Assert.Equal(ManagerClientIdentities.UpdateServiceCommonName, updateService.GetNameInfo(X509NameType.SimpleName, false));
                Assert.NotEqual(gateway.Thumbprint, updateService.Thumbprint);
                Assert.True(LocalCaMtlsBootstrap.ValidateClientCert(gateway, ca));
                Assert.True(LocalCaMtlsBootstrap.ValidateClientCert(updateService, ca));
                using ECDsa foreignKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
                var foreignRequest = new CertificateRequest(
                    "CN=" + ManagerClientIdentities.GatewayCommonName,
                    foreignKey,
                    HashAlgorithmName.SHA256);
                using X509Certificate2 foreign = foreignRequest.CreateSelfSigned(
                    DateTimeOffset.UtcNow.AddMinutes(-5),
                    DateTimeOffset.UtcNow.AddDays(1));
                Assert.Equal(ManagerClientIdentities.GatewayCommonName, foreign.GetNameInfo(X509NameType.SimpleName, false));
                Assert.False(LocalCaMtlsBootstrap.ValidateClientCert(foreign, ca));
            }
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }
}
