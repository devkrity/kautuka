using System.Threading.Channels;

using Kautuka.Gateway.Domain.Execution;
using Kautuka.Gateway.Domain.Hitl;
using Kautuka.Gateway.Domain.Runtime;
using Kautuka.Gateway.Domain.Stores;

using Mediator;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

using static Kautuka.IntegrationTests.ThreadCommandTestSupport;

namespace Kautuka.IntegrationTests;

public sealed class ApprovalContinuationDispatcherServiceTests
{
    [Fact]
    public async Task HeartbeatFailureRetriesItsWorkWithoutStoppingDispatcherOrBlockingNextApproval()
    {
        var queue = new TestApprovalContinuationQueue();
        var history = new MutableTerminalHistoryReader();
        var orchestrator = new WaitingContinuationOrchestrator();
        var waits = new FakeRunWaitStateStore(CreateApprovalWaitState() with { RootRunId = "run-caller-1" });
        var publisher = new CapturingPublisher();
        using var service = new ApprovalContinuationDispatcherService(queue,
            new FakeApprovalStore(CreatePackedAgentApproval("req-1", "tool-1", "approved"),
                CreatePackedAgentApproval("req-2", "tool-2", "approved") with { RunId = "next-run" }),
            orchestrator, waits, new FakeInvocationLinkStore(), publisher,
            NullLogger<ApprovalContinuationDispatcherService>.Instance, runtimeEventHistoryReader: history);
        await service.StartAsync(CancellationToken.None);
        try
        {
            await queue.EnqueueAsync(CreateApprovedContinuationWork("req-1"), TestContext.Current.CancellationToken);
            await orchestrator.Started.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            history.FailReads();
            await queue.EnqueueAsync(CreateApprovedContinuationWork("req-2"), TestContext.Current.CancellationToken);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            while (queue.FailedCount < 1 || queue.CompletedCount < 1) await Task.Delay(25, timeout.Token);
            Assert.Equal(1, queue.FailedCount);
            Assert.Equal(1, queue.CompletedCount);
            Assert.Equal("req-2", (await publisher.WaitForCompletedAsync()).RequestId);
            Assert.Equal(RunWaitStatuses.Ready, waits.GetByWaitId("wait-parent-1")!.Status);
            Assert.False(service.ExecuteTask!.IsCompleted);
        }
        finally { await service.StopAsync(CancellationToken.None); }
    }

