using System.Reflection;

using Kautuka.Manager.RuntimeContainer.Configuration;
using Kautuka.Manager.RuntimeContainer.Hosting;

namespace Kautuka.IntegrationTests;

public sealed class RemoteConnectorNetworkAuthorizationTests
{
    private static readonly NetworkAccessIdentity subject = new("agent", "owner", "workspace-alpha", "remote-connector", "owner::mail");
    private static readonly NetworkAccessIdentity gateway = new("core", "system", "gateway", "core", "gateway");
    private const string source = "10.247.1.2";
    private const string host = "mcp.example.com";

    [Fact]
    public void SharedGatewayGrantDoesNotReplaceDurableCallerApproval()
    {
        using var fixture = new Fixture([], [new(source, host, 443)]);
        Assert.Equal("missing_permission", fixture.Check().Decision);
        NetworkAccessRequest request = Assert.Single(fixture.Runtime.GetNetworkAccessPolicy(new("owner", false)).Policy.Requests);
        Assert.Equal(subject, request.Identity);
        var reloaded = new NetworkAccessPolicyStore(fixture.PolicyPath);
        Assert.Equal(request.Id, Assert.Single(reloaded.Read(new("owner", false)).Requests).Id);
        Assert.Equal("missing_permission", fixture.Check().Decision);
        Assert.Equal(2, Assert.Single(fixture.Runtime.GetNetworkAccessPolicy(new("owner", false)).Policy.Requests).Attempts);
    }

    [Theory]
    [InlineData("agent", "another-owner", "workspace-alpha", "remote-connector", "another-owner::mail")]
    [InlineData("core", "owner", "gateway", "remote-connector", "owner::mail")]
    [InlineData("agent", "owner", "workspace-alpha", "connector", "owner::mail")]
    [InlineData("agent", "owner", "workspace-alpha", "remote-connector", "another-owner::mail")]
    public void EvenAdministratorCannotForgeAnotherRemoteSubject(string scope, string owner, string agent, string role, string component)
    {
        using var fixture = new Fixture([], []);
        Assert.Throws<UnauthorizedAccessException>(() => fixture.Runtime.CheckRemoteConnectorNetwork(new("owner", true),
            new(scope, owner, agent, role, component), "https", host, 443));
        Assert.Empty(fixture.Runtime.GetNetworkAccessPolicy(new("owner", true)).Policy.Requests);
    }

    [Fact]
    public void LogicalAllowIsExactToCallerAgentConnectorAndPort()
    {
        using var fixture = new Fixture([Grant(subject)], [new(source, host, 443)]);
        Assert.Equal("allowed", fixture.Check().Decision);
        Assert.Equal("missing_permission", fixture.Runtime.CheckRemoteConnectorNetwork(new("owner", false), subject with { AgentId = "workspace-beta" }, "https", host, 443).Decision);
        Assert.Equal("missing_permission", fixture.Runtime.CheckRemoteConnectorNetwork(new("owner", false), subject with { ComponentId = "owner::other" }, "https", host, 443).Decision);
        Assert.Equal("missing_permission", fixture.Runtime.CheckRemoteConnectorNetwork(new("owner", false), subject, "https", host, 8443).Decision);
    }

    [Fact]
    public void CanonicalManagementIdentityPassesActualManagerValidation()
    {
        NetworkAccessIdentity management = subject with { ScopeType = "management", AgentId = "management" };
        using var fixture = new Fixture([Grant(management)], [new(source, host, 443)]);
        RemoteConnectorNetworkDecision allowed = fixture.Runtime.CheckRemoteConnectorNetwork(new("owner", false), management, "https", host, 443);
        Assert.Equal("allowed", allowed.Decision);
        Assert.Throws<ArgumentException>(() => fixture.Runtime.CheckRemoteConnectorNetwork(new("owner", false), management with { AgentId = "" }, "https", host, 443));
    }

