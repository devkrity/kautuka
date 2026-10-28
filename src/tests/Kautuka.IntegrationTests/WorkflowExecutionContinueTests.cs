using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;

using Kautuka.Gateway.Domain.Execution;
using Kautuka.Gateway.Domain.Stores;
using Kautuka.Gateway.Domain.Workflows;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

using static Kautuka.IntegrationTests.ThreadCommandTestSupport;

namespace Kautuka.IntegrationTests;

public sealed class WorkflowExecutionContinueTests
{
    [Fact]
    public async Task RecoveryContinuesWhenListingRecoverableInstancesFails()
    {
        using var hang = new ManualResetEventSlim(true);
        using var loggerProvider = new CapturingLoggerProvider();
        using var loggerFactory = LoggerFactory.Create(builder => builder.AddProvider(loggerProvider));
        var store = new HangOnSecondGetInstanceStore(PausedInstance(), hang, throwOnList: true);
        var execution = new WorkflowExecutionService(
            store,
            new UnusedChatGateway(),
            new NoOpWorkflowPublisher(),
            new EmptyHistory(),
            new RuntimeEventStreamHub(),
            new ThreadCommandApprovalStore(),
            new ThreadCommandMediator(),
            NullLogger<WorkflowExecutionService>.Instance);
        var recovery = new WorkflowRecoveryHostedService(
            store,
            execution,
            loggerFactory.CreateLogger<WorkflowRecoveryHostedService>());

        await recovery.StartAsync(TestContext.Current.CancellationToken);
        await loggerProvider.ListFailedLogged.Task.WaitAsync(
            TimeSpan.FromSeconds(5),
            TestContext.Current.CancellationToken);
        await recovery.StopAsync(TestContext.Current.CancellationToken);
        Assert.Contains(loggerProvider.Messages, message =>
            message.Contains("Could not list recoverable workflow instances", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ContinueApproveReturnsBeforeNextStepExecution()
    {
        using var hang = new ManualResetEventSlim(false);
        var store = new HangOnSecondGetInstanceStore(PausedInstance(), hang);
        var service = new WorkflowExecutionService(
            store,
            new UnusedChatGateway(),
            new NoOpWorkflowPublisher(),
            new EmptyHistory(),
            new RuntimeEventStreamHub(),
            new ThreadCommandApprovalStore(),
            new ThreadCommandMediator(),
            NullLogger<WorkflowExecutionService>.Instance);

        Task continueTask = Task.Run(
            () => service.ContinueAsync("wfi-1", "approve", TestContext.Current.CancellationToken),
            TestContext.Current.CancellationToken);
        Task winner = await Task.WhenAny(continueTask, Task.Delay(TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken));
        hang.Set();
        await continueTask.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);

        Assert.Same(continueTask, winner);
    }

    [Fact]
    public async Task ContinueApprovePublishesOneResumeForTheClosedPause()
    {
        using var hang = new ManualResetEventSlim(true);
        var publisher = new RecordingWorkflowPublisher();
        var store = new HangOnSecondGetInstanceStore(PausedInstance(), hang);
        var service = new WorkflowExecutionService(
            store,
            new UnusedChatGateway(),
            publisher,
            new EmptyHistory(),
            new RuntimeEventStreamHub(),
            new ThreadCommandApprovalStore(),
            new ThreadCommandMediator(),
            NullLogger<WorkflowExecutionService>.Instance);

        await service.ContinueAsync(
            "wfi-1",
            "approve",
            TestContext.Current.CancellationToken,
            waitId: "wait:wrev-1");
        await publisher.SawError.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        (string Type, JsonObject Body)[] resumed = publisher.Events
            .Where(item => item.Type == NaradEventTypes.WorkflowResumed)
            .ToArray();
        Assert.Single(resumed);
        Assert.Equal("wait:wrev-1", resumed[0].Body["waitId"]?.GetValue<string>());
    }

    [Fact]
    public async Task ExhaustedChangeRequestKeepsWorkflowPaused()
    {
        var publisher = new RecordingWorkflowPublisher();
        GatewayWorkflowInstance instance = PausedInstance() with
        {
            Steps = [
                FinishedAttempt(1),
                FinishedAttempt(2),
                FinishedAttempt(3),
            ],
        };
        var store = new HangOnSecondGetInstanceStore(instance, new ManualResetEventSlim(true));
        var service = new WorkflowExecutionService(
            store,
            new UnusedChatGateway(),
            publisher,
            new EmptyHistory(),
            new RuntimeEventStreamHub(),
            new ThreadCommandApprovalStore(),
            new ThreadCommandMediator(),
            NullLogger<WorkflowExecutionService>.Instance);

        await service.ContinueAsync(
            "wfi-1",
            "request_changes",
            TestContext.Current.CancellationToken,
            waitId: "wait:wrev-3");

        Assert.Equal(WorkflowInstanceStatuses.Paused, instance.Status);
        Assert.DoesNotContain(publisher.Events, item => item.Type == NaradEventTypes.WorkflowResumed);
        Assert.DoesNotContain(publisher.Events, item => item.Type == NaradEventTypes.WorkflowCancelled);
    }

    private static GatewayWorkflowStepAttempt FinishedAttempt(int attempt)
    {
        string now = DateTimeOffset.UtcNow.ToString("O");
        return new GatewayWorkflowStepAttempt(
            $"wattempt-{attempt}", "wfi-1", "n1", attempt, $"wrun-{attempt}", null,
            WorkflowStepAttemptStatuses.Finished, null, now, now, now);
    }

    private static GatewayWorkflowInstance PausedInstance()
    {
        string now = DateTimeOffset.UtcNow.ToString("O");
        var node = new GatewayWorkflowNode(
            "n1",
            "agent-1",
            "do",
            "text",
            null,
            [],
            "none",
            false,
            true,
            0);
        var definition = new GatewayWorkflowDefinition(
            "wf-1",
            "user-1",
            "t",
            false,
            1,
            new JsonObject(),
            new JsonObject(),
            new JsonObject(),
            [node],
            now,
            now);
        return new GatewayWorkflowInstance(
            "wfi-1",
            "wf-1",
            1,
            "user-1",
            null,
            WorkflowInstanceStatuses.Paused,
            false,
            "n1",
            new JsonObject(),
            "hash",
            1,
            definition,
            [],
            null,
            null,
            now,
            now,
            now,
            null);
    }

    private sealed class HangOnSecondGetInstanceStore(
        GatewayWorkflowInstance instance,
        ManualResetEventSlim hang,
        bool throwOnList = false) : IGatewayWorkflowStore
    {
        private int gets;

        public GatewayWorkflowInstance? GetInstance(string instanceId)
        {
            if (Interlocked.Increment(ref gets) > 1)
            {
                hang.Wait();
            }

            return string.Equals(instanceId, instance.Id, StringComparison.Ordinal) ? instance : null;
        }

        public GatewayWorkflowDefinition CreateDefinition(GatewayWorkflowDefinition definition) => throw new NotSupportedException();

        public bool UpdateDefinition(GatewayWorkflowDefinition definition) => throw new NotSupportedException();

        public GatewayWorkflowDefinition? GetDefinition(string workflowId) => throw new NotSupportedException();

        public IReadOnlyList<GatewayWorkflowDefinition> ListDefinitions(string ownerUserId, bool archived = false) => throw new NotSupportedException();
        public bool SetArchived(string workflowId, string ownerUserId, bool archived) => throw new NotSupportedException();
        public bool DeleteDefinitionIfNeverRun(string workflowId, string ownerUserId) => throw new NotSupportedException();
        public bool HasRuns(string workflowId, string ownerUserId) => throw new NotSupportedException();

        public GatewayWorkflowInstance CreateInstance(GatewayWorkflowInstance created, IReadOnlyList<WorkflowArtifact>? artifacts = null) => throw new NotSupportedException();

        public IReadOnlyList<WorkflowArtifact> ListArtifacts(string instanceId) => [];

        public GatewayWorkflowInstance? GetInstanceByStepSession(string sessionId) => null;

        public IReadOnlyList<GatewayWorkflowInstance> ListInstances(string ownerUserId, string? workflowId, int limit, int offset)
            => throw new NotSupportedException();

        public IReadOnlyList<GatewayWorkflowInstance> ListRecoverableInstances()
            => throwOnList ? throw new InvalidOperationException("database unavailable") : [];

        public bool TryRequestStop(string instanceId, string ownerUserId, DateTimeOffset nowUtc, out GatewayWorkflowInstance? updated)
        {
            updated = null;
            return false;
        }

        public bool TrySetInstanceStatus(
            string instanceId,
            string status,
            string? currentNodeId,
            string? failureCode,
            string? error,
            bool? stopRequested,
            DateTimeOffset nowUtc,
            out GatewayWorkflowInstance? updated)
        {
            updated = instance;
            return true;
        }

        public bool TryUpdateEnvelope(string instanceId, JsonObject envelope, DateTimeOffset nowUtc, out GatewayWorkflowInstance? updated)
        {
            updated = null;
            return false;
        }

        public bool TryUpdateStepAttempt(GatewayWorkflowStepAttempt attempt, DateTimeOffset nowUtc) => false;

        public bool TryAddStepAttempt(GatewayWorkflowStepAttempt attempt) => false;

        public bool TryClearMafCheckpoint(string instanceId, DateTimeOffset nowUtc) => false;

        public string? GetMafCheckpointJson(string instanceId) => null;

        public bool TrySaveMafCheckpointJson(string instanceId, string checkpointJson, DateTimeOffset nowUtc) => false;

        public NaradPersistedEvent AppendNaradEvent(NaradPersistedEvent persistedEvent) => persistedEvent;

        public IReadOnlyList<NaradPersistedEvent> ListNaradEvents(string instanceId, long afterSessionSequence, int limit) => [];

        public long ReadNaradSessionSequence(string instanceId) => 0;
    }

    private sealed class UnusedChatGateway : IChatGatewayService
    {
        public async IAsyncEnumerable<NaradPersistedEvent> StreamChatAsync(
            JsonElement body,
            AgentRequestContext? requestContext = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
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
            await Task.CompletedTask;
            yield break;
        }

        public void PersistRun(
            string sessionId,
            string userMessage,
            GatewayChatResult result,
            IReadOnlyList<string>? userAssetIds = null,
            string? userId = null,
            string? runId = null,
            string? sourceChannel = null)
        {
        }

        public void RecordUserTurnStart(
            string sessionId,
            string userMessage,
            IReadOnlyList<string>? userAssetIds = null,
            string? userId = null,
            string? sourceChannel = null)
        {
        }

        public void PersistAssistantRunResult(string sessionId, GatewayChatResult result, string? userId = null)
        {
        }
    }

    private sealed class RecordingWorkflowPublisher : IWorkflowNaradPublisher
    {
        private readonly ConcurrentQueue<(string Type, JsonObject Body)> events = new();

        public TaskCompletionSource SawError { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public (string Type, JsonObject Body)[] Events => events.ToArray();

        public NaradPersistedEvent Publish(GatewayWorkflowInstance instance, string eventType, JsonObject body)
        {
            events.Enqueue((eventType, body));
            if (eventType == NaradEventTypes.WorkflowError)
            {
                SawError.TrySetResult();
            }

            return Persisted(instance, eventType, body);
        }

        private static NaradPersistedEvent Persisted(GatewayWorkflowInstance instance, string eventType, JsonObject body)
            => new(
                Guid.NewGuid().ToString("N"),
                NaradProtocolVersions.V1,
                eventType,
                instance.Id,
                1,
                NaradRecordClasses.Authority,
                body.ToJsonString(),
                DateTimeOffset.UtcNow.ToString("O"),
                WorkflowId: instance.Id,
                WorkflowSequence: 1);
    }

    private sealed class NoOpWorkflowPublisher : IWorkflowNaradPublisher
    {
        public NaradPersistedEvent Publish(GatewayWorkflowInstance instance, string eventType, JsonObject body)
            => new(
                Guid.NewGuid().ToString("N"),
                NaradProtocolVersions.V1,
                eventType,
                instance.Id,
                1,
                NaradRecordClasses.Authority,
                body.ToJsonString(),
                DateTimeOffset.UtcNow.ToString("O"),
                WorkflowId: instance.Id,
                WorkflowSequence: 1);
    }

    private sealed class EmptyHistory : IRuntimeEventHistoryReader
    {
        public IReadOnlyList<NaradPersistedEvent> ReadRootHistory(string rootRunId) => [];

        public IReadOnlyList<NaradPersistedEvent> ReadSessionHistory(string rootAgentId, string sessionId) => [];
    }

    private sealed class CapturingLoggerProvider : ILoggerProvider
    {
        private readonly List<string> messages = [];
        public TaskCompletionSource ListFailedLogged { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public IReadOnlyList<string> Messages => messages;

        public ILogger CreateLogger(string categoryName) => new CapturingLogger(this);

        public void Dispose() { }

        private sealed class CapturingLogger(CapturingLoggerProvider provider) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(
                LogLevel logLevel,
                EventId eventId,
                TState state,
                Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                string message = formatter(state, exception);
                lock (provider.messages)
                    provider.messages.Add(message);
                if (message.Contains("Could not list recoverable workflow instances", StringComparison.Ordinal))
                    provider.ListFailedLogged.TrySetResult();
            }
        }
    }
}
