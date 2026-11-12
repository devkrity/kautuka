#pragma warning disable xUnit1051

using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;

using Grpc.Core;

using Kautuka.Gateway.Domain.Auth;
using Kautuka.Gateway.Domain.Autopilot;
using Kautuka.Gateway.Domain.Channels;
using Kautuka.Gateway.Domain.Stores;
using Kautuka.Gateway.Infrastructure.Channels;
using Kautuka.Gateway.Infrastructure.Credentials;
using Kautuka.Manager.Grpc;
using Kautuka.Manager.Protos;
using Kautuka.Manager.RuntimeContainer.Hosting.NetworkAccess;
using Kautuka.NetworkAccessController;
using Kautuka.Protos;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

namespace Kautuka.IntegrationTests.NetworkAccess;

public sealed class V7_4_And_V7_12_GatewayPrivilegeSyncTests
{
    [Fact]
    public async Task V7_4_UserCreation_Promotion_Demotion_SyncsPrivilegesAndRules_WithNegativeControl()
    {
        await using GatewayNacFixture fixture = await GatewayNacFixture.CreateAsync();

        // 1. Setup first admin -> Manager PolicyStore immediately persists (true, true).
        string adminToken = await fixture.SetupAndLoginFirstAdminAsync("admin", "Testing1!");
        string adminId = fixture.UserStore.ListUsers().Single(u => u.Username == "admin").Id;
        Assert.True(fixture.PolicyStore.HasBothPrivileges(adminId));
        AssertPersistedPrivileges(fixture, adminId, expected: true);

        // 2. Invite a second admin -> Manager PolicyStore immediately persists (true, true).
        using JsonDocument invitedAdminDoc = await fixture.SendJsonAsync(
            HttpMethod.Post,
            "/api/settings/users/invite",
            new { username = "second-admin", email = "admin2@example.com", password = "Testing1!", role = "admin" },
            adminToken);
        string secondAdminId = invitedAdminDoc.RootElement.GetProperty("user").GetProperty("id").GetString()!;
        Assert.True(fixture.PolicyStore.HasBothPrivileges(secondAdminId));
        AssertPersistedPrivileges(fixture, secondAdminId, expected: true);

        // 3. Invite a standard user -> privileges default to (false, false).
        using JsonDocument invitedUserDoc = await fixture.SendJsonAsync(
            HttpMethod.Post,
            "/api/settings/users/invite",
            new { username = "alice", email = "alice@example.com", password = "Testing1!", role = "user" },
            adminToken);
        string aliceId = invitedUserDoc.RootElement.GetProperty("user").GetProperty("id").GetString()!;
        Assert.False(fixture.PolicyStore.HasBothPrivileges(aliceId));
        AssertPersistedPrivileges(fixture, aliceId, expected: false);

        // 4. Promote Alice -> privileges persist as (true, true) and user-scope rules evaluate Allowed.
        using JsonDocument promoteDoc = await fixture.SendJsonAsync(
            HttpMethod.Put,
            $"/api/settings/users/{aliceId}/role",
            new { isAdmin = true },
            adminToken);
        Assert.True(promoteDoc.RootElement.GetProperty("success").GetBoolean());
        Assert.True(fixture.PolicyStore.HasBothPrivileges(aliceId));
        AssertPersistedPrivileges(fixture, aliceId, expected: true);

        fixture.Registry.Register(new RegistryEntry(
            "10.247.1.20", aliceId, "agent-1", "ctr-alice-1", "agent-sandbox", null, "agent-sandbox", false, "normal", "user-agent", null, DateTimeOffset.UtcNow));

        using JsonDocument privAfterPromote = await fixture.SendGetAsync($"/api/network-access/users/{aliceId}/privileges", adminToken);
        Assert.True(privAfterPromote.RootElement.GetProperty("canApproveOwn").GetBoolean());
        Assert.True(privAfterPromote.RootElement.GetProperty("canAllowAllAgents").GetBoolean());

        using JsonDocument createdRule = await fixture.SendJsonAsync(
            HttpMethod.Put,
            "/api/network-access/rules",
            new
            {
                scopeKind = "user",
                userId = aliceId,
                destination = "promoted.example.com",
                destKind = "host",
                port = 443,
                allowed = true,
                expectedRevision = fixture.PolicyStore.Revision,
            },
            adminToken);
        Assert.False(createdRule.RootElement.GetProperty("paused").GetBoolean());
        Assert.True(fixture.Controller.EvaluateContainerConnection(
            new ContainerConnectionInput("10.247.1.20", "93.184.216.34", 443, "promoted.example.com")).Allowed);

        // Negative control (.NC): Demoting Alice to standard user clears ON flags, pauses user-scope rules, and denies egress.
        using JsonDocument demoteDoc = await fixture.SendJsonAsync(
            HttpMethod.Put,
            $"/api/settings/users/{aliceId}/role",
            new { isAdmin = false },
            adminToken);
        Assert.True(demoteDoc.RootElement.GetProperty("success").GetBoolean());
        Assert.False(fixture.PolicyStore.HasBothPrivileges(aliceId));
        AssertPersistedPrivileges(fixture, aliceId, expected: false);

        using JsonDocument privAfterDemote = await fixture.SendGetAsync($"/api/network-access/users/{aliceId}/privileges", adminToken);
        Assert.False(privAfterDemote.RootElement.GetProperty("canApproveOwn").GetBoolean());
        Assert.False(privAfterDemote.RootElement.GetProperty("canAllowAllAgents").GetBoolean());

        Assert.False(fixture.Controller.EvaluateContainerConnection(
            new ContainerConnectionInput("10.247.1.20", "93.184.216.34", 443, "promoted.example.com")).Allowed);

        using JsonDocument rulesAfterDemote = await fixture.SendGetAsync($"/api/network-access/rules?userId={aliceId}", adminToken);
        JsonElement demotedRule = rulesAfterDemote.RootElement.GetProperty("rules").EnumerateArray()
            .Single(r => r.GetProperty("destination").GetString() == "promoted.example.com");
        Assert.True(demotedRule.GetProperty("paused").GetBoolean());
    }

