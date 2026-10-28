using System.Text.Json;

using Kautuka.Gateway.Domain.Execution;
using Kautuka.Gateway.Domain.Hitl;

using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

using static Kautuka.IntegrationTests.ThreadCommandTestSupport;

namespace Kautuka.IntegrationTests;

public sealed class ApprovalResolutionSideEffectDurabilityTests
{
    [Fact]
    public async Task DispatcherPublishesInterruptResolvedForStagedEvent()
    {
        using var temp = new TemporaryGatewayStateDirectory();
        KautukaDatabase database = CreateDatabase(temp.SqlitePath);
        var store = CreateGatewayApprovalStore(database);
        GatewayToolApprovalRequest request = MakeRequest();
        store.CreateRequest(request);
        var publisher = new CapturingRuntimeEventPublisher();
        ApprovalResolutionEventWork staged = MakeStopEvent(request);
        Assert.True(store.Resolve(
            request.Id,
            approved: false,
            reason: "stopped",
            decidedBy: "user-1",
            resolutionSource: "chat",
            continuation: null,
            staged));

        var dispatcher = new ApprovalResolutionEventDispatcher(
            store,
            publisher,
            new ApprovalResolutionSideEffectApplier(new ThreadCommandTestSupport.NoOpRunStopCommandService()),
            TimeProvider.System,
            NullLogger<ApprovalResolutionEventDispatcher>.Instance);
        await dispatcher.DispatchAsync(request.Id, TestContext.Current.CancellationToken);

        NaradPersistedEvent published = Assert.Single(publisher.Events);
        Assert.Equal(NaradEventTypes.InterruptResolved, published.EventType);
        Assert.True(IsPublished(database, request.Id));
    }

    [Fact]
    public async Task DispatcherSurvivesDisposedStoreDuringLease()
    {
        var dispatcher = new ApprovalResolutionEventDispatcher(
            new ThrowingLeaseStore(),
            new CapturingRuntimeEventPublisher(),
            new ApprovalResolutionSideEffectApplier(new ThreadCommandTestSupport.NoOpRunStopCommandService()),
            TimeProvider.System,
            NullLogger<ApprovalResolutionEventDispatcher>.Instance);

        await dispatcher.DispatchAsync(null, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task DuplicateSideEffectDispatchIsIdempotent()
    {
        NaradPersistedEvent started = RuntimeEvent(1, NaradEventTypes.RunStarted, new { trigger = "chat" });
        var agentStore = new ThreadCommandAgentStore(started);
        var stopSignals = new RunStopRegistry();
        var stopCommands = new RunStopCommandService(
            agentStore,
            new ThreadCommandPublisher(agentStore, () => stopSignals.IsStopped("run-1")),
            stopSignals);
        var sideEffectApplier = new ApprovalResolutionSideEffectApplier(stopCommands);
        GatewayToolApprovalRequest request = MakeRequest();
        ApprovalResolutionEventWork staged = MakeStopEvent(request);

        await sideEffectApplier.ApplyAsync(staged, TestContext.Current.CancellationToken);
        await sideEffectApplier.ApplyAsync(staged, TestContext.Current.CancellationToken);

        Assert.Single(
            agentStore.ReadRootHistory("run-1"),
            item => item.EventType == NaradEventTypes.RunStopRequested);
    }

    [Fact]
    public void ContinuationClaimBlockedUntilResolutionPublished()
    {
        using var temp = new TemporaryGatewayStateDirectory();
        KautukaDatabase database = CreateDatabase(temp.SqlitePath);
        var store = CreateGatewayApprovalStore(database);
        var queue = new ApprovalContinuationQueue(database, TimeProvider.System);
        GatewayToolApprovalRequest request = MakeRequest();
        store.CreateRequest(request);
        ApprovalContinuationWork continuation = new(
            request.Id,
            request.SessionId!,
            "agent",
            request.AgentId!,
            request.UserId,
            Approved: false,
            HitlResolutionSource.Browser,
            Reason: "stopped",
            DecidedByUserId: "user-1",
            StopRun: true);
        ApprovalResolutionEventWork staged = MakeStopEvent(request);
        Assert.True(store.Resolve(
            request.Id,
            approved: false,
            reason: "stopped",
            decidedBy: "user-1",
            resolutionSource: "chat",
            continuation,
            staged));

        DateTimeOffset now = DateTimeOffset.UtcNow;
        Assert.Null(queue.TryClaimForTest("worker"));

        ApprovalResolutionEventLease lease = Assert.IsType<ApprovalResolutionEventLease>(
            store.TryLeaseResolutionEvent("worker", now, now.AddMinutes(2), request.Id));
        Assert.Null(queue.TryClaimForTest("worker-2"));

        store.MarkResolutionEventPublished(lease, now);
        Assert.NotNull(queue.TryClaimForTest("worker-3"));
    }

    [Fact]
    public async Task RunStopAlreadyRequestedStillWakesInProcessExecution()
    {
        NaradPersistedEvent started = RuntimeEvent(1, NaradEventTypes.RunStarted, new { trigger = "chat" });
        var agentStore = new ThreadCommandAgentStore(started);
        var stopSignals = new RunStopRegistry();
        var stopCommands = new RunStopCommandService(
            agentStore,
            new ThreadCommandPublisher(agentStore, () => stopSignals.IsStopped("run-1")),
            stopSignals);
        var command = new RunStopCommand("agent-1", "session-1", "run-1", "user-1", "User", "chat");

        RunStopCommandStatus first = await stopCommands.RequestStopAsync(command, TestContext.Current.CancellationToken);
        RunStopCommandStatus second = await stopCommands.RequestStopAsync(command, TestContext.Current.CancellationToken);

        Assert.Equal(RunStopCommandStatus.Accepted, first);
        Assert.Equal(RunStopCommandStatus.AlreadyRequested, second);
        Assert.True(stopSignals.IsStopped("run-1"));
    }

    private static bool IsPublished(KautukaDatabase database, string requestId)
    {
        using var connection = new SqliteConnection(database.ConnectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT status FROM approval_resolution_events WHERE request_id = $requestId LIMIT 1;";
        command.Parameters.AddWithValue("$requestId", requestId);
        return string.Equals(command.ExecuteScalar()?.ToString(), "published", StringComparison.Ordinal);
    }

    private static KautukaDatabase CreateDatabase(string sqlitePath)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Kautuka:ContainerStateRoot"] = Path.GetDirectoryName(sqlitePath),
            })
            .Build();
        var database = new KautukaDatabase(configuration);
        database.EnsureReady();
        return database;
    }

