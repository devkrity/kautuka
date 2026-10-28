using Kautuka.Gateway.Domain.Execution;
using Kautuka.Gateway.Domain.Hitl;
using Kautuka.Gateway.Domain.Runtime;
using Kautuka.Gateway.Domain.Stores;

namespace Kautuka.IntegrationTests;

public sealed class PackedToolApprovalTests
{
    [Fact]
    public void TwoSameNameHitlCallsStayIndependentUntilEachIsDecided()
    {
        var pending = new InMemoryApprovalStore(
            CreateRow("apr-bbc", "call-bbc", Decision: null),
            CreateRow("apr-cnn", "call-cnn", Decision: null));
        Assert.True(PackedToolApproval.HasUndecidedHumanSiblings(pending, ["apr-bbc", "apr-cnn"], "apr-bbc"));
        Assert.True(PackedToolApproval.HasUndecidedHumanSiblings(pending, ["apr-bbc", "apr-cnn"], "apr-cnn"));

        var bbcApproved = new InMemoryApprovalStore(
            CreateRow("apr-bbc", "call-bbc", "approved"),
            CreateRow("apr-cnn", "call-cnn", Decision: null));
        Assert.True(PackedToolApproval.HasUndecidedHumanSiblings(bbcApproved, ["apr-bbc", "apr-cnn"], "apr-bbc"));
        Assert.False(PackedToolApproval.HasUndecidedHumanSiblings(bbcApproved, ["apr-bbc", "apr-cnn"], "apr-cnn"));
    }

    [Fact]
    public void WaitCoversEveryPackedRequestId()
    {
        RunWaitState wait = new(
            "wait-1",
            "continuation-1",
            "run-1",
            "agent",
            "agent-1",
            "session-1",
            "agent-1",
            "user-1",
            RunWaitReasons.Approval,
            "apr-bbc",
            "{}",
            RunClockEnvelope.Wrap("{}", TimeSpan.Zero, ["apr-bbc", "apr-cnn"]),
            "[]",
            "call-bbc",
            "kautuka_web_fetch",
            "apr-bbc",
            null,
            null,
            null,
            null,
            null,
            DateTimeOffset.UtcNow.ToString("O"),
            null,
            "event-1");

        Assert.True(PackedToolApproval.WaitCoversRequest(wait, "apr-bbc"));
        Assert.True(PackedToolApproval.WaitCoversRequest(wait, "apr-cnn"));
        Assert.False(PackedToolApproval.WaitCoversRequest(wait, "apr-other"));
    }

    [Fact]
    public void ToPackResponsesKeepsPerCallDecisions()
    {
        var store = new InMemoryApprovalStore(
            CreateRow("apr-bbc", "call-bbc", "approved"),
            CreateRow("apr-cnn", "call-cnn", "denied"));
        var triggering = new ToolApprovalResponseInfo(
            "apr-cnn",
            "call-cnn",
            "kautuka_web_fetch",
            "{}",
            Approved: false,
            Reason: "denied",
            ContinuationRunId: "continuation-1");

        IReadOnlyList<ToolApprovalResponseInfo> pack = PackedToolApproval.ToPackResponses(
            store,
            ["apr-bbc", "apr-cnn"],
            triggering);

        Assert.Equal(["apr-bbc", "apr-cnn"], pack.Select(static item => item.RequestId).ToArray());
        Assert.True(pack[0].Approved);
        Assert.False(pack[1].Approved);
        Assert.Equal("call-bbc", pack[0].ToolCallId);
        Assert.Equal("call-cnn", pack[1].ToolCallId);
    }

    [Fact]
    public void ShouldEmitInterruptResolvedHoldsUntilLastPackedSibling()
    {
        RunWaitState wait = new(
            "wait-1",
            "continuation-1",
            "run-1",
            "agent",
            "agent-1",
            "session-1",
            "agent-1",
            "user-1",
            RunWaitReasons.Approval,
            "apr-bbc",
            "{}",
            RunClockEnvelope.Wrap("{}", TimeSpan.Zero, ["apr-bbc", "apr-cnn"]),
            "[]",
            "call-bbc",
            "kautuka_web_fetch",
            "apr-bbc",
            null,
            null,
            null,
            null,
            null,
            DateTimeOffset.UtcNow.ToString("O"),
            null,
            "event-1");
        var waits = new SingleWaitStore(wait);
        var pending = new InMemoryApprovalStore(
            CreateRow("apr-bbc", "call-bbc", Decision: null),
            CreateRow("apr-cnn", "call-cnn", Decision: null));
        Assert.False(PackedToolApproval.ShouldEmitInterruptResolved(
            waits, pending, "apr-bbc", "continuation-1", out _, out _));

        var lastSibling = new InMemoryApprovalStore(
            CreateRow("apr-bbc", "call-bbc", "approved"),
            CreateRow("apr-cnn", "call-cnn", Decision: null));
        Assert.True(PackedToolApproval.ShouldEmitInterruptResolved(
            waits, lastSibling, "apr-cnn", "continuation-1", out string interruptId, out string waitId));
        Assert.Equal("apr-bbc", interruptId);
        Assert.Equal("wait:apr-bbc", waitId);
        Assert.Equal("apr-bbc", PackedToolApproval.ResolveJournalInterruptId(wait, "apr-cnn"));
        Assert.Equal("wait:apr-bbc", PackedToolApproval.ResolveJournalWaitId(wait, "apr-cnn"));
    }