    private static void AssertPersistedPrivileges(GatewayNacFixture fixture, string userId, bool expected)
    {
        using var reopenedStore = new NetworkAccessPolicyStore(fixture.PolicyStorePath);
        UserPrivileges privileges = reopenedStore.GetUserPrivileges(new NetworkAccessActor("test-admin", IsAdmin: true), userId);
        Assert.Equal(expected, privileges.CanApproveOwn);
        Assert.Equal(expected, privileges.CanAllowAllAgents);
    }

    [Fact]
    public async Task V7_12_RequestCards_TakeOwnerFlagsFromStoredRecord_NotAdminStatus_WithNegativeControl()
    {
        await using GatewayNacFixture fixture = await GatewayNacFixture.CreateAsync();
        string adminToken = await fixture.SetupAndLoginFirstAdminAsync("admin", "Testing1!");

        // Create standard non-admin owner Bob and explicitly grant him both privileges.
        using JsonDocument invitedBob = await fixture.SendJsonAsync(
            HttpMethod.Post,
            "/api/settings/users/invite",
            new { username = "bob", email = "bob@example.com", password = "Testing1!", role = "user" },
            adminToken);
        string bobId = invitedBob.RootElement.GetProperty("user").GetProperty("id").GetString()!;

        using JsonDocument putPriv = await fixture.SendJsonAsync(
            HttpMethod.Put,
            $"/api/network-access/users/{bobId}/privileges",
            new { canApproveOwn = true, canAllowAllAgents = true },
            adminToken);
        Assert.True(putPriv.RootElement.GetProperty("canApproveOwn").GetBoolean());
        Assert.True(putPriv.RootElement.GetProperty("canAllowAllAgents").GetBoolean());

        fixture.Registry.Register(new RegistryEntry(
            "10.247.1.30", bobId, "bob-agent", "ctr-bob-1", "agent-sandbox", null, "agent-sandbox", false, "normal", "user-agent", null, DateTimeOffset.UtcNow));

        _ = fixture.Controller.EvaluateContainerConnection(new ContainerConnectionInput("10.247.1.30", "93.184.216.34", 443, "card1.example.com"));
        _ = fixture.Controller.EvaluateContainerConnection(new ContainerConnectionInput("10.247.1.30", "93.184.216.34", 443, "card2.example.com"));

        // GET /requests reflects Bob's stored flags (true, true) even though Bob is non-admin.
        using JsonDocument listDoc = await fixture.SendGetAsync("/api/network-access/requests", adminToken);
        long rev = listDoc.RootElement.GetProperty("revision").GetInt64();
        JsonElement[] cards = [.. listDoc.RootElement.GetProperty("requests").EnumerateArray()];
        Assert.Equal(2, cards.Length);
        Assert.All(cards, card =>
        {
            Assert.True(card.GetProperty("ownerCanApproveOwn").GetBoolean());
            Assert.True(card.GetProperty("ownerCanAllowAllAgents").GetBoolean());
        });

        string req1Id = cards.Single(c => c.GetProperty("destination").GetString() == "card1.example.com").GetProperty("id").GetString()!;
        string req2Id = cards.Single(c => c.GetProperty("destination").GetString() == "card2.example.com").GetProperty("id").GetString()!;

        // POST /requests/{id}/decision previously used hardcoded ownerIsAdmin; now reads stored privileges too (v7-12).
        using JsonDocument decideDoc1 = await fixture.SendJsonAsync(
            HttpMethod.Post,
            $"/api/network-access/requests/{req1Id}/decision",
            new { allow = true, scope = "agent", expectedRevision = rev },
            adminToken);
        JsonElement decidedReq1 = decideDoc1.RootElement.GetProperty("request");
        Assert.True(decidedReq1.GetProperty("ownerCanApproveOwn").GetBoolean());
        Assert.True(decidedReq1.GetProperty("ownerCanAllowAllAgents").GetBoolean());
        long revAfterDecide1 = decideDoc1.RootElement.GetProperty("revision").GetInt64();

        // Negative control (.NC): updating stored privileges to (true, false) flips OwnerCanAllowAllAgents to false on both GET and decision DTOs.
        _ = await fixture.SendJsonAsync(
            HttpMethod.Put,
            $"/api/network-access/users/{bobId}/privileges",
            new { canApproveOwn = true, canAllowAllAgents = false },
            adminToken);

        using JsonDocument listAfterRevoke = await fixture.SendGetAsync("/api/network-access/requests", adminToken);
        JsonElement card2 = listAfterRevoke.RootElement.GetProperty("requests").EnumerateArray()
            .Single(c => c.GetProperty("id").GetString() == req2Id);
        Assert.True(card2.GetProperty("ownerCanApproveOwn").GetBoolean());
        Assert.False(card2.GetProperty("ownerCanAllowAllAgents").GetBoolean());

        using JsonDocument decideDoc2 = await fixture.SendJsonAsync(
            HttpMethod.Post,
            $"/api/network-access/requests/{req2Id}/decision",
            new { allow = true, scope = "agent", expectedRevision = revAfterDecide1 },
            adminToken);
        JsonElement decidedReq2 = decideDoc2.RootElement.GetProperty("request");
        Assert.True(decidedReq2.GetProperty("ownerCanApproveOwn").GetBoolean());
        Assert.False(decidedReq2.GetProperty("ownerCanAllowAllAgents").GetBoolean());
    }

