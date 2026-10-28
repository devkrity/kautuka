using System.Text.Json;

using Kautuka.Gateway.Domain.Execution;
using Kautuka.Gateway.Domain.Hitl;

namespace Kautuka.IntegrationTests;

public sealed class RunStopCommandServiceTests
{
    [Fact]
    public async Task PersistsAuditableCommandBeforeSignallingCancellation()
    {
        NaradPersistedEvent started = RuntimeEvent(1, NaradEventTypes.RunStarted, new { trigger = "chat" });
        var history = new MutableHistory(started);
        var signals = new RecordingStopRegistry();
        var publisher = new RecordingPublisher(history, () => signals.IsStopped(runId));
        var service = new RunStopCommandService(history, publisher, signals);

        RunStopCommandStatus status = await service.RequestStopAsync(
            Command(),
            TestContext.Current.CancellationToken);

        Assert.Equal(RunStopCommandStatus.Accepted, status);
        NaradPersistedEvent stop = Assert.Single(publisher.Published);
        Assert.Equal($"run-stop:{runId}", stop.EventId);
        Assert.Equal(NaradEventTypes.RunStopRequested, stop.EventType);
        Assert.Equal(NaradRecordClasses.Authority, stop.RecordClass);
        Assert.Equal("Admin User", NaradPersistedEventReader.String(stop, "actor"));
        Assert.Equal("chat", NaradPersistedEventReader.String(stop, "channel"));
        Assert.Equal("user_requested", NaradPersistedEventReader.String(stop, "reason"));
        Assert.False(publisher.SignalWasSetDuringPublish);
        Assert.True(signals.IsStopped(runId));
    }

    [Fact]
    public async Task StopBodyPassesJournalReservedKeyValidation()
    {
        NaradPersistedEvent started = RuntimeEvent(1, NaradEventTypes.RunStarted, new { trigger = "chat" });
        var history = new MutableHistory(started);
        var signals = new RecordingStopRegistry();
        var publisher = new ValidatingRecordingPublisher(history, () => signals.IsStopped(runId));
        var service = new RunStopCommandService(history, publisher, signals);

        RunStopCommandStatus status = await service.RequestStopAsync(
            Command(),
            TestContext.Current.CancellationToken);

        Assert.Equal(RunStopCommandStatus.Accepted, status);
        Assert.Single(publisher.Published);
        Assert.True(signals.IsStopped(runId));
    }

    [Fact]
    public async Task PersistenceFailureNeverSignalsCancellation()
    {
        NaradPersistedEvent started = RuntimeEvent(1, NaradEventTypes.RunStarted, new { trigger = "chat" });
        var history = new MutableHistory(started);
        var signals = new RecordingStopRegistry();
        var service = new RunStopCommandService(history, new FailingPublisher(), signals);

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await service.RequestStopAsync(Command(), TestContext.Current.CancellationToken));