    [Fact]
    public void ResolvePackedInterruptDecisionIsDeniedWhenPackIsMixed()
    {
        var store = new InMemoryApprovalStore(
            CreateRow("apr-roller", "call-roller", "denied"),
            CreateRow("apr-fetch", "call-fetch", "approved"));

        Assert.Equal(
            "denied",
            PackedToolApproval.ResolvePackedInterruptStorageDecision(
                store,
                ["apr-roller", "apr-fetch"],
                "apr-fetch",
                currentApproved: true,
                currentExpired: false,
                currentStopRun: false));
    }

    [Fact]
    public void ResolvePackedInterruptDecisionIsApprovedWhenEveryMemberApproved()
    {
        var store = new InMemoryApprovalStore(
            CreateRow("apr-roller", "call-roller", "approved"),
            CreateRow("apr-fetch", "call-fetch", Decision: null));

        Assert.Equal(
            "approved",
            PackedToolApproval.ResolvePackedInterruptStorageDecision(
                store,
                ["apr-roller", "apr-fetch"],
                "apr-fetch",
                currentApproved: true,
                currentExpired: false,
                currentStopRun: false));
    }

    [Fact]
    public void ResolvePackedInterruptDecisionIsExpiredWhenAbortOnTimeout()
    {
        var store = new InMemoryApprovalStore(CreateRow("apr-1", "call-1", Decision: null));

        Assert.Equal(
            "expired",
            PackedToolApproval.ResolvePackedInterruptStorageDecision(
                store,
                ["apr-1"],
                "apr-1",
                currentApproved: false,
                currentExpired: true,
                currentStopRun: true));
        Assert.Equal("expired", PackedToolApproval.ToProtocolDecision("expired"));
    }

    [Fact]
    public void ToProtocolDecisionMapsStorageWordsOntoInterruptActions()
    {
        Assert.Equal("approve", PackedToolApproval.ToProtocolDecision("approved"));
        Assert.Equal("deny", PackedToolApproval.ToProtocolDecision("denied"));
        Assert.Equal("stop_run", PackedToolApproval.ToProtocolDecision("stopped"));
        Assert.Equal("expired", PackedToolApproval.ToProtocolDecision("expired"));
    }

    [Fact]
    public void ToInterruptResolvedMembersEmitsPerMemberProtocolDecisions()
    {
        var store = new InMemoryApprovalStore(
            CreateRow("apr-roller", "call-roller", "denied"),
            CreateRow("apr-fetch", "call-fetch", "approved"));

        System.Text.Json.Nodes.JsonArray members = Assert.IsType<System.Text.Json.Nodes.JsonArray>(
            PackedToolApproval.ToInterruptResolvedMembers(store, ["apr-roller", "apr-fetch"]));
        Assert.Equal(2, members.Count);
        Assert.Equal("apr-roller", members[0]?["requestId"]?.GetValue<string>());
        Assert.Equal("deny", members[0]?["decision"]?.GetValue<string>());
        Assert.Equal("apr-fetch", members[1]?["requestId"]?.GetValue<string>());
        Assert.Equal("approve", members[1]?["decision"]?.GetValue<string>());
    }

    [Fact]
    public void ToInterruptResolvedMembersOverlaysStopOntoPendingLeftovers()
    {
        var store = new InMemoryApprovalStore(
            CreateRow("apr-roller", "call-roller", null),
            CreateRow("apr-fetch", "call-fetch", null));

        System.Text.Json.Nodes.JsonArray members = Assert.IsType<System.Text.Json.Nodes.JsonArray>(
            PackedToolApproval.ToInterruptResolvedMembers(
                store,
                ["apr-roller", "apr-fetch"],
                overlayRequestId: "apr-roller",
                overlayStorageDecision: "stopped",
                overlayRemainingPendingAsStopped: true));
        Assert.Equal("stop_run", members[0]?["decision"]?.GetValue<string>());
        Assert.Equal("stop_run", members[1]?["decision"]?.GetValue<string>());
    }