    private static GatewayToolApprovalRequest MakeRequest() => new(
        "apr-side-effect",
        "session-1",
        "run-1",
        null,
        "agent-1",
        "user-1",
        "tool-call-1",
        "tool-1",
        "mcp",
        null,
        "{}",
        DateTimeOffset.UtcNow.ToString("O"),
        null,
        null,
        null,
        null,
        null);

    private static ApprovalResolutionEventWork MakeStopEvent(GatewayToolApprovalRequest request) => new(
        $"approval-resolution:{request.Id}",
        request.Id,
        JsonSerializer.SerializeToElement(new { approvalRequestId = request.Id, approved = false }),
        new NaradEventContext(request.AgentId!, request.SessionId!, request.UserId!, request.RunId!, request.RunId!),
        request.ToolCallId,
        request.CheckpointId,
        StopRun: true,
        StopThreadId: request.SessionId,
        StopChannel: "chat",
        Actor: "user");

    private sealed class TemporaryGatewayStateDirectory : IDisposable
    {
        private readonly string root = Path.Combine(Path.GetTempPath(), "kautuka-side-effect-tests", Guid.NewGuid().ToString("N"));

        public TemporaryGatewayStateDirectory() => Directory.CreateDirectory(root);

        public string SqlitePath => Path.Combine(root, "kautuka.db");

        public void Dispose()
        {
            SqliteTestPoolCleanup.ClearPoolsUnderDirectory(root);
            Directory.Delete(root, recursive: true);
        }
    }

    private sealed class CapturingRuntimeEventPublisher : IRuntimeEventPublisher
    {
        public List<NaradPersistedEvent> Events { get; } = [];

        public ValueTask<NaradPersistedEvent> PublishAsync(
            NaradAuthorityEventDraft draft,
            NaradEventContext context,
            CancellationToken cancellationToken = default)
        {
            NaradPersistedEvent persisted = NaradTestEventFactory.FromDraft(draft, context, Events.Count + 1, Events.Count + 1);
            Events.Add(persisted);
            return ValueTask.FromResult(persisted);
        }

        public ValueTask<NaradPersistedEvent> PublishWithIdAsync(
            string eventId,
            string eventType,
            JsonElement body,
            NaradEventContext context,
            CancellationToken cancellationToken = default)
            => TestRuntimeEventPublisherSupport.PublishWithIdAsync(PublishAsync, eventId, eventType, body, context, cancellationToken);
    }

    private sealed class ThrowingLeaseStore : IApprovalResolutionEventStore
    {
        public ApprovalResolutionEventLease? TryLeaseResolutionEvent(
            string leaseOwner,
            DateTimeOffset nowUtc,
            DateTimeOffset leaseExpiresAtUtc,
            string? requestId = null)
            => throw new ObjectDisposedException("SqliteConnection");

        public void MarkResolutionEventPublished(ApprovalResolutionEventLease lease, DateTimeOffset nowUtc)
            => throw new NotSupportedException();

        public ApprovalResolutionEventFailureDisposition FailResolutionEvent(
            ApprovalResolutionEventLease lease,
            string safeError,
            DateTimeOffset nowUtc)
            => throw new NotSupportedException();

        public IReadOnlyList<ApprovalResolutionEventDeadLetter> ListResolutionEventDeadLetters(int limit)
            => throw new NotSupportedException();
    }
}

internal static class ApprovalContinuationQueueTestExtensions
{
    public static ApprovalContinuationLease? TryClaimForTest(this ApprovalContinuationQueue queue, string leaseOwner)
    {
        var method = typeof(ApprovalContinuationQueue).GetMethod(
            "TryClaim",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
            ?? throw new InvalidOperationException("Expected TryClaim method.");
        return (ApprovalContinuationLease?)method.Invoke(queue, [leaseOwner]);
    }
}
