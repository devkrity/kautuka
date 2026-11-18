using System.Net;
using System.Text.Json;

using Grpc.Core;

using Kautuka.Manager.Grpc;
using Kautuka.Manager.Protos;
using Kautuka.NetworkAccessController;

namespace Kautuka.IntegrationTests.NetworkAccess;

public sealed partial class V7_4_And_V7_12_GatewayPrivilegeSyncTests
{
    [Fact]
    public async Task GatewayProtectsAdministratorRulesAndReportsTightening()
    {
        await using GatewayNacFixture fixture = await GatewayNacFixture.CreateAsync();
        string adminToken = await fixture.SetupAndLoginFirstAdminAsync("admin", "Testing1!");
        using JsonDocument invite = await fixture.SendJsonAsync(HttpMethod.Post, "/api/settings/users/invite",
            new { username = "alice", email = "alice@example.com", password = "Testing1!", role = "user" }, adminToken);
        string aliceId = invite.RootElement.GetProperty("user").GetProperty("id").GetString()!;
        string aliceToken = await LoginAsync(fixture, "alice");
        var admin = new NetworkAccessActor("admin", true);
        fixture.PolicyStore.SetUserPrivileges(admin, aliceId, canApproveOwn: true);
        var rule = fixture.PolicyStore.SetRule(admin, new RuleWriteSpec("agent", aliceId, "secretary", null, "example.com", "host", 443, true), fixture.PolicyStore.Revision).Rule.Rule;
        using JsonDocument listed = await fixture.SendGetAsync("/api/network-access/rules", aliceToken);
        Assert.True(listed.RootElement.GetProperty("rules").EnumerateArray().Single(r => r.GetProperty("id").GetString() == rule.Id).GetProperty("protectedFromUser").GetBoolean());
        using (HttpResponseMessage deletion = await fixture.SendRawJsonAsync(HttpMethod.Delete, $"/api/network-access/rules/{rule.Id}?expectedRevision={fixture.PolicyStore.Revision}", new { }, aliceToken))
            Assert.Equal(HttpStatusCode.Forbidden, deletion.StatusCode);
        using JsonDocument blocked = await fixture.SendJsonAsync(HttpMethod.Put, "/api/network-access/rules", new { scopeKind = "agent", userId = aliceId, agentId = "secretary", destination = "example.com", destKind = "host", port = 443, allowed = false, expectedRevision = fixture.PolicyStore.Revision }, aliceToken);
        Assert.False(blocked.RootElement.GetProperty("protectedFromUser").GetBoolean());
        Assert.NotEqual(rule.Id, blocked.RootElement.GetProperty("id").GetString());
        string blockId = blocked.RootElement.GetProperty("id").GetString()!;
        using (HttpResponseMessage deletion = await fixture.SendRawJsonAsync(HttpMethod.Delete, $"/api/network-access/rules/{blockId}?expectedRevision={fixture.PolicyStore.Revision}", new { }, aliceToken))
            Assert.True(deletion.IsSuccessStatusCode);
        Assert.Contains(fixture.PolicyStore.ListRules(admin).Rules, r => r.Rule.Id == rule.Id && r.Rule.Allowed);
    }
    [Theory]
    [InlineData("decision")]
    [InlineData("set-rule")]
    [InlineData("delete-rule")]
    [InlineData("set-compat")]
    [InlineData("delete-compat")]
    public async Task NetworkAccessWritesReportUnconfirmedManagerOutage(string operation)
    {
        await using GatewayNacFixture fixture = await GatewayNacFixture.CreateAsync();
        string admin = await fixture.SetupAndLoginFirstAdminAsync("admin", "Testing1!");
        fixture.FailPrivilegeSync = true;
        string digest = "sha256:" + new string('a', 64);
        (HttpMethod Method, string Path, object Body) target = operation switch
        {
            "decision" => (HttpMethod.Post, "/api/network-access/requests/request-id/decision", new { allow = true, scope = "user", expectedRevision = 0 }),
            "set-rule" => (HttpMethod.Put, "/api/network-access/rules", new { scopeKind = "system", destination = "example.com", destKind = "host", port = 443, allowed = true, expectedRevision = 0 }),
            "delete-rule" => (HttpMethod.Delete, "/api/network-access/rules/rule-id?expectedRevision=0", new { }),
            "set-compat" => (HttpMethod.Post, "/api/network-access/compat/" + digest, new { relaxations = new[] { "writable-rootfs" } }),
            _ => (HttpMethod.Delete, "/api/network-access/compat/" + digest, new { }),
        };
        using HttpResponseMessage response = await fixture.SendRawJsonAsync(target.Method, target.Path, target.Body, admin);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
    }

    [Theory]
    [InlineData("requests")]
    [InlineData("rules")]
    [InlineData("activity")]
    [InlineData("compat")]
    public async Task NetworkAccessListsReportManagerOutageAsUnavailable(string section)
    {
        await using GatewayNacFixture fixture = await GatewayNacFixture.CreateAsync();
        string admin = await fixture.SetupAndLoginFirstAdminAsync("admin", "Testing1!");
        fixture.FailPrivilegeSync = true;
        using HttpResponseMessage response = await fixture.SendRawJsonAsync(HttpMethod.Get,
            $"/api/network-access/{section}", new { }, admin);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
    }