    [Fact]
    public void IndependentToolsExecuteOnDecideButAgentAsToolDoesNot()
    {
        Assert.True(PackedToolApproval.CanExecuteIndependently("kautuka_web_fetch"));
        Assert.True(PackedToolApproval.CanExecuteIndependently("run_shell"));
        Assert.False(PackedToolApproval.CanExecuteIndependently("invoke_agent_roller"));
        Assert.False(PackedToolApproval.CanExecuteIndependently("invoke_agent"));
    }

    [Fact]
    public void ApplyStoredResultCopiesEnvelopeOutcome()
    {
        RunWaitState wait = new(
            "wait-1",
            "continuation-1",
            "run-1",
            "agent",
            "agent-1",
            "session-1",
            "agent-1",
            "user-1",
            RunWaitReasons.Approval,
            "apr-bbc",
            "{}",
            RunClockEnvelope.PutPackedToolResult(
                RunClockEnvelope.Wrap("{}", TimeSpan.Zero, ["apr-bbc", "apr-cnn"]),
                "apr-bbc",
                """{"result":"fetched"}""",
                journaledStart: false),
            "[]",
            "call-bbc",
            "kautuka_web_fetch",
            "apr-bbc",
            null,
            null,
            null,
            null,
            null,
            DateTimeOffset.UtcNow.ToString("O"),
            null,
            "event-1");
        var triggering = new ToolApprovalResponseInfo(
            "apr-bbc",
            "call-bbc",
            "kautuka_web_fetch",
            "{}",
            Approved: true,
            Reason: "approved",
            ContinuationRunId: "continuation-1");

        ToolApprovalResponseInfo merged = PackedToolApproval.ApplyStoredResult(wait, triggering);
        Assert.Equal("""{"result":"fetched"}""", merged.ResultJson);
        Assert.False(merged.ToolLifecycleAlreadyJournaled);
    }

    [Fact]
    public void CreateOutcomeResultJsonMarksDenied()
    {
        string json = PackedToolApproval.CreateOutcomeResultJson("kautuka_web_fetch", "denied");
        Assert.Contains("denied", json, StringComparison.Ordinal);
        Assert.Contains("kautuka_web_fetch", json, StringComparison.Ordinal);
    }

    private static GatewayToolApprovalRequest CreateRow(string requestId, string toolCallId, string? Decision)
        => new(
            Id: requestId,
            SessionId: "session-1",
            RunId: "run-1",
            CheckpointId: null,
            AgentId: "agent-1",
            UserId: "user-1",
            ToolCallId: toolCallId,
            ToolName: "kautuka_web_fetch",
            ToolType: "mcp",
            McpServerId: null,
            ArgumentsJson: "{}",
            RequestedAt: DateTimeOffset.UtcNow.ToString("O"),
            Decision: Decision,
            DecidedBy: Decision is null ? null : "user-1",
            DecidedAt: Decision is null ? null : DateTimeOffset.UtcNow.ToString("O"),
            Reason: Decision,
            ExpiresAt: null);

    [Fact]
    public async Task InvokeThrottledUnlimitedOverlapsAllWork()
    {
        InFlightTracker tracker = new();
        int[] results = await PackedToolApproval.InvokeThrottledAsync(
            CreateTrackedWork(tracker, 4),
            maxParallelTools: 0,
            TestContext.Current.CancellationToken).ConfigureAwait(true);

        Assert.Equal([1, 2, 3, 4], results);
        Assert.True(tracker.MaxInFlight >= 4, $"expected overlapping work, max in-flight was {tracker.MaxInFlight}");
    }

    [Fact]
    public async Task InvokeThrottledSerialRunsOneAtATime()
    {
        InFlightTracker tracker = new();
        int[] results = await PackedToolApproval.InvokeThrottledAsync(
            CreateTrackedWork(tracker, 4),
            maxParallelTools: 1,
            TestContext.Current.CancellationToken).ConfigureAwait(true);

        Assert.Equal([1, 2, 3, 4], results);
        Assert.Equal(1, tracker.MaxInFlight);
    }

