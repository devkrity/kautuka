using Kautuka.Manager.RuntimeContainer.Configuration;
using Kautuka.Manager.RuntimeContainer.Hosting;

namespace Kautuka.IntegrationTests;

public sealed class RegistryNetworkApprovalTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "registry-network-" + Guid.NewGuid().ToString("N"));
    private static readonly NetworkAccessActor admin = new("owner", true);
    private static readonly NetworkAccessIdentity identity = new("core", "system", "management", "materializer", "image-acquisition");
    private string PolicyPath => Path.Combine(root, "egress", "policy", "state.json");
    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
    private ContainerEgressRuntime Runtime(IReadOnlyList<ContainerEgressGrant>? defaults = null)
        => new(new PodmanCommandRunner(), new ContainerEgressOptions { Enabled = true, Grants = defaults ?? [] }, RuntimeImageAllowlist.Empty, root);

    [Fact]
    public async Task UiGrantSatisfiesRegistryPrecheckAndExplicitBlockOverridesStaticDefault()
    {
        var grant = new ContainerEgressGrant
        {
            ScopeType = identity.ScopeType,
            UserId = identity.UserId,
            AgentId = identity.AgentId,
            Role = identity.Role,
            ComponentId = identity.ComponentId,
            Destination = "quay.io",
            Port = 443,
        };
        using var runtime = Runtime([grant]);
        runtime.RequireRegistryApproval("quay.io", 443);
        await runtime.SetNetworkAccessRuleAsync(admin, identity, "quay.io", 443, false, 0, TestContext.Current.CancellationToken);
        Assert.Throws<InvalidOperationException>(() => runtime.RequireRegistryApproval("quay.io", 443));
        Assert.Empty(runtime.GetNetworkAccessPolicy(admin).Policy.Requests);

        await runtime.SetNetworkAccessRuleAsync(admin, identity, "quay.io", 443, true, 1, TestContext.Current.CancellationToken);
        using var restartedWithoutStaticGrant = Runtime();
        restartedWithoutStaticGrant.RequireRegistryApproval("QUAY.IO.", 443);
        Assert.Empty(restartedWithoutStaticGrant.GetNetworkAccessPolicy(admin).Policy.Requests);
    }

    [Fact]
    public async Task MissingRegistryCreatesDurableRequestBeforeAnyBootstrapOrHelper()
    {
        int calls = 0;
        var runner = new PodmanCommandRunner((args, _) =>
        {
            Assert.Equal(new[] { "image", "exists", "quay.io/example/connector@sha256:" + new string('a', 64) }, args);
            calls++;
            return Task.FromResult(new PodmanCommandResult(1, "", ""));
        });
        using var runtime = new ContainerEgressRuntime(runner, new ContainerEgressOptions { Enabled = true }, RuntimeImageAllowlist.Empty, root);
        InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(() => runtime.ResolveRuntimeImageAsync(
            "quay.io/example/connector@sha256:" + new string('a', 64), TestContext.Current.CancellationToken));
        Assert.Contains("quay.io:443", error.Message, StringComparison.Ordinal);
        Assert.Equal(1, calls);
        NetworkAccessRequest request = Assert.Single(new NetworkAccessPolicyStore(PolicyPath).Read(admin).Requests);
        Assert.Equal(identity, request.Identity);
        Assert.Equal("quay.io", request.Destination);
        Assert.Equal("pending", request.Status);
        Assert.Throws<KeyNotFoundException>(() => new NetworkAccessPolicyStore(PolicyPath)
            .DecideRequest(new("owner", false), request.Id, true, 0));
    }

    [Fact]
    public void LayerHostDiagnosticsUseOnlyBoundedDirectRequestsForTheCurrentIdentityAndTime()
    {
        DateTimeOffset start = DateTimeOffset.UtcNow;
        var store = new NetworkAccessPolicyStore(PolicyPath);
        store.ObserveDenied(identity, "old.example.com", 443, start.AddSeconds(-1));
        store.ObserveDenied(identity with { ComponentId = "other" }, "unrelated.example.com", 443, start);
        for (int index = 0; index < 8; index++) store.ObserveDenied(identity, $"cdn-{index}.example.com", 443, start);
        using var runtime = Runtime();
        string failure = runtime.DescribeImageAcquisitionFailure(start);
        Assert.Contains("no direct pull", failure, StringComparison.Ordinal);
        Assert.Contains("permissions requested during this attempt", failure, StringComparison.Ordinal);
        Assert.Contains("cdn-0.example.com:443", failure, StringComparison.Ordinal);
        Assert.Contains("cdn-4.example.com:443", failure, StringComparison.Ordinal);
        Assert.DoesNotContain("cdn-5", failure, StringComparison.Ordinal);
        Assert.DoesNotContain("old.example", failure, StringComparison.Ordinal);
        Assert.DoesNotContain("unrelated.example", failure, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("169.254.169.254")]
    [InlineData("metadata.google.internal")]
    [InlineData("168.63.129.16")]
    public void ProtectedRegistryEndpointsCannotProduceUnfulfillableApprovalRequests(string host)
    {
        using var runtime = Runtime();
        Assert.Throws<InvalidOperationException>(() => runtime.RequireRegistryApproval(host, 443));
        Assert.Empty(runtime.GetNetworkAccessPolicy(admin).Policy.Requests);
    }
}
