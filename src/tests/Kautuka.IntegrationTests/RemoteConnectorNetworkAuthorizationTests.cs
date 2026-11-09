using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

using Kautuka.Manager.RuntimeContainer.Configuration;
using Kautuka.Manager.RuntimeContainer.Hosting;

namespace Kautuka.IntegrationTests;

public sealed class RemoteConnectorNetworkAuthorizationTests
{
    private static readonly NetworkAccessIdentity subject = new("agent", "owner", "workspace-alpha", "agent", "workspace-alpha");
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
    public void AgentPermissionIsSharedAcrossConnectorsButIsolatedByUserAgentAndPort()
    {
        using var fixture = new Fixture([Grant(subject)], [new(source, host, 443)]);
        Assert.Equal("allowed", fixture.Check().Decision);
        Assert.Equal("missing_permission", fixture.Runtime.CheckRemoteConnectorNetwork(new("owner", false),
            subject with { AgentId = "workspace-beta", ComponentId = "workspace-beta" }, "https", host, 443).Decision);
        Assert.Equal("missing_permission", fixture.Runtime.CheckRemoteConnectorNetwork(new("owner", false), subject, "https", host, 8443).Decision);
        Assert.Throws<UnauthorizedAccessException>(() => fixture.Runtime.CheckRemoteConnectorNetwork(new("another-owner", false), subject, "https", host, 443));
        Assert.Throws<UnauthorizedAccessException>(() => fixture.Runtime.CheckRemoteConnectorNetwork(new("owner", false), subject with { ComponentId = "owner::other" }, "https", host, 443));
    }

    [Fact]
    public void ExistingConnectorAllowIsSharedWithAgentSourceAndRemoteRequestWithoutChangingAttribution()
    {
        NetworkAccessIdentity connector = new("agent", "owner", "workspace-alpha", "connector", "owner::mail");
        using var fixture = new Fixture([], [new(source, host, 443)], store => store.SetRule(new("owner", false), connector, host, 443, true, 0));

        ContainerEgressGrant shared = Assert.Single(new NetworkAccessPolicyStore(fixture.PolicyPath).EffectiveGrants([]));
        Assert.Equal("agent", shared.Role);
        Assert.Equal("workspace-alpha", shared.ComponentId);
        Assert.Equal("allowed", fixture.Check().Decision);
        Assert.True(ContainerEgressRuntime.GrantAppliesToSource(shared,
            new ContainerEgressRuntime.SourceIdentity(new("owner", "workspace-alpha"), "agent", "workspace-alpha")));
        Assert.True(ContainerEgressRuntime.GrantAppliesToSource(shared,
            new ContainerEgressRuntime.SourceIdentity(new("owner", "workspace-alpha"), "connector", "owner::mail")));
        Assert.False(ContainerEgressRuntime.GrantAppliesToSource(shared,
            new ContainerEgressRuntime.SourceIdentity(new("owner", "workspace-beta"), "agent", "workspace-beta")));
        Assert.False(ContainerEgressRuntime.GrantAppliesToSource(shared,
            new ContainerEgressRuntime.SourceIdentity(new("system", "gateway"), "core", "gateway")));
        Assert.Equal(subject, Assert.Single(new NetworkAccessPolicyStore(fixture.PolicyPath).Read(new("owner", false)).Rules).Identity);
    }

    [Fact]
    public void ConflictingComponentBlockAndAllowFailClosedAcrossAgentScope()
    {
        NetworkAccessIdentity connector = new("agent", "owner", "workspace-alpha", "connector", "owner::mail");
        using var fixture = new Fixture([], [new(source, host, 443)], store =>
            store.SetRule(new("owner", false), subject, host, 443, true, 0),
            path => AddLegacyRule(path, connector, allowed: false));

        Assert.Empty(new NetworkAccessPolicyStore(fixture.PolicyPath).EffectiveGrants([]));
        Assert.Equal("blocked", fixture.Check().Decision);
        Assert.Empty(fixture.Runtime.GetNetworkAccessPolicy(new("owner", false)).Policy.Requests);
    }