    [Fact]
    public void PublicLogicalHostnameCannotUseANonpublicTransportPin()
    {
        using var fixture = new Fixture([Grant(subject)], [new(source, host, 443)]);
        RemoteConnectorNetworkDecision denied = fixture.Check("https", "192.168.1.12");
        Assert.Equal("private_route_conflict", denied.Decision);
        Assert.False(denied.AllowPrivateNetwork);
        Assert.Empty(fixture.Runtime.GetNetworkAccessPolicy(new("owner", false)).Policy.Requests);
        using var privateApproved = new Fixture([Grant(subject, true)], [new(source, host, 443, true)]);
        RemoteConnectorNetworkDecision allowed = privateApproved.Check("https", "192.168.1.12");
        Assert.Equal("allowed", allowed.Decision);
        Assert.True(allowed.AllowPrivateNetwork);
    }

    [Theory]
    [InlineData("mcp.example.com")]
    [InlineData("2606:4700:4700::1111")]
    [InlineData("127.0.0.1")]
    public void DisabledModeLeavesLegacyAddressValidationToGateway(string endpointHost)
    {
        string root = Path.Combine(Path.GetTempPath(), "kautuka-remote-disabled-" + Guid.NewGuid().ToString("N"));
        using var runtime = new ContainerEgressRuntime(new PodmanCommandRunner((_, _) => throw new InvalidOperationException("Disabled mode must not start Podman.")),
            new ContainerEgressOptions(), RuntimeImageAllowlist.Empty, root);
        RemoteConnectorNetworkDecision decision = runtime.CheckRemoteConnectorNetwork(new("owner", false), subject, "http", endpointHost, 443);
        Assert.False(decision.Enabled);
        Assert.Equal("allowed", decision.Decision);
        Assert.False(Directory.Exists(root));
    }