    [Fact]
    public async Task V7_4_DemotionIsRejectedWhenManagerPrivilegeSyncFails()
    {
        await using GatewayNacFixture fixture = await GatewayNacFixture.CreateAsync();
        string adminToken = await fixture.SetupAndLoginFirstAdminAsync("admin", "Testing1!");
        using JsonDocument invited = await fixture.SendJsonAsync(
            HttpMethod.Post,
            "/api/settings/users/invite",
            new { username = "alice", email = "alice@example.com", password = "Testing1!", role = "admin" },
            adminToken);
        string aliceId = invited.RootElement.GetProperty("user").GetProperty("id").GetString()!;
        Assert.True(fixture.PolicyStore.HasBothPrivileges(aliceId));

        fixture.FailPrivilegeSync = true;
        using HttpResponseMessage response = await fixture.SendRoleUpdateAsync(aliceId, isAdmin: false, adminToken);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.True(fixture.UserStore.TryGetUserById(aliceId, out GatewayUser? alice));
        Assert.Equal("admin", alice!.Role);
        Assert.True(fixture.PolicyStore.HasBothPrivileges(aliceId));
    }

    [Fact]
    public async Task V7_49_StartupSyncPreservesSavedChoicesAndBackfillsAsSystem_WithNegativeControl()
    {
        await using GatewayNacFixture fixture = await GatewayNacFixture.CreateAsync();
        string adminToken = await fixture.SetupAndLoginFirstAdminAsync("admin", "Testing1!");
        string adminId = fixture.UserStore.ListUsers().Single(u => u.Username == "admin").Id;

        using JsonDocument aliceInvite = await fixture.SendJsonAsync(
            HttpMethod.Post,
            "/api/settings/users/invite",
            new { username = "alice", email = "alice@example.com", password = "Testing1!", role = "user" },
            adminToken);
        string aliceId = aliceInvite.RootElement.GetProperty("user").GetProperty("id").GetString()!;
        using JsonDocument bobInvite = await fixture.SendJsonAsync(
            HttpMethod.Post,
            "/api/settings/users/invite",
            new { username = "bob", email = "bob@example.com", password = "Testing1!", role = "admin" },
            adminToken);
        string bobId = bobInvite.RootElement.GetProperty("user").GetProperty("id").GetString()!;
        using JsonDocument legacyInvite = await fixture.SendJsonAsync(
            HttpMethod.Post,
            "/api/settings/users/invite",
            new { username = "legacy", email = "legacy@example.com", password = "Testing1!", role = "admin" },
            adminToken);
        string legacyAdminId = legacyInvite.RootElement.GetProperty("user").GetProperty("id").GetString()!;

        using JsonDocument aliceSet = await fixture.SendJsonAsync(
            HttpMethod.Put,
            $"/api/network-access/users/{aliceId}/privileges",
            new { canApproveOwn = true, canAllowAllAgents = false },
            adminToken);
        Assert.True(aliceSet.RootElement.GetProperty("canApproveOwn").GetBoolean());
        Assert.False(aliceSet.RootElement.GetProperty("canAllowAllAgents").GetBoolean());

        using JsonDocument bobSet = await fixture.SendJsonAsync(
            HttpMethod.Put,
            $"/api/network-access/users/{bobId}/privileges",
            new { canApproveOwn = false, canAllowAllAgents = false },
            adminToken);
        Assert.False(bobSet.RootElement.GetProperty("canApproveOwn").GetBoolean());
        Assert.False(bobSet.RootElement.GetProperty("canAllowAllAgents").GetBoolean());

        // Simulate an older Gateway user without a corresponding NAC record, then restart against the reopened store.
        JsonNode policy = JsonNode.Parse(await File.ReadAllTextAsync(fixture.PolicyStorePath, TestContext.Current.CancellationToken))!;
        JsonArray records = policy["userPrivileges"]!.AsArray();
        for (int index = records.Count - 1; index >= 0; index--)
        {
            if (string.Equals(records[index]?["userId"]?.GetValue<string>(), legacyAdminId, StringComparison.Ordinal))
            {
                records.RemoveAt(index);
            }
        }
        await File.WriteAllTextAsync(fixture.PolicyStorePath, policy.ToJsonString(), TestContext.Current.CancellationToken);

        var auditRing = new NetworkAccessAuditRing();
        using var reopenedStore = new NetworkAccessPolicyStore(fixture.PolicyStorePath, auditRing: auditRing);
        var registry = new WorkloadRegistry();
        var controller = new Kautuka.NetworkAccessController.NetworkAccessController(
            reopenedStore,
            registry,
            auditRing);
        var grpc = new WorkspaceGrpcService(
            null!, null!, null!, null!, null!, null!, null!, null!,
            NullLogger<WorkspaceGrpcService>.Instance,
            controller,
            new ImageCompatibilityStore(Path.Combine(Path.GetDirectoryName(fixture.PolicyStorePath)!, "compat.json")));
        var restartedManager = new InProcessManagerGrpcService(grpc);
        var startupSync = new GatewayUserRolePrivilegesStartupSyncService(
            restartedManager,
            fixture.UserStore,
            NullLogger<GatewayUserRolePrivilegesStartupSyncService>.Instance);

        await startupSync.StartAsync(TestContext.Current.CancellationToken);

        using var afterRestart = new NetworkAccessPolicyStore(fixture.PolicyStorePath);
        var system = new NetworkAccessActor("system", IsAdmin: true);
        UserPrivileges alice = afterRestart.GetUserPrivileges(system, aliceId);
        UserPrivileges bob = afterRestart.GetUserPrivileges(system, bobId, targetIsAdmin: true);
        Assert.True(alice.CanApproveOwn);
        Assert.False(alice.CanAllowAllAgents);
        Assert.False(bob.CanApproveOwn);
        Assert.False(bob.CanAllowAllAgents);
        Assert.Equal(adminId, afterRestart.GetRawStateSnapshot().UserPrivileges.Single(item => item.UserId == aliceId).SetBy);
        Assert.Equal(adminId, afterRestart.GetRawStateSnapshot().UserPrivileges.Single(item => item.UserId == bobId).SetBy);

        UserPrivileges legacy = afterRestart.GetRawStateSnapshot().UserPrivileges.Single(item => item.UserId == legacyAdminId);
        Assert.True(legacy.CanApproveOwn);
        Assert.True(legacy.CanAllowAllAgents);
        Assert.Equal("system", legacy.SetBy);
        Assert.Contains(auditRing.List(system), entry => entry.Reason.Contains("actor=system", StringComparison.Ordinal));
    }

