using Kautuka.Manager.RuntimeContainer.Configuration;
using Kautuka.Manager.RuntimeContainer.Hosting;

namespace Kautuka.IntegrationTests;

public sealed class NetworkAccessPolicyStoreTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "kautuka-network-policy-" + Guid.NewGuid().ToString("N"));
    private readonly NetworkAccessActor alice = new("alice", false);
    private readonly NetworkAccessActor admin = new("owner", true);
    private readonly NetworkAccessIdentity connector = new("agent", "alice", "agent-one", "connector", "alice::mail");
    private string PolicyPath => Path.Combine(root, "policy", "state.json");

    public void Dispose()
    {
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        GC.SuppressFinalize(this);
    }

    [Fact]
    public void AllowedDecisionPersistsAndNormalizesAnExactDestination()
    {
        var store = new NetworkAccessPolicyStore(PolicyPath);
        NetworkAccessRule rule = store.SetRule(alice, connector, "Graph.Microsoft.COM.", 443, true, 0);
        Assert.Equal("graph.microsoft.com", rule.Destination);
        var restarted = new NetworkAccessPolicyStore(PolicyPath);
        NetworkAccessPolicyState saved = restarted.Read(alice);
        Assert.Equal(1, saved.Revision);
        Assert.Equal(rule, Assert.Single(saved.Rules));
        Assert.Equal("graph.microsoft.com", Assert.Single(restarted.EffectiveGrants([])).Destination);
    }

    [Fact]
    public void OwnersCannotReadOrChangeAnotherOwnersOrSystemPolicy()
    {
        var store = new NetworkAccessPolicyStore(PolicyPath);
        store.SetRule(admin, connector with { UserId = "bob" }, "example.com", 443, true, 0);
        Assert.Empty(store.Read(alice).Rules);
        Assert.Throws<UnauthorizedAccessException>(() => store.SetRule(alice, connector with { UserId = "bob" }, "example.com", 443, true, 1));
        Assert.Throws<UnauthorizedAccessException>(() => store.SetRule(alice, connector with { ScopeType = "core" }, "example.com", 443, true, 1));
        Assert.Throws<UnauthorizedAccessException>(() => store.SetRule(alice, connector with { UserId = "system" }, "example.com", 443, true, 1));
        Assert.Single(new NetworkAccessPolicyStore(PolicyPath).Read(admin).Rules);
    }

    [Fact]
    public void RepeatedDenialsGroupAndKeepBlockedDoesNotReopenAfterRestart()
    {
        var store = new NetworkAccessPolicyStore(PolicyPath);
        DateTimeOffset first = DateTimeOffset.UtcNow;
        store.ObserveDenied(connector, "Example.COM", 443, first);
        store.ObserveDenied(connector, "example.com.", 443, first.AddSeconds(1));
        NetworkAccessRequest pending = Assert.Single(store.Read(alice).Requests);
        Assert.Equal(2, pending.Attempts);
        Assert.Equal("pending", pending.Status);
        Assert.Equal(0, store.Revision);
        store.DecideRequest(alice, pending.Id, false, 0);
        var restarted = new NetworkAccessPolicyStore(PolicyPath);
        restarted.ObserveDenied(connector, "example.com", 443, first.AddSeconds(2));
        NetworkAccessRequest blocked = Assert.Single(restarted.Read(alice).Requests);
        Assert.Equal("blocked", blocked.Status);
        Assert.Equal(2, blocked.Attempts);
        Assert.Empty(restarted.EffectiveGrants([]));
    }

    [Fact]
    public void AllowChangesPolicyWithoutReplayingAnExecution()
    {
        var store = new NetworkAccessPolicyStore(PolicyPath);
        store.ObserveDenied(connector, "example.com", 8443, DateTimeOffset.UtcNow);
        NetworkAccessRequest pending = Assert.Single(store.Read(alice).Requests);
        NetworkAccessRule rule = store.DecideRequest(alice, pending.Id, true, 0);
        Assert.True(rule.Allowed);
        Assert.Equal("allowed", Assert.Single(store.Read(alice).Requests).Status);
        ContainerEgressGrant grant = Assert.Single(store.EffectiveGrants([]));
        Assert.Equal(connector.AgentId, grant.AgentId);
        Assert.Equal("agent", grant.Role);
        Assert.Equal(connector.AgentId, grant.ComponentId);
        Assert.Equal(8443, grant.Port);
    }

    [Fact]
    public void AgentRequestsAndDecisionsUseOneCanonicalIdentityAcrossConnectors()
    {
        var store = new NetworkAccessPolicyStore(PolicyPath);
        DateTimeOffset observed = DateTimeOffset.UtcNow;
        store.ObserveDenied(connector, "shared.example.com", 443, observed);
        NetworkAccessIdentity secondConnector = connector with { ComponentId = "alice::calendar" };
        store.ObserveDenied(secondConnector, "shared.example.com", 443, observed.AddSeconds(1));

        NetworkAccessRequest request = Assert.Single(store.Read(alice).Requests);
        Assert.Equal("agent", request.Identity.Role);
        Assert.Equal(request.Identity.AgentId, request.Identity.ComponentId);
        Assert.Equal(2, request.Attempts);
        NetworkAccessRule rule = store.DecideRequest(alice, request.Id, true, 0);
        Assert.Equal(request.Identity, rule.Identity);
        Assert.Single(store.EffectiveGrants([]));
    }

    [Fact]
    public void ExplicitAgentDecisionReplacesItsBootstrapDefaultAndReconcilesLegacyRows()
    {
        var store = new NetworkAccessPolicyStore(PolicyPath);
        ContainerEgressGrant bootstrap = new()
        {
            ScopeType = "agent",
            UserId = "alice",
            AgentId = "agent-one",
            Role = "connector",
            ComponentId = "alice::mail",
            Destination = "shared.example.com",
            Port = 443,
            AllowPrivateNetwork = true,
        };
        store.SetRule(alice, connector, "shared.example.com", 443, true, 0);
        ContainerEgressGrant grant = Assert.Single(store.EffectiveGrants([bootstrap]));
        Assert.False(grant.AllowPrivateNetwork);
        NetworkAccessPolicyState persisted = store.Read(alice);
        Assert.Equal("agent", Assert.Single(persisted.Rules).Identity.Role);
        Assert.Empty(persisted.Requests);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LegacyPrivateLiteralAllowNeedsApprovalAndObservedDenialReopensDurably(bool previouslyRequested)
    {
        var original = new NetworkAccessPolicyStore(PolicyPath);
        DateTimeOffset now = DateTimeOffset.UtcNow;
        if (previouslyRequested) original.ObserveDenied(connector, "10.0.0.7", 11434, now);
        original.SetRule(admin, connector, "10.0.0.7", 11434, true, 0, allowPrivateNetwork: true);
        // The old release wrote AllowPrivateNetwork for every admin Allow, without
        // evidence that private-network approval was explicitly chosen.
        File.WriteAllText(PolicyPath, File.ReadAllText(PolicyPath).Replace(
            "\"privateNetworkApprovalExplicit\": true", "\"legacyField\": true", StringComparison.Ordinal));
        var store = new NetworkAccessPolicyStore(PolicyPath);
        Assert.Empty(store.EffectiveGrants([])); // No upstream chain: Envoy can report a policy denial.
        store.ObserveDenied(connector, "10.0.0.7", 11434, now.AddSeconds(1));
        NetworkAccessRequest request = Assert.Single(store.Read(alice).Requests);
        Assert.Equal("pending", request.Status);
        Assert.Equal(previouslyRequested ? 2 : 1, request.Attempts);
        Assert.Equal(1, store.Revision);
        var restarted = new NetworkAccessPolicyStore(PolicyPath);
        Assert.Equal(request, Assert.Single(restarted.Read(alice).Requests));
        restarted.ObserveDenied(connector, "10.0.0.7", 11434, now.AddSeconds(1));
        Assert.Equal(request.Attempts + 1, Assert.Single(restarted.Read(alice).Requests).Attempts);
        Assert.Throws<UnauthorizedAccessException>(() => restarted.DecideRequest(alice, request.Id, true, 1, true));
        restarted.DecideRequest(admin, request.Id, true, 1, true);
        Assert.True(Assert.Single(new NetworkAccessPolicyStore(PolicyPath).EffectiveGrants([])).AllowPrivateNetwork);
        Assert.Equal("allowed", Assert.Single(new NetworkAccessPolicyStore(PolicyPath).Read(alice).Requests).Status);
    }

    [Fact]
    public void EffectiveAllowDoesNotReopenButExplicitKeepBlockedResolvesLegacyPrivateRequest()
    {
        var store = new NetworkAccessPolicyStore(PolicyPath);
        store.SetRule(admin, connector, "public.example.com", 443, true, 0);
        store.ObserveDenied(connector, "public.example.com", 443, DateTimeOffset.UtcNow);
        Assert.Empty(store.Read(alice).Requests);
        store.ObserveDenied(connector, "10.0.0.7", 11434, DateTimeOffset.UtcNow);
        NetworkAccessRequest request = Assert.Single(store.Read(alice).Requests);
        store.DecideRequest(alice, request.Id, false, 1);
        var restarted = new NetworkAccessPolicyStore(PolicyPath);
        restarted.ObserveDenied(connector, "10.0.0.7", 11434, DateTimeOffset.UtcNow);
        Assert.Equal("blocked", Assert.Single(restarted.Read(alice).Requests).Status);
        Assert.DoesNotContain(restarted.EffectiveGrants([]), grant => grant.Destination == "10.0.0.7");
    }

    [Fact]
    public void StaleEditsCannotReplaceANewerDecision()
    {
        var store = new NetworkAccessPolicyStore(PolicyPath);
        store.SetRule(alice, connector, "example.com", 443, false, 0);
        Assert.Throws<InvalidOperationException>(() => store.SetRule(alice, connector, "example.com", 443, true, 0));
        Assert.False(Assert.Single(new NetworkAccessPolicyStore(PolicyPath).Read(alice).Rules).Allowed);
    }

    [Fact]
    public void BlockingABootstrapGrantDoesNotChangeAnotherAgent()
    {
        var store = new NetworkAccessPolicyStore(PolicyPath);
        ContainerEgressGrant grant = new()
        {
            ScopeType = "agent",
            UserId = "alice",
            AgentId = "agent-one",
            Role = "connector",
            ComponentId = "alice::mail",
            Destination = "EXAMPLE.com",
            Port = 443,
        };
        ContainerEgressGrant other = new()
        {
            ScopeType = "agent",
            UserId = "alice",
            AgentId = "agent-two",
            Role = "connector",
            ComponentId = "alice::mail",
            Destination = "example.com",
            Port = 443,
        };
        store.SetRule(alice, connector, "example.com", 443, false, 0);
        Assert.Equal("agent-two", Assert.Single(store.EffectiveGrants([grant, other])).AgentId);
    }

    [Theory]
    [InlineData("*.example.com", 443)]
    [InlineData("https://example.com/path", 443)]
    [InlineData("::1", 443)]
    [InlineData("127.0.0.1", 443)]
    [InlineData("example.com", 0)]
    [InlineData("example.com", 65536)]
    public void InvalidRulesAreRejectedBeforeWritingState(string destination, int port)
    {
        var store = new NetworkAccessPolicyStore(PolicyPath);
        Assert.ThrowsAny<ArgumentException>(() => store.SetRule(alice, connector, destination, port, true, 0));
        Assert.False(File.Exists(PolicyPath));
    }

    [Fact]
    public void CorruptPersistedPolicyCannotFallBackToBootstrapAllows()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(PolicyPath)!);
        File.WriteAllText(PolicyPath, "{\"schemaVersion\":9,\"revision\":0,\"rules\":[],\"requests\":[]}");
        var store = new NetworkAccessPolicyStore(PolicyPath);
        Assert.Throws<InvalidDataException>(() => store.EffectiveGrants([]));
    }

    [Fact]
    public void PostReplacementFailureCannotOverwriteTheNewDiskRevisionFromOldMemory()
    {
        var store = new NetworkAccessPolicyStore(PolicyPath, _ => throw new IOException("Directory flush failed."));
        Assert.Equal(0, store.Revision);
        Assert.Throws<IOException>(() => store.SetRule(alice, connector, "example.com", 443, true, 0));
        Assert.Throws<IOException>(() => store.Read(alice));
        Assert.Throws<IOException>(() => store.SetRule(alice, connector, "other.example.com", 443, false, 0));
        var restarted = new NetworkAccessPolicyStore(PolicyPath);
        Assert.Equal(1, restarted.Revision);
        Assert.Equal("example.com", Assert.Single(restarted.Read(alice).Rules).Destination);
    }

    [Theory]
    [InlineData("agent")]
    [InlineData("materializer")]
    public void ActualRuntimeRolesCanReceiveScopedDecisions(string role)
    {
        var store = new NetworkAccessPolicyStore(PolicyPath);
        store.ObserveDenied(connector with { Role = role }, "example.com", 443, DateTimeOffset.UtcNow);
        NetworkAccessRequest request = Assert.Single(store.Read(alice).Requests);
        store.DecideRequest(alice, request.Id, true, 0);
        Assert.Equal(role, Assert.Single(store.EffectiveGrants([])).Role);
    }

    [Fact]
    public async Task PersistedDecisionIsPendingUntilAnActualProxyAcknowledgement()
    {
        var runner = new PodmanCommandRunner((_, _) => throw new InvalidOperationException("No runtime bootstrap is requested by a policy edit."));
        using (var runtime = new ContainerEgressRuntime(runner, new ContainerEgressOptions { Enabled = true }, RuntimeImageAllowlist.Empty, root))
        {
            NetworkAccessPolicyView view = await runtime.SetNetworkAccessRuleAsync(alice, connector, "example.com", 443, true, 0, TestContext.Current.CancellationToken);
            Assert.Equal("pending", view.ActivationState);
            Assert.Equal(1, view.Policy.Revision);
            Assert.Equal(-1, view.ActiveRevision);
        }
        using var restarted = new ContainerEgressRuntime(runner, new ContainerEgressOptions { Enabled = true }, RuntimeImageAllowlist.Empty, root);
        Assert.Equal("pending", restarted.GetNetworkAccessPolicy(alice).ActivationState);
        Assert.True(Assert.Single(restarted.GetNetworkAccessPolicy(alice).Policy.Rules).Allowed);
    }
}
