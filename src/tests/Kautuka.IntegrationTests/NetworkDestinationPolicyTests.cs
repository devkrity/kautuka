using System.Text.Json;

using Kautuka.Manager.RuntimeContainer.Hosting;

namespace Kautuka.IntegrationTests;

public sealed class NetworkDestinationPolicyTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "kautuka-destination-" + Guid.NewGuid().ToString("N"));
    private static readonly NetworkAccessIdentity identity = new("agent", "alice", "agent", "connector", "alice::tools");
    private NetworkAccessPolicyStore Store() => new(Path.Combine(root, "policy.json"));

    [Theory]
    [InlineData("10.0.0.1")]
    [InlineData("172.16.0.1")]
    [InlineData("192.168.1.1")]
    [InlineData("100.64.0.1")]
    [InlineData("169.254.169.253")]
    [InlineData("198.18.0.1")]
    [InlineData("192.0.2.1")]
    public void OrdinaryOwnerCannotSelfApproveNonPublicAddresses(string address)
    {
        Assert.Throws<UnauthorizedAccessException>(() => Store().SetRule(new("alice", false), identity, address, 443, true, 0));
        Assert.Equal(0, Store().Revision);
        NetworkAccessRule rule = Store().SetRule(new("admin", true), identity, address, 443, true, 0);
        Assert.True(rule.AllowPrivateNetwork);
        Assert.True(Assert.Single(Store().EffectiveGrants([])).AllowPrivateNetwork);
    }

    [Theory]
    [InlineData("169.254.169.254")]
    [InlineData("169.254.170.2")]
    [InlineData("169.254.170.23")]
    [InlineData("100.100.100.200")]
    [InlineData("168.63.129.16")]
    [InlineData("metadata.google.internal")]
    public void MetadataCannotBeAllowedEvenByAdministrator(string address)
    {
        Assert.Throws<UnauthorizedAccessException>(() => Store().SetRule(new("admin", true), identity, address, 80, true, 0));
        Assert.Equal(0, Store().Revision);
        Assert.Throws<ArgumentException>(() => EnvoyEgressConfiguration.Compile([new("10.1.1.2", address, 80, true)],
            "10.2.1.2", privateSourceAddress: "10.3.1.2"));
    }

    [Fact]
    public void HostnameApprovalDoesNotGiveOrdinaryOwnerPrivateEntitlement()
    {
        NetworkAccessPolicyStore store = Store();
        store.SetRule(new("admin", true), identity, "ollama.example.com", 11434, true, 0);
        Assert.True(Assert.Single(Store().EffectiveGrants([])).AllowPrivateNetwork);
        store.SetRule(new("alice", false), identity, "ollama.example.com", 11434, true, 1);
        Assert.False(Assert.Single(Store().EffectiveGrants([])).AllowPrivateNetwork);
    }

    [Fact]
    public void CompilerSeparatesSameHostnameClustersByApprovalAuthority()
    {
        EnvoyEgressConfiguration config = EnvoyEgressConfiguration.Compile(
            [new("10.1.1.2", "same.example.com", 443), new("10.1.1.3", "same.example.com", 443, true)],
            "10.2.1.2", privateSourceAddress: "10.3.1.2");
        using JsonDocument document = JsonDocument.Parse(config.ClusterDocument);
        JsonElement[] clusters = [.. document.RootElement.GetProperty("resources").EnumerateArray()];
        Assert.Equal(2, clusters.Length);
        Assert.NotEqual(clusters[0].GetProperty("name").GetString(), clusters[1].GetProperty("name").GetString());
        Assert.Equal(new[] { "10.2.1.2", "10.3.1.2" }, clusters.Select(item => item.GetProperty("upstream_bind_config")
            .GetProperty("source_address").GetProperty("address").GetString()).Order(StringComparer.Ordinal));
        foreach (JsonElement cluster in clusters) Assert.Contains(cluster.GetProperty("name").GetString()!, config.ListenerDocument, StringComparison.Ordinal);
    }

    [Fact]
    public void NativeRulesSeparateDnsPrivateAndFixedServiceExceptions()
    {
        string nft = NetworkDestinationPolicy.RenderBoundary("10.1.0.0/24", "10.1.0.2", "10.1.0.2", "10.2.0.2",
            ["169.254.169.253"], "10.1.0.3", "10.1.0.4");
        Assert.DoesNotContain('\r', nft);
        Assert.Contains("ip saddr 127.0.0.1 ip daddr 127.0.0.1 udp dport 53 accept", nft, StringComparison.Ordinal);
        Assert.Contains("ip saddr 127.0.0.1 ip daddr 127.0.0.1 tcp dport 53 accept", nft, StringComparison.Ordinal);
        Assert.True(nft.IndexOf(NetworkDestinationPolicy.MetadataSet, StringComparison.Ordinal) < nft.IndexOf("ct state established,related ct direction reply", StringComparison.Ordinal));
        Assert.Contains("meta skuid 65534 ip daddr 169.254.169.253 udp dport 53 accept", nft, StringComparison.Ordinal);
        Assert.DoesNotContain("meta skuid 65532 ip daddr 169.254.169.253", nft, StringComparison.Ordinal);
        Assert.Contains("meta skuid 65532 ip saddr 192.0.0.8", nft, StringComparison.Ordinal);
        Assert.Contains("meta skuid 65532 ip saddr 10.1.0.2 ip daddr 10.1.0.3 tcp dport 20101", nft, StringComparison.Ordinal);
        Assert.Contains("ip saddr 192.0.0.8 snat to 10.2.0.2", nft, StringComparison.Ordinal);
        Assert.True(nft.IndexOf("ip daddr { 10.1.0.0/24 } counter reject", StringComparison.Ordinal) < nft.IndexOf("meta skuid 65532 ip saddr 192.0.0.8", StringComparison.Ordinal));
    }

    [Fact]
    public void AzurePlatformAddressOnlyExemptsConfiguredDnsUidAndPort()
    {
        string nft = NetworkDestinationPolicy.RenderBoundary("", "", null, "10.2.0.2", ["168.63.129.16"], null, null);
        Assert.Contains("meta skuid 65534 ip daddr 168.63.129.16 tcp dport 53 accept", nft, StringComparison.Ordinal);
        Assert.True(nft.IndexOf("meta skuid 65534 ip daddr 168.63.129.16", StringComparison.Ordinal) < nft.IndexOf("ip daddr 168.63.129.16 counter reject", StringComparison.Ordinal));
        Assert.True(nft.IndexOf("ip daddr 168.63.129.16 counter reject", StringComparison.Ordinal) < nft.IndexOf("ct state established,related ct direction reply", StringComparison.Ordinal));
        string unconfigured = NetworkDestinationPolicy.RenderBoundary("", "", null, "10.2.0.2", ["1.1.1.1"], null, null);
        Assert.DoesNotContain("meta skuid 65534 ip daddr 168.63.129.16", unconfigured, StringComparison.Ordinal);
    }

    [Fact]
    public void ConfiguredGceDnsExemptsOnlyResolverUidAndDnsPortsBeforeMetadataDeny()
    {
        string nft = NetworkDestinationPolicy.RenderBoundary("", "", null, "10.2.0.2", ["169.254.169.254"], null, null);
        foreach (string protocol in new[] { "tcp", "udp" })
            Assert.True(nft.IndexOf($"meta skuid 65534 ip daddr 169.254.169.254 {protocol} dport 53 accept", StringComparison.Ordinal)
                < nft.IndexOf($"ip daddr {{ {NetworkDestinationPolicy.MetadataSet}", StringComparison.Ordinal));
        Assert.DoesNotContain("meta skuid 65532 ip daddr 169.254.169.254", nft, StringComparison.Ordinal);
        Assert.DoesNotContain("dport 80 accept", nft, StringComparison.Ordinal);
        string unconfigured = NetworkDestinationPolicy.RenderBoundary("", "", null, "10.2.0.2", ["1.1.1.1"], null, null);
        Assert.DoesNotContain("meta skuid 65534 ip daddr 169.254.169.254", unconfigured, StringComparison.Ordinal);
    }

    [Fact]
    public void ProtectedDnsCannotBypassManagedNetworkIsolation()
    {
        Assert.Throws<InvalidOperationException>(() => NetworkDestinationPolicy.RenderBoundary(
            "10.0.0.0/24, 169.254.169.0/24", "", null, "10.2.0.2", ["169.254.169.254"], null, null));
    }

    public void Dispose()
    {
        if (Directory.Exists(root)) Directory.Delete(root, true);
        GC.SuppressFinalize(this);
    }
}