    [Fact]
    public async Task InvokeThrottledCapLimitsInFlight()
    {
        InFlightTracker tracker = new();
        int[] results = await PackedToolApproval.InvokeThrottledAsync(
            CreateTrackedWork(tracker, 4),
            maxParallelTools: 2,
            TestContext.Current.CancellationToken).ConfigureAwait(true);

        Assert.Equal([1, 2, 3, 4], results);
        Assert.Equal(2, tracker.MaxInFlight);
    }

    private static List<Func<CancellationToken, Task<int>>> CreateTrackedWork(InFlightTracker tracker, int count)
    {
        var work = new List<Func<CancellationToken, Task<int>>>(count);
        for (int index = 1; index <= count; index++)
        {
            int value = index;
            work.Add(ct => tracker.RunAsync(value, ct));
        }

        return work;
    }

    private sealed class InFlightTracker
    {
        private readonly object gate = new();
        private int inFlight;

        public int MaxInFlight { get; private set; }

        public async Task<int> RunAsync(int value, CancellationToken cancellationToken)
        {
            lock (gate)
            {
                inFlight++;
                if (inFlight > MaxInFlight)
                {
                    MaxInFlight = inFlight;
                }
            }

            try
            {
                await Task.Delay(80, cancellationToken).ConfigureAwait(false);
                return value;
            }
            finally
            {
                lock (gate)
                {
                    inFlight--;
                }
            }
        }
    }

    private sealed class InMemoryApprovalStore(params GatewayToolApprovalRequest[] requests) : IGatewayApprovalStore
    {
        private readonly IReadOnlyDictionary<string, GatewayToolApprovalRequest> requestsById =
            requests.ToDictionary(static request => request.Id, StringComparer.Ordinal);

        public void CreateRequest(GatewayToolApprovalRequest request) => throw new NotSupportedException();

        public bool Resolve(string requestId, bool approved, string? reason, string? decidedBy, string resolutionSource, ApprovalContinuationWork? continuation = null)
            => throw new NotSupportedException();

        public IReadOnlyList<GatewayToolApprovalRequest> ListPending(string? userId, bool adminAll) => [];

        public GatewayToolApprovalRequest? GetDecidedByRunAndTool(string runId, string toolCallId, string toolName) => null;

        public GatewayToolApprovalRequest? GetDecidedByRunToolAndArguments(string runId, string toolName, string? argumentsJson) => null;

        public GatewayToolApprovalRequest? GetById(string requestId)
            => requestsById.GetValueOrDefault(requestId);

        public void SetChannelTetherMode(string requestId, ChannelHitlTetherMode mode)
            => throw new NotSupportedException();

        public IReadOnlyList<GatewayToolApprovalRequest> ListBySession(string sessionId) => [];

        public IReadOnlyList<GatewayToolApprovalRequest> ListHistory(string? userId, bool adminAll, int limit, int offset) => [];
    }

    private sealed class SingleWaitStore(RunWaitState wait) : IRunWaitStateStore
    {
        public RunWaitState PauseRunForApproval(RunWaitStateCreateRequest request) => throw new NotSupportedException();
        public RunWaitState PauseRunForChild(RunWaitStateCreateRequest request) => throw new NotSupportedException();
        public RunWaitState? GetByWaitId(string waitId)
            => string.Equals(wait.WaitId, waitId, StringComparison.Ordinal) ? wait : null;
        public RunWaitState? GetActiveByContinuationRunId(string continuationRunId)
            => string.Equals(wait.ContinuationRunId, continuationRunId, StringComparison.Ordinal) ? wait : null;
        public RunWaitState? GetActiveByCorrelationId(string correlationId)
            => PackedToolApproval.WaitCoversRequest(wait, correlationId) ? wait : null;
        public RunWaitState? GetActiveChildPauseForCaller(string callerSessionId, string callerToolCallId) => null;
        public bool TryClaimExecution(string waitId, string leaseOwner, TimeSpan leaseDuration) => throw new NotSupportedException();
        public void RenewExecutionLease(string waitId, string leaseOwner, TimeSpan leaseDuration) => throw new NotSupportedException();
        public void ReleaseForRetry(string waitId, string leaseOwner) => throw new NotSupportedException();
        public void MarkPausedAgain(string waitId) => throw new NotSupportedException();
        public void ReleaseExecutingWait(string waitId) => throw new NotSupportedException();
        public void MarkCompleted(string waitId) => throw new NotSupportedException();
        public void MarkFailed(string waitId, string failureCode) => throw new NotSupportedException();
        public IReadOnlyList<RunWaitState> ListRecoverable() => [wait];
        public void DeleteBySession(string sessionId) => throw new NotSupportedException();
    }
}