    private sealed class GatewayNacFixture : IAsyncDisposable
    {
        private readonly string root;
        private readonly WebApplication app;
        private readonly HttpClient client;

        public NetworkAccessPolicyStore PolicyStore { get; }
        public WorkloadRegistry Registry { get; }
        public Kautuka.NetworkAccessController.NetworkAccessController Controller { get; }
        public string PolicyStorePath => Path.Combine(root, "nac", "policy.json");
        public IGatewayUserStore UserStore => app.Services.GetRequiredService<IGatewayUserStore>();
        public IManagerGrpcService ManagerService => managerService;
        public bool FailPrivilegeSync
        {
            get => managerService.FailPrivilegeSync;
            set => managerService.FailPrivilegeSync = value;
        }

        private InProcessManagerGrpcService managerService = null!;

        private GatewayNacFixture(
            string root,
            WebApplication app,
            HttpClient client,
            NetworkAccessPolicyStore policyStore,
            WorkloadRegistry registry,
            Kautuka.NetworkAccessController.NetworkAccessController controller)
        {
            this.root = root;
            this.app = app;
            this.client = client;
            PolicyStore = policyStore;
            Registry = registry;
            Controller = controller;
        }

        public static async Task<GatewayNacFixture> CreateAsync()
        {
            string root = Path.Combine(Path.GetTempPath(), "kautuka-v7-4-12-" + Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(root);
            string sqlitePath = Path.Combine(root, "gateway.db");

            var policyStore = new NetworkAccessPolicyStore(Path.Combine(root, "nac", "policy.json"));
            var registry = new WorkloadRegistry();
            var auditRing = new NetworkAccessAuditRing(Path.Combine(root, "nac", "audit.jsonl"));
            var imageCompat = new ImageCompatibilityStore(Path.Combine(root, "nac", "compat.json"));
            var controller = new Kautuka.NetworkAccessController.NetworkAccessController(policyStore, registry, auditRing);
            var grpcService = new WorkspaceGrpcService(
                null!, null!, null!, null!, null!, null!, null!, null!,
                NullLogger<WorkspaceGrpcService>.Instance,
                controller,
                imageCompat);

            var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = Environments.Development });
            builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Kautuka:ContainerStateRoot"] = root,
                ["Kautuka:DatabasePath"] = sqlitePath,
            });

            builder.WebHost.UseTestServer();
            GatewayAppBootstrap.ConfigureServices(builder);
            RemoveHostedService<WorkspaceDesiredStateStartupService>(builder.Services);
            RemoveHostedService<ChannelHostOutboxWorker>(builder.Services);
            RemoveHostedService<ChannelHostIngressWorker>(builder.Services);
            RemoveHostedService<AutopilotSchedulerService>(builder.Services);
            var managerService = new InProcessManagerGrpcService(grpcService);
            builder.Services.AddSingleton<IManagerGrpcService>(managerService);
            builder.Services.RemoveAll<CredentialResolverService>();
            builder.Services.AddSingleton(new CredentialResolverService(
                new Lazy<CredentialsService.CredentialsServiceClient>(new NoOpCredentialsClient())));
            builder.Services.RemoveAll<IChannelRegistrationProvisioningService>();
            builder.Services.AddSingleton<IChannelRegistrationProvisioningService>(new NoOpChannelProvisioning());
            builder.Services.Configure<HostOptions>(o => o.BackgroundServiceExceptionBehavior = BackgroundServiceExceptionBehavior.Ignore);

            WebApplication app = builder.Build();
            var database = app.Services.GetRequiredService<KautukaDatabase>();
            GatewayAppBootstrap.ConfigureApp(app, database);
            await app.StartAsync();

            var fixture = new GatewayNacFixture(root, app, app.GetTestClient(), policyStore, registry, controller)
            {
                managerService = managerService,
            };
            return fixture;
        }

        public async Task<string> SetupAndLoginFirstAdminAsync(string username, string password)
        {
            string setupToken = app.Services.GetRequiredService<SetupPossession>().Peek();
            using JsonDocument setupDoc = await SendJsonAsync(
                HttpMethod.Post,
                "/api/auth/setup",
                new { username, password, setupToken },
                accessToken: null);
            Assert.True(setupDoc.RootElement.GetProperty("success").GetBoolean());

            using JsonDocument loginDoc = await SendJsonAsync(
                HttpMethod.Post,
                "/api/auth/login",
                new { username, password },
                accessToken: null);
            return loginDoc.RootElement.GetProperty("accessToken").GetString()!;
        }

        public async Task<JsonDocument> SendGetAsync(string path, string accessToken)
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, path);
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            using HttpResponseMessage res = await client.SendAsync(req);
            string json = await res.Content.ReadAsStringAsync();
            Assert.True(res.StatusCode == HttpStatusCode.OK, $"GET {path} returned {(int)res.StatusCode}: {json}");
            return JsonDocument.Parse(json);
        }

        public async Task<JsonDocument> SendJsonAsync(HttpMethod method, string path, object body, string? accessToken)
        {
            using var req = new HttpRequestMessage(method, path) { Content = JsonContent.Create(body) };
            if (!string.IsNullOrWhiteSpace(accessToken))
            {
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            }

            using HttpResponseMessage res = await client.SendAsync(req);
            string json = await res.Content.ReadAsStringAsync();
            Assert.True(res.StatusCode == HttpStatusCode.OK, $"{method} {path} returned {(int)res.StatusCode}: {json}");
            return JsonDocument.Parse(json);
        }

        public async Task<HttpResponseMessage> SendRoleUpdateAsync(string userId, bool isAdmin, string accessToken)
        {
            using var req = new HttpRequestMessage(HttpMethod.Put, $"/api/settings/users/{userId}/role")
            {
                Content = JsonContent.Create(new { isAdmin }),
            };
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            return await client.SendAsync(req);
        }

        public async ValueTask DisposeAsync()
        {
            client.Dispose();
            await app.DisposeAsync();
            await TestDirectoryCleanup.DeleteDirectoryWithRetryAsync(root);
        }

        private static void RemoveHostedService<THostedService>(IServiceCollection services) where THostedService : class, IHostedService
        {
            foreach (ServiceDescriptor descriptor in services
                .Where(d => d.ServiceType == typeof(IHostedService) && d.ImplementationType == typeof(THostedService))
                .ToArray())
            {
                services.Remove(descriptor);
            }
        }
    }

    private sealed class InProcessManagerGrpcService : IManagerGrpcService
    {
        public InProcessManagerGrpcService(WorkspaceGrpcService grpc)
        {
            client = new InProcessManagerServiceClient(grpc, () => FailPrivilegeSync);
        }

        public bool FailPrivilegeSync { get; set; }

        private readonly InProcessManagerServiceClient client;

        public ManagerService.ManagerServiceClient Client => client;

        public Task<WorkspaceBoolResponse> PingAsync(WorkspacePingRequest request, CancellationToken cancellationToken = default)
            => Task.FromResult(new WorkspaceBoolResponse { Ok = true, Message = request.Message });

        public Task<ManagerStatusResponse> GetHostStatusAsync(WorkspacePingRequest request, CancellationToken cancellationToken = default)
            => Task.FromResult(new ManagerStatusResponse { Ok = true, CoreReady = true, AgentsReady = true, Lifecycle = "reconciled" });

        public Task<ManagerRuntimeContainersResponse> ListRuntimeContainersAsync(WorkspacePingRequest request, CancellationToken cancellationToken = default)
            => Task.FromResult(new ManagerRuntimeContainersResponse { Ok = true });

        public Task<WorkspaceContextResponse> DescribeWorkspaceAsync(WorkspaceResolvedRequest request, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<WorkspaceContextResponse> RebuildWorkspaceAsync(WorkspaceResolvedRequest request, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<WorkspaceDeleteResponse> DeleteWorkspaceAsync(WorkspaceResolvedRequest request, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<WorkspaceSandboxResponse> SpawnSandboxAsync(WorkspaceResolvedRequest request, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<WorkspaceSandboxResponse> InspectSandboxAsync(WorkspaceResolvedRequest request, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<WorkspaceExecutionResponse> ExecAsync(WorkspaceExecRequest request, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<WorkspaceBoolResponse> StopSandboxAsync(WorkspaceResolvedRequest request, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<WorkspaceBoolResponse> PrimeWorkspaceAsync(WorkspaceResolvedRequest request, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<WorkspaceMcpProbeResponse> ProbeMcpContainerAsync(WorkspaceMcpContainerProbeRequest request, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<WorkspaceMcpContainerListToolsResponse> ListMcpContainerToolsAsync(WorkspaceMcpContainerCallRequest request, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<WorkspaceMcpContainerCallResponse> CallMcpContainerToolAsync(WorkspaceMcpContainerCallRequest request, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<DesiredStateUpdateResponse> UpdateDesiredStateAsync(ManagerDesiredStateUpdateRequest request, CancellationToken cancellationToken = default)
            => Task.FromResult(new DesiredStateUpdateResponse { Outcome = DesiredStateUpdateOutcome.Applied });
    }

    private sealed class InProcessManagerServiceClient(WorkspaceGrpcService grpc, Func<bool> failPrivilegeSync) : ManagerService.ManagerServiceClient
    {
        private static readonly TestServerCallContext ctx = new();

        public override AsyncUnaryCall<NetworkAccessListRequestsResponse> ListRequestsAsync(NetworkAccessListRequestsRequest request, CallOptions options)
            => Wrap(grpc.ListRequests(request, ctx));

        public override AsyncUnaryCall<NetworkAccessDecideResponse> DecideRequestAsync(NetworkAccessDecideRequest request, CallOptions options)
            => Wrap(grpc.DecideRequest(request, ctx));

        public override AsyncUnaryCall<NetworkAccessListRulesResponse> ListRulesAsync(NetworkAccessListRulesRequest request, CallOptions options)
            => Wrap(grpc.ListRules(request, ctx));

        public override AsyncUnaryCall<NetworkAccessSetRuleResponse> SetRuleAsync(NetworkAccessSetRuleRequest request, CallOptions options)
            => Wrap(grpc.SetRule(request, ctx));

        public override AsyncUnaryCall<NetworkAccessUserPrivilegesMessage> GetUserPrivilegesAsync(NetworkAccessGetUserPrivilegesRequest request, CallOptions options)
            => Wrap(grpc.GetUserPrivileges(request, ctx));

        public override AsyncUnaryCall<NetworkAccessUserPrivilegesMessage> SetUserPrivilegesAsync(NetworkAccessSetUserPrivilegesRequest request, CallOptions options)
            => failPrivilegeSync()
                ? throw new RpcException(new Status(StatusCode.Unavailable, "synthetic manager outage"))
                : Wrap(grpc.SetUserPrivileges(request, ctx));

        private static AsyncUnaryCall<T> Wrap<T>(Task<T> task)
            => new(task, Task.FromResult(new Metadata()), () => Status.DefaultSuccess, () => new Metadata(), () => { });
    }

    private sealed class NoOpCredentialsClient : CredentialsService.CredentialsServiceClient;

    private sealed class NoOpChannelProvisioning : IChannelRegistrationProvisioningService
    {
        private static readonly ChannelRegistrationProvisioningResult ok = new(true, "skipped", "test");
        public Task<ChannelRegistrationProvisioningResult> ProvisionRegistrationAsync(string registrationId, CancellationToken cancellationToken) => Task.FromResult(ok);
        public Task<ChannelRegistrationProvisioningResult> ProvisionAsync(string registrationId, string channelDeviceId, string? defaultTargetType, string? defaultTargetId, CancellationToken cancellationToken) => Task.FromResult(ok);
    }

    private sealed class TestServerCallContext : ServerCallContext
    {
        private readonly Metadata headers = [];
        private readonly Metadata trailers = [];
        private readonly Dictionary<object, object> state = [];
        protected override string MethodCore => "/kautuka.manager.v1.ManagerService/Test";
        protected override string HostCore => "localhost";
        protected override string PeerCore => "inproc";
        protected override DateTime DeadlineCore => DateTime.MaxValue;
        protected override Metadata RequestHeadersCore => headers;
        protected override CancellationToken CancellationTokenCore => CancellationToken.None;
        protected override Metadata ResponseTrailersCore => trailers;
        protected override Status StatusCore { get; set; }
        protected override WriteOptions? WriteOptionsCore { get; set; }
        protected override AuthContext AuthContextCore => new(string.Empty, []);
        protected override IDictionary<object, object> UserStateCore => state;
        protected override ContextPropagationToken CreatePropagationTokenCore(ContextPropagationOptions? options) => throw new NotSupportedException();
        protected override Task WriteResponseHeadersAsyncCore(Metadata responseHeaders) => Task.CompletedTask;
    }
}
