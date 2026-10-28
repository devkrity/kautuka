using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;

using Kautuka.Gateway.Domain.Auth;
using Kautuka.Gateway.Domain.Execution;
using Kautuka.Gateway.Domain.Hitl;
using Kautuka.Gateway.Domain.Stores;
using Kautuka.Gateway.Endpoints.Runtime;

using Mediator;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

using static Kautuka.IntegrationTests.ThreadCommandTestSupport;

namespace Kautuka.IntegrationTests;

public sealed class GatewayApprovalEndpointMappingsTests
{
    private static readonly JsonSerializerOptions jsonOptions = new()
    {
        TypeInfoResolver = new DefaultJsonTypeInfoResolver(),
    };

    // ── Shared test app factory ─────────────────────────────────────────────

    private static async Task<(WebApplication App, CapturingPublisher Publisher, FakeApprovalStore Store, FakeBypassRegistry BypassRegistry, FakeAgentToolInvocationLinkStore InvocationLinks, ThreadCommandAgentStore AgentStore, RecordingThreadCommandService ThreadCommands)>
        BuildTestAppAsync(
            string? storeResolveForRequestId = "req-123")
    {
        var fakeApprovalStore = new FakeApprovalStore(storeResolveForRequestId);
        var fakePublisher = new CapturingPublisher();
        var bypassRegistry = new FakeBypassRegistry();
        var authGuard = new AllowAllAuthGuard();
        var invocationLinkStore = new FakeAgentToolInvocationLinkStore();
        var agentStore = new ThreadCommandAgentStore(RuntimeEvent(
            1,
            NaradEventTypes.RunStarted,
            new { trigger = "chat" },
            runId: "run-1",
            rootRunId: "run-1"));
        var catalogStore = new FakeCatalogStore();
        var threadCommands = new RecordingThreadCommandService();

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.ConfigureHttpJsonOptions(options => options.SerializerOptions.TypeInfoResolver = new DefaultJsonTypeInfoResolver());
        builder.Services.AddSingleton<IGatewayApprovalStore>(fakeApprovalStore);
        builder.Services.AddSingleton<IGatewayCatalogStore>(catalogStore);
        builder.Services.AddSingleton<IAgentToolInvocationLinkStore>(invocationLinkStore);
        builder.Services.AddSingleton<IAgentDefinitionStore>(agentStore);
        builder.Services.AddSingleton<IGatewaySessionStore>(new NoOpSessionStore());
        builder.Services.AddSingleton<IGatewayUserStore>(new NoOpUserStore());
        builder.Services.AddSingleton<IGatewayContinuationStore>(new NoOpContinuationStore());
        builder.Services.AddSingleton<IGatewayWorkflowStore>(new TestGatewayWorkflowStore());
        builder.Services.AddSingleton<IMediator>(fakePublisher);
        builder.Services.AddSingleton<IGatewayAuthGuard>(authGuard);
        builder.Services.AddSingleton<IApprovalBypassRegistry>(bypassRegistry);
        builder.Services.AddSingleton<IRunStopRegistry>(new RunStopRegistry());
        builder.Services.AddSingleton<IApprovalResolutionEventDispatcher>(new NoOpApprovalResolutionEventDispatcher());
        builder.Services.AddSingleton<IRuntimeEventPublisher, TestRuntimeEventPublisher>();
        builder.Services.AddSingleton<IApprovalResolutionService, ApprovalResolutionService>();
        builder.Services.AddSingleton<IThreadCommandService>(threadCommands);
        builder.Services.AddSingleton<IApprovalContinuationQueue, RecordingContinuationQueue>();
        builder.Services.AddSingleton<ILogger<ApprovalResolutionService>>(
            static _ => NullLogger<ApprovalResolutionService>.Instance);

        var app = builder.Build();
        threadCommands.Resolution = app.Services.GetRequiredService<IApprovalResolutionService>();
        app.MapGatewayApprovalEndpoints();
        app.MapNaradThreadCommandEndpoints();
        await app.StartAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);
        return (app, fakePublisher, fakeApprovalStore, bypassRegistry, invocationLinkStore, agentStore, threadCommands);
    }

    private sealed class NoOpApprovalResolutionEventDispatcher : IApprovalResolutionEventDispatcher
    {
        public ValueTask DispatchAsync(string? requestId, CancellationToken cancellationToken)
            => ValueTask.CompletedTask;
    }

    // ── Resolve endpoint — approve ─────────────────────────────────────────

    [Fact]
    public async Task PatchApprovalResolveShimIsGone()
    {
        var (app, _, _, _, _, _, _) = await BuildTestAppAsync().ConfigureAwait(true);
        using var _2 = app;
        HttpClient client = app.GetTestClient();

        using HttpRequestMessage resolve = new(HttpMethod.Patch, "/api/approvals/req-123/resolve")
        {
            Content = JsonContent.Create(new { approved = true }, options: jsonOptions)
        };
        using HttpResponseMessage resolveResponse = await client.SendAsync(resolve, TestContext.Current.CancellationToken).ConfigureAwait(true);
        Assert.False(
            resolveResponse.StatusCode is HttpStatusCode.OK or HttpStatusCode.Accepted or HttpStatusCode.BadRequest,
            $"PATCH /api/approvals/{{id}}/resolve must not exist; got {(int)resolveResponse.StatusCode}.");
        Assert.Equal(HttpStatusCode.NotFound, resolveResponse.StatusCode);

        using HttpRequestMessage idOnly = new(HttpMethod.Patch, "/api/approvals/req-123")
        {
            Content = JsonContent.Create(new { approved = true }, options: jsonOptions)
        };
        using HttpResponseMessage idResponse = await client.SendAsync(idOnly, TestContext.Current.CancellationToken).ConfigureAwait(true);
        Assert.False(
            idResponse.StatusCode is HttpStatusCode.OK or HttpStatusCode.Accepted or HttpStatusCode.BadRequest,
            $"PATCH /api/approvals/{{id}} must not resolve; got {(int)idResponse.StatusCode}.");
    }

    [Fact]
    public async Task ResolveInterruptCommandPublishesApprovalResolvedEvent()
    {
        var (app, fakePublisher, _, _, _, _, _) = await BuildTestAppAsync().ConfigureAwait(true);
        using var _2 = app;

        HttpClient client = app.GetTestClient();
        using HttpRequestMessage request = new(HttpMethod.Post, "/api/narad/v1/commands/resolve-interrupt")
        {
            Content = JsonContent.Create(new { interruptId = "req-123", decision = "approve", reason = "test" }, options: jsonOptions)
        };

        using HttpResponseMessage response = await client.SendAsync(request, TestContext.Current.CancellationToken).ConfigureAwait(true);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);

        HitlApprovalResolved? evt = Assert.IsType<HitlApprovalResolved>(fakePublisher.LastNotification);
        Assert.Equal("req-123", evt.RequestId);
        Assert.True(evt.Approved);
        Assert.Equal(HitlResolutionSource.Browser, evt.Source);
        Assert.False(fakePublisher.LastTokenCanBeCanceled);
    }

    [Fact]
    public async Task ResolveInterruptCommandDoesNotScheduleASecondResumeOwner()
    {
        var (app, _, _, _, _, _, threadCommands) = await BuildTestAppAsync().ConfigureAwait(true);
        using var _2 = app;

        HttpClient client = app.GetTestClient();
        using HttpRequestMessage request = new(HttpMethod.Post, "/api/narad/v1/commands/resolve-interrupt")
        {
            Content = JsonContent.Create(new { interruptId = "req-123", decision = "approve", reason = "test" }, options: jsonOptions)
        };

        using HttpResponseMessage response = await client.SendAsync(request, TestContext.Current.CancellationToken).ConfigureAwait(true);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);

        Assert.NotNull(threadCommands.LastResolveCommand);
        Assert.Equal("req-123", threadCommands.LastResolveCommand.InterruptId);
        Assert.Equal("approve", threadCommands.LastResolveCommand.Decision);
        Assert.Null(threadCommands.LastResumeCommand);
    }

    [Fact]
    public async Task ResolveInterruptCommandDenyPublishesResolvedEventWithApprovedFalse()
    {
        var (app, fakePublisher, _, _, _, _, _) = await BuildTestAppAsync().ConfigureAwait(true);
        using var _2 = app;

        HttpClient client = app.GetTestClient();
        using HttpRequestMessage request = new(HttpMethod.Post, "/api/narad/v1/commands/resolve-interrupt")
        {
            Content = JsonContent.Create(new { interruptId = "req-123", decision = "deny", reason = "not allowed" }, options: jsonOptions)
        };

        using HttpResponseMessage response = await client.SendAsync(request, TestContext.Current.CancellationToken).ConfigureAwait(true);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);

        string body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);
        var json = JsonNode.Parse(body);
        Assert.Equal("denied", json?["decision"]?.GetValue<string>());

        HitlApprovalResolved? evt = Assert.IsType<HitlApprovalResolved>(fakePublisher.LastNotification);
        Assert.Equal("req-123", evt.RequestId);
        Assert.False(evt.Approved);
    }

    [Fact]
    public async Task ResolveInterruptCommandWhenNotFoundReturns404()
    {
        var (app, _, _, _, _, _, _) = await BuildTestAppAsync(storeResolveForRequestId: null).ConfigureAwait(true);
        using var _2 = app;

        HttpClient client = app.GetTestClient();
        using HttpRequestMessage request = new(HttpMethod.Post, "/api/narad/v1/commands/resolve-interrupt")
        {
            Content = JsonContent.Create(new { interruptId = "unknown-req", decision = "approve" }, options: jsonOptions)
        };

        using HttpResponseMessage response = await client.SendAsync(request, TestContext.Current.CancellationToken).ConfigureAwait(true);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // ── Session approvals endpoint ──────────────────────────────────────────

    [Fact]
    public async Task SessionApprovalsEndpointIncludesLinkedAgentToolApprovalsForCallerSession()
    {
        var (app, _, store, _, _, _, _) = await BuildTestAppAsync().ConfigureAwait(true);
        using var _2 = app;

        store.RequestsById["req-linked"] = new GatewayToolApprovalRequest(
            Id: "req-linked",
            SessionId: "session-inner",
            RunId: "run-1",
            CheckpointId: null,
            AgentId: "agent-1",
            UserId: "user-1",
            ToolCallId: "tool-1",
            ToolName: "run_shell",
            ToolType: "mcp",
            McpServerId: null,
            ArgumentsJson: "{}",
            RequestedAt: DateTimeOffset.UtcNow.ToString("O"),
            Decision: null,
            DecidedBy: null,
            DecidedAt: null,
            Reason: null,
            ExpiresAt: null,
            OuterSessionId: "session-caller",
            OuterAgentId: "agent-1",
            ChannelTetherMode: null);

        HttpClient client = app.GetTestClient();
        using HttpResponseMessage response = await client.GetAsync(new Uri("/api/approvals/session/session-caller", UriKind.Relative), TestContext.Current.CancellationToken).ConfigureAwait(true);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        JsonNode? payload = JsonNode.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken).ConfigureAwait(true));
        JsonArray items = Assert.IsType<JsonArray>(payload);
        Assert.Single(items);
        Assert.Equal("req-linked", items[0]?["id"]?.GetValue<string>());
        Assert.Equal("session-inner", items[0]?["sessionId"]?.GetValue<string>());
    }

    [Fact]
    public async Task ApprovalsStatsPathIsNotALiteralRoute()
    {
        var (app, _, _, _, _, _, _) = await BuildTestAppAsync().ConfigureAwait(true);
        using var _2 = app;
        HttpClient client = app.GetTestClient();
        using HttpResponseMessage response = await client.GetAsync(
            new Uri("/api/approvals/stats", UriKind.Relative),
            TestContext.Current.CancellationToken).ConfigureAwait(true);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task AgentStatsEndpointReturnsHumanApproveDenyCounts()
    {
        var (app, _, store, _, _, _, _) = await BuildTestAppAsync().ConfigureAwait(true);
        using var _2 = app;
        store.Stats.Add(new HitlApprovalStatRow
        {
            ToolName = "fetch",
            ToolType = "connector",
            McpServerId = "github",
            Approved = 12,
            Denied = 0,
            AgentCount = 1,
            LastDeniedAt = null,
        });

        HttpClient client = app.GetTestClient();
        using HttpResponseMessage response = await client.GetAsync(
            new Uri("/api/agents/agent-1/stats", UriKind.Relative),
            TestContext.Current.CancellationToken).ConfigureAwait(true);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        JsonNode? payload = JsonNode.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken).ConfigureAwait(true));
        Assert.Equal(30, payload?["windowDays"]?.GetValue<int>());
        JsonArray items = Assert.IsType<JsonArray>(payload?["items"]);
        Assert.Equal("fetch", items[0]?["toolName"]?.GetValue<string>());
        Assert.Equal(12, items[0]?["approved"]?.GetValue<int>());
        Assert.Equal(0, items[0]?["denied"]?.GetValue<int>());
    }

    [Fact]
    public async Task AgentStatsEndpointAcceptsAllTimeWindow()
    {
        var (app, _, store, _, _, _, _) = await BuildTestAppAsync().ConfigureAwait(true);
        using var _2 = app;
        store.Stats.Add(new HitlApprovalStatRow
        {
            ToolName = "fetch",
            ToolType = "connector",
            McpServerId = "github",
            Approved = 1,
            Denied = 0,
            AgentCount = 1,
            AgentDefinitionRevision = 4,
        });

        HttpClient client = app.GetTestClient();
        using HttpResponseMessage response = await client.GetAsync(
            new Uri("/api/agents/agent-1/stats?days=all", UriKind.Relative),
            TestContext.Current.CancellationToken).ConfigureAwait(true);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        JsonNode? payload = JsonNode.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken).ConfigureAwait(true));
        Assert.Equal(0, payload?["windowDays"]?.GetValue<int>());
        Assert.Equal(string.Empty, payload?["since"]?.GetValue<string>());
        Assert.Equal(4, payload?["items"]?[0]?["agentDefinitionRevision"]?.GetValue<long>());
    }

    [Fact]
    public async Task ConnectorStatsEndpointReturnsHumanApproveDenyCounts()
    {
        var (app, _, store, _, _, _, _) = await BuildTestAppAsync().ConfigureAwait(true);
        using var _2 = app;
        store.Stats.Add(new HitlApprovalStatRow
        {
            ToolName = "fetch",
            ToolType = "connector",
            McpServerId = "github",
            Approved = 4,
            Denied = 1,
            AgentCount = 2,
            LastDeniedAt = null,
        });

        HttpClient client = app.GetTestClient();
        using HttpResponseMessage response = await client.GetAsync(
            new Uri("/api/connectors/github/stats", UriKind.Relative),
            TestContext.Current.CancellationToken).ConfigureAwait(true);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        JsonNode? payload = JsonNode.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken).ConfigureAwait(true));
        JsonArray items = Assert.IsType<JsonArray>(payload?["items"]);
        Assert.Equal("fetch", items[0]?["toolName"]?.GetValue<string>());
        Assert.Equal(4, items[0]?["approved"]?.GetValue<int>());
        Assert.Equal(1, items[0]?["denied"]?.GetValue<int>());
    }

    [Fact]
    public async Task SkillStatsEndpointReturnsHumanApproveDenyCounts()
    {
        var (app, _, store, _, _, _, _) = await BuildTestAppAsync().ConfigureAwait(true);
        using var _2 = app;
        store.Stats.Add(new HitlApprovalStatRow
        {
            ToolName = "roll-dice",
            ToolType = "skill",
            SkillId = "roll-dice",
            Approved = 134,
            Denied = 0,
            AgentCount = 1,
            LastDeniedAt = null,
        });

        HttpClient client = app.GetTestClient();
        using HttpResponseMessage response = await client.GetAsync(
            new Uri("/api/skills/roll-dice/stats", UriKind.Relative),
            TestContext.Current.CancellationToken).ConfigureAwait(true);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        JsonNode? payload = JsonNode.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken).ConfigureAwait(true));
        JsonArray items = Assert.IsType<JsonArray>(payload?["items"]);
        Assert.Equal("roll-dice", items[0]?["toolName"]?.GetValue<string>());
        Assert.Equal(134, items[0]?["approved"]?.GetValue<int>());
    }

    [Fact]
    public void HitlApprovalStatsResponseSerializesWithSourceGenerator()
    {
        var payload = new HitlApprovalStatsResponse(
            30,
            "2026-08-13T00:00:00.0000000+00:00",
            [
                new HitlApprovalStatItem("roll-dice", "skill", 134, 0, 1, null, "roll-dice", null),
                new HitlApprovalStatItem("invoke_agent_roller", "agent", 171, 3, 1),
            ]);

        string json = JsonSerializer.Serialize(payload, KautukaJsonContext.Default.HitlApprovalStatsResponse);
        JsonNode? node = JsonNode.Parse(json);
        Assert.Equal(30, node?["windowDays"]?.GetValue<int>());
        JsonArray items = Assert.IsType<JsonArray>(node?["items"]);
        Assert.Equal("roll-dice", items[0]?["toolName"]?.GetValue<string>());
        Assert.Equal(134, items[0]?["approved"]?.GetValue<int>());
        Assert.Equal("invoke_agent_roller", items[1]?["toolName"]?.GetValue<string>());
    }

    [Fact]
    public async Task DeniedStatsEndpointReturnsDeniedRows()
    {
        var (app, _, store, _, _, _, _) = await BuildTestAppAsync().ConfigureAwait(true);
        using var _2 = app;
        store.Denied.Add(new GatewayToolApprovalRequest(
            Id: "apr-denied",
            SessionId: "session-1",
            RunId: "run-1",
            CheckpointId: null,
            AgentId: "agent-1",
            UserId: "user-1",
            ToolCallId: "call-1",
            ToolName: "fetch",
            ToolType: "connector",
            McpServerId: "github",
            ArgumentsJson: """{"url":"https://evil.example"}""",
            RequestedAt: DateTimeOffset.UtcNow.ToString("O"),
            Decision: "denied",
            DecidedBy: "user-1",
            DecidedAt: DateTimeOffset.UtcNow.ToString("O"),
            Reason: "blocked",
            ExpiresAt: null));

        HttpClient client = app.GetTestClient();
        using HttpResponseMessage response = await client.GetAsync(
            new Uri("/api/agents/agent-1/stats/denied?toolName=fetch", UriKind.Relative),
            TestContext.Current.CancellationToken).ConfigureAwait(true);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        JsonNode? payload = JsonNode.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken).ConfigureAwait(true));
        JsonArray items = Assert.IsType<JsonArray>(payload);
        Assert.Equal("apr-denied", items[0]?["id"]?.GetValue<string>());
        Assert.Equal("denied", items[0]?["decision"]?.GetValue<string>());
    }

    private sealed class AllowAllAuthGuard : IGatewayAuthGuard
    {
        public bool TryAuthorize(HttpContext context, out GatewayUser? user)
        {
            user = BuildUser();
            return true;
        }

        public IResult? RequireAuthenticated(HttpContext context, out GatewayUser? user)
        {
            user = BuildUser();
            return null;
        }

        public IResult? RequireAdmin(HttpContext context, out GatewayUser? user)
        {
            user = BuildUser();
            return null;
        }

        private static GatewayUser BuildUser() => new(
            Id: "user-1",
            Username: "tester",
            Password: "pwd",
            Role: "admin",
            IsActive: true,
            Email: null,
                DisplayName: null);
    }

    private sealed class FakeBypassRegistry : IApprovalBypassRegistry
    {
        public HashSet<string> Registered { get; } = [];

        public void RegisterBypass(string runId, string userId) => Registered.Add(runId);
        public bool IsActive(string runId, out string userId) { userId = null!; return Registered.Contains(runId); }
        public void Clear(string runId) => Registered.Remove(runId);
        public List<string> ListRegisteredRootRunIds() => Registered.ToList();
    }

    private sealed class NoOpSessionStore : IGatewaySessionStore
    {
        public GatewaySession CreateSession(string targetType, string targetId, string ownerUserId, string? originator = null) => throw new NotSupportedException();
        public GatewaySession EnsureSession(string targetType, string targetId, string sessionId, string ownerUserId, string? originator = null) => throw new NotSupportedException();
        public IReadOnlyList<GatewaySession> ListSessions(string targetType, string targetId, string ownerUserId, bool includeArchived = false) => [];
        public GatewaySession? UpdateSessionTitle(string sessionId, string title) => null;
        public GatewaySession? ArchiveSession(string sessionId) => null;
        public IReadOnlyList<GatewaySession> ListRecentSessions(string ownerUserId, int limit) => [];
        public IReadOnlyList<string> PruneSessions(string targetType, string targetId, string ownerUserId, int retainCount, string preserveSessionId) => [];
        public bool TryGetSession(string sessionId, out GatewaySession? session) { session = null; return false; }
        public bool DeleteSession(string sessionId) => false;
        public GatewaySession? CompactSession(string sessionId, string? reason = null, string? summary = null) => null;
        public GatewaySession AppendUserMessage(string sessionId, GatewayMessage userMessage, IReadOnlyList<string>? userAssetIds = null, string? userId = null) => throw new NotSupportedException();
        public GatewaySession AppendAssistantMessage(string sessionId, GatewayMessage assistantMessage, string? userId = null) => throw new NotSupportedException();
        public GatewayMemoriesResponse GetMemories(string targetType, string targetId, string ownerUserId) => new([], []);
        public bool DeleteMemoryFact(string factId) => false;
        public bool UpdateMemoryFact(string factId, string title, string content, string category) => false;
        public IReadOnlyList<GatewayRun> ListRuns() => [];
        public bool TryGetRun(string runId, out GatewayRun? run) { run = null; return false; }
        public GatewaySession AppendMessages(string sessionId, GatewayMessage userMessage, GatewayMessage assistantMessage, IReadOnlyList<string>? userAssetIds = null, string? userId = null) => throw new NotSupportedException();
        public GatewayRun AppendRun(string targetType, string targetId, string userId, string? sessionId, string input, string output, GatewayMetrics? metrics = null, EffectiveRunBehavior? effectiveBehavior = null, string? runId = null, string? error = null, string? traceId = null) => throw new NotSupportedException();
        public GatewayRun CreateInteractionRun(string targetType, string targetId, string userId, string? sessionId, string interactionId, string prompt, IReadOnlyList<string> options) => throw new NotSupportedException();
        public GatewayRun? UpdateInteractionRun(string interactionId, string status, Dictionary<string, object?> output, string? error = null) => null;
    }

    private sealed class NoOpUserStore : IGatewayUserStore
    {
        public bool CanSetup => false;
        public bool TryGetUserById(string userId, out GatewayUser? user) { user = null; return false; }
        public bool TryGetUserByUsername(string username, out GatewayUser? user) { user = null; return false; }
        public IReadOnlyList<GatewayUserListItem> ListUsers() => [];
        public GatewayUserListItem? CreateFirstAdmin(string username, string password, out string? error) { error = null; return null; }
        public GatewayUserListItem? InviteUser(JsonElement body, out string? error) { error = null; return null; }
        public bool UpdateRole(string userId, GatewayUserRoleUpdateRequest request, out string? error) { error = null; return false; }
        public bool UpdateIsActive(string userId, bool isActive, out string? error) { error = null; return false; }
        public bool UpdateImportPermissions(string userId, GatewayUserImportPermissionsUpdateRequest request) => false;
        public bool UpdateProfile(string userId, string? email, string? displayName) => false;
        public bool UpdatePassword(string userId, string currentPassword, string newPassword, out string? error) { error = null; return false; }


        public bool TryClearExternalLogin(string userId, string provider, string actorId, out string? error)
        {
            error = null;
            return false;
        }

        public string[] ExternalProvidersFor(string userId) => [];
    }

    private sealed class NoOpContinuationStore : IGatewayContinuationStore
    {
        public void CreateCheckpoint(GatewayContinuationCheckpoint checkpoint) => throw new NotSupportedException();
        public int GetNextCheckpointIndex(string continuationRunId) => 0;
        public GatewayContinuationCheckpoint? GetById(string checkpointId) => null;
        public GatewayContinuationCheckpoint? GetByResumeTokenHash(string resumeTokenHash) => null;
        public void DeleteBySessions(IReadOnlyCollection<string> sessionIds) { }
    }

    private sealed class FakeApprovalStore : IGatewayApprovalStore
    {
        private readonly string? resolveForRequestId;
        public Dictionary<string, GatewayToolApprovalRequest> RequestsById { get; } = new(StringComparer.Ordinal);

        public FakeApprovalStore(string? resolveForRequestId = "req-123") => this.resolveForRequestId = resolveForRequestId;

        public void CreateRequest(GatewayToolApprovalRequest request) => RequestsById[request.Id] = request;

        public bool Resolve(string requestId, bool approved, string? reason, string? decidedBy, string resolutionSource, ApprovalContinuationWork? continuation = null)
            => Resolve(
                requestId,
                approved ? "approved" : "denied",
                reason,
                decidedBy,
                resolutionSource,
                continuation,
                null);

        public bool Resolve(
            string requestId,
            string decision,
            string? reason,
            string? decidedBy,
            string resolutionSource,
            ApprovalContinuationWork? continuation,
            ApprovalResolutionEventWork? resolutionEvent)
        {
            if (resolveForRequestId is null || !string.Equals(requestId, resolveForRequestId, StringComparison.Ordinal))
            {
                return false;
            }

            GatewayToolApprovalRequest? existing = GetById(requestId);
            if (existing is null || !string.IsNullOrWhiteSpace(existing.Decision))
            {
                return false;
            }

            RequestsById[requestId] = existing with
            {
                Decision = decision,
                Reason = reason,
                DecidedBy = decidedBy,
                DecidedAt = DateTimeOffset.UtcNow.ToString("O"),
                ResolutionSource = resolutionSource,
            };
            return true;
        }

        public IReadOnlyList<GatewayToolApprovalRequest> ListPending(string? userId, bool adminAll) => [];

        public GatewayToolApprovalRequest? GetDecidedByRunAndTool(string runId, string toolCallId, string toolName) => null;
        public GatewayToolApprovalRequest? GetDecidedByRunToolAndArguments(string runId, string toolName, string? argumentsJson) => null;
        public GatewayToolApprovalRequest? GetById(string requestId)
        {
            if (RequestsById.TryGetValue(requestId, out GatewayToolApprovalRequest? request))
            {
                return request;
            }

            return resolveForRequestId is not null && string.Equals(requestId, resolveForRequestId, StringComparison.Ordinal)
                ? new GatewayToolApprovalRequest(
                    Id: requestId,
                    SessionId: "session-1",
                    RunId: "run-1",
                    CheckpointId: null,
                    AgentId: "agent-1",
                    UserId: "user-1",
                    ToolCallId: "tool-1",
                    ToolName: "tool",
                    ToolType: "mcp",
                    McpServerId: null,
                    ArgumentsJson: "{}",
                    RequestedAt: DateTimeOffset.UtcNow.ToString("O"),
                    Decision: null,
                    DecidedBy: null,
                    DecidedAt: null,
                    Reason: null,
                    ExpiresAt: null,
                    ChannelTetherMode: null)
                : null;
        }

        public void SetChannelTetherMode(string requestId, ChannelHitlTetherMode mode) { }

        public IReadOnlyList<GatewayToolApprovalRequest> ListBySession(string sessionId)
            => [.. RequestsById.Values.Where(request => string.Equals(request.SessionId, sessionId, StringComparison.Ordinal))];

        public IReadOnlyList<GatewayToolApprovalRequest> ListByOuterSession(string sessionId)
            => [.. RequestsById.Values.Where(request => string.Equals(request.OuterSessionId, sessionId, StringComparison.Ordinal))];

        public IReadOnlyList<GatewayToolApprovalRequest> ListHistory(string? userId, bool adminAll, int limit, int offset) => [];

        public List<HitlApprovalStatRow> Stats { get; } = [];

        public List<GatewayToolApprovalRequest> Denied { get; } = [];

        public IReadOnlyList<HitlApprovalStatRow> ListHitlStats(HitlApprovalStatsQuery query) => Stats;

        public IReadOnlyList<GatewayToolApprovalRequest> ListDeniedHistory(HitlApprovalStatsQuery query, int limit)
            => [.. Denied.Take(limit)];
    }

    private sealed class FakeCatalogStore : IGatewayCatalogStore
    {
        private readonly Dictionary<string, GatewayAgent> agents = new(StringComparer.Ordinal)
        {
            ["agent-1"] = new(
                Id: "agent-1",
                Name: "Planner",
                ModelId: "model-1",
                SystemPrompt: string.Empty,
                SuperExecMode: "off",
                Session: new JsonObject(),
                Planning: new JsonObject(),
                Sandbox: new JsonObject(),
                SkillAttachments: [],
                OwnerUserId: "user-1",
                CreatedAt: DateTimeOffset.UtcNow.ToString("O"),
                ApprovalConfig: null,
                ToolExposure: null,
                Scope: GatewayOwnershipScopes.User,
                Enabled: true,
                Persona: null)
        };

        private readonly Dictionary<string, GatewayMcpServer> servers = new(StringComparer.Ordinal)
        {
            ["github"] = new GatewayMcpServer(
                "github",
                "GitHub",
                string.Empty,
                string.Empty,
                null,
                "http",
                "https://example.invalid",
                null,
                null,
                [],
                null,
                null,
                null,
                null,
                null,
                null,
                null,
                true,
                false,
                DateTimeOffset.UtcNow.ToString("O"),
                DateTimeOffset.UtcNow.ToString("O"),
                GatewayOwnershipScopes.User,
                "user-1")
        };

        private readonly Dictionary<string, GatewaySkill> skills = new(StringComparer.Ordinal)
        {
            ["roll-dice"] = new GatewaySkill(
                "roll-dice",
                "Roll Dice",
                string.Empty,
                "hash",
                "ready",
                "/tmp/roll-dice",
                false,
                true,
                true,
                [],
                DateTimeOffset.UtcNow.ToString("O"),
                new Dictionary<string, string>(),
                GatewayOwnershipScopes.User,
                "user-1")
        };

        public bool TryGetProvider(string id, out GatewayProvider? provider)
        {
            provider = null;
            return false;
        }

        public IReadOnlyList<GatewayProvider> ListProviders() => [];

        public GatewayProvider SaveProvider(JsonElement body) => throw new NotSupportedException();

        public bool DeleteProvider(string id) => throw new NotSupportedException();

        public bool TryGetModel(string id, out GatewayModel? model)
        {
            model = null;
            return false;
        }

        public IReadOnlyList<GatewayModel> ListModels() => [];

        public GatewayModel SaveModel(JsonElement body) => throw new NotSupportedException();

        public void UpdateModelCapabilities(string modelId, GatewayModelCapabilities capabilities) => throw new NotSupportedException();

        public bool DeleteModel(string id) => throw new NotSupportedException();

        public bool TryGetAgent(string id, out GatewayAgent? agent) => agents.TryGetValue(id, out agent);

        public IReadOnlyList<GatewayAgent> ListAgents() => [.. agents.Values];

        public GatewayAgent SaveAgent(string agentId, GatewayAgentUpsertRequest request, string ownerUserId) => throw new NotSupportedException();


        public bool DeleteAgent(string agentId) => throw new NotSupportedException();

        public IReadOnlyList<GatewayMcpServer> ListMcpServers() => [.. servers.Values];

        public bool TryGetMcpServer(string id, out GatewayMcpServer? server)
            => servers.TryGetValue(id, out server);

        public GatewayMcpServer SaveMcpServer(string serverId, GatewayConnectorUpsertRequest request, string scope, string? ownerUserId) => throw new NotSupportedException();

        public GatewayMcpServer SaveMcpServerDiscovery(string serverId, int? toolCount, IReadOnlyList<string>? toolNames) => throw new NotSupportedException();

        public bool DeleteMcpServer(string serverId) => throw new NotSupportedException();

        public IReadOnlyList<GatewaySkill> ListSkills() => [.. skills.Values];

        public bool TryGetSkill(string id, out GatewaySkill? skill)
            => skills.TryGetValue(id, out skill);

        public GatewaySkill SaveSkill(string skillId, JsonElement body, string scope, string? ownerUserId) => throw new NotSupportedException();

        public bool DeleteSkill(string skillId) => throw new NotSupportedException();
    }

    private sealed class FakeAgentToolInvocationLinkStore : IAgentToolInvocationLinkStore
    {
        public static FakeAgentToolInvocationLinkStore? Instance { get; private set; }

        public FakeAgentToolInvocationLinkStore()
        {
            Instance = this;
        }

        public List<AgentToolInvocationLink> Links { get; } = [];

        public void Upsert(AgentToolInvocationLink link)
        {
            int existingIndex = Links.FindIndex(candidate => string.Equals(candidate.ApprovalRequestId, link.ApprovalRequestId, StringComparison.Ordinal));
            if (existingIndex >= 0)
            {
                Links[existingIndex] = link;
                return;
            }

            Links.Add(link);
        }

        public AgentToolInvocationLink? GetByApprovalRequestId(string approvalRequestId)
            => Links.FirstOrDefault(link => string.Equals(link.ApprovalRequestId, approvalRequestId, StringComparison.Ordinal));

        public IReadOnlyList<AgentToolInvocationLink> ListByCallerSession(string callerSessionId)
            => [.. Links.Where(link => string.Equals(link.CallerSessionId, callerSessionId, StringComparison.Ordinal))];

        public AgentToolInvocationLink? GetByChildSessionId(string childSessionId)
            => Links.FirstOrDefault(link => string.Equals(link.ChildSessionId, childSessionId, StringComparison.Ordinal));
        public IReadOnlyList<AgentToolInvocationLink> ListByRootRunId(string rootRunId)
            => [.. Links.Where(link => string.Equals(link.RootRunId, rootRunId, StringComparison.Ordinal))];
        public bool HasLinksForSession(string sessionId)
            => Links.Any(link => string.Equals(link.CallerSessionId, sessionId, StringComparison.Ordinal));
    }

    private sealed class CapturingPublisher : IMediator
    {
        public object? LastNotification { get; private set; }
        public bool LastTokenCanBeCanceled { get; private set; } = true;

        public ValueTask<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public ValueTask<TResponse> Send<TResponse>(ICommand<TResponse> command, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public ValueTask<TResponse> Send<TResponse>(IQuery<TResponse> query, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public ValueTask<object?> Send(object message, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public IAsyncEnumerable<TResponse> CreateStream<TResponse>(IStreamQuery<TResponse> query, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public IAsyncEnumerable<TResponse> CreateStream<TResponse>(IStreamRequest<TResponse> request, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public IAsyncEnumerable<TResponse> CreateStream<TResponse>(IStreamCommand<TResponse> command, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public IAsyncEnumerable<object?> CreateStream(object message, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public ValueTask Publish(object notification, CancellationToken cancellationToken = default)
        {
            LastNotification = notification;
            LastTokenCanBeCanceled = cancellationToken.CanBeCanceled;
            return ValueTask.CompletedTask;
        }

        public ValueTask Publish<TNotification>(TNotification notification, CancellationToken cancellationToken = default)
            where TNotification : INotification
        {
            LastNotification = notification;
            LastTokenCanBeCanceled = cancellationToken.CanBeCanceled;
            return ValueTask.CompletedTask;
        }
    }
}
