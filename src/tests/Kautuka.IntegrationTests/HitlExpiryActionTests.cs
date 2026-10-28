using Kautuka.Gateway.Domain.Autopilot;
using Kautuka.Gateway.Domain.Hitl;
using Kautuka.Gateway.Domain.Runtime;
using Kautuka.Gateway.Domain.Stores;

using Microsoft.Extensions.Configuration;

using static Kautuka.IntegrationTests.ThreadCommandTestSupport;

namespace Kautuka.IntegrationTests;

public sealed class HitlExpiryActionTests
{
    [Fact]
    public async Task WorkflowReviewRejectsAnUnavailableActionWithoutClosingTheReview()
    {
        using var temp = new TemporaryStateDirectory();
        GatewayApprovalStore store = CreateGatewayApprovalStore(CreateDatabase(temp.SqlitePath));
        DateTimeOffset now = DateTimeOffset.UtcNow;
        store.CreateRequest(DueRequest("apr-review-invalid", now) with
        {
            ToolType = "workflow_review",
            Kind = "workflow_review",
            WorkflowId = "workflow-1",
            InstanceId = "instance-1",
            NodeId = "step-1",
            ExpiresAt = DateTimeOffset.UtcNow.AddHours(1).ToString("O"),
        });
        ApprovalResolutionService service = CreateResolutionService(store);

        ApprovalResolutionResult result = await service.ResolveAsync(new ApprovalResolutionCommand(
            RequestId: "apr-review-invalid",
            Approved: false,
            Reason: null,
            DecidedByUserId: "owner",
            Source: HitlResolutionSource.Browser,
            Action: "deny"), CancellationToken.None).ConfigureAwait(true);

        Assert.Equal(ApprovalResolutionStatus.Rejected, result.Status);
        Assert.Null(store.GetById("apr-review-invalid")?.Decision);
    }

    [Fact]
    public async Task WorkflowReviewCanStillBeStoppedWhenItsInstanceIsMissing()
    {
        using var temp = new TemporaryStateDirectory();
        GatewayApprovalStore store = CreateGatewayApprovalStore(CreateDatabase(temp.SqlitePath));
        DateTimeOffset now = DateTimeOffset.UtcNow;
        store.CreateRequest(DueRequest("apr-review-missing-instance", now) with
        {
            ToolType = "workflow_review",
            Kind = "workflow_review",
            WorkflowId = "workflow-1",
            InstanceId = "missing-instance",
            NodeId = "step-1",
            ExpiresAt = now.AddHours(1).ToString("O"),
        });
        ApprovalResolutionService service = CreateResolutionService(store);

        ApprovalResolutionResult result = await service.ResolveAsync(new ApprovalResolutionCommand(
            RequestId: "apr-review-missing-instance",
            Approved: false,
            Reason: null,
            DecidedByUserId: "owner",
            Source: HitlResolutionSource.Browser,
            StopRun: true,
            Action: ApprovalActionCatalog.StopRun), CancellationToken.None).ConfigureAwait(true);

        Assert.Equal(ApprovalResolutionStatus.Resolved, result.Status);
        Assert.Equal("stopped", store.GetById("apr-review-missing-instance")?.Decision);
    }

    [Fact]
    public void ExpireStoresExpiredNotDenied()
    {
        using var temp = new TemporaryStateDirectory();
        GatewayApprovalStore store = CreateGatewayApprovalStore(CreateDatabase(temp.SqlitePath));
        DateTimeOffset now = DateTimeOffset.UtcNow;
        store.CreateRequest(DueRequest("apr-expired", now));

        Assert.True(store.Expire("apr-expired", now, HitlExpiryReasons.Abort));

        GatewayToolApprovalRequest? request = store.GetById("apr-expired");
        Assert.NotNull(request);
        Assert.Equal("expired", request.Decision);
        Assert.NotEqual("denied", request.Decision);
        Assert.Equal("system_timeout", request.ResolutionSource);
        Assert.Equal(HitlExpiryReasons.Abort, request.Reason);
    }

    [Fact]
    public async Task ExpireAbortIsTheDefaultAction()
    {
        using var temp = new TemporaryStateDirectory();
        GatewayApprovalStore store = CreateGatewayApprovalStore(CreateDatabase(temp.SqlitePath));
        DateTimeOffset now = DateTimeOffset.UtcNow;
        store.CreateRequest(DueRequest("apr-abort", now));
        ApprovalResolutionService service = CreateResolutionService(store);

        ApprovalResolutionResult result = await service.ExpireAsync("apr-abort", now, CancellationToken.None).ConfigureAwait(true);

        Assert.Equal(ApprovalResolutionStatus.Expired, result.Status);
        Assert.Equal("expired", result.Request?.Decision);
        Assert.Equal(HitlExpiryReasons.Abort, result.Request?.Reason);
        Assert.NotEqual("denied", result.Request?.Decision);
    }

    [Fact]
    public async Task ExpireInformContinuesWithoutAbortReason()
    {
        using var temp = new TemporaryStateDirectory();
        GatewayApprovalStore store = CreateGatewayApprovalStore(CreateDatabase(temp.SqlitePath));
        DateTimeOffset now = DateTimeOffset.UtcNow;
        store.CreateRequest(DueRequest("apr-inform", now));
        ApprovalResolutionService service = CreateResolutionService(
            store,
            runLimitResolver: new StubRunLimitResolver(HitlExpiryAction.Inform));

        ApprovalResolutionResult result = await service.ExpireAsync("apr-inform", now, CancellationToken.None).ConfigureAwait(true);

        Assert.Equal(ApprovalResolutionStatus.Expired, result.Status);
        Assert.Equal("expired", result.Request?.Decision);
        Assert.Equal(HitlExpiryReasons.Inform, result.Request?.Reason);
        Assert.NotEqual("denied", result.Request?.Decision);
    }