    [Fact]
    public void ExplicitLogicalBlockDoesNotCreateAnApproval()
    {
        using var fixture = new Fixture([], [new(source, host, 443)], store => store.SetRule(new("owner", false), subject, host, 443, false, 0));
        Assert.Equal("blocked", fixture.Check().Decision);
        Assert.Empty(fixture.Runtime.GetNetworkAccessPolicy(new("owner", false)).Policy.Requests);
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public void SharedPrivatePrivilegeMustMatchLogicalPrivilege(bool logicalPrivate, bool outerPrivate)
    {
        using var fixture = new Fixture([Grant(subject, logicalPrivate)], [new(source, host, 443, outerPrivate)]);
        Assert.Equal("private_route_conflict", fixture.Check().Decision);
        Assert.Empty(fixture.Runtime.GetNetworkAccessPolicy(new("owner", false)).Policy.Requests);
    }

    [Fact]
    public void ExplicitPrivateLogicalApprovalDoesNotCreateSharedGatewayLanRoute()
    {
        using var fixture = new Fixture([Grant(subject, true)], []);
        Assert.Equal("outer_route_required", fixture.Check().Decision);
        Assert.Empty(fixture.Runtime.GetNetworkAccessPolicy(new("owner", false)).Policy.Requests);
        Assert.DoesNotContain(fixture.Runtime.WithRemotePublicGatewayGrants([Grant(subject, true)]), grant => NetworkAccessPolicyStore.Identity(grant) == gateway);
    }

    [Fact]
    public void PublicLogicalGrantsDeriveOnlyPublicGatewayRoutesAndRespectExplicitCoreBlock()
    {
        using var fixture = new Fixture([], [], store => store.SetRule(new("admin", true), gateway, host, 443, false, 0));
        IReadOnlyList<ContainerEgressGrant> result = fixture.Runtime.WithRemotePublicGatewayGrants([Grant(subject), Grant(subject with { ComponentId = "owner::other" })]);
        Assert.DoesNotContain(result, grant => NetworkAccessPolicyStore.Identity(grant) == gateway);
        using var unblocked = new Fixture([], []);
        result = unblocked.Runtime.WithRemotePublicGatewayGrants([Grant(subject), Grant(subject with { ComponentId = "owner::other" })]);
        ContainerEgressGrant derived = Assert.Single(result, grant => NetworkAccessPolicyStore.Identity(grant) == gateway);
        Assert.False(derived.AllowPrivateNetwork);
        Assert.Equal(host, derived.Destination);
    }

    [Fact]
    public void PlainHttpHostnameRequiresItsPinnedLiteralOuterRoute()
    {
        using var fixture = new Fixture([Grant(subject)], [new(source, host, 443)]);
        Assert.Equal("pin_required", fixture.Check("http").Decision);
        Assert.Equal("outer_route_required", fixture.Check("http", "93.184.216.34").Decision);
        fixture.Accept([new(source, "93.184.216.34", 443)]);
        Assert.Equal("allowed", fixture.Check("http", "93.184.216.34").Decision);
        Assert.Equal("outer_route_required", fixture.Check("http", "93.184.216.35").Decision);
        Assert.Empty(fixture.Runtime.GetNetworkAccessPolicy(new("owner", false)).Policy.Requests);
    }

    [Theory]
    [InlineData("169.254.169.254")]
    [InlineData("metadata.google.internal")]
    public void ProtectedDestinationsCannotCreateApprovableRequests(string destination)
    {
        using var fixture = new Fixture([], []);
        Assert.Equal("blocked", fixture.Runtime.CheckRemoteConnectorNetwork(new("owner", true), subject, "https", destination, 443).Decision);
        Assert.Equal("blocked", fixture.Check("http", "169.254.169.254").Decision);
        Assert.Empty(fixture.Runtime.GetNetworkAccessPolicy(new("owner", true)).Policy.Requests);
    }

    [Fact]
    public void InvalidLoopbackLiteralIsRejectedBeforeAnApprovalCanBeCreated()
    {
        using var fixture = new Fixture([], []);
        Assert.Throws<ArgumentException>(() => fixture.Runtime.CheckRemoteConnectorNetwork(new("owner", true), subject, "https", "127.0.0.1", 443));
        Assert.Empty(fixture.Runtime.GetNetworkAccessPolicy(new("owner", true)).Policy.Requests);
    }

    [Fact]
    public void RevocationAndRoutePrivilegeTransitionsFailClosedWithoutDuplicateRequest()
    {
        using var fixture = new Fixture([Grant(subject)], [new(source, host, 443)]);
        fixture.Runtime.BeginAuthorizationTransition(fixture.Snapshot([new(source, host, 443, true)]));
        Assert.Equal("transition_unavailable", fixture.Check().Decision);
        fixture.Runtime.RestoreAuthorizationTransition();
        Assert.Equal("allowed", fixture.Check().Decision);
        fixture.Runtime.BeginAuthorizationTransition(fixture.Snapshot([new(source, host, 443)]) with { RemotePermissions = [] });
        Assert.Equal("transition_unavailable", fixture.Check().Decision);
        Assert.Empty(fixture.Runtime.GetNetworkAccessPolicy(new("owner", false)).Policy.Requests);
    }

    [Fact]
    public void UnchangedPermissionSurvivesAnUnrelatedRepresentedPolicyRevision()
    {
        using var fixture = new Fixture([Grant(subject)], [new(source, host, 443)]);
        var policy = (NetworkAccessPolicyStore)typeof(ContainerEgressRuntime).GetField("networkPolicy", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(fixture.Runtime)!;
        policy.SetRule(new("owner", false), subject with { ComponentId = "owner::unrelated" }, "other.example.com", 443, true, policy.Revision);
        Assert.Equal("transition_unavailable", fixture.Check().Decision);
        fixture.Runtime.BeginAuthorizationTransition(fixture.Snapshot([new(source, host, 443)]) with { PolicyRevision = policy.Revision });
        Assert.Equal("allowed", fixture.Check().Decision);
        Assert.Empty(fixture.Runtime.GetNetworkAccessPolicy(new("owner", false)).Policy.Requests);
    }

    [Fact]
    public void TransportBindingChangesOnPrivilegeOrAllocationButNotUnrelatedGeneration()
    {
        using var fixture = new Fixture([Grant(subject, true)], [new(source, host, 443, true)]);
        string initial = fixture.Check().RouteBinding;
        Assert.NotEmpty(initial);
        fixture.Accept([new(source, host, 443, true)]);
        Assert.Equal(initial, fixture.Check().RouteBinding);
        fixture.Runtime.CompleteAuthorizationTransition(fixture.Snapshot([new(source, host, 443, true)]) with { Allocations = new Dictionary<string, string> { [source] = "replacement" } });
        Assert.NotEqual(initial, fixture.Check().RouteBinding);
        fixture.Runtime.CompleteAuthorizationTransition(fixture.Snapshot([new(source, host, 443)]) with { RemotePermissions = [new(subject, host, 443, false)] });
        RemoteConnectorNetworkDecision downgraded = fixture.Check();
        Assert.Equal("allowed", downgraded.Decision);
        Assert.NotEqual(initial, downgraded.RouteBinding);
    }

    [Fact]
    public void SourceAllocationReplacementAndUnavailableStateAreOperationalErrors()
    {
        using var fixture = new Fixture([Grant(subject)], [new(source, host, 443)]);
        fixture.Runtime.BeginAuthorizationTransition(fixture.Snapshot([new(source, host, 443)]) with { Allocations = new Dictionary<string, string> { [source] = "new-container" } });
        Assert.Equal("transition_unavailable", fixture.Check().Decision);
        fixture.Runtime.CompleteAuthorizationTransition(fixture.Snapshot([new(source, host, 443)]));
        typeof(ContainerEgressRuntime).GetField("ready", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(fixture.Runtime, false);
        Assert.Equal("transition_unavailable", fixture.Check().Decision);
        Assert.Empty(fixture.Runtime.GetNetworkAccessPolicy(new("owner", false)).Policy.Requests);
    }

    private static ContainerEgressGrant Grant(NetworkAccessIdentity identity, bool privateNetwork = false)
        => new()
        {
            ScopeType = identity.ScopeType,
            UserId = identity.UserId,
            AgentId = identity.AgentId,
            Role = identity.Role,
            ComponentId = identity.ComponentId,
            Destination = host,
            Port = 443,
            AllowPrivateNetwork = privateNetwork
        };

    private sealed class Fixture : IDisposable
    {
        private readonly string root = Path.Combine(Path.GetTempPath(), "kautuka-remote-policy-" + Guid.NewGuid().ToString("N"));
        private readonly RemoteConnectorPermission[] permissions;
        private readonly long revision;
        public string PolicyPath => Path.Combine(root, "egress", "policy", "state.json");
        public ContainerEgressRuntime Runtime { get; }
        public Fixture(ContainerEgressGrant[] logicalGrants, EgressTcpRule[] outerRoutes, Action<NetworkAccessPolicyStore>? seed = null)
        {
            var store = new NetworkAccessPolicyStore(PolicyPath);
            seed?.Invoke(store);
            revision = store.Revision;
            permissions = ContainerEgressRuntime.RemotePermissions(store.EffectiveGrants(logicalGrants));
            Runtime = new(new PodmanCommandRunner((_, _) => throw new InvalidOperationException("Checks must not invoke Podman.")),
                new ContainerEgressOptions { Enabled = true }, RuntimeImageAllowlist.Empty, root);
            Accept(outerRoutes);
            typeof(ContainerEgressRuntime).GetField("ready", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(Runtime, true);
        }
        public EgressAuthorizationSnapshot Snapshot(EgressTcpRule[] routes)
            => new(Guid.NewGuid().ToString("N"), new Dictionary<string, NetworkAccessIdentity> { [source] = gateway }, routes,
                new Dictionary<string, string> { [source] = "gateway-container" })
            { RemotePermissions = permissions, PolicyRevision = revision };
        public void Accept(EgressTcpRule[] routes) => Runtime.CompleteAuthorizationTransition(Snapshot(routes));
        public RemoteConnectorNetworkDecision Check(string scheme = "https", string? pin = null)
            => Runtime.CheckRemoteConnectorNetwork(new("owner", false), subject, scheme, host, 443, pin);
        public void Dispose() { Runtime.Dispose(); if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
