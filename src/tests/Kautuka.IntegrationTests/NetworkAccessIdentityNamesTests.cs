using System.Text.Json;

using Kautuka.Gateway.Endpoints.Runtime;
using Kautuka.Gateway.Domain.Stores;
using Kautuka.Manager.Protos;

using Microsoft.Extensions.DependencyInjection;

namespace Kautuka.IntegrationTests;

public sealed class NetworkAccessIdentityNamesTests
{
    [Fact]
    public void RuntimeWorkspaceIdentityResolvesThroughCatalogLookup()
    {
        using ServiceProvider services = new ServiceCollection()
            .AddSingleton<IGatewayCatalogStore>(new NaradTestSupport.FakeCatalogStore())
            .BuildServiceProvider();
        NetworkAccessIdentityMessage scoped = identity.Clone();
        scoped.UserId = "user-1";
        scoped.AgentId = "user-1-agent-1";
        NetworkAccessIdentityDto result = GatewayNetworkAccessEndpointMappings.CreateIdentityDescriber(
            caller with { Id = "user-1" }, services)(scoped);
        Assert.Equal("Agent One", result.AgentName);
        Assert.Equal(scoped.AgentId, result.AgentId);
    }

    [Theory]
    [InlineData("user-f2c14ef823c04a1a9ef0a34a84b8b43e", "agent-1794103575374")]
    [InlineData("owner with spaces", "agent/with/slashes")]
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb")]
    public void RuntimeScopeUsesCanonicalWorkspaceNameAndKeepsIdentity(string ownerId, string agentId)
    {
        GatewayAgent agent = Agent(ownerId) with { Id = agentId, Name = "Desk Assistant" };
        NetworkAccessIdentityMessage scoped = identity.Clone();
        scoped.UserId = ownerId;
        scoped.AgentId = WorkspaceRuntimeContextResolver.BuildAgentWorkspaceScopeId(agentId, ownerId);
        GatewayUser user = caller with { Id = ownerId };
        NetworkAccessIdentityDto result = GatewayNetworkAccessEndpointMappings.DescribeIdentity(scoped, user, agent, null, user);
        Assert.Equal("Desk Assistant", result.AgentName);
        Assert.Equal(scoped.AgentId, result.AgentId);
        Assert.Equal("Renamed assistant", GatewayNetworkAccessEndpointMappings.DescribeIdentity(
            scoped, user, agent with { Name = "Renamed assistant" }, null, user).AgentName);
        Assert.Null(GatewayNetworkAccessEndpointMappings.DescribeIdentity(
            scoped, caller with { Role = "admin" }, agent, null, user).AgentName);
    }

    [Fact]
    public void RuntimeScopeCannotBorrowAnotherOwnersAgentName()
    {
        NetworkAccessIdentityMessage scoped = identity.Clone();
        scoped.AgentId = WorkspaceRuntimeContextResolver.BuildAgentWorkspaceScopeId("agent", "victim");
        Assert.Null(GatewayNetworkAccessEndpointMappings.DescribeIdentity(scoped,
            caller with { Role = "admin" }, Agent("victim") with { Scope = "user" }, null, caller).AgentName);
        Assert.Null(GatewayNetworkAccessEndpointMappings.DescribeIdentity(scoped,
            caller, Agent("owner"), null, caller).AgentName);
    }

    private sealed class CountingServices : IServiceProvider
    {
        internal int Calls { get; private set; }
        public object? GetService(Type serviceType) { Calls++; return null; }
    }

    [Fact]
    public void RepeatedIdentitiesAreCachedOnlyWithinOneResponse()
    {
        var services = new CountingServices();
        Func<NetworkAccessIdentityMessage, NetworkAccessIdentityDto> describe = GatewayNetworkAccessEndpointMappings.CreateIdentityDescriber(caller, services);
        for (int index = 0; index < 50; index++) describe(identity.Clone());
        Assert.Equal(2, services.Calls);
        GatewayNetworkAccessEndpointMappings.CreateIdentityDescriber(caller, services)(identity.Clone());
        Assert.Equal(4, services.Calls);
    }

