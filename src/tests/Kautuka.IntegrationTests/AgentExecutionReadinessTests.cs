using Grpc.Core;

using Kautuka.Gateway.Domain.AgentFiles;
using Kautuka.Gateway.Domain.Execution;
using Kautuka.Gateway.Domain.Usage;
using Kautuka.Manager.Protos;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace Kautuka.IntegrationTests;

public sealed class AgentExecutionReadinessTests
{
    [Fact]
    public async Task ManagerProbeReportsManagerUnavailable()
    {
        var manager = new FakeManagerGrpcService(
            exception: new RpcException(new Status(StatusCode.Unavailable, "connection refused")));
        var probe = new ManagerExecutionDependencyProbe(manager);

        AgentExecutionDependencyStatus result = await probe.CheckAsync(TestContext.Current.CancellationToken);

        Assert.False(result.Ready);
        Assert.Equal("MANAGER_UNAVAILABLE", result.Code);
        Assert.DoesNotContain("connection refused", result.Message, StringComparison.Ordinal);
        Assert.Equal(2, manager.CallCount);
    }

    [Fact]
    public async Task ManagerProbeDoesNotPublishAOneShotTransportFailure()
    {
        var manager = new FakeManagerGrpcService(
            status: new ManagerStatusResponse
            {
                Ok = true,
                CoreReady = true,
                Lifecycle = "reconciled",
            },
            exception: new RpcException(new Status(StatusCode.Unavailable, "transient")),
            exceptionCount: 1);
        var probe = new ManagerExecutionDependencyProbe(manager);

        AgentExecutionDependencyStatus result = await probe.CheckAsync(TestContext.Current.CancellationToken);

        Assert.True(result.Ready);
        Assert.Equal("READY", result.Code);
        Assert.Equal(2, manager.CallCount);
    }

    [Fact]
    public async Task ManagerProbeConfirmsAPersistentDeadline()
    {
        var manager = new FakeManagerGrpcService(
            exception: new RpcException(new Status(StatusCode.DeadlineExceeded, "deadline")));
        var probe = new ManagerExecutionDependencyProbe(manager);

        AgentExecutionDependencyStatus result = await probe.CheckAsync(TestContext.Current.CancellationToken);

        Assert.False(result.Ready);
        Assert.Equal("MANAGER_UNAVAILABLE", result.Code);
        Assert.Equal(2, manager.CallCount);
    }

    [Fact]
    public async Task ManagerProbeDoesNotPublishAOneShotDeadline()
    {
        var manager = new FakeManagerGrpcService(
            status: new ManagerStatusResponse
            {
                Ok = true,
                CoreReady = true,
                Lifecycle = "reconciled",
            },
            exception: new RpcException(new Status(StatusCode.DeadlineExceeded, "transient deadline")),
            exceptionCount: 1);
        var probe = new ManagerExecutionDependencyProbe(manager);

        AgentExecutionDependencyStatus result = await probe.CheckAsync(TestContext.Current.CancellationToken);

        Assert.True(result.Ready);
        Assert.Equal("READY", result.Code);
        Assert.Equal(2, manager.CallCount);
    }

    [Fact]
    public async Task ManagerProbeDistinguishesUnavailableContainerRuntime()
    {
        var probe = new ManagerExecutionDependencyProbe(new FakeManagerGrpcService(status: new ManagerStatusResponse
        {
            Ok = true,
            CoreReady = true,
            Lifecycle = "failed",
            LastFailureClass = "runtime_unavailable",
        }));

        AgentExecutionDependencyStatus result = await probe.CheckAsync(TestContext.Current.CancellationToken);

        Assert.False(result.Ready);
        Assert.Equal("container_runtime", result.Dependency);
        Assert.Equal("CONTAINER_RUNTIME_UNAVAILABLE", result.Code);
        Assert.Equal("runtime_unavailable", result.Detail);
    }