    [Fact]
    public async Task ExpireRetryOnceThenAborts()
    {
        using var temp = new TemporaryStateDirectory();
        GatewayApprovalStore store = CreateGatewayApprovalStore(CreateDatabase(temp.SqlitePath));
        DateTimeOffset firstNow = DateTimeOffset.UtcNow;
        store.CreateRequest(DueRequest("apr-retry", firstNow));
        ApprovalResolutionService service = CreateResolutionService(
            store,
            runLimitResolver: new StubRunLimitResolver(HitlExpiryAction.Retry, hitlExpiry: TimeSpan.FromSeconds(30)));

        ApprovalResolutionResult first = await service.ExpireAsync("apr-retry", firstNow, CancellationToken.None).ConfigureAwait(true);

        Assert.Equal(ApprovalResolutionStatus.Expired, first.Status);
        GatewayToolApprovalRequest? retried = store.GetById("apr-retry");
        Assert.NotNull(retried);
        Assert.Null(retried.Decision);
        Assert.Equal(HitlExpiryReasons.RetryMarker, retried.Reason);

        DateTimeOffset secondNow = firstNow.AddSeconds(31);
        ApprovalResolutionResult second = await service.ExpireAsync("apr-retry", secondNow, CancellationToken.None).ConfigureAwait(true);

        Assert.Equal(ApprovalResolutionStatus.Expired, second.Status);
        Assert.Equal("expired", second.Request?.Decision);
        Assert.Equal(HitlExpiryReasons.Abort, second.Request?.Reason);
    }

    [Fact]
    public void ListDueForExpiryLeavesValidAutopilotApprovalToSchedulerButIncludesOrphans()
    {
        using var temp = new TemporaryStateDirectory();
        KautukaDatabase database = CreateDatabase(temp.SqlitePath);
        GatewayApprovalStore store = CreateGatewayApprovalStore(database);
        DateTimeOffset now = DateTimeOffset.UtcNow;
        new GatewayAutopilotStore(database).InsertRun(new GatewayScheduledRun(
            Id: "run-1", ScheduleId: "schedule-1", OwnerUserId: "user-1", TargetType: "agent", TargetId: "agent-1",
            Status: AutopilotRunStatuses.AwaitingPreRunApproval, TriggerType: "cron", ScheduledFor: now.ToString("O"),
            SessionId: null, RuntimeRunId: null, PreRunApprovalRequestId: "apr-autopilot", InputJson: "{}",
            OutputJson: null, Error: null, StateCategory: AutopilotRunStateCategories.PendingApproval,
            SkipReason: null, BlockingRunId: null, BlockingRunStatusSnapshot: null, BlockingRunStateCategory: null,
            BlockedAt: null, CreatedAt: now.ToString("O"), UpdatedAt: now.ToString("O"), StartedAt: null, EndedAt: null));
        store.CreateRequest(DueRequest("apr-in-run", now));
        store.CreateRequest(DueRequest("apr-autopilot", now) with { ToolType = "autopilot_prerun" });
        store.CreateRequest(DueRequest("apr-orphan", now) with { ToolType = "autopilot_prerun", RunId = "deleted-run" });

        IReadOnlyList<GatewayToolApprovalRequest> due = store.ListDueForExpiry(now);
        Assert.Equal(2, due.Count);
        Assert.Contains(due, item => item.Id == "apr-in-run");
        Assert.Contains(due, item => item.Id == "apr-orphan");
        Assert.DoesNotContain(due, item => item.Id == "apr-autopilot");
    }

    private static GatewayToolApprovalRequest DueRequest(string id, DateTimeOffset now)
        => new(
            Id: id,
            SessionId: "session-1",
            RunId: "run-1",
            CheckpointId: "checkpoint-1",
            AgentId: "agent-1",
            UserId: "user-1",
            ToolCallId: "tool-call-1",
            ToolName: "run_shell",
            ToolType: "mcp",
            McpServerId: null,
            ArgumentsJson: "{}",
            RequestedAt: now.AddMinutes(-2).ToString("O"),
            Decision: null,
            DecidedBy: null,
            DecidedAt: null,
            Reason: null,
            ExpiresAt: now.AddSeconds(-1).ToString("O"));

    private static KautukaDatabase CreateDatabase(string sqlitePath)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Kautuka:ContainerStateRoot"] = Path.GetDirectoryName(sqlitePath)
                    ?? throw new InvalidOperationException("Expected sqlite path to include a parent directory."),
            })
            .Build();

        var database = new KautukaDatabase(configuration);
        database.EnsureReady();
        return database;
    }

    private sealed class TemporaryStateDirectory : IDisposable
    {
        private readonly string root = Path.Combine(Path.GetTempPath(), "kautuka-hitl-expiry-tests", Guid.NewGuid().ToString("N"));

        public TemporaryStateDirectory()
        {
            Directory.CreateDirectory(root);
            SqlitePath = Path.Combine(root, "kautuka.db");
        }

        public string SqlitePath { get; }

        public void Dispose()
        {
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }

    private sealed class StubRunLimitResolver(HitlExpiryAction action, TimeSpan? hitlExpiry = null) : IRunLimitResolver
    {
        public TimeSpan GetModelTimeout() => TimeSpan.FromSeconds(300);

        public ResolvedRunLimits ResolveForAgent(string? agentId)
            => new(TimeSpan.FromSeconds(300), null, hitlExpiry, action);

        public ResolvedRunLimits Resolve(AgentRuntimeDefinition? agentRuntime)
            => ResolveForAgent(null);
    }
}