    [Fact]
    public async Task AlreadyCompletedSqliteWaitDoesNotCrashDispatcherAndNextApprovalStillRuns()
    {
        string directory = Path.Combine(Path.GetTempPath(), "kautuka-wait-completion-race", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var database = new KautukaDatabase(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            { ["Kautuka:ContainerStateRoot"] = directory }).Build());
            database.EnsureReady();
            var waits = new SqliteRunWaitStateStore(database);
            RunWaitState definition = CreateApprovalWaitState();
            RunWaitState wait = waits.PauseRunForApproval(new RunWaitStateCreateRequest(
                definition.ContinuationRunId, definition.RunId, definition.TargetType, definition.TargetId, definition.SessionId,
                definition.AgentId, definition.UserId, "req-1", definition.SessionStateJson, definition.BehaviorJson,
                definition.ArtifactManifestJson, definition.CanonicalEventId, ToolCallId: definition.ToolCallId,
                ToolName: definition.ToolName, ApprovalRequestId: "req-1", RootRunId: "run-caller-1"));
            var queue = new TestApprovalContinuationQueue();
            var history = new MutableTerminalHistoryReader();
            RunWaitState? closedByStream = null;
            var orchestrator = new WaitingContinuationOrchestrator(() =>
            {
                waits.MarkCompleted(wait.WaitId);
                closedByStream = waits.GetByWaitId(wait.WaitId);
            });
            var publisher = new CapturingPublisher();
            using var service = new ApprovalContinuationDispatcherService(queue,
                new FakeApprovalStore(CreatePackedAgentApproval("req-1", "tool-1", "approved"),
                    CreatePackedAgentApproval("req-2", "tool-2", "approved") with { RunId = "next-run" }),
                orchestrator, waits, new FakeInvocationLinkStore(), publisher,
                NullLogger<ApprovalContinuationDispatcherService>.Instance, runtimeEventHistoryReader: history);
            await service.StartAsync(CancellationToken.None);
            try
            {
                await queue.EnqueueAsync(CreateApprovedContinuationWork("req-1"), TestContext.Current.CancellationToken);
                await orchestrator.Started.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
                history.Terminate(finished: true);
                await queue.EnqueueAsync(CreateApprovedContinuationWork("req-2"), TestContext.Current.CancellationToken);
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
                while (queue.CompletedCount < 2) await Task.Delay(25, timeout.Token);
                Assert.Equal(0, queue.FailedCount);
                Assert.Equal("req-2", (await publisher.WaitForCompletedAsync()).RequestId);
                Assert.NotNull(closedByStream);
                Assert.Equal(RunWaitStatuses.Completed, closedByStream.Status);
                Assert.Equal(closedByStream, waits.GetByWaitId(wait.WaitId));
                Assert.False(service.ExecuteTask!.IsCompleted);
            }
            finally { await service.StopAsync(CancellationToken.None); }
        }
        finally
        {
            SqliteTestPoolCleanup.ClearPoolsUnderDirectory(directory);
            Directory.Delete(directory, recursive: true);
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task TerminalRunReleasesDispatcherWithoutReexecutingToolsOrBlockingNextApproval(bool terminalBeforeDispatch)
    {
        var queue = new TestApprovalContinuationQueue();
        var history = new MutableTerminalHistoryReader();
        var orchestrator = new WaitingContinuationOrchestrator();
        var waits = new FakeRunWaitStateStore(CreateApprovalWaitState() with { RootRunId = "run-caller-1" });
        var publisher = new CapturingPublisher();
        using var service = new ApprovalContinuationDispatcherService(queue,
            new FakeApprovalStore(CreatePackedAgentApproval("req-1", "tool-1", "approved"),
                CreatePackedAgentApproval("req-2", "tool-2", "approved") with { RunId = "next-run" }),
            orchestrator, waits, new FakeInvocationLinkStore(), publisher,
            NullLogger<ApprovalContinuationDispatcherService>.Instance, runtimeEventHistoryReader: history);
        if (terminalBeforeDispatch) history.Terminate();
        await service.StartAsync(CancellationToken.None);
        try
        {
            await queue.EnqueueAsync(CreateApprovedContinuationWork("req-1"), TestContext.Current.CancellationToken);
            if (!terminalBeforeDispatch)
            {
                await orchestrator.Started.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
                history.Terminate();
            }
            await queue.EnqueueAsync(CreateApprovedContinuationWork("req-2"), TestContext.Current.CancellationToken);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            while (queue.CompletedCount < 2) await Task.Delay(25, timeout.Token);
            Assert.Equal(0, queue.FailedCount);
            Assert.Equal(terminalBeforeDispatch ? 0 : 1, orchestrator.WaitingCalls);
            Assert.Equal("req-2", (await publisher.WaitForCompletedAsync()).RequestId);
            Assert.Equal(RunWaitStatuses.Failed, waits.GetByWaitId("wait-parent-1")!.Status);
        }
        finally { await service.StopAsync(CancellationToken.None); }
    }

    private sealed class MutableTerminalHistoryReader : IRuntimeEventHistoryReader
    {
        private int terminal;
        public void Terminate(bool finished = false) => Interlocked.Exchange(ref terminal, finished ? 2 : 1);
        public void FailReads() => Interlocked.Exchange(ref terminal, 3);
        public IReadOnlyList<NaradPersistedEvent> ReadRootHistory(string rootRunId)
            => rootRunId == "run-caller-1" && Volatile.Read(ref terminal) == 3
                ? throw new InvalidOperationException("Simulated wait monitor failure.")
                : rootRunId == "run-caller-1" && Volatile.Read(ref terminal) != 0
                ? [NaradTestEventFactory.FromBody(Volatile.Read(ref terminal) == 2 ? NaradEventTypes.RunFinished : NaradEventTypes.RunError,
                    Volatile.Read(ref terminal) == 2 ? NaradEventBodies.RunFinished(NaradEventBodies.Usage(0, 0, 0), "done") : NaradEventBodies.RunError("gateway_process_lost", "Interrupted", null),
                    sessionId: "session-caller-1", runId: rootRunId, rootRunId: rootRunId,
                    sessionSequence: 1, rootSequence: 1)] : [];
        public IReadOnlyList<NaradPersistedEvent> ReadSessionHistory(string rootAgentId, string sessionId) => [];
    }

    private sealed class WaitingContinuationOrchestrator(Action? onCancellation = null) : IApprovalContinuationOrchestrator
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int WaitingCalls { get; private set; }
        public async IAsyncEnumerable<NaradPersistedEvent> StreamAsync(ApprovalContinuationStreamRequest request,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            if (request.ApprovalResponse.RequestId == "req-1")
            {
                WaitingCalls++;
                Started.TrySetResult();
                try { await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken); }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    onCancellation?.Invoke();
                    throw;
                }
            }
            yield break;
        }
    }

    [Fact]
    public async Task DispatcherDoesNotStopRunBeforeResolutionSideEffects()
    {
        var queue = new TestApprovalContinuationQueue();
        var stopCommands = new RecordingRunStopCommandService();
        var approvalStore = new FakeApprovalStore(new GatewayToolApprovalRequest(
            Id: "req-1",
            SessionId: "session-inner-1",
            RunId: "run-inner-1",
            CheckpointId: null,
            AgentId: "agent-inner-1",
            UserId: "user-1",
            ToolCallId: "tool-call-inner-1",
            ToolName: "run_skill_script",
            ToolType: "mcp",
            McpServerId: null,
            ArgumentsJson: "{}",
            RequestedAt: DateTimeOffset.UtcNow.ToString("O"),
            Decision: "denied",
            DecidedBy: "user-1",
            DecidedAt: DateTimeOffset.UtcNow.ToString("O"),
            Reason: "stopped",
            ExpiresAt: DateTimeOffset.UtcNow.AddMinutes(5).ToString("O")));
        var continuationOrchestrator = new CapturingContinuationOrchestrator();
        var runWaitStateStore = new FakeRunWaitStateStore(CreateApprovalWaitState());
        var publisher = new CapturingPublisher();
        using var service = new ApprovalContinuationDispatcherService(
            queue,
            approvalStore,
            continuationOrchestrator,
            runWaitStateStore,
            new FakeInvocationLinkStore(),
            publisher,
            NullLogger<ApprovalContinuationDispatcherService>.Instance);

        await service.StartAsync(CancellationToken.None).ConfigureAwait(true);
        try
        {
            await queue.EnqueueAsync(new ApprovalContinuationWork(
                RequestId: "req-1",
                SessionId: "session-inner-1",
                TargetType: "agent",
                TargetId: "agent-inner-1",
                UserId: "user-1",
                Approved: false,
                Source: HitlResolutionSource.ChannelCallback,
                Reason: "stopped",
                DecidedByUserId: "user-1",
                StopRun: true),
                CancellationToken.None).ConfigureAwait(true);

            _ = await publisher.WaitForCompletedAsync().ConfigureAwait(true);
            Assert.Empty(stopCommands.Commands);
        }
        finally
        {
            await service.StopAsync(CancellationToken.None).ConfigureAwait(true);
        }
    }

    [Fact]
    public async Task DispatcherRoutesStopRunThroughContinuationOrchestrator()
    {
        var queue = new TestApprovalContinuationQueue();
        var publisher = new CapturingPublisher();
        var approvalStore = new FakeApprovalStore(new GatewayToolApprovalRequest(
            Id: "req-1",
            SessionId: "session-inner-1",
            RunId: "run-inner-1",
            CheckpointId: null,
            AgentId: "agent-inner-1",
            UserId: "user-1",
            ToolCallId: "tool-call-inner-1",
            ToolName: "run_skill_script",
            ToolType: "mcp",
            McpServerId: null,
            ArgumentsJson: "{}",
            RequestedAt: DateTimeOffset.UtcNow.ToString("O"),
            Decision: "denied",
            DecidedBy: "user-1",
            DecidedAt: DateTimeOffset.UtcNow.ToString("O"),
            Reason: "stopped",
            ExpiresAt: DateTimeOffset.UtcNow.AddMinutes(5).ToString("O")));
        var continuationOrchestrator = new CapturingContinuationOrchestrator();
        var runWaitStateStore = new FakeRunWaitStateStore(CreateApprovalWaitState());
        using var service = new ApprovalContinuationDispatcherService(
            queue,
            approvalStore,
            continuationOrchestrator,
            runWaitStateStore,
            new FakeInvocationLinkStore(),
            publisher,
            NullLogger<ApprovalContinuationDispatcherService>.Instance);

        await service.StartAsync(CancellationToken.None).ConfigureAwait(true);
        try
        {
            await queue.EnqueueAsync(new ApprovalContinuationWork(
                RequestId: "req-1",
                SessionId: "session-inner-1",
                TargetType: "agent",
                TargetId: "agent-inner-1",
                UserId: "user-1",
                Approved: false,
                Source: HitlResolutionSource.ChannelCallback,
                Reason: "stopped",
                DecidedByUserId: "user-1",
                StopRun: true),
                CancellationToken.None).ConfigureAwait(true);

            _ = await publisher.WaitForCompletedAsync().ConfigureAwait(true);
            ApprovalContinuationStreamRequest request = Assert.IsType<ApprovalContinuationStreamRequest>(continuationOrchestrator.Request);
            Assert.True(request.ApprovalResponse.StopRun);
            Assert.False(request.ApprovalResponse.Approved);
        }
        finally
        {
            await service.StopAsync(CancellationToken.None).ConfigureAwait(true);
        }
    }

    [Fact]
    public async Task DispatcherHoldsFiccResumeUntilPackedHumanSiblingsAreDecided()
    {
        var queue = new TestApprovalContinuationQueue();
        var publisher = new CapturingPublisher();
        var continuationOrchestrator = new CapturingContinuationOrchestrator();
        GatewayToolApprovalRequest bbc = CreatePackedFetchApproval("req-bbc", "call-bbc", Decision: "approved");
        GatewayToolApprovalRequest cnn = CreatePackedFetchApproval("req-cnn", "call-cnn", Decision: null);
        var approvalStore = new FakeApprovalStore(bbc, cnn);
        RunWaitState waitState = CreatePackedApprovalWaitState("req-bbc", "req-cnn");
        var runWaitStateStore = new FakeRunWaitStateStore(waitState);
        var packedInvoker = new StubPackedToolInvoker();
        using var service = new ApprovalContinuationDispatcherService(
            queue,
            approvalStore,
            continuationOrchestrator,
            runWaitStateStore,
            new FakeInvocationLinkStore(),
            publisher,
            NullLogger<ApprovalContinuationDispatcherService>.Instance,
            packedInvoker);

        await service.StartAsync(CancellationToken.None).ConfigureAwait(true);
        try
        {
            await queue.EnqueueAsync(new ApprovalContinuationWork(
                RequestId: "req-bbc",
                SessionId: "session-caller-1",
                TargetType: "agent",
                TargetId: "agent-caller-1",
                UserId: "user-1",
                Approved: true,
                Source: HitlResolutionSource.ChannelCallback,
                Reason: "approved",
                DecidedByUserId: "user-1",
                StopRun: false),
                CancellationToken.None).ConfigureAwait(true);

            await WaitForQueueCompletedAsync(queue).ConfigureAwait(true);
            Assert.Null(continuationOrchestrator.Request);
            Assert.Equal(RunWaitStatuses.Ready, runWaitStateStore.GetByWaitId(waitState.WaitId)?.Status);
            Assert.False(RunClockEnvelope.TryReadPackedToolResults(
                runWaitStateStore.GetByWaitId(waitState.WaitId)?.BehaviorJson,
                out _));
            Assert.Empty(packedInvoker.InvokedNames);
        }
        finally
        {
            await service.StopAsync(CancellationToken.None).ConfigureAwait(true);
        }
    }

    [Fact]
    public async Task DispatcherDoesNotExecuteAgentAsToolWhileSiblingsRemain()
    {
        var queue = new TestApprovalContinuationQueue();
        var publisher = new CapturingPublisher();
        var continuationOrchestrator = new CapturingContinuationOrchestrator();
        GatewayToolApprovalRequest roller = CreatePackedAgentApproval("req-roller", "call-roller", "approved");
        GatewayToolApprovalRequest fetch = CreatePackedFetchApproval("req-fetch", "call-fetch", Decision: null);
        var approvalStore = new FakeApprovalStore(roller, fetch);
        RunWaitState waitState = CreatePackedApprovalWaitState("req-roller", "req-fetch");
        var runWaitStateStore = new FakeRunWaitStateStore(waitState);
        var packedInvoker = new StubPackedToolInvoker();
        using var service = new ApprovalContinuationDispatcherService(
            queue,
            approvalStore,
            continuationOrchestrator,
            runWaitStateStore,
            new FakeInvocationLinkStore(),
            publisher,
            NullLogger<ApprovalContinuationDispatcherService>.Instance,
            packedInvoker);

        await service.StartAsync(CancellationToken.None).ConfigureAwait(true);
        try
        {
            await queue.EnqueueAsync(new ApprovalContinuationWork(
                RequestId: "req-roller",
                SessionId: "session-caller-1",
                TargetType: "agent",
                TargetId: "agent-caller-1",
                UserId: "user-1",
                Approved: true,
                Source: HitlResolutionSource.ChannelCallback,
                Reason: "approved",
                DecidedByUserId: "user-1",
                StopRun: false),
                CancellationToken.None).ConfigureAwait(true);

            await WaitForQueueCompletedAsync(queue).ConfigureAwait(true);
            Assert.Null(continuationOrchestrator.Request);
            Assert.Empty(packedInvoker.InvokedNames);
            Assert.False(RunClockEnvelope.TryReadPackedToolResults(
                runWaitStateStore.GetByWaitId(waitState.WaitId)?.BehaviorJson,
                out _));
        }
        finally
        {
            await service.StopAsync(CancellationToken.None).ConfigureAwait(true);
        }
    }

    [Fact]
    public async Task DispatcherStoresEveryDecidedPackedMemberWhenPackHeadJobRuns()
    {
        var queue = new TestApprovalContinuationQueue();
        var publisher = new CapturingPublisher();
        var continuationOrchestrator = new CapturingContinuationOrchestrator();
        GatewayToolApprovalRequest roller = CreatePackedAgentApproval("req-roller", "call-roller", "denied");
        GatewayToolApprovalRequest sibling = CreatePackedAgentApproval("req-roller-2", "call-roller-2", "denied");
        GatewayToolApprovalRequest fetch = CreatePackedFetchApproval("req-fetch", "call-fetch", Decision: null);
        var approvalStore = new FakeApprovalStore(roller, sibling, fetch);
        RunWaitState waitState = CreatePackedApprovalWaitState("req-roller", "req-roller-2", "req-fetch");
        var runWaitStateStore = new FakeRunWaitStateStore(waitState);
        using var service = new ApprovalContinuationDispatcherService(
            queue,
            approvalStore,
            continuationOrchestrator,
            runWaitStateStore,
            new FakeInvocationLinkStore(),
            publisher,
            NullLogger<ApprovalContinuationDispatcherService>.Instance);

        await service.StartAsync(CancellationToken.None).ConfigureAwait(true);
        try
        {
            await queue.EnqueueAsync(new ApprovalContinuationWork(
                RequestId: "req-roller",
                SessionId: "session-caller-1",
                TargetType: "agent",
                TargetId: "agent-caller-1",
                UserId: "user-1",
                Approved: false,
                Source: HitlResolutionSource.Browser,
                Reason: "denied",
                DecidedByUserId: "user-1",
                StopRun: false),
                CancellationToken.None).ConfigureAwait(true);

            await WaitForQueueCompletedAsync(queue).ConfigureAwait(true);
            Assert.Null(continuationOrchestrator.Request);
            Assert.True(RunClockEnvelope.TryReadPackedToolResults(
                runWaitStateStore.GetByWaitId(waitState.WaitId)?.BehaviorJson,
                out IReadOnlyDictionary<string, PackedToolResult> results));
            Assert.True(results.ContainsKey("req-roller"));
            Assert.True(results.ContainsKey("req-roller-2"));
            Assert.False(results.ContainsKey("req-fetch"));
            Assert.Contains("denied", results["req-roller"].ResultJson, StringComparison.Ordinal);
            Assert.Contains("denied", results["req-roller-2"].ResultJson, StringComparison.Ordinal);
        }
        finally
        {
            await service.StopAsync(CancellationToken.None).ConfigureAwait(true);
        }
    }

    [Fact]
    public async Task DispatcherInvokesIndependentPackedFetchesInParallel()
    {
        var queue = new TestApprovalContinuationQueue();
        var publisher = new CapturingPublisher();
        var continuationOrchestrator = new CapturingContinuationOrchestrator();
        GatewayToolApprovalRequest[] fetches =
        [
            CreatePackedFetchApproval("req-a", "call-a", "approved"),
            CreatePackedFetchApproval("req-b", "call-b", "approved"),
            CreatePackedFetchApproval("req-c", "call-c", "approved"),
            CreatePackedFetchApproval("req-d", "call-d", "approved"),
        ];
        var approvalStore = new FakeApprovalStore(fetches);
        RunWaitState waitState = CreatePackedApprovalWaitState("req-a", "req-b", "req-c", "req-d");
        var runWaitStateStore = new FakeRunWaitStateStore(waitState);
        var packedInvoker = new OverlappingPackedToolInvoker();
        using var service = new ApprovalContinuationDispatcherService(
            queue,
            approvalStore,
            continuationOrchestrator,
            runWaitStateStore,
            new FakeInvocationLinkStore(),
            publisher,
            NullLogger<ApprovalContinuationDispatcherService>.Instance,
            packedInvoker);

        await service.StartAsync(CancellationToken.None).ConfigureAwait(true);
        try
        {
            await queue.EnqueueAsync(new ApprovalContinuationWork(
                RequestId: "req-d",
                SessionId: "session-caller-1",
                TargetType: "agent",
                TargetId: "agent-caller-1",
                UserId: "user-1",
                Approved: true,
                Source: HitlResolutionSource.ChannelCallback,
                Reason: "approved",
                DecidedByUserId: "user-1",
                StopRun: false),
                CancellationToken.None).ConfigureAwait(true);

            _ = await publisher.WaitForCompletedAsync().ConfigureAwait(true);
            Assert.Equal(4, packedInvoker.InvokeCount);
            Assert.True(packedInvoker.MaxInFlight >= 4, $"expected 4 overlapping invokes, max in-flight was {packedInvoker.MaxInFlight}");
            Assert.True(RunClockEnvelope.TryReadPackedToolResults(
                runWaitStateStore.GetByWaitId(waitState.WaitId)?.BehaviorJson,
                out IReadOnlyDictionary<string, PackedToolResult> results));
            Assert.Equal(4, results.Count);
        }
        finally
        {
            await service.StopAsync(CancellationToken.None).ConfigureAwait(true);
        }
    }

    [Fact]
    public async Task DispatcherInvokesIndependentPackedFetchesInParallelAfterSequentialSiblingJobs()
    {
        var queue = new TestApprovalContinuationQueue();
        var publisher = new CapturingPublisher();
        var continuationOrchestrator = new CapturingContinuationOrchestrator();
        var approvalStore = new MutableApprovalStore(
            CreatePackedFetchApproval("req-a", "call-a", "approved"),
            CreatePackedFetchApproval("req-b", "call-b", Decision: null),
            CreatePackedFetchApproval("req-c", "call-c", Decision: null),
            CreatePackedFetchApproval("req-d", "call-d", Decision: null));
        RunWaitState waitState = CreatePackedApprovalWaitState("req-a", "req-b", "req-c", "req-d");
        var runWaitStateStore = new FakeRunWaitStateStore(waitState);
        var packedInvoker = new OverlappingPackedToolInvoker();
        using var service = new ApprovalContinuationDispatcherService(
            queue,
            approvalStore,
            continuationOrchestrator,
            runWaitStateStore,
            new FakeInvocationLinkStore(),
            publisher,
            NullLogger<ApprovalContinuationDispatcherService>.Instance,
            packedInvoker);

        await service.StartAsync(CancellationToken.None).ConfigureAwait(true);
        try
        {
            await queue.EnqueueAsync(CreateApprovedContinuationWork("req-a"), CancellationToken.None)
                .ConfigureAwait(true);
            await WaitForQueueCompletedAsync(queue).ConfigureAwait(true);
            Assert.Equal(0, packedInvoker.InvokeCount);
            Assert.Null(continuationOrchestrator.Request);

            approvalStore.SetDecision("req-b", "approved");
            approvalStore.SetDecision("req-c", "approved");
            approvalStore.SetDecision("req-d", "approved");
            queue.ResetCompletedCount();
            await queue.EnqueueAsync(CreateApprovedContinuationWork("req-d"), CancellationToken.None)
                .ConfigureAwait(true);

            _ = await publisher.WaitForCompletedAsync().ConfigureAwait(true);
            Assert.Equal(4, packedInvoker.InvokeCount);
            Assert.True(
                packedInvoker.MaxInFlight >= 4,
                $"expected 4 overlapping invokes after the last sibling, max in-flight was {packedInvoker.MaxInFlight}");
        }
        finally
        {
            await service.StopAsync(CancellationToken.None).ConfigureAwait(true);
        }
    }

    [Fact]
    public async Task DispatcherCapsPackedIndependentInvokesByMaxParallelToolsEvenWhenConcurrentIsOff()
    {
        var queue = new TestApprovalContinuationQueue();
        var publisher = new CapturingPublisher();
        var continuationOrchestrator = new CapturingContinuationOrchestrator();
        GatewayToolApprovalRequest[] fetches =
        [
            CreatePackedFetchApproval("req-a", "call-a", "approved"),
            CreatePackedFetchApproval("req-b", "call-b", "approved"),
            CreatePackedFetchApproval("req-c", "call-c", "approved"),
            CreatePackedFetchApproval("req-d", "call-d", "approved"),
        ];
        var approvalStore = new FakeApprovalStore(fetches);
        RunWaitState waitState = CreatePackedApprovalWaitState("req-a", "req-b", "req-c", "req-d");
        var runWaitStateStore = new FakeRunWaitStateStore(waitState);
        var packedInvoker = new OverlappingPackedToolInvoker();
        using var service = new ApprovalContinuationDispatcherService(
            queue,
            approvalStore,
            continuationOrchestrator,
            runWaitStateStore,
            new FakeInvocationLinkStore(),
            publisher,
            NullLogger<ApprovalContinuationDispatcherService>.Instance,
            packedInvoker,
            new FixedMaxParallelResolver(2));

        await service.StartAsync(CancellationToken.None).ConfigureAwait(true);
        try
        {
            await queue.EnqueueAsync(CreateApprovedContinuationWork("req-d"), CancellationToken.None)
                .ConfigureAwait(true);

            _ = await publisher.WaitForCompletedAsync().ConfigureAwait(true);
            Assert.Equal(4, packedInvoker.InvokeCount);
            Assert.Equal(2, packedInvoker.MaxInFlight);
        }
        finally
        {
            await service.StopAsync(CancellationToken.None).ConfigureAwait(true);
        }
    }

    [Fact]
    public async Task DispatcherResumesFiccWithEveryPackedDecisionOnLastSibling()
    {
        var queue = new TestApprovalContinuationQueue();
        var publisher = new CapturingPublisher();
        var continuationOrchestrator = new CapturingContinuationOrchestrator();
        GatewayToolApprovalRequest bbc = CreatePackedFetchApproval("req-bbc", "call-bbc", Decision: "approved");
        GatewayToolApprovalRequest cnn = CreatePackedFetchApproval("req-cnn", "call-cnn", Decision: "denied");
        var approvalStore = new FakeApprovalStore(bbc, cnn);
        RunWaitState waitState = CreatePackedApprovalWaitState("req-bbc", "req-cnn");
        var runWaitStateStore = new FakeRunWaitStateStore(waitState);
        runWaitStateStore.PutPackedToolResult(
            waitState.WaitId,
            "req-bbc",
            """{"result":"fetched"}""",
            journaledStart: false);
        using var service = new ApprovalContinuationDispatcherService(
            queue,
            approvalStore,
            continuationOrchestrator,
            runWaitStateStore,
            new FakeInvocationLinkStore(),
            publisher,
            NullLogger<ApprovalContinuationDispatcherService>.Instance);

        await service.StartAsync(CancellationToken.None).ConfigureAwait(true);
        try
        {
            await queue.EnqueueAsync(new ApprovalContinuationWork(
                RequestId: "req-cnn",
                SessionId: "session-caller-1",
                TargetType: "agent",
                TargetId: "agent-caller-1",
                UserId: "user-1",
                Approved: false,
                Source: HitlResolutionSource.ChannelCallback,
                Reason: "denied",
                DecidedByUserId: "user-1",
                StopRun: false),
                CancellationToken.None).ConfigureAwait(true);

            _ = await publisher.WaitForCompletedAsync().ConfigureAwait(true);
            ApprovalContinuationStreamRequest request = Assert.IsType<ApprovalContinuationStreamRequest>(continuationOrchestrator.Request);
            Assert.Equal("req-cnn", request.ApprovalResponse.RequestId);
            Assert.False(request.ApprovalResponse.Approved);
            Assert.Contains("denied", request.ApprovalResponse.ResultJson, StringComparison.Ordinal);
            ToolApprovalResponseInfo sibling = Assert.Single(
                request.ApprovalResponse.EnumeratePack(),
                static item => string.Equals(item.RequestId, "req-bbc", StringComparison.Ordinal));
            Assert.True(sibling.Approved);
            Assert.Equal("call-bbc", sibling.ToolCallId);
            Assert.Equal("""{"result":"fetched"}""", sibling.ResultJson);
        }
        finally
        {
            await service.StopAsync(CancellationToken.None).ConfigureAwait(true);
        }
    }

    [Fact]
    public async Task DispatcherDoesNotRepublishHitlApprovalRequestedWhenContinuationRequestsAnotherApproval()
    {
        var queue = new TestApprovalContinuationQueue();
        var publisher = new CapturingPublisher();
        var approvalStore = new FakeApprovalStore(new GatewayToolApprovalRequest(
            Id: "req-1",
            SessionId: "session-1",
            RunId: null,
            CheckpointId: null,
            AgentId: "agent-1",
            UserId: "user-1",
            ToolCallId: "tool-call-1",
            ToolName: "run_shell",
            ToolType: "mcp",
            McpServerId: null,
            ArgumentsJson: "{}",
            RequestedAt: DateTimeOffset.UtcNow.ToString("O"),
            Decision: "approved",
            DecidedBy: "user-1",
            DecidedAt: DateTimeOffset.UtcNow.ToString("O"),
            Reason: null,
            ExpiresAt: DateTimeOffset.UtcNow.AddMinutes(5).ToString("O")));

        var chatGateway = new FakeChatGatewayService(FakeChatGatewayService.NestedApprovalEvents());
        var invocationLinkStore = new FakeInvocationLinkStore();
        var runWaitStateStore = new FakeRunWaitStateStore(CreateApprovalWaitState());
        var sessionStore = new FakeSessionStore();
        using var service = new ApprovalContinuationDispatcherService(
            queue,
            approvalStore,
            CreateOrchestrator(chatGateway, approvalStore, invocationLinkStore, runWaitStateStore, sessionStore),
            runWaitStateStore,
            invocationLinkStore,
            publisher,
            NullLogger<ApprovalContinuationDispatcherService>.Instance);

        await service.StartAsync(CancellationToken.None).ConfigureAwait(true);

        try
        {
            await queue.EnqueueAsync(new ApprovalContinuationWork(
                RequestId: "req-1",
                SessionId: "session-1",
                TargetType: "agent",
                TargetId: "agent-1",
                UserId: "user-1",
                Approved: true,
                Source: HitlResolutionSource.ChannelCallback,
                Reason: null,
                DecidedByUserId: "user-1"),
                CancellationToken.None).ConfigureAwait(true);

            HitlContinuationCompleted completed = await publisher.WaitForCompletedAsync().ConfigureAwait(true);
            Assert.Equal("req-1", completed.RequestId);
            Assert.Equal("session-1", completed.SessionId);
            Assert.True(completed.HasNestedApprovalRequest);
            Assert.Equal(string.Empty, completed.OutputText);
            await publisher.AssertNoRequestedAsync().ConfigureAwait(true);
        }
        finally
        {
            await service.StopAsync(CancellationToken.None).ConfigureAwait(true);
        }
    }

    [Fact]
    public async Task DispatcherRecoversNestedApprovalFromDurableChildPauseWhenOuterStreamOnlyYieldsPause()
    {
        var queue = new TestApprovalContinuationQueue();
        var publisher = new CapturingPublisher();
        var outerApproval = new GatewayToolApprovalRequest(
            Id: "req-outer",
            SessionId: "session-caller-1",
            RunId: "run-caller-1",
            CheckpointId: null,
            AgentId: "agent-caller-1",
            UserId: "user-1",
            ToolCallId: "tool-call-caller-1",
            ToolName: "invoke_agent_roller",
            ToolType: "agent",
            McpServerId: null,
            ArgumentsJson: "{}",
            RequestedAt: DateTimeOffset.UtcNow.ToString("O"),
            Decision: "approved",
            DecidedBy: "user-1",
            DecidedAt: DateTimeOffset.UtcNow.ToString("O"),
            Reason: null,
            ExpiresAt: null);
        var innerApproval = new GatewayToolApprovalRequest(
            Id: "req-inner",
            SessionId: "session-inner-1",
            RunId: "run-inner-1",
            CheckpointId: null,
            AgentId: "agent-inner-1",
            UserId: "user-1",
            ToolCallId: "tool-call-inner-1",
            ToolName: "run_skill_script",
            ToolType: "skill",
            McpServerId: null,
            ArgumentsJson: "{}",
            RequestedAt: DateTimeOffset.UtcNow.ToString("O"),
            Decision: null,
            DecidedBy: null,
            DecidedAt: null,
            Reason: null,
            ExpiresAt: null,
            OuterSessionId: "session-caller-1",
            OuterAgentId: "agent-caller-1");
        RunWaitState outerApprovalWait = CreateApprovalWaitState() with
        {
            WaitId = "wait-outer",
            CorrelationId = outerApproval.Id,
            ApprovalRequestId = outerApproval.Id,
        };
        RunWaitState durableChildPause = CreateParentWaitState() with
        {
            WaitId = "wait-child",
            ApprovalRequestId = innerApproval.Id,
            ChildSessionId = innerApproval.SessionId,
            ChildRunId = innerApproval.RunId,
            CanonicalEventId = "event-inner-approval",
        };
        var approvalStore = new FakeApprovalStore(outerApproval, innerApproval);
        var runWaitStateStore = new FakeRunWaitStateStore(outerApprovalWait, durableChildPause);
        var invocationLinkStore = new FakeInvocationLinkStore(new AgentToolInvocationLink(
            ApprovalRequestId: outerApproval.Id,
            CallerSessionId: outerApproval.SessionId!,
            CreatedAt: DateTimeOffset.UtcNow.ToString("O"),
            UpdatedAt: DateTimeOffset.UtcNow.ToString("O"),
            CanonicalEventId: "event-outer-approval",
            CallerToolCallId: outerApproval.ToolCallId,
            CallerToolName: outerApproval.ToolName,
            CallerContinuationRunId: "continuation-caller-1",
            CallerRunId: outerApproval.RunId,
            ChildSessionId: innerApproval.SessionId,
            ChildRunId: innerApproval.RunId,
            ChildAgentId: innerApproval.AgentId,
            RootRunId: outerApproval.RunId,
            InvocationId: "invocation-outer-1"));

        using var service = new ApprovalContinuationDispatcherService(
            queue,
            approvalStore,
            new PauseOnlyContinuationOrchestrator(),
            runWaitStateStore,
            invocationLinkStore,
            publisher,
            NullLogger<ApprovalContinuationDispatcherService>.Instance);

        await service.StartAsync(CancellationToken.None).ConfigureAwait(true);
        try
        {
            await queue.EnqueueAsync(new ApprovalContinuationWork(
                outerApproval.Id,
                outerApproval.SessionId!,
                "agent",
                outerApproval.AgentId!,
                outerApproval.UserId,
                Approved: true,
                Source: HitlResolutionSource.ApprovalApi,
                Reason: null,
                DecidedByUserId: outerApproval.UserId), CancellationToken.None).ConfigureAwait(true);

            HitlContinuationCompleted completed = await publisher.WaitForCompletedAsync().ConfigureAwait(true);
            Assert.Equal(outerApproval.Id, completed.RequestId);
            Assert.True(completed.HasNestedApprovalRequest);
            Assert.Equal(string.Empty, completed.OutputText);
        }
        finally
        {
            await service.StopAsync(CancellationToken.None).ConfigureAwait(true);
        }
    }

    [Fact]
    public async Task DispatcherRecoversNestedApprovalFromDurableChildPauseWhenStreamHasNoParentPause()
    {
        var queue = new TestApprovalContinuationQueue();
        var publisher = new CapturingPublisher();
        var outerApproval = new GatewayToolApprovalRequest(
            Id: "req-outer",
            SessionId: "session-caller-1",
            RunId: "run-caller-1",
            CheckpointId: null,
            AgentId: "agent-caller-1",
            UserId: "user-1",
            ToolCallId: "tool-call-caller-1",
            ToolName: "invoke_agent_roller",
            ToolType: "agent",
            McpServerId: null,
            ArgumentsJson: "{}",
            RequestedAt: DateTimeOffset.UtcNow.ToString("O"),
            Decision: "approved",
            DecidedBy: "user-1",
            DecidedAt: DateTimeOffset.UtcNow.ToString("O"),
            Reason: null,
            ExpiresAt: null);
        var innerApproval = new GatewayToolApprovalRequest(
            Id: "req-inner",
            SessionId: "session-inner-1",
            RunId: "run-inner-1",
            CheckpointId: null,
            AgentId: "agent-inner-1",
            UserId: "user-1",
            ToolCallId: "tool-call-inner-1",
            ToolName: "run_skill_script",
            ToolType: "skill",
            McpServerId: null,
            ArgumentsJson: "{}",
            RequestedAt: DateTimeOffset.UtcNow.ToString("O"),
            Decision: null,
            DecidedBy: null,
            DecidedAt: null,
            Reason: null,
            ExpiresAt: null,
            OuterSessionId: "session-caller-1",
            OuterAgentId: "agent-caller-1");
        RunWaitState outerApprovalWait = CreateApprovalWaitState() with
        {
            WaitId = "wait-outer",
            CorrelationId = outerApproval.Id,
            ApprovalRequestId = outerApproval.Id,
        };
        RunWaitState durableChildPause = CreateParentWaitState() with
        {
            WaitId = "wait-child",
            ApprovalRequestId = innerApproval.Id,
            ChildSessionId = innerApproval.SessionId,
            ChildRunId = innerApproval.RunId,
            CanonicalEventId = "event-inner-approval",
        };
        var approvalStore = new FakeApprovalStore(outerApproval, innerApproval);
        var runWaitStateStore = new FakeRunWaitStateStore(outerApprovalWait, durableChildPause);
        var outerInvocationLink = new AgentToolInvocationLink(
            ApprovalRequestId: outerApproval.Id,
            CallerSessionId: outerApproval.SessionId!,
            CreatedAt: DateTimeOffset.UtcNow.ToString("O"),
            UpdatedAt: DateTimeOffset.UtcNow.ToString("O"),
            CanonicalEventId: "event-outer-approval",
            CallerToolCallId: outerApproval.ToolCallId,
            CallerToolName: outerApproval.ToolName,
            CallerContinuationRunId: "continuation-caller-1",
            CallerRunId: outerApproval.RunId,
            ChildSessionId: innerApproval.SessionId,
            ChildRunId: innerApproval.RunId,
            ChildAgentId: innerApproval.AgentId,
            RootRunId: outerApproval.RunId,
            InvocationId: "invocation-outer-1");

        using var service = new ApprovalContinuationDispatcherService(
            queue,
            approvalStore,
            new EmptyContinuationOrchestrator(),
            runWaitStateStore,
            new FakeInvocationLinkStore(outerInvocationLink),
            publisher,
            NullLogger<ApprovalContinuationDispatcherService>.Instance);

        await service.StartAsync(CancellationToken.None).ConfigureAwait(true);
        try
        {
            await queue.EnqueueAsync(new ApprovalContinuationWork(
                outerApproval.Id,
                outerApproval.SessionId!,
                "agent",
                outerApproval.AgentId!,
                outerApproval.UserId,
                Approved: true,
                Source: HitlResolutionSource.ApprovalApi,
                Reason: null,
                DecidedByUserId: outerApproval.UserId), CancellationToken.None).ConfigureAwait(true);

            HitlContinuationCompleted completed = await publisher.WaitForCompletedAsync().ConfigureAwait(true);
            Assert.Equal(outerApproval.Id, completed.RequestId);
            Assert.True(completed.HasNestedApprovalRequest);
            Assert.Equal(string.Empty, completed.OutputText);
        }
        finally
        {
            await service.StopAsync(CancellationToken.None).ConfigureAwait(true);
        }
    }

    [Fact]
    public async Task AaatContinuationPersistsLineageForASecondSequentialApproval()
    {
        var initialLink = new AgentToolInvocationLink(
            ApprovalRequestId: "req-1",
            CallerSessionId: "session-caller-1",
            CreatedAt: DateTimeOffset.UtcNow.ToString("O"),
            UpdatedAt: DateTimeOffset.UtcNow.ToString("O"),
            CanonicalEventId: "event-approval-1",
            CallerToolCallId: "tool-call-caller-1",
            CallerToolName: "invoke_agent_roller",
            CallerContinuationRunId: "continuation-caller-1",
            ChildSessionId: "session-inner-1",
            CallerRunId: "run-caller-1",
            ChildRunId: "run-inner-1",
            ChildAgentId: "agent-inner-1",
            RootRunId: "run-caller-1",
            InvocationId: "invocation-1");
        var invocationLinkStore = new FakeInvocationLinkStore(initialLink);
        RunWaitState childApprovalWait = CreateApprovalWaitState() with
        {
            WaitId = "wait-inner-1",
            ContinuationRunId = "continuation-inner-1",
            RunId = "run-inner-1",
            SessionId = "session-inner-1",
            TargetId = "agent-inner-1",
            AgentId = "agent-inner-1",
        };
        var orchestrator = CreateOrchestrator(
            new FakeChatGatewayService(FakeChatGatewayService.NestedApprovalEvents()),
            new FakeApprovalStore(),
            invocationLinkStore,
            new FakeRunWaitStateStore(CreateParentWaitState(), childApprovalWait),
            new FakeSessionStore());

        List<NaradPersistedEvent> events = [];
        await foreach (NaradPersistedEvent evt in orchestrator.StreamAsync(
            new ApprovalContinuationStreamRequest(
                "agent",
                "agent-inner-1",
                "session-inner-1",
                new ToolApprovalResponseInfo(
                    "req-1",
                    "tool-call-inner-1",
                    "run_shell",
                    "{}",
                    Approved: true,
                    Reason: null,
                    ContinuationRunId: "continuation-inner-1"),
                "user-1"),
            CancellationToken.None).ConfigureAwait(true))
        {
            events.Add(evt);
        }

        NaradPersistedEvent nestedEvent = Assert.Single(events);
        Assert.Equal(NaradEventTypes.InterruptRequested, nestedEvent.EventType);
        AgentToolInvocationLink secondLink = Assert.IsType<AgentToolInvocationLink>(invocationLinkStore.GetByApprovalRequestId("req-2"));
        Assert.Equal(initialLink.CallerSessionId, secondLink.CallerSessionId);
        Assert.Equal(initialLink.CallerRunId, secondLink.CallerRunId);
        Assert.Equal(initialLink.CallerToolCallId, secondLink.CallerToolCallId);
        Assert.Equal(initialLink.RootRunId, secondLink.RootRunId);
        Assert.Equal("run-inner-2", secondLink.ChildRunId);
        Assert.Equal(nestedEvent.EventId, secondLink.CanonicalEventId);
    }

    [Fact]
    public async Task DispatcherPublishesContinuationCompletedWithoutMirroringIntoCallerSession()
    {
        var queue = new TestApprovalContinuationQueue();
        var publisher = new CapturingPublisher();
        var approvalStore = new FakeApprovalStore(new GatewayToolApprovalRequest(
            Id: "req-1",
            SessionId: "session-agent-tool-1",
            RunId: null,
            CheckpointId: "cp-audit-agent-tool-1",
            AgentId: "agent-agent-tool-1",
            UserId: "user-1",
            ToolCallId: "tool-call-1",
            ToolName: "run_shell",
            ToolType: "mcp",
            McpServerId: null,
            ArgumentsJson: "{}",
            RequestedAt: DateTimeOffset.UtcNow.ToString("O"),
            Decision: "approved",
            DecidedBy: "user-1",
            DecidedAt: DateTimeOffset.UtcNow.ToString("O"),
            Reason: null,
            ExpiresAt: DateTimeOffset.UtcNow.AddMinutes(5).ToString("O")));

        var chatGateway = new FakeChatGatewayService(FakeChatGatewayService.ContinuationOutputEvents());
        var invocationLinkStore = new FakeInvocationLinkStore();
        var runWaitStateStore = new FakeRunWaitStateStore(CreateApprovalWaitState());
        var sessionStore = new FakeSessionStore();
        using var service = new ApprovalContinuationDispatcherService(
            queue,
            approvalStore,
            CreateOrchestrator(chatGateway, approvalStore, invocationLinkStore, runWaitStateStore, sessionStore),
            runWaitStateStore,
            invocationLinkStore,
            publisher,
            NullLogger<ApprovalContinuationDispatcherService>.Instance);

        await service.StartAsync(CancellationToken.None).ConfigureAwait(true);

        try
        {
            await queue.EnqueueAsync(new ApprovalContinuationWork(
                RequestId: "req-1",
                SessionId: "session-agent-tool-1",
                TargetType: "agent",
                TargetId: "agent-agent-tool-1",
                UserId: "user-1",
                Approved: true,
                Source: HitlResolutionSource.ChannelCallback,
                Reason: "telegram",
                DecidedByUserId: "user-1"),
                CancellationToken.None).ConfigureAwait(true);

            HitlContinuationCompleted completed = await publisher.WaitForCompletedAsync().ConfigureAwait(true);
            Assert.Equal("req-1", completed.RequestId);
            Assert.Equal("session-agent-tool-1", completed.SessionId);
            Assert.Equal("Continuation output from agent-tool run.", completed.OutputText);
            Assert.False(completed.HasNestedApprovalRequest);
            Assert.NotNull(chatGateway.LastApprovalResponse);
            Assert.Null(chatGateway.LastApprovalResponse!.CheckpointId);
        }
        finally
        {
            await service.StopAsync(CancellationToken.None).ConfigureAwait(true);
        }
    }

    [Fact]
    public async Task DispatcherCompletesLeaseWhenChildRunFailedIsTerminal()
    {
        var queue = new TestApprovalContinuationQueue();
        var publisher = new CapturingPublisher();
        var approvalStore = new FakeApprovalStore(new GatewayToolApprovalRequest(
            Id: "req-1",
            SessionId: "session-agent-tool-1",
            RunId: null,
            CheckpointId: "cp-audit-agent-tool-1",
            AgentId: "agent-agent-tool-1",
            UserId: "user-1",
            ToolCallId: "tool-call-1",
            ToolName: "run_shell",
            ToolType: "mcp",
            McpServerId: null,
            ArgumentsJson: "{}",
            RequestedAt: DateTimeOffset.UtcNow.ToString("O"),
            Decision: "approved",
            DecidedBy: "user-1",
            DecidedAt: DateTimeOffset.UtcNow.ToString("O"),
            Reason: null,
            ExpiresAt: DateTimeOffset.UtcNow.AddMinutes(5).ToString("O")));

        var chatGateway = new FakeChatGatewayService(FakeChatGatewayService.ContinuationRunFailedEvents());
        using var service = new ApprovalContinuationDispatcherService(
            queue,
            approvalStore,
            CreateOrchestrator(chatGateway, approvalStore, new FakeInvocationLinkStore(), new FakeRunWaitStateStore(CreateApprovalWaitState()), new FakeSessionStore()),
            new FakeRunWaitStateStore(CreateApprovalWaitState()),
            new FakeInvocationLinkStore(),
            publisher,
            NullLogger<ApprovalContinuationDispatcherService>.Instance);

        await service.StartAsync(CancellationToken.None).ConfigureAwait(true);

        try
        {
            await queue.EnqueueAsync(new ApprovalContinuationWork(
                RequestId: "req-1",
                SessionId: "session-agent-tool-1",
                TargetType: "agent",
                TargetId: "agent-agent-tool-1",
                UserId: "user-1",
                Approved: true,
                Source: HitlResolutionSource.ChannelCallback,
                Reason: "telegram",
                DecidedByUserId: "user-1"),
                CancellationToken.None).ConfigureAwait(true);

            HitlContinuationFailed failed = await publisher.WaitForFailedAsync().ConfigureAwait(true);
            Assert.Equal("req-1", failed.RequestId);
            Assert.Contains("failed", failed.Error, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(1, queue.CompletedCount);
            Assert.Equal(0, queue.FailedCount);
        }
        finally
        {
            await service.StopAsync(CancellationToken.None).ConfigureAwait(true);
        }
    }

    [Fact]
    public async Task DispatcherResumesCallerRunAfterAgentToolApprovalContinuationCompletes()
    {
        var queue = new TestApprovalContinuationQueue();
        var publisher = new CapturingPublisher();
        var approvalStore = new FakeApprovalStore(new GatewayToolApprovalRequest(
            Id: "req-1",
            SessionId: "session-inner-1",
            RunId: "run-inner-1",
            CheckpointId: null,
            AgentId: "agent-inner-1",
            UserId: "user-1",
            ToolCallId: "tool-call-inner-1",
            ToolName: "run_skill_script",
            ToolType: "mcp",
            McpServerId: null,
            ArgumentsJson: "{}",
            RequestedAt: DateTimeOffset.UtcNow.ToString("O"),
            Decision: "approved",
            DecidedBy: "user-1",
            DecidedAt: DateTimeOffset.UtcNow.ToString("O"),
            Reason: null,
            ExpiresAt: DateTimeOffset.UtcNow.AddMinutes(5).ToString("O"),
            OuterSessionId: "session-caller-1",
            OuterAgentId: "agent-caller-1"));

        var chatGateway = new FakeChatGatewayService(
            FakeChatGatewayService.InnerContinuationOnlyEvents(),
            FakeChatGatewayService.CallerOnlyResumeEvents());
        var invocationLinkStore = new FakeInvocationLinkStore(new AgentToolInvocationLink(
            ApprovalRequestId: "req-1",
            CallerSessionId: "session-caller-1",
            CreatedAt: DateTimeOffset.UtcNow.ToString("O"),
             UpdatedAt: DateTimeOffset.UtcNow.ToString("O"),
             CallerToolCallId: "tool-call-caller-1",
             CallerToolName: "invoke_agent_roller",
             CanonicalEventId: "event-approval-1",
             CallerContinuationRunId: "continuation-caller-1",
             CallerRunId: "run-caller-1",
             ChildSessionId: "session-inner-1",
             ChildRunId: "run-inner-1",
             ChildAgentId: "agent-inner-1",
             RootRunId: "run-caller-1",
             InvocationId: "invocation-1"));
        var sessionStore = new FakeSessionStore();
        var runWaitStateStore = new FakeRunWaitStateStore(CreateParentWaitState());

        using var service = new ApprovalContinuationDispatcherService(
            queue,
            approvalStore,
            CreateOrchestrator(chatGateway, approvalStore, invocationLinkStore, runWaitStateStore, sessionStore),
            runWaitStateStore,
            invocationLinkStore,
            publisher,
            NullLogger<ApprovalContinuationDispatcherService>.Instance);

        await service.StartAsync(CancellationToken.None).ConfigureAwait(true);

        try
        {
            await queue.EnqueueAsync(new ApprovalContinuationWork(
                RequestId: "req-1",
                SessionId: "session-inner-1",
                TargetType: "agent",
                TargetId: "agent-inner-1",
                UserId: "user-1",
                Approved: true,
                Source: HitlResolutionSource.ChannelCallback,
                Reason: "telegram",
                DecidedByUserId: "user-1"),
                CancellationToken.None).ConfigureAwait(true);

            HitlContinuationCompleted completed = await publisher.WaitForCompletedAsync().ConfigureAwait(true);
            Assert.Equal("req-1", completed.RequestId);
            Assert.Equal("session-caller-1", completed.SessionId);
            Assert.Equal("Final caller response.", completed.OutputText);
            Assert.False(completed.HasNestedApprovalRequest);
            Assert.Equal(2, chatGateway.StreamChatWithApprovalCallCount);
            Assert.Equal("session-caller-1", chatGateway.InvocationContextCallerSessionIds[0]);
            AgentToolInvocationContextState childContext = Assert.IsType<AgentToolInvocationContextState>(chatGateway.InvocationContexts[0]);
            Assert.Equal("agent-caller-1", childContext.CallerAgentId);
            Assert.Equal("run-caller-1", childContext.CallerRunId);
            Assert.Equal("tool-call-caller-1", childContext.CallerToolCallId);
            Assert.Equal("run-caller-1", childContext.RootRunId);
            Assert.Null(chatGateway.InvocationContexts[1]);
            Assert.Equal("run-caller-1", chatGateway.ApprovalResponses[0].RootRunId);
            Assert.Equal("continuation-caller-1", chatGateway.ApprovalResponses[1].ContinuationRunId);
            Assert.Equal("tool-call-caller-1", chatGateway.ApprovalResponses[1].ToolCallId);
            Assert.Equal("{\"result\":\"Inner continuation output.\"}", chatGateway.ApprovalResponses[1].ResultJson);
            Assert.Equal("""{"message":"roll a d222"}""", chatGateway.ApprovalResponses[1].ArgumentsJson);
        }
        finally
        {
            await service.StopAsync(CancellationToken.None).ConfigureAwait(true);
        }
    }

    [Fact]
    public async Task DispatcherResumesCallerRunWhenParentWaitMatchesCallerToolIdentity()
    {
        var queue = new TestApprovalContinuationQueue();
        var publisher = new CapturingPublisher();
        var approvalStore = new FakeApprovalStore(new GatewayToolApprovalRequest(
            Id: "req-inner-1",
            SessionId: "session-inner-1",
            RunId: "run-inner-1",
            CheckpointId: null,
            AgentId: "agent-inner-1",
            UserId: "user-1",
            ToolCallId: "tool-call-inner-1",
            ToolName: "run_skill_script",
            ToolType: "skill",
            McpServerId: null,
            ArgumentsJson: "{}",
            RequestedAt: DateTimeOffset.UtcNow.ToString("O"),
            Decision: "approved",
            DecidedBy: "user-1",
            DecidedAt: DateTimeOffset.UtcNow.ToString("O"),
            Reason: null,
            ExpiresAt: DateTimeOffset.UtcNow.AddMinutes(5).ToString("O"),
            OuterSessionId: "session-caller-1",
            OuterAgentId: "agent-caller-1"));

        var chatGateway = new FakeChatGatewayService(
            FakeChatGatewayService.InnerContinuationOnlyEvents(),
            FakeChatGatewayService.CallerOnlyResumeEvents());
        var invocationLinkStore = new FakeInvocationLinkStore(new AgentToolInvocationLink(
            ApprovalRequestId: "req-inner-1",
            CallerSessionId: "session-caller-1",
            CreatedAt: DateTimeOffset.UtcNow.ToString("O"),
            UpdatedAt: DateTimeOffset.UtcNow.ToString("O"),
            CallerToolCallId: "tool-call-caller-1",
            CallerToolName: "invoke_agent_roller",
            CanonicalEventId: "event-approval-1",
            CallerContinuationRunId: "stale-continuation-id",
            CallerRunId: "run-caller-1",
            ChildSessionId: "session-inner-1",
            ChildRunId: "run-inner-1",
            ChildAgentId: "agent-inner-1",
            RootRunId: "run-caller-1",
            InvocationId: "invocation-1"));
        RunWaitState childApprovalWait = CreateApprovalWaitState() with
        {
            WaitId = "wait-inner-1",
            ContinuationRunId = "continuation-inner-1",
            RunId = "run-inner-1",
            SessionId = "session-inner-1",
            TargetId = "agent-inner-1",
            AgentId = "agent-inner-1",
            CorrelationId = "req-inner-1",
        };
        var sessionStore = new FakeSessionStore();
        var runWaitStateStore = new FakeRunWaitStateStore(CreateParentWaitState(), childApprovalWait);

        using var service = new ApprovalContinuationDispatcherService(
            queue,
            approvalStore,
            CreateOrchestrator(chatGateway, approvalStore, invocationLinkStore, runWaitStateStore, sessionStore),
            runWaitStateStore,
            invocationLinkStore,
            publisher,
            NullLogger<ApprovalContinuationDispatcherService>.Instance);

        await service.StartAsync(CancellationToken.None).ConfigureAwait(true);

        try
        {
            await queue.EnqueueAsync(new ApprovalContinuationWork(
                RequestId: "req-inner-1",
                SessionId: "session-inner-1",
                TargetType: "agent",
                TargetId: "agent-inner-1",
                UserId: "user-1",
                Approved: true,
                Source: HitlResolutionSource.Browser,
                Reason: null,
                DecidedByUserId: "user-1"),
                CancellationToken.None).ConfigureAwait(true);

            HitlContinuationCompleted completed = await publisher.WaitForCompletedAsync().ConfigureAwait(true);
            Assert.Equal("req-inner-1", completed.RequestId);
            Assert.Equal("session-caller-1", completed.SessionId);
            Assert.Equal("Final caller response.", completed.OutputText);
            Assert.False(completed.HasNestedApprovalRequest);
            Assert.Equal(2, chatGateway.StreamChatWithApprovalCallCount);
        }
        finally
        {
            await service.StopAsync(CancellationToken.None).ConfigureAwait(true);
        }
    }

    [Fact]
    public async Task DispatcherResumesCallerRunWhenInnerContinuationReturnsToolResultOnly()
    {
        var queue = new TestApprovalContinuationQueue();
        var publisher = new CapturingPublisher();
        var approvalStore = new FakeApprovalStore(new GatewayToolApprovalRequest(
            Id: "req-1",
            SessionId: "session-inner-1",
            RunId: "run-inner-1",
            CheckpointId: null,
            AgentId: "agent-inner-1",
            UserId: "user-1",
            ToolCallId: "tool-call-inner-1",
            ToolName: "run_skill_script",
            ToolType: "mcp",
            McpServerId: null,
            ArgumentsJson: "{}",
            RequestedAt: DateTimeOffset.UtcNow.ToString("O"),
            Decision: "approved",
            DecidedBy: "user-1",
            DecidedAt: DateTimeOffset.UtcNow.ToString("O"),
            Reason: null,
            ExpiresAt: DateTimeOffset.UtcNow.AddMinutes(5).ToString("O")));

        var chatGateway = new FakeChatGatewayService(
            FakeChatGatewayService.InnerContinuationToolResultOnlyEvents(),
            FakeChatGatewayService.CallerOnlyResumeEvents());
        var invocationLinkStore = new FakeInvocationLinkStore(new AgentToolInvocationLink(
            ApprovalRequestId: "req-1",
            CallerSessionId: "session-caller-1",
            CreatedAt: DateTimeOffset.UtcNow.ToString("O"),
             UpdatedAt: DateTimeOffset.UtcNow.ToString("O"),
             CallerToolCallId: "tool-call-caller-1",
             CallerToolName: "invoke_agent_roller",
             CanonicalEventId: "event-approval-1",
             CallerContinuationRunId: "continuation-caller-1",
             CallerRunId: "run-caller-1",
             ChildSessionId: "session-inner-1",
             ChildRunId: "run-inner-1",
             ChildAgentId: "agent-inner-1",
             RootRunId: "run-caller-1",
             InvocationId: "invocation-1"));
        var sessionStore = new FakeSessionStore();
        var runWaitStateStore = new FakeRunWaitStateStore(CreateParentWaitState());

        using var service = new ApprovalContinuationDispatcherService(
            queue,
            approvalStore,
            CreateOrchestrator(chatGateway, approvalStore, invocationLinkStore, runWaitStateStore, sessionStore),
            runWaitStateStore,
            invocationLinkStore,
            publisher,
            NullLogger<ApprovalContinuationDispatcherService>.Instance);

        await service.StartAsync(CancellationToken.None).ConfigureAwait(true);

        try
        {
            await queue.EnqueueAsync(new ApprovalContinuationWork(
                RequestId: "req-1",
                SessionId: "session-inner-1",
                TargetType: "agent",
                TargetId: "agent-inner-1",
                UserId: "user-1",
                Approved: true,
                Source: HitlResolutionSource.ChannelCallback,
                Reason: "telegram",
                DecidedByUserId: "user-1"),
                CancellationToken.None).ConfigureAwait(true);

            HitlContinuationCompleted completed = await publisher.WaitForCompletedAsync().ConfigureAwait(true);
            Assert.Equal("req-1", completed.RequestId);
            Assert.Equal("session-caller-1", completed.SessionId);
            Assert.Equal("Final caller response.", completed.OutputText);
            Assert.False(completed.HasNestedApprovalRequest);
            Assert.Equal(2, chatGateway.StreamChatWithApprovalCallCount);
            Assert.Equal("{\"result\":\"73\"}", chatGateway.ApprovalResponses[1].ResultJson);
        }
        finally
        {
            await service.StopAsync(CancellationToken.None).ConfigureAwait(true);
        }
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData(null, true)]
    public async Task InnerChildHitlOutcomeFeedsParentToolResultAndContinues(
        string? reason,
        bool stopRun)
    {
        string childLabel = stopRun ? "stopped" : "denied";
        var chatGateway = new FakeChatGatewayService(
            FakeChatGatewayService.InnerNegativeDecisionEvents(childLabel),
            FakeChatGatewayService.CallerOnlyResumeEvents());
        var invocationLinkStore = new FakeInvocationLinkStore(new AgentToolInvocationLink(
            ApprovalRequestId: "req-1",
            CallerSessionId: "session-caller-1",
            CreatedAt: DateTimeOffset.UtcNow.ToString("O"),
            UpdatedAt: DateTimeOffset.UtcNow.ToString("O"),
            CallerToolCallId: "tool-call-caller-1",
            CallerToolName: "invoke_agent_roller",
            CanonicalEventId: "event-approval-1",
            CallerContinuationRunId: "continuation-caller-1",
            CallerRunId: "run-caller-1",
            ChildSessionId: "session-inner-1",
            ChildRunId: "run-inner-1",
            ChildAgentId: "agent-inner-1",
            RootRunId: "run-caller-1",
            InvocationId: "invocation-1"));
        var invocationStore = new InMemoryAgentToolInvocationStore(invocationLinkStore);
        RunWaitState childApprovalWait = CreateApprovalWaitState() with
        {
            WaitId = "wait-inner-1",
            ContinuationRunId = "continuation-inner-1",
            RunId = "run-inner-1",
            SessionId = "session-inner-1",
            TargetId = "agent-inner-1",
            AgentId = "agent-inner-1",
        };
        var orchestrator = CreateOrchestrator(
            chatGateway,
            new FakeApprovalStore(),
            invocationLinkStore,
            new FakeRunWaitStateStore(CreateParentWaitState(), childApprovalWait),
            new FakeSessionStore(),
            invocationStore);

        var continuationRequest = new ApprovalContinuationStreamRequest(
            "agent",
            "agent-inner-1",
            "session-inner-1",
            new ToolApprovalResponseInfo(
                "req-1",
                "tool-call-inner-1",
                "run_skill_script",
                "{}",
                Approved: false,
                Reason: reason,
                StopRun: stopRun,
                ContinuationRunId: "continuation-inner-1"),
            "user-1");
        List<NaradPersistedEvent> events = [];
        await foreach (NaradPersistedEvent evt in orchestrator.StreamAsync(
            continuationRequest,
            CancellationToken.None).ConfigureAwait(true))
        {
            events.Add(evt);
        }

        Assert.NotEmpty(events);
        Assert.Equal(2, chatGateway.StreamChatWithApprovalCallCount);
        Assert.False(chatGateway.ApprovalResponses[0].Approved);

        ToolApprovalResponseInfo callerResponse = chatGateway.ApprovalResponses[1];
        Assert.True(callerResponse.Approved);
        Assert.Null(callerResponse.TerminalOutcomeScope);
        Assert.False(callerResponse.StopRun);
        Assert.Equal("tool-call-caller-1", callerResponse.ToolCallId);
        Assert.Equal("continuation-caller-1", callerResponse.ContinuationRunId);
        Assert.NotNull(callerResponse.ResultJson);
        using System.Text.Json.JsonDocument result = System.Text.Json.JsonDocument.Parse(callerResponse.ResultJson);
        Assert.Equal($"Child approval was {childLabel}.", result.RootElement.GetProperty("result").GetString());
        if (stopRun)
        {
            Assert.Equal("stopped", result.RootElement.GetProperty("outcome").GetString());
            Assert.Equal(AgentToolInvocationStates.Failed, invocationStore.Get("invocation-1")!.State);
        }
        else
        {
            Assert.False(result.RootElement.TryGetProperty("outcome", out _));
            Assert.Equal(AgentToolInvocationStates.Completed, invocationStore.Get("invocation-1")!.State);
        }
    }

    [Fact]
    public async Task DurableChildStopResumesParentWithToolResultAfterChildWaitConsumed()
    {
        var chatGateway = new FakeChatGatewayService(FakeChatGatewayService.CallerOnlyResumeEvents());
        var invocationLinkStore = new FakeInvocationLinkStore(new AgentToolInvocationLink(
            ApprovalRequestId: "req-1",
            CallerSessionId: "session-caller-1",
            CreatedAt: DateTimeOffset.UtcNow.ToString("O"),
            UpdatedAt: DateTimeOffset.UtcNow.ToString("O"),
            CallerToolCallId: "tool-call-caller-1",
            CallerToolName: "invoke_agent_roller",
            CanonicalEventId: "event-approval-1",
            CallerContinuationRunId: "continuation-caller-1",
            CallerRunId: "run-caller-1",
            ChildSessionId: "session-inner-1",
            ChildRunId: "run-inner-1",
            ChildAgentId: "agent-inner-1",
            RootRunId: "run-caller-1",
            InvocationId: "invocation-1"));
        var invocationStore = new InMemoryAgentToolInvocationStore(invocationLinkStore);
        invocationStore.RecordChildCompletion(
            "invocation-1",
            "{\"result\":\"Recovered child result.\",\"outcome\":\"stopped\"}",
            "stopped");
        invocationStore.SimulateExpiredParentCompletionLease();
        var orchestrator = CreateOrchestrator(
            chatGateway,
            new FakeApprovalStore(),
            invocationLinkStore,
            new FakeRunWaitStateStore(CreateParentWaitState()),
            new FakeSessionStore(),
            invocationStore);

        List<NaradPersistedEvent> events = [];
        await foreach (NaradPersistedEvent evt in orchestrator.StreamAsync(
            new ApprovalContinuationStreamRequest(
                "agent",
                "agent-inner-1",
                "session-inner-1",
                new ToolApprovalResponseInfo(
                    "req-1",
                    "tool-call-inner-1",
                    "run_skill_script",
                    "{}",
                    Approved: false,
                    Reason: "stopped",
                    StopRun: true,
                    ContinuationRunId: "continuation-inner-consumed"),
                "user-1"),
            CancellationToken.None).ConfigureAwait(true))
        {
            events.Add(evt);
        }

        Assert.Equal(1, chatGateway.StreamChatWithApprovalCallCount);
        ToolApprovalResponseInfo callerResponse = Assert.Single(chatGateway.ApprovalResponses);
        Assert.Equal("continuation-caller-1", callerResponse.ContinuationRunId);
        Assert.True(callerResponse.Approved);
        Assert.Null(callerResponse.TerminalOutcomeScope);
        Assert.False(callerResponse.StopRun);
        Assert.Contains("stopped", callerResponse.ResultJson, StringComparison.Ordinal);
        Assert.DoesNotContain(events, evt => string.Equals(evt.EventId, "aaat:invocation-1:tool-terminal", StringComparison.Ordinal));
        Assert.Equal(AgentToolInvocationStates.Failed, invocationStore.Get("invocation-1")!.State);
    }

    [Fact]
    public async Task PositiveDecisionRecoveryUsesDurableChildResultAfterChildWaitWasConsumed()
    {
        var chatGateway = new FakeChatGatewayService(FakeChatGatewayService.CallerOnlyResumeEvents());
        var invocationLinkStore = new FakeInvocationLinkStore(new AgentToolInvocationLink(
            ApprovalRequestId: "req-1",
            CallerSessionId: "session-caller-1",
            CreatedAt: DateTimeOffset.UtcNow.ToString("O"),
            UpdatedAt: DateTimeOffset.UtcNow.ToString("O"),
            CallerToolCallId: "tool-call-caller-1",
            CallerToolName: "invoke_agent_roller",
            CanonicalEventId: "event-approval-1",
            CallerContinuationRunId: "continuation-caller-1",
            CallerRunId: "run-caller-1",
            ChildSessionId: "session-inner-1",
            ChildRunId: "run-inner-1",
            ChildAgentId: "agent-inner-1",
            RootRunId: "run-caller-1",
            InvocationId: "invocation-1"));
        var invocationStore = new InMemoryAgentToolInvocationStore(invocationLinkStore);
        invocationStore.RecordChildCompletion("invocation-1", "{\"result\":\"73\"}", "completed");
        invocationStore.SimulateExpiredParentCompletionLease();
        var orchestrator = CreateOrchestrator(
            chatGateway,
            new FakeApprovalStore(),
            invocationLinkStore,
            new FakeRunWaitStateStore(CreateParentWaitState()),
            new FakeSessionStore(),
            invocationStore);

        await foreach (NaradPersistedEvent _ in orchestrator.StreamAsync(
            new ApprovalContinuationStreamRequest(
                "agent",
                "agent-inner-1",
                "session-inner-1",
                new ToolApprovalResponseInfo(
                    "req-1",
                    "tool-call-inner-1",
                    "run_skill_script",
                    "{}",
                    Approved: true,
                    Reason: null,
                    ContinuationRunId: "continuation-inner-consumed"),
                "user-1"),
            CancellationToken.None).ConfigureAwait(true))
        {
        }

        Assert.Equal(1, chatGateway.StreamChatWithApprovalCallCount);
        ToolApprovalResponseInfo callerResponse = Assert.Single(chatGateway.ApprovalResponses);
        Assert.True(callerResponse.Approved);
        Assert.Equal("{\"result\":\"73\"}", callerResponse.ResultJson);
        Assert.Equal("""{"message":"roll a d222"}""", callerResponse.ArgumentsJson);
        Assert.Equal(AgentToolInvocationStates.Completed, invocationStore.Get("invocation-1")!.State);
    }

    private sealed class FakeChatGatewayService(params IAsyncEnumerable<GatewayChatStreamEvent>[] eventSequences) : IChatGatewayService
    {
        public int PersistRunCallCount { get; private set; }
        public ToolApprovalResponseInfo? LastApprovalResponse { get; private set; }
        public int StreamChatWithApprovalCallCount { get; private set; }
        public List<ToolApprovalResponseInfo> ApprovalResponses { get; } = [];
        public List<string?> InvocationContextCallerSessionIds { get; } = [];
        public List<AgentToolInvocationContextState?> InvocationContexts { get; } = [];

        public static async IAsyncEnumerable<GatewayChatStreamEvent> NestedApprovalEvents()
        {
            await Task.Yield();
            yield return new GatewayChatApprovalRequest("req-2", "tool-call-2", "run_shell", "{\"path\":\".\"}", RunId: "run-inner-2");
            yield return new GatewayChatStreamDone(new GatewayChatResult(
                TargetType: "agent",
                TargetId: "agent-inner-1",
                SessionId: "session-inner-1",
                Response: string.Empty,
                Compacted: false,
                Metrics: new GatewayMetrics(0, 0, 0),
                ToolCalls: [],
                Run: new GatewayRunState(GatewayRunPhases.AwaitingToolApproval)));
        }

        public static async IAsyncEnumerable<GatewayChatStreamEvent> ContinuationOutputEvents()
        {
            await Task.Yield();
            yield return new GatewayChatTokenDelta("Continuation output from agent-tool run.", false);
            yield return new GatewayChatStreamDone(new GatewayChatResult(
                TargetType: "agent",
                TargetId: "agent-agent-tool-1",
                SessionId: "session-agent-tool-1",
                Response: "Continuation output from agent-tool run.",
                Compacted: false,
                Metrics: new GatewayMetrics(0, 0, 0),
                ToolCalls: []));
        }

        public static async IAsyncEnumerable<GatewayChatStreamEvent> ContinuationRunFailedEvents()
        {
            await Task.Yield();
            yield return new GatewayChatStreamDone(new GatewayChatResult(
                TargetType: "agent",
                TargetId: "agent-agent-tool-1",
                SessionId: "session-agent-tool-1",
                Response: string.Empty,
                Compacted: false,
                Metrics: new GatewayMetrics(0, 0, 0),
                ToolCalls: [],
                Run: new GatewayRunState(GatewayRunPhases.Failed, ErrorMessage: "Child run failed.")));
        }

        public static async IAsyncEnumerable<GatewayChatStreamEvent> InnerContinuationOnlyEvents()
        {
            await Task.Yield();

            yield return new GatewayChatTokenDelta("Inner continuation output.", false);
            yield return new GatewayChatStreamDone(new GatewayChatResult(
                TargetType: "agent",
                TargetId: "agent-inner-1",
                SessionId: "session-inner-1",
                Response: "Inner continuation output.",
                Compacted: false,
                Metrics: new GatewayMetrics(0, 0, 0),
                ToolCalls: []));
        }

        public static async IAsyncEnumerable<GatewayChatStreamEvent> InnerContinuationToolResultOnlyEvents()
        {
            await Task.Yield();

            yield return new GatewayChatToolResult("tool-call-inner-1", "73");
            yield return new GatewayChatStreamDone(new GatewayChatResult(
            TargetType: "agent",
            TargetId: "agent-inner-1",
            SessionId: "session-inner-1",
            Response: string.Empty,
            Compacted: false,
            Metrics: new GatewayMetrics(0, 0, 0),
            ToolCalls: [new GatewayToolCall("tool-call-inner-1", "run_skill_script", "agent-inner-1", "{}", "73", "done", "skill")]));
        }

        public static async IAsyncEnumerable<GatewayChatStreamEvent> InnerNegativeDecisionEvents(string outcome)
        {
            await Task.Yield();

            string response = $"Child approval was {outcome}.";
            yield return new GatewayChatTokenDelta(response, false);
            yield return new GatewayChatStreamDone(new GatewayChatResult(
                TargetType: "agent",
                TargetId: "agent-inner-1",
                SessionId: "session-inner-1",
                Response: response,
                Compacted: false,
                Metrics: new GatewayMetrics(0, 0, 0),
                ToolCalls: []));
        }

        public static async IAsyncEnumerable<GatewayChatStreamEvent> CallerOnlyResumeEvents()
        {
            await Task.Yield();

            yield return new GatewayChatTokenDelta("Final caller response.", false);
            yield return new GatewayChatStreamDone(new GatewayChatResult(
                TargetType: "agent",
                TargetId: "agent-caller-1",
                SessionId: "session-caller-1",
                Response: "Final caller response.",
                Compacted: false,
                Metrics: new GatewayMetrics(0, 0, 0),
                ToolCalls: []));
        }

        public async IAsyncEnumerable<NaradPersistedEvent> StreamChatAsync(System.Text.Json.JsonElement body, AgentRequestContext? requestContext = null, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            yield break;
        }

        public async IAsyncEnumerable<NaradPersistedEvent> StreamChatWithApprovalAsync(
            string targetType,
            string targetId,
            string sessionId,
            ToolApprovalResponseInfo approvalResponse,
            string? userId,
            Func<NaradPersistedEvent, CancellationToken, ValueTask>? beforeWaitTerminal = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            StreamChatWithApprovalCallCount++;
            LastApprovalResponse = approvalResponse;
            ApprovalResponses.Add(approvalResponse);
            InvocationContextCallerSessionIds.Add(AgentToolInvocationContext.Current?.CallerSessionId);
            InvocationContexts.Add(AgentToolInvocationContext.Current);

            IAsyncEnumerable<GatewayChatStreamEvent> events = eventSequences[Math.Min(StreamChatWithApprovalCallCount - 1, eventSequences.Length - 1)];

            await foreach (NaradPersistedEvent evt in NaradTestEventFactory.ToNaradEvents(events, cancellationToken).ConfigureAwait(false))
            {
                if (beforeWaitTerminal is not null
                    && evt.EventType is NaradEventTypes.RunFinished or NaradEventTypes.RunError or NaradEventTypes.RunPaused)
                {
                    await beforeWaitTerminal(evt, cancellationToken).ConfigureAwait(false);
                }
                yield return evt;
            }
        }

        public void PersistRun(string sessionId, string userMessage, GatewayChatResult result, IReadOnlyList<string>? userAssetIds = null, string? userId = null, string? runId = null, string? sourceChannel = null)
        {
            PersistRunCallCount++;
        }

        public void RecordUserTurnStart(string sessionId, string userMessage, IReadOnlyList<string>? userAssetIds = null, string? userId = null, string? sourceChannel = null)
        {
        }

        public void PersistAssistantRunResult(string sessionId, GatewayChatResult result, string? userId = null)
        {
            PersistRunCallCount++;
        }
    }

    private sealed class CapturingContinuationOrchestrator : IApprovalContinuationOrchestrator
    {
        public ApprovalContinuationStreamRequest? Request { get; private set; }

        public async IAsyncEnumerable<NaradPersistedEvent> StreamAsync(
            ApprovalContinuationStreamRequest request,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Request = request;
            await Task.Yield();
            yield break;
        }
    }

    private sealed class EmptyContinuationOrchestrator : IApprovalContinuationOrchestrator
    {
        public async IAsyncEnumerable<NaradPersistedEvent> StreamAsync(
            ApprovalContinuationStreamRequest request,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.Yield();
            yield break;
        }
    }

    private sealed class PauseOnlyContinuationOrchestrator : IApprovalContinuationOrchestrator
    {
        public async IAsyncEnumerable<NaradPersistedEvent> StreamAsync(
            ApprovalContinuationStreamRequest request,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.Yield();
            // Outer continuation reaches run.paused without a nested interrupt on this stream.
            // Nested HITL is recovered from durable child wait + invocation link (catalogue AAAT matrix).
            string waitId = NaradEventBodies.ApprovalWaitId(request.ApprovalResponse.RequestId);
            yield return NaradTestEventFactory.FromBody(
                NaradEventTypes.RunPaused,
                NaradEventBodies.RunPaused(waitId, "interrupt", request.ApprovalResponse.RequestId),
                sessionId: request.SessionId,
                runId: request.ApprovalResponse.ContinuationRunId ?? "run-outer",
                rootRunId: request.ApprovalResponse.ContinuationRunId ?? "run-outer",
                sessionSequence: 1,
                rootSequence: 1);
            cancellationToken.ThrowIfCancellationRequested();
        }
    }

    private static ApprovalContinuationOrchestrator CreateOrchestrator(
        IChatGatewayService chatGateway,
        IGatewayApprovalStore approvalStore,
        IAgentToolInvocationLinkStore invocationLinkStore,
        IRunWaitStateStore runWaitStateStore,
        IGatewaySessionStore sessionStore,
        IAgentToolInvocationStore? invocationStore = null,
        IRuntimeEventHistoryReader? historyReader = null)
        => new(
            chatGateway,
            invocationLinkStore,
            invocationStore ?? new InMemoryAgentToolInvocationStore(invocationLinkStore),
            approvalStore,
            runWaitStateStore,
            sessionStore,
            historyReader ?? new EmptyRuntimeEventHistoryReader(),
            NullLogger<ApprovalContinuationOrchestrator>.Instance);

    private sealed class EmptyRuntimeEventHistoryReader : IRuntimeEventHistoryReader
    {
        public IReadOnlyList<NaradPersistedEvent> ReadRootHistory(string rootRunId) => [];

        public IReadOnlyList<NaradPersistedEvent> ReadSessionHistory(string rootAgentId, string sessionId) => [];
    }

    private sealed class InMemoryAgentToolInvocationStore(IAgentToolInvocationLinkStore links) : IAgentToolInvocationStore
    {
        private AgentToolInvocation invocation = new(
            "invocation-1", "run-caller-1", "session-caller-1", "tool-call-caller-1",
            "invoke_agent_roller", "run-inner-1", "session-inner-1", "agent-inner-1",
            "run-caller-1", AgentToolInvocationStates.WaitingOnChild,
            DateTimeOffset.UtcNow.ToString("O"), DateTimeOffset.UtcNow.ToString("O"));

        public AgentToolInvocation CreateWaiting(AgentToolInvocationCreateRequest request) => invocation;
        public AgentToolInvocation? Get(string invocationId) => invocation;
        public AgentToolInvocation? GetByParentToolCall(string parentRunId, string parentToolCallId) => invocation;
        public AgentToolInvocation? GetByChildRunId(string childRunId)
            => invocation is not null && string.Equals(invocation.ChildRunId, childRunId, StringComparison.Ordinal)
                ? invocation
                : null;
        public IReadOnlyList<AgentToolInvocation> ListRecoverable() => [invocation];
        public void SimulateExpiredParentCompletionLease()
            => invocation = invocation with
            {
                State = AgentToolInvocationStates.CompletingParent,
                CompletionLeaseOwner = "stopped-gateway",
                CompletionLeaseExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-1).ToString("O"),
            };
        public void RecordApprovalLink(AgentToolInvocationLink link)
        {
            invocation = invocation with { State = AgentToolInvocationStates.WaitingOnChild, ChildRunId = link.ChildRunId! };
            links.Upsert(link);
        }

        public void RecordChildCompletion(string invocationId, string childResultJson, string childOutcome)
            => invocation = invocation with
            {
                State = AgentToolInvocationStates.ParentCompletionPending,
                ChildResultJson = childResultJson,
                ChildOutcome = childOutcome,
                ChildCompletedAt = DateTimeOffset.UtcNow.ToString("O"),
            };

        public bool TryClaimParentCompletion(string invocationId, string leaseOwner, TimeSpan leaseDuration)
        {
            if (invocation.State is not (AgentToolInvocationStates.ParentCompletionPending or AgentToolInvocationStates.CompletingParent))
            {
                return false;
            }

            invocation = invocation with { State = AgentToolInvocationStates.CompletingParent, CompletionLeaseOwner = leaseOwner };
            return true;
        }

        public void MarkWaitingOnChild(string invocationId, string childRunId)
            => invocation = invocation with { State = AgentToolInvocationStates.WaitingOnChild, ChildRunId = childRunId };

        public void MarkCompleted(string invocationId, string leaseOwner)
            => invocation = invocation with { State = AgentToolInvocationStates.Completed };

        public bool TrySealCompleted(string invocationId)
        {
            if (invocation.State is not (AgentToolInvocationStates.CompletingParent or AgentToolInvocationStates.ParentCompletionPending)
                || string.IsNullOrWhiteSpace(invocation.ChildOutcome))
            {
                return false;
            }

            invocation = invocation with { State = AgentToolInvocationStates.Completed, CompletionLeaseOwner = null, CompletionLeaseExpiresAt = null };
            return true;
        }

        public void MarkFailed(string invocationId, string failureCode)
            => invocation = invocation with { State = AgentToolInvocationStates.Failed, FailureCode = failureCode };
    }

    private sealed class TestApprovalContinuationQueue : IApprovalContinuationQueue
    {
        private readonly Channel<ApprovalContinuationLease> channel = Channel.CreateUnbounded<ApprovalContinuationLease>();

        public async ValueTask<ApprovalContinuationEnqueueResult> EnqueueAsync(ApprovalContinuationWork work, CancellationToken cancellationToken)
        {
            await channel.Writer.WriteAsync(new ApprovalContinuationLease(work, "test-worker", 1), cancellationToken).ConfigureAwait(false);
            return ApprovalContinuationEnqueueResult.Inserted;
        }

        public IAsyncEnumerable<ApprovalContinuationLease> LeaseAllAsync(string leaseOwner, CancellationToken cancellationToken)
            => channel.Reader.ReadAllAsync(cancellationToken);

        public ValueTask CompleteAsync(ApprovalContinuationLease lease, CancellationToken cancellationToken)
        {
            CompletedCount++;
            return ValueTask.CompletedTask;
        }

        public ValueTask RenewAsync(ApprovalContinuationLease lease, CancellationToken cancellationToken)
            => ValueTask.CompletedTask;

        public ValueTask<ApprovalContinuationFailureDisposition> FailAsync(ApprovalContinuationLease lease, string safeError, CancellationToken cancellationToken)
        {
            FailedCount++;
            return ValueTask.FromResult(ApprovalContinuationFailureDisposition.RetryScheduled);
        }

        public int CompletedCount { get; private set; }
        public int FailedCount { get; private set; }

        public void ResetCompletedCount() => CompletedCount = 0;
    }

    private sealed class MutableApprovalStore(params GatewayToolApprovalRequest[] requests) : IGatewayApprovalStore
    {
        private readonly Dictionary<string, GatewayToolApprovalRequest> requestsById =
            requests.ToDictionary(static request => request.Id, StringComparer.Ordinal);

        public void SetDecision(string requestId, string decision)
        {
            GatewayToolApprovalRequest current = requestsById[requestId];
            requestsById[requestId] = current with
            {
                Decision = decision,
                DecidedBy = "user-1",
                DecidedAt = DateTimeOffset.UtcNow.ToString("O"),
                Reason = decision,
            };
        }

        public void CreateRequest(GatewayToolApprovalRequest request)
            => throw new NotSupportedException();

        public bool Resolve(string requestId, bool approved, string? reason, string? decidedBy, string resolutionSource, ApprovalContinuationWork? continuation = null)
            => throw new NotSupportedException();

        public IReadOnlyList<GatewayToolApprovalRequest> ListPending(string? userId, bool adminAll)
            => [];

        public GatewayToolApprovalRequest? GetDecidedByRunAndTool(string runId, string toolCallId, string toolName) => null;
        public GatewayToolApprovalRequest? GetDecidedByRunToolAndArguments(string runId, string toolName, string? argumentsJson) => null;
        public GatewayToolApprovalRequest? GetById(string requestId)
            => requestsById.GetValueOrDefault(requestId);

        public void SetChannelTetherMode(string requestId, ChannelHitlTetherMode mode)
            => throw new NotSupportedException();

        public IReadOnlyList<GatewayToolApprovalRequest> ListBySession(string sessionId)
            => [];

        public IReadOnlyList<GatewayToolApprovalRequest> ListHistory(string? userId, bool adminAll, int limit, int offset)
            => [];
    }

    private sealed class FakeApprovalStore(params GatewayToolApprovalRequest[] requests) : IGatewayApprovalStore
    {
        private readonly IReadOnlyDictionary<string, GatewayToolApprovalRequest> requestsById =
            requests.ToDictionary(static request => request.Id, StringComparer.Ordinal);

        public void CreateRequest(GatewayToolApprovalRequest request)
            => throw new NotSupportedException();

        public bool Resolve(string requestId, bool approved, string? reason, string? decidedBy, string resolutionSource, ApprovalContinuationWork? continuation = null)
            => throw new NotSupportedException();

        public IReadOnlyList<GatewayToolApprovalRequest> ListPending(string? userId, bool adminAll)
            => [];

        public GatewayToolApprovalRequest? GetDecidedByRunAndTool(string runId, string toolCallId, string toolName) => null;
        public GatewayToolApprovalRequest? GetDecidedByRunToolAndArguments(string runId, string toolName, string? argumentsJson) => null;
        public GatewayToolApprovalRequest? GetById(string requestId)
            => requestsById.GetValueOrDefault(requestId);

        public void SetChannelTetherMode(string requestId, ChannelHitlTetherMode mode)
            => throw new NotSupportedException();

        public IReadOnlyList<GatewayToolApprovalRequest> ListBySession(string sessionId)
            => [];

        public IReadOnlyList<GatewayToolApprovalRequest> ListHistory(string? userId, bool adminAll, int limit, int offset)
            => [];
    }

    private sealed class FakeInvocationLinkStore(AgentToolInvocationLink? link = null) : IAgentToolInvocationLinkStore
    {
        private AgentToolInvocationLink? current = link;

        public void Upsert(AgentToolInvocationLink link)
            => current = link;

        public AgentToolInvocationLink? GetByApprovalRequestId(string approvalRequestId)
            => current is not null && string.Equals(current.ApprovalRequestId, approvalRequestId, StringComparison.Ordinal)
                ? current
                : null;

        public IReadOnlyList<AgentToolInvocationLink> ListByCallerSession(string callerSessionId)
            => current is not null && string.Equals(current.CallerSessionId, callerSessionId, StringComparison.Ordinal)
                ? [current]
                : [];

        public AgentToolInvocationLink? GetByChildSessionId(string childSessionId)
            => current is not null && string.Equals(current.ChildSessionId, childSessionId, StringComparison.Ordinal)
                ? current
                : null;
        public IReadOnlyList<AgentToolInvocationLink> ListByRootRunId(string rootRunId)
            => current is not null && string.Equals(current.RootRunId, rootRunId, StringComparison.Ordinal) ? [current] : [];
        public bool HasLinksForSession(string sessionId)
            => current is not null && string.Equals(current.CallerSessionId, sessionId, StringComparison.Ordinal);
    }

    private static RunWaitState CreateParentWaitState()
        => new(
            "wait-parent-1",
            "continuation-caller-1",
            "run-caller-1",
            "agent",
            "agent-caller-1",
            "session-caller-1",
            "agent-caller-1",
            "user-1",
            RunWaitReasons.Child,
            "req-1",
            """{"stateBag":{"InMemoryChatHistoryProvider":{"messages":[{"role":"assistant","contents":[{"$type":"functionCall","callId":"tool-call-caller-1","name":"invoke_agent_roller","arguments":{"message":"roll a d222"}}]}]}}}""",
            "{}",
            "[]",
            "tool-call-caller-1",
            "invoke_agent_roller",
            "req-1",
            "session-inner-1",
            null,
            null,
            null,
            null,
            DateTimeOffset.UtcNow.ToString("O"),
            null,
            "event-parent-1");

    private static RunWaitState CreateApprovalWaitState()
        => CreateParentWaitState() with
        {
            WaitReason = RunWaitReasons.Approval,
            CorrelationId = "req-1",
        };

    private static RunWaitState CreatePackedApprovalWaitState(params string[] packedRequestIds)
        => CreateApprovalWaitState() with
        {
            CorrelationId = packedRequestIds[0],
            ApprovalRequestId = packedRequestIds[0],
            BehaviorJson = RunClockEnvelope.Wrap("{}", TimeSpan.Zero, packedRequestIds),
        };

    private static ApprovalContinuationWork CreateApprovedContinuationWork(string requestId)
        => new(
            RequestId: requestId,
            SessionId: "session-caller-1",
            TargetType: "agent",
            TargetId: "agent-caller-1",
            UserId: "user-1",
            Approved: true,
            Source: HitlResolutionSource.ChannelCallback,
            Reason: "approved",
            DecidedByUserId: "user-1",
            StopRun: false);

    private static GatewayToolApprovalRequest CreatePackedFetchApproval(
        string requestId,
        string toolCallId,
        string? Decision)
        => CreatePackedApproval(requestId, toolCallId, "kautuka_web_fetch", "mcp", Decision);

    private static GatewayToolApprovalRequest CreatePackedAgentApproval(
        string requestId,
        string toolCallId,
        string? Decision)
        => CreatePackedApproval(requestId, toolCallId, "invoke_agent_roller", "agent", Decision);

    private static GatewayToolApprovalRequest CreatePackedApproval(
        string requestId,
        string toolCallId,
        string toolName,
        string toolType,
        string? Decision)
        => new(
            Id: requestId,
            SessionId: "session-caller-1",
            RunId: "run-caller-1",
            CheckpointId: null,
            AgentId: "agent-caller-1",
            UserId: "user-1",
            ToolCallId: toolCallId,
            ToolName: toolName,
            ToolType: toolType,
            McpServerId: null,
            ArgumentsJson: "{}",
            RequestedAt: DateTimeOffset.UtcNow.ToString("O"),
            Decision: Decision,
            DecidedBy: Decision is null ? null : "user-1",
            DecidedAt: Decision is null ? null : DateTimeOffset.UtcNow.ToString("O"),
            Reason: Decision,
            ExpiresAt: DateTimeOffset.UtcNow.AddMinutes(5).ToString("O"));

    private static async Task WaitForQueueCompletedAsync(TestApprovalContinuationQueue queue)
    {
        using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (queue.CompletedCount == 0)
        {
            timeoutCts.Token.ThrowIfCancellationRequested();
            await Task.Delay(25, timeoutCts.Token).ConfigureAwait(true);
        }
    }

    private sealed class FakeRunWaitStateStore(params RunWaitState[] states) : IRunWaitStateStore
    {
        private readonly List<RunWaitState> currentStates = [.. states];

        public RunWaitState PauseRunForApproval(RunWaitStateCreateRequest request) => throw new NotSupportedException();

        public RunWaitState PauseRunForChild(RunWaitStateCreateRequest request) => throw new NotSupportedException();


        public RunWaitState? GetByWaitId(string waitId)
            => currentStates.FirstOrDefault(state => string.Equals(state.WaitId, waitId, StringComparison.Ordinal));

        public RunWaitState? GetActiveByContinuationRunId(string continuationRunId)
            => currentStates.FirstOrDefault(state => string.Equals(state.ContinuationRunId, continuationRunId, StringComparison.Ordinal) && IsActive(state));

        public RunWaitState? GetActiveByCorrelationId(string correlationId)
            => currentStates.FirstOrDefault(state => IsActive(state) && PackedToolApproval.WaitCoversRequest(state, correlationId));

        public RunWaitState? GetActiveChildPauseForCaller(string callerSessionId, string callerToolCallId)
            => currentStates.FirstOrDefault(state =>
                string.Equals(state.SessionId, callerSessionId, StringComparison.Ordinal)
                && string.Equals(state.ToolCallId, callerToolCallId, StringComparison.Ordinal)
                && string.Equals(state.WaitReason, RunWaitReasons.Child, StringComparison.Ordinal)
                && IsActive(state));

        public bool TryClaimExecution(string waitId, string leaseOwner, TimeSpan leaseDuration)
        {
            int index = currentStates.FindIndex(state => string.Equals(state.WaitId, waitId, StringComparison.Ordinal));
            if (index < 0 || !IsActive(currentStates[index]))
            {
                return false;
            }

            currentStates[index] = currentStates[index] with { Status = RunWaitStatuses.Executing, LeaseOwner = leaseOwner };
            return true;
        }

        public void RenewExecutionLease(string waitId, string leaseOwner, TimeSpan leaseDuration) { }

        public void ReleaseForRetry(string waitId, string leaseOwner) => SetStatus(waitId, RunWaitStatuses.Ready);
        public void MarkPausedAgain(string waitId) => SetStatus(waitId, RunWaitStatuses.PausedAgain);

        public void ReleaseExecutingWait(string waitId) => SetStatus(waitId, RunWaitStatuses.Ready);
        public void MarkCompleted(string waitId) => SetStatus(waitId, RunWaitStatuses.Completed);
        public void MarkFailed(string waitId, string failureCode) => SetStatus(waitId, RunWaitStatuses.Failed);
        public IReadOnlyList<RunWaitState> ListRecoverable() => [.. currentStates.Where(IsActive)];

        public void DeleteBySession(string sessionId)
            => currentStates.RemoveAll(state => string.Equals(state.SessionId, sessionId, StringComparison.Ordinal));

        public void PutPackedToolResult(string waitId, string requestId, string resultJson, bool journaledStart)
        {
            int index = currentStates.FindIndex(state => string.Equals(state.WaitId, waitId, StringComparison.Ordinal));
            if (index < 0)
            {
                return;
            }

            currentStates[index] = currentStates[index] with
            {
                BehaviorJson = RunClockEnvelope.PutPackedToolResult(
                    currentStates[index].BehaviorJson,
                    requestId,
                    resultJson,
                    journaledStart),
            };
        }

        private static bool IsActive(RunWaitState state)
            => state.Status is RunWaitStatuses.Ready or RunWaitStatuses.Executing;

        private void SetStatus(string waitId, string status)
        {
            int index = currentStates.FindIndex(state => string.Equals(state.WaitId, waitId, StringComparison.Ordinal));
            if (index >= 0)
            {
                currentStates[index] = currentStates[index] with { Status = status, LeaseOwner = null };
            }
        }
    }

    private sealed class FixedMaxParallelResolver(int maxParallelTools) : IToolInvocationSettingsResolver
    {
        public ResolvedToolInvocationSettings ResolveForAgent(string? agentId)
            => ToolInvocationSettings.Normalize(true, false, maxParallelTools);

        public ResolvedToolInvocationSettings Resolve(AgentRuntimeDefinition? agentRuntime)
            => ResolveForAgent(null);
    }

    private sealed class OverlappingPackedToolInvoker : IPackedApprovedToolInvoker
    {
        private readonly object gate = new();
        private int inFlight;
        private int invokeCount;

        public int MaxInFlight { get; private set; }

        public int InvokeCount => invokeCount;

        public async ValueTask<string> InvokeAsync(
            string agentId,
            string sessionId,
            string? userId,
            string toolName,
            string? argumentsJson,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref invokeCount);
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
                await Task.Delay(150, cancellationToken).ConfigureAwait(false);
                return """{"result":"fetched"}""";
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

    private sealed class StubPackedToolInvoker : IPackedApprovedToolInvoker
    {
        public List<string> InvokedNames { get; } = [];

        public ValueTask<string> InvokeAsync(
            string agentId,
            string sessionId,
            string? userId,
            string toolName,
            string? argumentsJson,
            CancellationToken cancellationToken)
        {
            InvokedNames.Add(toolName);
            return ValueTask.FromResult("""{"result":"fetched"}""");
        }
    }

    private sealed class FakeSessionStore : IGatewaySessionStore
    {
        public GatewaySession CreateSession(string targetType, string targetId, string ownerUserId, string? originator = null) => throw new NotSupportedException();
        public GatewaySession EnsureSession(string targetType, string targetId, string sessionId, string ownerUserId, string? originator = null) => throw new NotSupportedException();
        public IReadOnlyList<GatewaySession> ListSessions(string targetType, string targetId, string ownerUserId, bool includeArchived = false) => [];
        public GatewaySession? UpdateSessionTitle(string sessionId, string title) => null;
        public GatewaySession? ArchiveSession(string sessionId) => null;
        public IReadOnlyList<GatewaySession> ListRecentSessions(string ownerUserId, int limit) => [];
        public IReadOnlyList<string> PruneSessions(string targetType, string targetId, string ownerUserId, int retainCount, string preserveSessionId) => [];
        public bool TryGetSession(string sessionId, out GatewaySession? session)
        {
            if (string.Equals(sessionId, "session-caller-1", StringComparison.Ordinal))
            {
                session = new GatewaySession(
                    Id: "session-caller-1",
                    TargetType: "agent",
                    TargetId: "agent-caller-1",
                    OwnerUserId: "user-1",
                    Title: "Caller",
                    CreatedAt: DateTimeOffset.UtcNow.ToString("O"),
                    UpdatedAt: DateTimeOffset.UtcNow.ToString("O"),
                    Messages: [],
                    Compacted: false,
                    CompactSummary: null);
                return true;
            }

            session = null;
            return false;
        }

        public bool DeleteSession(string sessionId) => throw new NotSupportedException();
        public GatewaySession? CompactSession(string sessionId, string? reason = null, string? summary = null) => throw new NotSupportedException();
        public GatewaySession AppendUserMessage(string sessionId, GatewayMessage userMessage, IReadOnlyList<string>? userAssetIds = null, string? userId = null) => throw new NotSupportedException();
        public GatewaySession AppendAssistantMessage(string sessionId, GatewayMessage assistantMessage, string? userId = null) => throw new NotSupportedException();
        public GatewayMemoriesResponse GetMemories(string targetType, string targetId, string ownerUserId) => throw new NotSupportedException();
        public bool DeleteMemoryFact(string factId) => throw new NotSupportedException();
        public bool UpdateMemoryFact(string factId, string title, string content, string category) => throw new NotSupportedException();
        public IReadOnlyList<GatewayRun> ListRuns() => [];
        public bool TryGetRun(string runId, out GatewayRun? run)
        {
            run = null;
            return false;
        }

        public GatewaySession AppendMessages(string sessionId, GatewayMessage userMessage, GatewayMessage assistantMessage, IReadOnlyList<string>? userAssetIds = null, string? userId = null) => throw new NotSupportedException();
        public GatewayRun AppendRun(string targetType, string targetId, string userId, string? sessionId, string input, string output, GatewayMetrics? metrics = null, EffectiveRunBehavior? effectiveBehavior = null, string? runId = null, string? error = null, string? traceId = null) => throw new NotSupportedException();
        public GatewayRun CreateInteractionRun(string targetType, string targetId, string userId, string? sessionId, string interactionId, string prompt, IReadOnlyList<string> options) => throw new NotSupportedException();
        public GatewayRun? UpdateInteractionRun(string interactionId, string status, Dictionary<string, object?> output, string? error = null) => throw new NotSupportedException();
    }

    private sealed class CapturingPublisher : IMediator
    {
        private readonly TaskCompletionSource<HitlApprovalRequested> requestedTcs =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<HitlContinuationCompleted> completedTcs =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<HitlContinuationFailed> failedTcs =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

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
            if (notification is HitlApprovalRequested requested)
            {
                requestedTcs.TrySetResult(requested);
            }

            if (notification is HitlContinuationCompleted completed)
            {
                completedTcs.TrySetResult(completed);
            }

            if (notification is HitlContinuationFailed failed)
            {
                failedTcs.TrySetResult(failed);
            }

            return ValueTask.CompletedTask;
        }

        public ValueTask Publish<TNotification>(TNotification notification, CancellationToken cancellationToken = default)
            where TNotification : INotification
        {
            if (notification is HitlApprovalRequested requested)
            {
                requestedTcs.TrySetResult(requested);
            }

            if (notification is HitlContinuationCompleted completed)
            {
                completedTcs.TrySetResult(completed);
            }

            if (notification is HitlContinuationFailed failed)
            {
                failedTcs.TrySetResult(failed);
            }

            return ValueTask.CompletedTask;
        }

        public async Task<HitlApprovalRequested> WaitForRequestedAsync()
        {
            using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            Task completed = await Task.WhenAny(requestedTcs.Task, Task.Delay(Timeout.InfiniteTimeSpan, timeoutCts.Token)).ConfigureAwait(true);
            if (!ReferenceEquals(completed, requestedTcs.Task))
            {
                throw new TimeoutException("Timed out waiting for HitlApprovalRequested event.");
            }

            return await requestedTcs.Task.ConfigureAwait(true);
        }

        public async Task AssertNoRequestedAsync()
        {
            Task completed = await Task.WhenAny(requestedTcs.Task, Task.Delay(TimeSpan.FromMilliseconds(250))).ConfigureAwait(true);
            Assert.False(ReferenceEquals(completed, requestedTcs.Task));
        }

        public async Task<HitlContinuationCompleted> WaitForCompletedAsync()
        {
            using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            Task completed = await Task.WhenAny(completedTcs.Task, Task.Delay(Timeout.InfiniteTimeSpan, timeoutCts.Token)).ConfigureAwait(true);
            if (!ReferenceEquals(completed, completedTcs.Task))
            {
                throw new TimeoutException("Timed out waiting for HitlContinuationCompleted event.");
            }

            return await completedTcs.Task.ConfigureAwait(true);
        }

        public async Task<HitlContinuationFailed> WaitForFailedAsync()
        {
            using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            Task completed = await Task.WhenAny(failedTcs.Task, Task.Delay(Timeout.InfiniteTimeSpan, timeoutCts.Token)).ConfigureAwait(true);
            if (!ReferenceEquals(completed, failedTcs.Task))
            {
                throw new TimeoutException("Timed out waiting for HitlContinuationFailed event.");
            }

            return await failedTcs.Task.ConfigureAwait(true);
        }
    }
}