    [Fact]
    public async Task ManagerProbeReportsReadyOnlyWhenManagerCoreIsReady()
    {
        var probe = new ManagerExecutionDependencyProbe(new FakeManagerGrpcService(status: new ManagerStatusResponse
        {
            Ok = true,
            CoreReady = true,
            Lifecycle = "reconciled",
        }));

        AgentExecutionDependencyStatus result = await probe.CheckAsync(TestContext.Current.CancellationToken);

        Assert.True(result.Ready);
        Assert.Equal("READY", result.Code);
    }

    [Fact]
    public async Task AggregateFailsClosedWithDeterministicDependencyOrder()
    {
        IAgentExecutionDependencyProbe[] probes =
        [
            new FixedProbe(new("manager", true, "READY", "ready")),
            new FixedProbe(new("credentials_host", false, "CREDENTIALS_HOST_UNAVAILABLE", "credentials unavailable")),
        ];
        var service = new AgentExecutionReadinessService(probes);

        AgentExecutionReadiness result = await service.CheckAsync(TestContext.Current.CancellationToken);

        Assert.False(result.Ready);
        Assert.Equal("CREDENTIALS_HOST_UNAVAILABLE", result.Code);
        Assert.Equal(["credentials_host", "manager"], result.Dependencies.Select(static item => item.Dependency));
    }

    [Fact]
    public void CentralExecutionBoundaryDoesNotBlockTerminalApprovalDecisions()
    {
        Assert.True(AgentChatService.RequiresExecutionDependencies(null));
        Assert.True(AgentChatService.RequiresExecutionDependencies(Approval(approved: true, stopRun: false)));
        Assert.False(AgentChatService.RequiresExecutionDependencies(Approval(approved: false, stopRun: false)));
        Assert.False(AgentChatService.RequiresExecutionDependencies(Approval(approved: false, stopRun: true)));
        Assert.False(AgentChatService.RequiresExecutionDependencies(Approval(approved: true, stopRun: true)));
    }

    [Fact]
    public async Task CentralExecutionBoundaryGuardsNewRunsAndApprovedContinuations()
    {
        AgentExecutionReadiness failure = UnavailableReadiness();
        var newRunReadiness = new FixedReadinessService(failure);
        var approvedReadiness = new FixedReadinessService(failure);

        AgentExecutionReadiness? newRunFailure = await AgentChatService.GetExecutionReadinessFailureAsync(
            null,
            newRunReadiness,
            TestContext.Current.CancellationToken);
        AgentExecutionReadiness? approvedFailure = await AgentChatService.GetExecutionReadinessFailureAsync(
            Approval(approved: true, stopRun: false),
            approvedReadiness,
            TestContext.Current.CancellationToken);

        Assert.Same(failure, newRunFailure);
        Assert.Same(failure, approvedFailure);
        Assert.Equal(1, newRunReadiness.CheckCount);
        Assert.Equal(1, approvedReadiness.CheckCount);
    }

    [Fact]
    public async Task StreamAsyncPublishesCanonicalAndNaradFailureWithoutConstructingRuntimeForNewRun()
    {
        await AssertUnavailableDependencyStopsExecutionAsync(approvalResponse: null).ConfigureAwait(true);
    }

    [Fact]
    public async Task StreamAsyncPublishesCanonicalAndNaradFailureWithoutConstructingRuntimeForApprovedContinuation()
    {
        await AssertUnavailableDependencyStopsExecutionAsync(
            Approval(approved: true, stopRun: false)).ConfigureAwait(true);
    }