    [Theory]
    [InlineData("read")]
    [InlineData("user")]
    [InlineData("system")]
    public async Task BrakeEndpointsReportManagerOutageAsUnavailable(string action)
    {
        await using GatewayNacFixture fixture = await GatewayNacFixture.CreateAsync();
        string admin = await fixture.SetupAndLoginFirstAdminAsync("admin", "Testing1!");
        fixture.FailPrivilegeSync = true;
        HttpMethod method = action == "read" ? HttpMethod.Get : HttpMethod.Put;
        string path = action == "system" ? "/api/network-access/brake/system" : "/api/network-access/brake";
        using HttpResponseMessage response = await fixture.SendRawJsonAsync(method, path, new { on = true }, admin);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
    }

    [Fact]
    public async Task PrivilegeReadReportsManagerOutageAsUnavailable()
    {
        await using GatewayNacFixture fixture = await GatewayNacFixture.CreateAsync();
        string admin = await fixture.SetupAndLoginFirstAdminAsync("admin", "Testing1!");
        string adminId = fixture.UserStore.ListUsers().Single(user => user.Username == "admin").Id;
        fixture.FailPrivilegeSync = true;
        using HttpResponseMessage response = await fixture.SendRawJsonAsync(HttpMethod.Get,
            $"/api/network-access/users/{adminId}/privileges", new { }, admin);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
    }

    [Fact]
    public async Task PrivilegeUpdateReportsManagerOutageAsUnavailable()
    {
        await using GatewayNacFixture fixture = await GatewayNacFixture.CreateAsync();
        string admin = await fixture.SetupAndLoginFirstAdminAsync("admin", "Testing1!");
        string adminId = fixture.UserStore.ListUsers().Single(user => user.Username == "admin").Id;
        fixture.FailPrivilegeSync = true;
        using HttpResponseMessage response = await fixture.SendRawJsonAsync(HttpMethod.Put,
            $"/api/network-access/users/{adminId}/privileges", new { canApproveOwn = false }, admin);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.True(fixture.PolicyStore.CanManageOwnNetworkAccess(adminId));
    }

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
        using (HttpResponseMessage readDenied = await fixture.SendRawJsonAsync(HttpMethod.Get, "/api/settings/system/network-access", new { }, alice))
            Assert.Equal(HttpStatusCode.Forbidden, readDenied.StatusCode);
        using HttpResponseMessage denied = await fixture.SendRawJsonAsync(HttpMethod.Put, "/api/settings/system/network-access", new { global = 200, perUser = 20 }, alice);
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        Assert.Equal(new NetworkAccessApprovalLimits(100, 10), fixture.PolicyStore.GetApprovalLimits(new NetworkAccessActor("admin", true)));
    }

    [Theory]
    [InlineData("GetApprovalLimits")]
    [InlineData("SetApprovalLimits")]
    public async Task ApprovalLimitsInterceptorRejectsNonGatewayCertificates(string method)
    {
        var interceptor = new ManagerClientAuthorizationInterceptor();
        foreach (string name in new[] { ManagerClientIdentities.ChannelHostCommonName, ManagerClientIdentities.UpdateServiceCommonName, "unknown-client" })
        {
            var ctx = new TestServerCallContext(method, name);
            RpcException error = await Assert.ThrowsAsync<RpcException>(() => interceptor.UnaryServerHandler(
                new NetworkAccessActorMessage { UserId = "admin", IsAdmin = true }, ctx,
                static (_, _) => Task.FromResult(new NetworkAccessApprovalLimitsMessage())));
            Assert.Equal(StatusCode.PermissionDenied, error.StatusCode);
        }
        var missing = new TestServerCallContext(method);
        RpcException unauthenticated = await Assert.ThrowsAsync<RpcException>(() => interceptor.UnaryServerHandler(
            new NetworkAccessActorMessage { UserId = "admin", IsAdmin = true }, missing,
            static (_, _) => Task.FromResult(new NetworkAccessApprovalLimitsMessage())));
        Assert.Equal(StatusCode.Unauthenticated, unauthenticated.StatusCode);
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
            "10.247.1.41", userId, "agent-one", "ctr-web", "connector", $"{userId}::web", "isolated", "normal", "user-agent", null, DateTimeOffset.UtcNow));
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
            "10.247.1.41", aliceId, "alice-agent-one", "ctr-alice-one", "agent-sandbox", null, "agent-sandbox", "normal", "user-agent", null, DateTimeOffset.UtcNow));
        fixture.Registry.Register(new RegistryEntry(
            "10.247.1.42", aliceId, "alice-agent-two", "ctr-alice-two", "agent-sandbox", null, "agent-sandbox", "normal", "user-agent", null, DateTimeOffset.UtcNow));
        fixture.Registry.Register(new RegistryEntry(
            "10.247.1.43", bobId, "bob-agent", "ctr-bob", "agent-sandbox", null, "agent-sandbox", "normal", "user-agent", null, DateTimeOffset.UtcNow));

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
