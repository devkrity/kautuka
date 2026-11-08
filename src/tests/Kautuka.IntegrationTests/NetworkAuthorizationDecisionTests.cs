using System.Reflection;

using Kautuka.Manager.RuntimeContainer.Configuration;
using Kautuka.Manager.RuntimeContainer.Hosting;

namespace Kautuka.IntegrationTests;

public sealed class NetworkAuthorizationDecisionTests
{
    [Fact]
    public void MissingPermissionCannotBeAllowedWhenApprovalPersistenceFails()
    {
        string root = Path.Combine(Path.GetTempPath(), "nac-direct-storage-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(root);
            File.WriteAllText(Path.Combine(root, "egress"), "blocks creation of the policy directory");
            using var runtime = Create(root, []);
            Assert.ThrowsAny<IOException>(() => runtime.AuthorizeNetworkConnection("accepted", "10.247.91.2", "93.184.216.34", 443, "unknown.example.com"));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public void MissingPermissionIsDurableWithoutAuditCollection()
    {
        string root = Path.Combine(Path.GetTempPath(), "nac-direct-" + Guid.NewGuid().ToString("N"));
        try
        {
            using (ContainerEgressRuntime runtime = Create(root, []))
            {
                Assert.False(runtime.AuthorizeNetworkConnection("accepted", "10.247.91.2", "93.184.216.34", 443, "unknown.example.com"));
                NetworkAccessRequest request = Assert.Single(runtime.GetNetworkAccessPolicy(new("owner", false)).Policy.Requests);
                Assert.Equal("unknown.example.com", request.Destination);
                Assert.Equal("pending", request.Status);
                Assert.Equal(Identity, request.Identity);
            }
            using var restarted = Create(root, []);
            Assert.Single(restarted.GetNetworkAccessPolicy(new("owner", false)).Policy.Requests);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Theory]
    [InlineData("stale", "10.247.91.2", "93.184.216.34", "unknown.example.com")]
    [InlineData("accepted", "10.247.91.3", "93.184.216.34", "unknown.example.com")]
    [InlineData("accepted", "10.247.91.2", "169.254.169.254", "unknown.example.com")]
    [InlineData("accepted", "10.247.91.2", "93.184.216.34", "metadata.google.internal")]
    public void UnattributableOrProtectedChecksNeverCreateApproval(string generation, string source, string target, string sni)
    {
        string root = Path.Combine(Path.GetTempPath(), "nac-direct-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var runtime = Create(root, []);
            Assert.False(runtime.AuthorizeNetworkConnection(generation, source, target, 443, sni));
            Assert.Empty(runtime.GetNetworkAccessPolicy(new("owner", false)).Policy.Requests);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public void AcceptedHostnameGrantKeepsExistingHostnameRoutingSemantics()
    {
        string root = Path.Combine(Path.GetTempPath(), "nac-direct-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var runtime = Create(root, [new("10.247.91.2", "allowed.example.com", 443)]);
            // Compiler still selects the configured DNS cluster, never this arbitrary original IP.
            Assert.True(runtime.AuthorizeNetworkConnection("accepted", "10.247.91.2", "93.184.216.35", 443, "allowed.example.com"));
            Assert.False(runtime.AuthorizeNetworkConnection("accepted", "10.247.91.2", "93.184.216.35", 8443, "allowed.example.com"));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    private static readonly NetworkAccessIdentity Identity = new("agent", "owner", "alpha", "connector", "owner::mail");
    private static ContainerEgressRuntime Create(string root, EgressTcpRule[] rules)
    {
        var runtime = new ContainerEgressRuntime(new PodmanCommandRunner(), new ContainerEgressOptions { Enabled = true }, RuntimeImageAllowlist.Empty, root);
        typeof(ContainerEgressRuntime).GetField("authorization", BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(runtime, new EgressAuthorizationSnapshot("accepted", new Dictionary<string, NetworkAccessIdentity> { ["10.247.91.2"] = Identity }, rules));
        return runtime;
    }
}