    private static readonly GatewayUser caller = new("owner", "alice", "", "user", true, null, null);
    private static readonly NetworkAccessIdentityMessage identity = new()
    {
        ScopeType = "agent",
        UserId = "owner",
        AgentId = "agent",
        Role = "connector",
        ComponentId = "connector",
    };
    private static GatewayAgent Agent(string owner) => new("agent", "Research assistant", "model", "", "disabled", [], [], [], [], owner, "");
    private static GatewayMcpServer Connector(string owner) => new("connector", "Mail tools", "", "", null,
        "container", null, null, null, [], null, null, null, null, null, null, null, true, false, "", "", "user", owner);

    [Fact]
    public void OwnNamesSerializeThroughExistingAotAuditContextWithoutChangingIdentity()
    {
        NetworkAccessIdentityMessage scoped = identity.Clone(); scoped.ComponentId = "owner::connector";
        NetworkAccessIdentityDto result = GatewayNetworkAccessEndpointMappings.DescribeIdentity(scoped, caller, Agent("owner"), Connector("owner"), caller);
        var page = new NetworkAuditPageDto([new(1, "event", "", "", result, "container", "", "example.com", 443,
            "denied", false, true, 0, 0, 0)], 0, 0, 0, null);
        using JsonDocument json = JsonDocument.Parse(JsonSerializer.Serialize(page, KautukaJsonContext.Default.NetworkAuditPageDto));
        JsonElement value = json.RootElement.GetProperty("events")[0].GetProperty("identity");
        Assert.Equal("Mail tools", value.GetProperty("displayName").GetString());
        Assert.Equal("Research assistant", value.GetProperty("agentName").GetString());
        Assert.Equal("alice", value.GetProperty("userName").GetString());
        Assert.Equal("owner::connector", value.GetProperty("componentId").GetString());
        Assert.Equal("owner", value.GetProperty("userId").GetString());
    }

    [Fact]
    public void ForeignScopedPrefixIsNotReinterpretedAsOwnConnector()
    {
        NetworkAccessIdentityMessage scoped = identity.Clone(); scoped.ComponentId = "victim::connector";
        NetworkAccessIdentityDto result = GatewayNetworkAccessEndpointMappings.DescribeIdentity(scoped, caller, Agent("owner"), Connector("owner"), caller);
        Assert.Null(result.DisplayName);
    }

    [Fact]
    public void ForeignCatalogObjectsCannotLeakThroughAnOwnedIdentity()
    {
        NetworkAccessIdentityDto result = GatewayNetworkAccessEndpointMappings.DescribeIdentity(identity, caller,
            Agent("victim"), Connector("victim"), caller with { Id = "victim", Username = "private name" });
        Assert.Null(result.DisplayName);
        Assert.Null(result.AgentName);
        Assert.Null(result.UserName);
    }

    [Fact]
    public void OrdinaryCallerCannotEnrichForeignIdentityEvenIfCatalogObjectShared()
    {
        NetworkAccessIdentityMessage foreign = identity.Clone(); foreign.UserId = "victim";
        NetworkAccessIdentityDto result = GatewayNetworkAccessEndpointMappings.DescribeIdentity(foreign, caller,
            Agent("victim") with { Scope = "system" }, Connector("victim") with { Scope = "system" }, caller with { Id = "victim" });
        Assert.Null(result.DisplayName);
        Assert.Null(result.AgentName);
        Assert.Null(result.UserName);
    }

    [Fact]
    public void AdministratorCanIdentifyAnotherOwnerAndCoreService()
    {
        GatewayUser admin = caller with { Role = "admin" };
        NetworkAccessIdentityMessage foreign = identity.Clone(); foreign.UserId = "victim";
        NetworkAccessIdentityDto result = GatewayNetworkAccessEndpointMappings.DescribeIdentity(foreign, admin,
            Agent("victim"), Connector("victim"), caller with { Id = "victim", Username = "bob" });
        Assert.Equal("bob", result.UserName);
        // Administrator network visibility does not expand private catalogue access.
        Assert.Null(result.DisplayName);
        Assert.Null(result.AgentName);
        result = GatewayNetworkAccessEndpointMappings.DescribeIdentity(new() { ScopeType = "core", UserId = "system", Role = "core", ComponentId = "gateway" }, admin, null, null, null);
        Assert.Equal("Gateway", result.DisplayName);
    }
}