    [Fact]
    public async Task StreamAsyncTreatsContradictoryApprovedStopAsTerminalWithoutConstructingRuntime()
    {
        var runtimeFactory = new CountingRuntimeFactory();
        AgentChatService service = CreateService(
            runtimeFactory,
            new FixedReadinessService(new AgentExecutionReadiness(
                true,
                "READY",
                "ready",
                [])));

        NaradPersistedEvent[] events = await CollectAsync(service.StreamAsync(
            "agent",
            "session",
            "user",
            "approval",
            string.Empty,
            null,
            [],
            [],
            [],
            Approval(approved: true, stopRun: true),
            null,
            ct: TestContext.Current.CancellationToken)).ConfigureAwait(true);

        Assert.Equal(0, runtimeFactory.BuildCount);
        Assert.Contains(
            events,
            static item => item.EventType is NaradEventTypes.RunFinished or NaradEventTypes.RunCancelled);
        Assert.DoesNotContain(events, static item => item.EventType == NaradEventTypes.RunError);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task CentralExecutionBoundaryBypassesTerminalApprovalDecisions(bool approved, bool stopRun)
    {
        var readiness = new FixedReadinessService(UnavailableReadiness());

        AgentExecutionReadiness? failure = await AgentChatService.GetExecutionReadinessFailureAsync(
            Approval(approved, stopRun),
            readiness,
            TestContext.Current.CancellationToken);

        Assert.Null(failure);
        Assert.Equal(0, readiness.CheckCount);
    }

    private static ToolApprovalResponseInfo Approval(bool approved, bool stopRun)
        => new("request", "call", "tool", null, approved, null, StopRun: stopRun);

    private static AgentExecutionReadiness UnavailableReadiness()
        => new(
            false,
            "MANAGER_UNAVAILABLE",
            "Manager unavailable.",
            [new("manager", false, "MANAGER_UNAVAILABLE", "Manager unavailable.")]);

    private static async Task AssertUnavailableDependencyStopsExecutionAsync(
        ToolApprovalResponseInfo? approvalResponse)
    {
        var runtimeFactory = new CountingRuntimeFactory();
        AgentChatService service = CreateService(runtimeFactory, new FixedReadinessService(UnavailableReadiness()));

        NaradPersistedEvent[] events = await CollectAsync(service.StreamAsync(
            "agent",
            "session",
            "user",
            "chat",
            "hello",
            null,
            [],
            Array.Empty<AgentWorkspaceFileReference>(),
            Array.Empty<InjectedMemoryFact>(),
            approvalResponse,
            null,
            ct: TestContext.Current.CancellationToken)).ConfigureAwait(true);

        Assert.Equal(0, runtimeFactory.BuildCount);
        NaradPersistedEvent failed = Assert.Single(
            events,
            static item => item.EventType == NaradEventTypes.RunError);
        Assert.Equal("MANAGER_UNAVAILABLE", NaradPersistedEventReader.String(failed, "code"));
    }

    private static AgentChatService CreateService(
        IAgentRuntimeFactory runtimeFactory,
        IAgentExecutionReadinessService readinessService)
        => new(
            runtimeFactory: runtimeFactory,
            capabilityService: null!,
            observationStore: null!,
            mediator: null!,
            logger: NullLogger<AgentChatService>.Instance,
            approvalStore: null!,
            continuationStore: null!,
            resumeTokenProvider: null!,
            assetStore: null!,
            assetIngestionService: null!,
            gatewayState: null!,
            agentRunMetrics: new NoOpAgentRunMetrics(),
            usageCaptureAccessor: new GatewayUsageCaptureAccessor(),
            sessionStore: null!,
            agentToolInvocationLinkStore: null!,
            runWaitStateStore: null!,
            platformConfigurationProvider: null!,
            toolExecutionPolicyService: null!,
            runtimeEventPublisher: new TestRuntimeEventPublisher(),
            modelTurnOrdinalCounter: new ModelTurnOrdinalCounter(),
            runtimeEventHistoryReader: new EmptyRuntimeEventHistoryReader(),
            executionReadinessService: readinessService,
            configuration: new ConfigurationBuilder().Build());

    private static async Task<NaradPersistedEvent[]> CollectAsync(IAsyncEnumerable<NaradPersistedEvent> source)
    {
        var events = new List<NaradPersistedEvent>();
        await foreach (NaradPersistedEvent item in source.ConfigureAwait(false))
            events.Add(item);
        return [.. events];
    }

    private sealed class CountingRuntimeFactory : IAgentRuntimeFactory
    {
        public int BuildCount { get; private set; }

        public ValueTask<AgentRuntimeResult> BuildAsync(
            string agentId,
            string sessionId,
            string? userId,
            string? stageTransitionId,
            IReadOnlyList<InjectedMemoryFact> memoryFacts,
            bool isContinuationResume,
            bool requireApprovalForAllTools,
            string? sessionStateOverrideJson,
            string? effectiveBehaviorOverrideJson,
            CancellationToken ct)
        {
            BuildCount++;
            return ValueTask.FromResult(AgentRuntimeResult.Failure(
                AgentRuntimeError.ClientCreationFailed,
                "Runtime factory should not have been called."));
        }

        public ValueTask SaveSessionAsync(AgentRuntime runtime, CancellationToken ct)
            => throw new NotSupportedException();
    }

    private sealed class NoOpAgentRunMetrics : IAgentRunMetrics
    {
        public void RecordRunStarted(AgentRunMetricContext context)
        {
        }

        public void RecordRunCompleted(AgentRunMetricContext context, AgentRunMetricObservation observation)
        {
        }
    }

    private sealed class FixedReadinessService(AgentExecutionReadiness readiness) : IAgentExecutionReadinessService
    {
        public int CheckCount { get; private set; }

        public Task<AgentExecutionReadiness> CheckAsync(CancellationToken cancellationToken)
        {
            CheckCount++;
            return Task.FromResult(readiness);
        }
    }

    private sealed class FixedProbe(AgentExecutionDependencyStatus status) : IAgentExecutionDependencyProbe
    {
        public string Dependency => status.Dependency;

        public Task<AgentExecutionDependencyStatus> CheckAsync(CancellationToken cancellationToken)
            => Task.FromResult(status);
    }

    private sealed class FakeManagerGrpcService(
        ManagerStatusResponse? status = null,
        Exception? exception = null,
        int exceptionCount = int.MaxValue) : IManagerGrpcService
    {
        public int CallCount { get; private set; }

        public Task<ManagerStatusResponse> GetHostStatusAsync(WorkspacePingRequest request, CancellationToken cancellationToken = default)
        {
            CallCount++;
            return exception is not null && CallCount <= exceptionCount
                ? Task.FromException<ManagerStatusResponse>(exception)
                : Task.FromResult(status!);
        }

        public Task<WorkspaceBoolResponse> PingAsync(WorkspacePingRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<ManagerRuntimeContainersResponse> ListRuntimeContainersAsync(WorkspacePingRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<WorkspaceContextResponse> DescribeWorkspaceAsync(WorkspaceResolvedRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<WorkspaceContextResponse> RebuildWorkspaceAsync(WorkspaceResolvedRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<WorkspaceDeleteResponse> DeleteWorkspaceAsync(WorkspaceResolvedRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<WorkspaceSandboxResponse> SpawnSandboxAsync(WorkspaceResolvedRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<WorkspaceSandboxResponse> InspectSandboxAsync(WorkspaceResolvedRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<WorkspaceExecutionResponse> ExecAsync(WorkspaceExecRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<WorkspaceBoolResponse> StopSandboxAsync(WorkspaceResolvedRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<WorkspaceBoolResponse> PrimeWorkspaceAsync(WorkspaceResolvedRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<WorkspaceMcpProbeResponse> ProbeMcpContainerAsync(WorkspaceMcpContainerProbeRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<WorkspaceMcpContainerListToolsResponse> ListMcpContainerToolsAsync(WorkspaceMcpContainerCallRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<WorkspaceMcpContainerCallResponse> CallMcpContainerToolAsync(WorkspaceMcpContainerCallRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<DesiredStateUpdateResponse> UpdateDesiredStateAsync(ManagerDesiredStateUpdateRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class EmptyRuntimeEventHistoryReader : IRuntimeEventHistoryReader
    {
        public IReadOnlyList<NaradPersistedEvent> ReadRootHistory(string rootRunId) => [];

        public IReadOnlyList<NaradPersistedEvent> ReadSessionHistory(string rootAgentId, string sessionId) => [];
    }
}