        Assert.False(signals.IsStopped(runId));
    }

    [Fact]
    public async Task RejectsTerminalOrMismatchedRunWithoutWritingOrSignalling()
    {
        NaradPersistedEvent started = RuntimeEvent(1, NaradEventTypes.RunStarted, new { trigger = "chat" });
        NaradPersistedEvent terminal = RuntimeEvent(2, NaradEventTypes.RunFinished, new { result = "done" });
        var history = new MutableHistory(started, terminal);
        var signals = new RecordingStopRegistry();
        var publisher = new RecordingPublisher(history, () => signals.IsStopped(runId));
        var service = new RunStopCommandService(history, publisher, signals);

        Assert.Equal(
            RunStopCommandStatus.AlreadyTerminal,
            await service.RequestStopAsync(Command(), TestContext.Current.CancellationToken));
        Assert.Equal(
            RunStopCommandStatus.NotFound,
            await service.RequestStopAsync(Command() with { RootRunId = "other-run" }, TestContext.Current.CancellationToken));
        Assert.Empty(publisher.Published);
        Assert.False(signals.IsStopped(runId));
    }

    [Fact]
    public async Task ChildTerminalDoesNotMakeTheRootRunTerminal()
    {
        NaradPersistedEvent started = RuntimeEvent(1, NaradEventTypes.RunStarted, new { trigger = "chat" });
        NaradPersistedEvent childTerminal = RuntimeEvent(
            2,
            NaradEventTypes.RunFinished,
            new { result = "child done" },
            runId: "child-run",
            sessionId: "child-thread");
        var history = new MutableHistory(started, childTerminal);
        var signals = new RecordingStopRegistry();
        var publisher = new RecordingPublisher(history, () => signals.IsStopped(runId));
        var service = new RunStopCommandService(history, publisher, signals);

        Assert.Equal(
            RunStopCommandStatus.Accepted,
            await service.RequestStopAsync(Command(), TestContext.Current.CancellationToken));
        Assert.True(signals.IsStopped(runId));
    }

    private static RunStopCommand Command()
        => new("agent-1", threadId, runId, "user-1", "Admin User", "chat");

    private sealed class MutableHistory(params NaradPersistedEvent[] events) : IRuntimeEventHistoryReader
    {
        private readonly List<NaradPersistedEvent> events = [.. events];

        public void Append(NaradPersistedEvent persistedEvent) => events.Add(persistedEvent);

        public IReadOnlyList<NaradPersistedEvent> ReadRootHistory(string rootRunId)
            => events.Where(item => string.Equals(item.RootRunId, rootRunId, StringComparison.Ordinal)).ToArray();

        public IReadOnlyList<NaradPersistedEvent> ReadSessionHistory(string rootAgentId, string sessionId)
        {
            _ = rootAgentId;
            return events.Where(item => string.Equals(item.SessionId, sessionId, StringComparison.Ordinal)).ToArray();
        }
    }

    private static NaradPersistedEvent RuntimeEvent(
        long sequence,
        string eventType,
        object payload,
        string runId = RunStopCommandServiceTests.runId,
        string sessionId = threadId)
    {
        string eventId = $"event-{sequence}";
        return NaradTestEventFactory.FromBody(
            eventType,
            JsonSerializer.SerializeToElement(payload),
            sessionId,
            runId,
            RunStopCommandServiceTests.runId,
            sequence,
            sequence);
    }

    private sealed class RecordingPublisher(MutableHistory history, Func<bool> signalState) : IRuntimeEventPublisher
    {
        public List<NaradPersistedEvent> Published { get; } = [];
        public bool SignalWasSetDuringPublish { get; private set; }

        public ValueTask<NaradPersistedEvent> PublishAsync(
            NaradAuthorityEventDraft draft,
            NaradEventContext context,
            CancellationToken cancellationToken = default)
        {
            SignalWasSetDuringPublish = signalState();
            NaradPersistedEvent persistedEvent = NaradTestEventFactory.FromDraft(
                draft,
                context,
                history.ReadSessionHistory(context.AgentId, context.SessionId).Count + Published.Count + 1,
                history.ReadRootHistory(context.RootRunId).Count + 1);
            history.Append(persistedEvent);
            Published.Add(persistedEvent);
            return ValueTask.FromResult(persistedEvent);
        }

        public ValueTask<NaradPersistedEvent> PublishWithIdAsync(
            string eventId,
            string eventType,
            JsonElement body,
            NaradEventContext context,
            CancellationToken cancellationToken = default)
            => TestRuntimeEventPublisherSupport.PublishWithIdAsync(PublishAsync, eventId, eventType, body, context, cancellationToken);
    }

    private sealed class ValidatingRecordingPublisher(MutableHistory history, Func<bool> signalState) : IRuntimeEventPublisher
    {
        private readonly RecordingPublisher inner = new(history, signalState);

        public List<NaradPersistedEvent> Published => inner.Published;

        public ValueTask<NaradPersistedEvent> PublishAsync(
            NaradAuthorityEventDraft draft,
            NaradEventContext context,
            CancellationToken cancellationToken = default)
        {
            NaradJournalValidation.ValidateAuthorityDraft(draft);
            return inner.PublishAsync(draft, context, cancellationToken);
        }

        public ValueTask<NaradPersistedEvent> PublishWithIdAsync(
            string eventId,
            string eventType,
            JsonElement body,
            NaradEventContext context,
            CancellationToken cancellationToken = default)
            => TestRuntimeEventPublisherSupport.PublishWithIdAsync(PublishAsync, eventId, eventType, body, context, cancellationToken);
    }

    private const string threadId = "thread-1";
    private const string runId = "run-1";

    private sealed class FailingPublisher : IRuntimeEventPublisher
    {
        public ValueTask<NaradPersistedEvent> PublishAsync(
            NaradAuthorityEventDraft draft,
            NaradEventContext context,
            CancellationToken cancellationToken = default)
            => ValueTask.FromException<NaradPersistedEvent>(new InvalidOperationException("persistence failed"));

        public ValueTask<NaradPersistedEvent> PublishWithIdAsync(
            string eventId,
            string eventType,
            JsonElement body,
            NaradEventContext context,
            CancellationToken cancellationToken = default)
            => TestRuntimeEventPublisherSupport.PublishWithIdAsync(PublishAsync, eventId, eventType, body, context, cancellationToken);
    }

    private sealed class RecordingStopRegistry : IRunStopRegistry
    {
        private readonly HashSet<string> stopped = new(StringComparer.Ordinal);

        public void RegisterStop(string runId) => stopped.Add(runId);
        public CancellationToken GetStopToken(string runId) => CancellationToken.None;
        public bool IsStopped(string runId) => stopped.Contains(runId);
        public void Clear(string runId) => stopped.Remove(runId);
    }
}