    [Fact]
    public void LegacyAgentScopedRemoteConnectorBlockCannotBeBypassedByConnectorAllow()
    {
        NetworkAccessIdentity legacyRemote = new("agent", "owner", "workspace-alpha", "remote-connector", "owner::mail");
        NetworkAccessIdentity connector = new("agent", "owner", "workspace-alpha", "connector", "owner::mail");
        using var fixture = new Fixture([], [new(source, host, 443)], store =>
            store.SetRule(new("owner", false), subject, host, 443, true, 0),
            path => AddLegacyRule(path, legacyRemote, allowed: false));

        Assert.Empty(new NetworkAccessPolicyStore(fixture.PolicyPath).EffectiveGrants([]));
        Assert.Equal("blocked", fixture.Check().Decision);
        Assert.Empty(fixture.Runtime.GetNetworkAccessPolicy(new("owner", false)).Policy.Requests);
    }

    [Fact]
    public void ConflictingPublicAndPrivateApprovalsDoNotCreatePrivateAgentEntitlement()
    {
        NetworkAccessIdentity connector = new("agent", "owner", "workspace-alpha", "connector", "owner::mail");
        using var fixture = new Fixture([], [new(source, host, 443, true)], store =>
            store.SetRule(new("owner", false), subject, host, 443, true, 0),
            path => AddLegacyRule(path, connector, allowed: true, privateNetwork: true));

        Assert.Empty(new NetworkAccessPolicyStore(fixture.PolicyPath).EffectiveGrants([]));
        Assert.Equal("blocked", fixture.Check().Decision);
        Assert.Empty(fixture.Runtime.GetNetworkAccessPolicy(new("owner", false)).Policy.Requests);
    }

    [Fact]
    public void CanonicalManagementIdentityPassesActualManagerValidation()
    {
        NetworkAccessIdentity management = new("management", "owner", "management", "remote-connector", "owner::mail");
        using var fixture = new Fixture([Grant(management)], [new(source, host, 443)]);
        RemoteConnectorNetworkDecision allowed = fixture.Runtime.CheckRemoteConnectorNetwork(new("owner", false), management, "https", host, 443);
        Assert.Equal("allowed", allowed.Decision);
        Assert.Throws<ArgumentException>(() => fixture.Runtime.CheckRemoteConnectorNetwork(new("owner", false), management with { AgentId = "" }, "https", host, 443));
    }

