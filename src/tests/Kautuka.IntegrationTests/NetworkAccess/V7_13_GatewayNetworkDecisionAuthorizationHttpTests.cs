using System.Net;
using System.Text.Json;

using Kautuka.NetworkAccessController;

namespace Kautuka.IntegrationTests.NetworkAccess;

public sealed partial class V7_4_And_V7_12_GatewayPrivilegeSyncTests
{
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

        // Negative control: canApproveOwn alone does not authorize a user-scope rule.
        long revisionBeforeUserScopeAttempt = fixture.PolicyStore.Revision;
        string[] rulesBeforeUserScopeAttempt = fixture.PolicyStore.ListRules(adminActor).Rules
            .Select(view => view.Rule.Id)
            .Order(StringComparer.Ordinal)
            .ToArray();
        using (HttpResponseMessage userScopeDenied = await fixture.SendRawJsonAsync(
            HttpMethod.Post,
            $"/api/network-access/requests/{aliceAllAgentsRequest.RequestId}/decision",
            new { allow = true, scope = "user", expectedRevision = fixture.PolicyStore.Revision },
            aliceToken))
        {
            Assert.Equal(HttpStatusCode.Forbidden, userScopeDenied.StatusCode);
        }
        Assert.Equal(revisionBeforeUserScopeAttempt, fixture.PolicyStore.Revision);
        Assert.Equal(rulesBeforeUserScopeAttempt, fixture.PolicyStore.ListRules(adminActor).Rules
            .Select(view => view.Rule.Id)
            .Order(StringComparer.Ordinal));
        NetworkAccessRequestView allAgentsRequestAfterDeniedAttempt = fixture.PolicyStore.ListRequests(adminActor).Requests
            .Single(view => view.Request.Id == aliceAllAgentsRequest.RequestId);
        Assert.Equal("pending", allAgentsRequestAfterDeniedAttempt.Request.Status);

        _ = await fixture.SendJsonAsync(
            HttpMethod.Put,
            $"/api/network-access/users/{aliceId}/privileges",
            new { canApproveOwn = true, canAllowAllAgents = true },
            adminToken);
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
