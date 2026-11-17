using System.Net;
using System.Text.Json;

using Kautuka.NetworkAccessController;

namespace Kautuka.IntegrationTests.NetworkAccess;

public sealed partial class V7_4_And_V7_12_GatewayPrivilegeSyncTests
{
    [Theory]
    [InlineData("scope")]
    [InlineData("destination")]
    [InlineData("kind")]
    public async Task RulePayloadWithNullRequiredFieldsReturnsBadRequest(string missing)
    {
        await using GatewayNacFixture fixture = await GatewayNacFixture.CreateAsync();
        string admin = await fixture.SetupAndLoginFirstAdminAsync("admin", "Testing1!");
        using HttpResponseMessage response = await fixture.SendRawJsonAsync(HttpMethod.Put, "/api/network-access/rules", new
        {
            scopeKind = missing == "scope" ? null : "system",
            destination = missing == "destination" ? null : "example.com",
            destKind = missing == "kind" ? null : "host",
            port = 443,
            allowed = true,
            expectedRevision = fixture.PolicyStore.Revision,
        }, admin);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CompatibilityPayloadWithoutRelaxationsReturnsBadRequest(bool nullEntry)
    {
        await using GatewayNacFixture fixture = await GatewayNacFixture.CreateAsync();
        string admin = await fixture.SetupAndLoginFirstAdminAsync("admin", "Testing1!");
        using HttpResponseMessage response = await fixture.SendRawJsonAsync(HttpMethod.Post,
            "/api/network-access/compat/sha256:" + new string('a', 64), new { reason = "test", relaxations = nullEntry ? new string?[] { null } : null }, admin);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task NetworkAccessLimitsSettingsAreAdminOnlyAndPersistThroughManager()
    {
        await using GatewayNacFixture fixture = await GatewayNacFixture.CreateAsync();
        string admin = await fixture.SetupAndLoginFirstAdminAsync("admin", "Testing1!");
        using JsonDocument defaults = await fixture.SendGetAsync("/api/settings/system/network-access", admin);
        Assert.Equal(500, defaults.RootElement.GetProperty("global").GetInt32());
        Assert.Equal(64, defaults.RootElement.GetProperty("perUser").GetInt32());
        using JsonDocument saved = await fixture.SendJsonAsync(HttpMethod.Put, "/api/settings/system/network-access", new { global = 100, perUser = 10 }, admin);
        Assert.Equal(100, saved.RootElement.GetProperty("global").GetInt32());
        Assert.Equal(new NetworkAccessApprovalLimits(100, 10), fixture.PolicyStore.GetApprovalLimits(new NetworkAccessActor("admin", true)));
        using JsonDocument invite = await fixture.SendJsonAsync(HttpMethod.Post, "/api/settings/users/invite", new { username = "alice", email = "alice@example.com", password = "Testing1!", role = "user" }, admin);
        string alice = await LoginAsync(fixture, "alice");
        using HttpResponseMessage denied = await fixture.SendRawJsonAsync(HttpMethod.Put, "/api/settings/system/network-access", new { global = 200, perUser = 20 }, alice);
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        Assert.Equal(new NetworkAccessApprovalLimits(100, 10), fixture.PolicyStore.GetApprovalLimits(new NetworkAccessActor("admin", true)));
    }
    [Fact]
    public async Task RequestsResolveUserScopedConnectorToCatalogueName()
    {
        await using GatewayNacFixture fixture = await GatewayNacFixture.CreateAsync();
        string token = await fixture.SetupAndLoginFirstAdminAsync("admin", "Testing1!");
        string userId = fixture.UserStore.ListUsers().Single(user => user.Username == "admin").Id;
        await fixture.CatalogStore.SaveMcpServerAsync("web", new GatewayConnectorUpsertRequest
        {
            Name = "Web Tools",
            Transport = "http",
            HttpUrl = "https://example.com/mcp",
            Enabled = true,
        }, "system", null, TestContext.Current.CancellationToken);
        fixture.Registry.Register(new RegistryEntry(
            "10.247.1.41", userId, "agent-one", "ctr-web", "connector", $"{userId}::web", "isolated", false, "normal", "user-agent", null, DateTimeOffset.UtcNow));
        _ = fixture.Controller.EvaluateContainerConnection(new ContainerConnectionInput("10.247.1.41", "93.184.216.34", 443, "www.bbc.com"));
        using JsonDocument result = await fixture.SendGetAsync("/api/network-access/requests", token);
        JsonElement request = result.RootElement.GetProperty("requests").EnumerateArray().Single();
        Assert.Equal("Web Tools", request.GetProperty("connectorDisplayName").GetString());
        Assert.Equal($"{userId}::web", request.GetProperty("connectorId").GetString());
    }
    [Fact]
    public async Task V7_13_DirectDecisionHttpEnforcesOwnerAndAllAgentsPrivileges_WithNegativeControl()
    {
        await using GatewayNacFixture fixture = await GatewayNacFixture.CreateAsync();
        string adminToken = await fixture.SetupAndLoginFirstAdminAsync("admin", "Testing1!");

        using JsonDocument aliceInvite = await fixture.SendJsonAsync(
            HttpMethod.Post,
            "/api/settings/users/invite",
            new { username = "alice", email = "alice@example.com", password = "Testing1!", role = "user" },
            adminToken);
        string aliceId = aliceInvite.RootElement.GetProperty("user").GetProperty("id").GetString()!;
        using JsonDocument bobInvite = await fixture.SendJsonAsync(
            HttpMethod.Post,
            "/api/settings/users/invite",
            new { username = "bob", email = "bob@example.com", password = "Testing1!", role = "user" },
            adminToken);
        string bobId = bobInvite.RootElement.GetProperty("user").GetProperty("id").GetString()!;
        string adminId = fixture.UserStore.ListUsers().Single(user => user.Username == "admin").Id;
        string aliceToken = await LoginAsync(fixture, "alice");

        // Alice has permission to approve her own agent requests, but not all-agent scope yet.
        _ = await fixture.SendJsonAsync(
            HttpMethod.Put,
            $"/api/network-access/users/{aliceId}/privileges",
            new { canApproveOwn = true, canAllowAllAgents = false },
            adminToken);
        _ = await fixture.SendJsonAsync(
            HttpMethod.Put,
            $"/api/network-access/users/{bobId}/privileges",
            new { canApproveOwn = true, canAllowAllAgents = false },
            adminToken);

        fixture.Registry.Register(new RegistryEntry(
            "10.247.1.41", aliceId, "alice-agent-one", "ctr-alice-one", "agent-sandbox", null, "agent-sandbox", false, "normal", "user-agent", null, DateTimeOffset.UtcNow));
        fixture.Registry.Register(new RegistryEntry(
            "10.247.1.42", aliceId, "alice-agent-two", "ctr-alice-two", "agent-sandbox", null, "agent-sandbox", false, "normal", "user-agent", null, DateTimeOffset.UtcNow));
        fixture.Registry.Register(new RegistryEntry(
            "10.247.1.43", bobId, "bob-agent", "ctr-bob", "agent-sandbox", null, "agent-sandbox", false, "normal", "user-agent", null, DateTimeOffset.UtcNow));

        NetworkAccessDecisionResult aliceOwnRequest = fixture.Controller.EvaluateContainerConnection(
            new ContainerConnectionInput("10.247.1.41", "93.184.216.34", 443, "alice-own.example.com"));
        NetworkAccessDecisionResult aliceAllAgentsRequest = fixture.Controller.EvaluateContainerConnection(
            new ContainerConnectionInput("10.247.1.42", "93.184.216.34", 443, "alice-all-agents.example.com"));
        NetworkAccessDecisionResult bobRequest = fixture.Controller.EvaluateContainerConnection(
            new ContainerConnectionInput("10.247.1.43", "93.184.216.34", 443, "bob-private.example.com"));
        Assert.False(aliceOwnRequest.Allowed);
        Assert.False(aliceAllAgentsRequest.Allowed);
        Assert.False(bobRequest.Allowed);

        var adminActor = new NetworkAccessActor(adminId, IsAdmin: true);
        long revisionBeforeCrossUserAttempt = fixture.PolicyStore.Revision;
        string[] rulesBeforeCrossUserAttempt = fixture.PolicyStore.ListRules(adminActor).Rules
            .Select(view => view.Rule.Id)
            .Order(StringComparer.Ordinal)
            .ToArray();

        // The HTTP body cannot forge the authenticated actor; the decision still applies as Alice and is denied cross-user.
        using (HttpResponseMessage crossUser = await fixture.SendRawJsonAsync(
            HttpMethod.Post,
            $"/api/network-access/requests/{bobRequest.RequestId}/decision",
            new
            {
                allow = true,
                scope = "agent",
                expectedRevision = fixture.PolicyStore.Revision,
                actor = new { userId = "admin", isAdmin = true },
            },
            aliceToken))
        {
            Assert.Equal(HttpStatusCode.Forbidden, crossUser.StatusCode);
        }
        Assert.Equal(revisionBeforeCrossUserAttempt, fixture.PolicyStore.Revision);
        Assert.Equal(rulesBeforeCrossUserAttempt, fixture.PolicyStore.ListRules(adminActor).Rules
            .Select(view => view.Rule.Id)
            .Order(StringComparer.Ordinal));
        NetworkAccessRequestView bobRequestAfterDeniedAttempt = fixture.PolicyStore.ListRequests(adminActor).Requests
            .Single(view => view.Request.Id == bobRequest.RequestId);
        Assert.Equal("pending", bobRequestAfterDeniedAttempt.Request.Status);
        NetworkAccessDecisionResult bobStillBlocked = fixture.Controller.EvaluateContainerConnection(
            new ContainerConnectionInput("10.247.1.43", "93.184.216.34", 443, "bob-private.example.com"));
        Assert.False(bobStillBlocked.Allowed);

        using JsonDocument ownAgentAllowed = await fixture.SendJsonAsync(
            HttpMethod.Post,
            $"/api/network-access/requests/{aliceOwnRequest.RequestId}/decision",
            new { allow = true, scope = "agent", expectedRevision = fixture.PolicyStore.Revision },
            aliceToken);
        Assert.Equal("agent", ownAgentAllowed.RootElement.GetProperty("rule").GetProperty("scopeKind").GetString());

        // The single management privilege authorizes Alice's user-scope rule.
        using JsonDocument allAgentsAllowed = await fixture.SendJsonAsync(
            HttpMethod.Post,
            $"/api/network-access/requests/{aliceAllAgentsRequest.RequestId}/decision",
            new { allow = true, scope = "user", expectedRevision = fixture.PolicyStore.Revision },
            aliceToken);
        Assert.Equal("user", allAgentsAllowed.RootElement.GetProperty("rule").GetProperty("scopeKind").GetString());

        // The resulting user rule applies to Alice's other agent as well.
        Assert.True(fixture.Controller.EvaluateContainerConnection(
            new ContainerConnectionInput("10.247.1.41", "93.184.216.34", 443, "alice-all-agents.example.com")).Allowed);
    }

    private static async Task<string> LoginAsync(GatewayNacFixture fixture, string username)
    {
        using JsonDocument login = await fixture.SendJsonAsync(
            HttpMethod.Post,
            "/api/auth/login",
            new { username, password = "Testing1!" },
            accessToken: null);
        return login.RootElement.GetProperty("accessToken").GetString()!;
    }

}