    [Fact]
    public void ManagementConnectorPermissionsRemainScopedToConnector()
    {
        NetworkAccessIdentity managementMail = new("management", "owner", "management", "remote-connector", "owner::mail");
        using var fixture = new Fixture([Grant(managementMail)], [new(source, host, 443)]);

        Assert.Equal("allowed", fixture.Runtime.CheckRemoteConnectorNetwork(new("owner", false), managementMail, "https", host, 443).Decision);
        Assert.Equal("missing_permission", fixture.Runtime.CheckRemoteConnectorNetwork(new("owner", false),
            managementMail with { ComponentId = "owner::search" }, "https", host, 443).Decision);
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
    public void ExplicitAgentBlockDoesNotCreateAnApproval()
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
    public void LogicalAllowRequiresSeparateCoreGatewayRouteAndCreatesAdminRequest()
    {
        using var fixture = new Fixture([Grant(subject)], []);
        RemoteConnectorNetworkDecision decision = fixture.Check();
        Assert.Equal("outer_route_required", decision.Decision);
        Assert.Contains("separate administrator route approval", decision.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(fixture.Runtime.GetNetworkAccessPolicy(new("owner", false)).Policy.Requests);
        NetworkAccessRequest request = Assert.Single(fixture.Runtime.GetNetworkAccessPolicy(new("owner", true)).Policy.Requests);
        Assert.Equal(gateway, request.Identity);
        Assert.Equal(host, request.Destination);
        Assert.Equal(443, request.Port);
        Assert.Equal("pending", request.Status);
        ContainerEgressGrant[] effective = [.. new NetworkAccessPolicyStore(fixture.PolicyPath).EffectiveGrants([Grant(subject)])];
        Assert.Contains(effective, grant => NetworkAccessPolicyStore.Identity(grant) == subject);
        Assert.DoesNotContain(effective, grant => NetworkAccessPolicyStore.Identity(grant) == gateway);
    }

    [Fact]
    public void ExplicitCoreBlockIsAuthoritativeAndDoesNotCreateAnotherGatewayRequest()
    {
        using var fixture = new Fixture([Grant(subject)], [], store => store.SetRule(new("owner", true), gateway, host, 443, false, 0));
        Assert.Equal("blocked", fixture.Check().Decision);
        Assert.Empty(fixture.Runtime.GetNetworkAccessPolicy(new("owner", true)).Policy.Requests);
    }

    [Fact]
    public void MissingGatewayAllocationAndUnacceptedLogicalAllowDoNotCreateCoreRequests()
    {
        using (var fixture = new Fixture([Grant(subject)], []))
        {
            EgressAuthorizationSnapshot detached = fixture.Snapshot([]) with
            {
                Sources = new Dictionary<string, NetworkAccessIdentity>(),
                Allocations = new Dictionary<string, string>(),
            };
            fixture.Runtime.CompleteAuthorizationTransition(detached);
            Assert.Equal("transition_unavailable", fixture.Check().Decision);
            Assert.Empty(fixture.Runtime.GetNetworkAccessPolicy(new("owner", true)).Policy.Requests);
        }

        using (var fixture = new Fixture([], []))
        {
            var policy = (NetworkAccessPolicyStore)typeof(ContainerEgressRuntime)
                .GetField("networkPolicy", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(fixture.Runtime)!;
            policy.SetRule(new("owner", false), subject, host, 443, true, policy.Revision);
            EgressAuthorizationSnapshot candidate = fixture.Snapshot([]) with
            {
                RemotePermissions = [new(subject, host, 443, false)],
                PolicyRevision = policy.Revision,
            };
            fixture.Runtime.BeginAuthorizationTransition(candidate);
            Assert.Equal("transition_unavailable", fixture.Check().Decision);
            Assert.Empty(fixture.Runtime.GetNetworkAccessPolicy(new("owner", true)).Policy.Requests);
        }
    }

    [Fact]
    public void PlainHttpHostnameRequiresItsPinnedLiteralOuterRoute()
    {
        using var fixture = new Fixture([Grant(subject)], [new(source, host, 443)]);
        Assert.Equal("pin_required", fixture.Check("http").Decision);
        Assert.Equal("outer_route_required", fixture.Check("http", "93.184.216.34").Decision);
        NetworkAccessRequest request = Assert.Single(fixture.Runtime.GetNetworkAccessPolicy(new("owner", true)).Policy.Requests);
        Assert.Equal(gateway, request.Identity);
        Assert.Equal("93.184.216.34", request.Destination);
        Assert.Equal(443, request.Port);
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

    private static void AddLegacyRule(string path, NetworkAccessIdentity identity, bool allowed, bool privateNetwork = false)
    {
        JsonObject state = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        string id = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\0', identity.ScopeType,
            identity.UserId, identity.AgentId, identity.Role, identity.ComponentId, host, "443"))));
        state["rules"]!.AsArray().Add(new JsonObject
        {
            ["id"] = id,
            ["identity"] = new JsonObject
            {
                ["scopeType"] = identity.ScopeType,
                ["userId"] = identity.UserId,
                ["agentId"] = identity.AgentId,
                ["role"] = identity.Role,
                ["componentId"] = identity.ComponentId,
            },
            ["destination"] = host,
            ["port"] = 443,
            ["allowed"] = allowed,
            ["updatedAt"] = "2026-01-01T00:00:00+00:00",
            ["allowPrivateNetwork"] = privateNetwork,
            ["privateNetworkApprovalExplicit"] = privateNetwork,
        });
        File.WriteAllText(path, state.ToJsonString());
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string root = Path.Combine(Path.GetTempPath(), "kautuka-remote-policy-" + Guid.NewGuid().ToString("N"));
        private readonly RemoteConnectorPermission[] permissions;
        private readonly long revision;
        public string PolicyPath => Path.Combine(root, "egress", "policy", "state.json");
        public ContainerEgressRuntime Runtime { get; }
        public Fixture(ContainerEgressGrant[] logicalGrants, EgressTcpRule[] outerRoutes, Action<NetworkAccessPolicyStore>? seed = null,
            Action<string>? seedLegacy = null)
        {
            NetworkAccessPolicyStore store = new(PolicyPath);
            seed?.Invoke(store);
            seedLegacy?.Invoke(PolicyPath);
            store = new NetworkAccessPolicyStore(PolicyPath);
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
