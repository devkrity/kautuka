using System.Globalization;
using System.Text.Json;

using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;

namespace Kautuka.IntegrationTests;

public sealed class NaradJournalPersistenceTests
{
    [Fact]
    public void FreshAgentStoreUsesNaradSchemaMarker()
    {
        using NaradJournalTestDatabase temp = NaradJournalTestDatabase.Create();
        temp.EnsureReady();
        temp.InsertAgent("agent-1", "agent-1");
        var store = new SqliteAgentDefinitionStore(temp.Configuration, new KautukaDatabase(temp.Configuration));
        store.AppendNaradAuthorityEvent(
            "agent-1",
            "agent-1",
            Draft("evt-schema", "session-schema", rootRunId: "run-schema", runId: "run-schema"));

        using SqliteConnection connection = temp.OpenAgentStore("agent-1");
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT schema_id FROM agent_store_schema;";
        Assert.Equal("agent-store-2026-09-21.1", command.ExecuteScalar()?.ToString());
    }

    [Fact]
    public void IncompatibleAgentStoreSchemaIsRejected()
    {
        using NaradJournalTestDatabase temp = NaradJournalTestDatabase.Create();
        temp.EnsureReady();
        temp.InsertAgent("agent-legacy", "agent-legacy");
        var store = new SqliteAgentDefinitionStore(temp.Configuration, new KautukaDatabase(temp.Configuration));
        store.AppendNaradAuthorityEvent(
            "agent-legacy",
            "agent-legacy",
            Draft("evt-bootstrap", "session-bootstrap", rootRunId: "run-bootstrap", runId: "run-bootstrap"));
        temp.WriteLegacySchemaMarker("agent-legacy");
        temp.ForgetAgentStoreSchemaCache("agent-legacy");

        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(() =>
            store.AppendNaradAuthorityEvent("agent-legacy", "agent-legacy", Draft("evt-legacy", "session-legacy")));
        Assert.Contains("schema marker is incompatible", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AppendAllocatesSessionAndRootSequencesAndOutboxAtomically()
    {
        using NaradJournalTestDatabase temp = NaradJournalTestDatabase.Create();
        temp.EnsureReady();
        temp.InsertAgent("agent-1", "agent-1");
        var store = CreateStore(temp);

        NaradPersistedEvent first = store.AppendNaradAuthorityEvent(
            "agent-1",
            "agent-1",
            Draft("evt-1", "session-1", rootRunId: "run-1", runId: "run-1", body: Body("first")));
        NaradPersistedEvent second = store.AppendNaradAuthorityEvent(
            "agent-1",
            "agent-1",
            Draft("evt-2", "session-1", rootRunId: "run-1", runId: "run-1", body: Body("second")));

        Assert.Equal([1L, 2L], store.ListNaradSessionHistory("agent-1", "session-1").Select(static evt => evt.SessionSequence));
        Assert.Equal([1L, 2L], store.ListNaradRootHistory("agent-1", "run-1").Select(static evt => evt.Sequence!.Value));
        Assert.Equal(2, temp.ReadOutboxCount("agent-1"));
        Assert.Contains("\"sessionSequence\":1", first.EventJson, StringComparison.Ordinal);
        Assert.Contains("\"sequence\":2", second.EventJson, StringComparison.Ordinal);
    }

    [Fact]
    public void OutboxDiscoverySkipsExcludedStorageLocatorKeys()
    {
        using NaradJournalTestDatabase temp = NaradJournalTestDatabase.Create();
        temp.EnsureReady();
        temp.InsertAgent("agent-1", "agent-1");
        var store = CreateStore(temp);
        store.AppendNaradAuthorityEvent(
            "agent-1",
            "agent-1",
            Draft("evt-1", "session-1", rootRunId: "run-1", runId: "run-1"));

        Assert.Equal(["agent-1"], store.ListNaradEventOutboxStorageLocatorKeys());
        Assert.Empty(store.ListNaradEventOutboxStorageLocatorKeys(new HashSet<string>(StringComparer.Ordinal) { "agent-1" }));
    }

    [Fact]
    public void PreviousJournalTableIsMovedToCurrentName()
    {
        using NaradJournalTestDatabase temp = NaradJournalTestDatabase.Create();
        temp.EnsureReady();
        temp.InsertAgent("agent-1", "agent-1");
        var store = CreateStore(temp);
        store.AppendNaradAuthorityEvent(
            "agent-1",
            "agent-1",
            Draft("evt-keep", "session-keep", rootRunId: "run-keep", runId: "run-keep", body: Body("keep")));

        temp.RenameJournalToPreviousName("agent-1");
        temp.ForgetAgentStoreSchemaCache("agent-1");

        NaradPersistedEvent next = store.AppendNaradAuthorityEvent(
            "agent-1",
            "agent-1",
            Draft("evt-after", "session-keep", rootRunId: "run-keep", runId: "run-keep", body: Body("after")));

        Assert.Equal(
            ["evt-keep", "evt-after"],
            store.ListNaradSessionHistory("agent-1", "session-keep").Select(static evt => evt.EventId));
        Assert.Equal("evt-after", next.EventId);
    }

    [Fact]
    public void AgentStoreUpgradesFrom202608071CreatesToolSurfaceTable()
    {
        using NaradJournalTestDatabase temp = NaradJournalTestDatabase.Create();
        temp.EnsureReady();
        temp.InsertAgent("agent-1", "agent-1");
        var store = CreateStore(temp);
        store.AppendNaradAuthorityEvent(
            "agent-1",
            "agent-1",
            Draft("evt-keep", "session-keep", rootRunId: "run-keep", runId: "run-keep", body: Body("keep")));

        temp.DowngradeToolSurfaceTo202608071("agent-1");
        temp.ForgetAgentStoreSchemaCache("agent-1");

        NaradPersistedEvent next = store.AppendNaradAuthorityEvent(
            "agent-1",
            "agent-1",
            Draft("evt-after-surface", "session-keep", rootRunId: "run-keep", runId: "run-keep", body: Body("after")));

        Assert.Equal("evt-after-surface", next.EventId);
        using SqliteConnection connection = temp.OpenAgentStore("agent-1");
        using SqliteCommand marker = connection.CreateCommand();
        marker.CommandText = "SELECT schema_id FROM agent_store_schema;";
        Assert.Equal("agent-store-2026-09-21.1", marker.ExecuteScalar()?.ToString());
        using SqliteCommand surface = connection.CreateCommand();
        surface.CommandText = """
            SELECT COUNT(*) FROM sqlite_schema
            WHERE type = 'table'
              AND name = 'agent_tool_surface_current';
            """;
        Assert.Equal(1L, Convert.ToInt64(surface.ExecuteScalar(), CultureInfo.InvariantCulture));
    }

    [Fact]
    public void ParentSessionProjectionAllocatesParentSequenceWithoutDuplicatingRootHistory()
    {
        using NaradJournalTestDatabase temp = NaradJournalTestDatabase.Create();
        temp.EnsureReady();
        temp.InsertAgent("agent-1", "agent-1");
        var store = CreateStore(temp);

        NaradPersistedEvent child = store.AppendNaradAuthorityEvent(
            "agent-1",
            "agent-1",
            Draft(
                "evt-child-1",
                "session-child",
                rootRunId: "run-root",
                runId: "run-child",
                body: Body("child"),
                eventType: "run.started") with
            {
                ParentSessionId = "session-parent",
                ParentRunId = "run-root",
                ParentToolCallId = "tool-invoke",
            });

        Assert.Contains("\"parentSessionId\":\"session-parent\"", child.EventJson, StringComparison.Ordinal);

        NaradPersistedEvent projection = store.AppendNaradParentSessionProjection(
            "agent-1",
            child,
            "session-parent");

        Assert.Equal("session-parent", projection.SessionId);
        Assert.Equal(1L, projection.SessionSequence);
        Assert.Null(projection.Sequence);
        Assert.Null(projection.RunId);
        Assert.Null(projection.RootRunId);
        Assert.Contains("\"sourceEventId\":\"evt-child-1\"", projection.EventJson, StringComparison.Ordinal);
        Assert.Contains("\"sourceSessionId\":\"session-child\"", projection.EventJson, StringComparison.Ordinal);
        Assert.Contains("\"runId\":\"run-child\"", projection.EventJson, StringComparison.Ordinal);
        Assert.Contains("\"rootRunId\":\"run-root\"", projection.EventJson, StringComparison.Ordinal);
        Assert.DoesNotContain("\"sequence\"", projection.EventJson, StringComparison.Ordinal);
        Assert.DoesNotContain("\"parentSessionId\"", projection.EventJson, StringComparison.Ordinal);

        Assert.Equal(["evt-child-1"], store.ListNaradRootHistory("agent-1", "run-root").Select(static evt => evt.EventId));
        Assert.Equal(["aaatproj:evt-child-1"], store.ListNaradSessionHistory("agent-1", "session-parent").Select(static evt => evt.EventId));

        NaradPersistedEvent again = store.AppendNaradParentSessionProjection("agent-1", child, "session-parent");
        Assert.Equal(projection.EventId, again.EventId);
        Assert.Single(store.ListNaradSessionHistory("agent-1", "session-parent"));
    }

    [Fact]
    public void AppendRejectsRootRunIdWithoutRunId()
    {
        using NaradJournalTestDatabase temp = NaradJournalTestDatabase.Create();
        temp.EnsureReady();
        temp.InsertAgent("agent-1", "agent-1");
        var store = CreateStore(temp);

        ArgumentException exception = Assert.Throws<ArgumentException>(() => store.AppendNaradAuthorityEvent(
            "agent-1",
            "agent-1",
            Draft("evt-root-only", "session-root-only", rootRunId: "run-root-only")));

        Assert.Contains("rootRunId and runId together", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(NaradEventTypes.GraphSnapshot)]
    [InlineData(NaradEventTypes.GraphDelta)]
    public void AppendRejectsHydrateDerivedGraphEventTypes(string eventType)
    {
        ArgumentNullException.ThrowIfNull(eventType);
        using NaradJournalTestDatabase temp = NaradJournalTestDatabase.Create();
        temp.EnsureReady();
        temp.InsertAgent("agent-1", "agent-1");
        var store = CreateStore(temp);

        ArgumentException exception = Assert.Throws<ArgumentException>(() => store.AppendNaradAuthorityEvent(
            "agent-1",
            "agent-1",
            Draft(
                $"evt-{eventType.Replace('.', '-')}",
                "session-graph-reject",
                eventType: eventType,
                rootRunId: "run-graph-reject",
                runId: "run-graph-reject")));

        Assert.Contains("hydrate-derived", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(NaradEventTypes.GraphSnapshot)]
    [InlineData(NaradEventTypes.GraphDelta)]
    public void MirrorRejectsHydrateDerivedGraphEventTypes(string eventType)
    {
        ArgumentNullException.ThrowIfNull(eventType);
        using NaradJournalTestDatabase temp = NaradJournalTestDatabase.Create();
        temp.EnsureReady();
        temp.InsertAgent("agent-1", "agent-1");
        var store = CreateStore(temp);

        InvalidDataException exception = Assert.Throws<InvalidDataException>(() => store.MirrorNaradAuthorityEvent(
            "agent-1",
            MirroredEvent(
                $"evt-mirror-{eventType.Replace('.', '-')}",
                eventType,
                "session-graph-mirror-reject",
                sessionSequence: 1,
                rootRunId: "run-graph-mirror-reject",
                runId: "run-graph-mirror-reject",
                rootSequence: 1)));

        Assert.Contains("hydrate-derived", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AppendRejectsRunIdWithoutRootRunId()
    {
        using NaradJournalTestDatabase temp = NaradJournalTestDatabase.Create();
        temp.EnsureReady();
        temp.InsertAgent("agent-1", "agent-1");
        var store = CreateStore(temp);

        ArgumentException exception = Assert.Throws<ArgumentException>(() => store.AppendNaradAuthorityEvent(
            "agent-1",
            "agent-1",
            Draft("evt-run-only", "session-run-only", runId: "run-only")));

        Assert.Contains("rootRunId and runId together", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void FailedAppendAfterAllocationDoesNotConsumeSequenceCounters()
    {
        using NaradJournalTestDatabase temp = NaradJournalTestDatabase.Create();
        temp.EnsureReady();
        temp.InsertAgent("agent-1", "agent-1");
        var store = CreateStore(temp);

        Assert.Throws<InvalidOperationException>(() => store.AppendNaradAuthorityEvent(
            "agent-1",
            "agent-1",
            Draft(
                "evt-oversize",
                "session-rollback",
                rootRunId: "run-rollback",
                runId: "run-rollback",
                body: OversizeBody())));

        NaradPersistedEvent persisted = store.AppendNaradAuthorityEvent(
            "agent-1",
            "agent-1",
            Draft("evt-rollback", "session-rollback", rootRunId: "run-rollback", runId: "run-rollback"));
        Assert.Equal(1L, persisted.SessionSequence);
        Assert.Equal(1L, persisted.Sequence);
    }

    [Fact]
    public async Task ConcurrentAppendsProduceUniqueOrderedSequences()
    {
        using NaradJournalTestDatabase temp = NaradJournalTestDatabase.Create();
        temp.EnsureReady();
        temp.InsertAgent("agent-1", "agent-1");
        var store = CreateStore(temp);
        const int eventCount = 24;

        Task<NaradPersistedEvent>[] appends = Enumerable.Range(0, eventCount)
            .Select(index => Task.Run(() => store.AppendNaradAuthorityEvent(
                "agent-1",
                "agent-1",
                Draft(
                    $"evt-{index}",
                    "session-concurrent",
                    rootRunId: "run-concurrent",
                    runId: "run-concurrent",
                    body: Body(index.ToString(CultureInfo.InvariantCulture))))))
            .ToArray();

        await Task.WhenAll(appends);

        IReadOnlyList<NaradPersistedEvent> sessionHistory = store.ListNaradSessionHistory("agent-1", "session-concurrent");
        IReadOnlyList<NaradPersistedEvent> rootHistory = store.ListNaradRootHistory("agent-1", "run-concurrent");
        Assert.Equal(eventCount, sessionHistory.Count);
        Assert.Equal(eventCount, rootHistory.Count);
        Assert.Equal(
            Enumerable.Range(1, eventCount).Select(static value => (long)value),
            sessionHistory.Select(static evt => evt.SessionSequence));
        Assert.Equal(
            Enumerable.Range(1, eventCount).Select(static value => (long)value),
            rootHistory.Select(static evt => evt.Sequence!.Value));
    }

    [Fact]
    public void MirrorAdvancesSequenceCountersForSessionRootAndWorkflowScopes()
    {
        using NaradJournalTestDatabase temp = NaradJournalTestDatabase.Create();
        temp.EnsureReady();
        temp.InsertAgent("agent-1", "agent-1");
        var store = CreateStore(temp);

        store.MirrorNaradAuthorityEvent("agent-1", MirroredEvent(
            "evt-mirror-session",
            "session.revision",
            "session-mirror",
            sessionSequence: 4));
        store.MirrorNaradAuthorityEvent("agent-1", MirroredEvent(
            "evt-mirror-workflow",
            "workflow.started",
            "session-mirror",
            sessionSequence: 5,
            workflowId: "workflow-mirror",
            workflowSequence: 3));
        store.MirrorNaradAuthorityEvent("agent-1", MirroredEvent(
            "evt-mirror-root",
            "run.started",
            "session-mirror",
            sessionSequence: 6,
            rootRunId: "run-mirror",
            runId: "run-mirror",
            rootSequence: 8));

        NaradPersistedEvent sessionAppend = store.AppendNaradAuthorityEvent(
            "agent-1",
            "agent-1",
            Draft("evt-local-session", "session-mirror"));
        NaradPersistedEvent workflowAppend = store.AppendNaradAuthorityEvent(
            "agent-1",
            "agent-1",
            Draft("evt-local-workflow", "session-mirror", workflowId: "workflow-mirror"));
        NaradPersistedEvent rootAppend = store.AppendNaradAuthorityEvent(
            "agent-1",
            "agent-1",
            Draft("evt-local-root", "session-mirror", rootRunId: "run-mirror", runId: "run-mirror"));

        Assert.Equal(7L, sessionAppend.SessionSequence);
        Assert.Equal(4L, workflowAppend.WorkflowSequence);
        Assert.Equal(9L, rootAppend.Sequence);
    }

    [Fact]
    public void NaradOutboxListAndClaimReturnTypedJournalEvents()
    {
        using NaradJournalTestDatabase temp = NaradJournalTestDatabase.Create();
        temp.EnsureReady();
        temp.InsertAgent("agent-1", "agent-1");
        var store = CreateStore(temp);
        store.AppendNaradAuthorityEvent(
            "agent-1",
            "agent-1",
            Draft("evt-outbox", "session-outbox", rootRunId: "run-outbox", runId: "run-outbox"));

        IReadOnlyList<NaradRuntimeEventOutboxEntry> pending = store.ListPendingNaradEventOutbox("agent-1", limit: 10);
        Assert.Single(pending);
        Assert.Equal("evt-outbox", pending[0].Event.EventId);
        Assert.Equal("run.started", pending[0].Event.EventType);

        IReadOnlyList<NaradRuntimeEventOutboxEntry> claimed = store.ClaimNaradEventOutbox(
            "agent-1",
            leaseOwner: "worker-1",
            leaseDuration: TimeSpan.FromMinutes(1),
            limit: 1);
        Assert.Single(claimed);
        Assert.Equal("worker-1", claimed[0].LeaseOwner);
        Assert.False(claimed[0].IsPublished);
    }

    [Fact]
    public void EventIdReplayWithIdenticalCanonicalDataIsIdempotent()
    {
        using NaradJournalTestDatabase temp = NaradJournalTestDatabase.Create();
        temp.EnsureReady();
        temp.InsertAgent("agent-1", "agent-1");
        var store = CreateStore(temp);
        NaradAuthorityEventDraft draft = Draft("evt-idempotent", "session-idempotent", rootRunId: "run-idempotent", runId: "run-idempotent");

        NaradPersistedEvent first = store.AppendNaradAuthorityEvent("agent-1", "agent-1", draft);
        NaradPersistedEvent second = store.AppendNaradAuthorityEvent("agent-1", "agent-1", draft);

        Assert.Equal(first.EventJson, second.EventJson);
        Assert.Single(store.ListNaradSessionHistory("agent-1", "session-idempotent"));
        Assert.Equal(1, temp.ReadOutboxCount("agent-1"));
    }

    [Fact]
    public void EventIdReplayWithDifferentCanonicalDataFailsClosed()
    {
        using NaradJournalTestDatabase temp = NaradJournalTestDatabase.Create();
        temp.EnsureReady();
        temp.InsertAgent("agent-1", "agent-1");
        var store = CreateStore(temp);
        store.AppendNaradAuthorityEvent(
            "agent-1",
            "agent-1",
            Draft("evt-conflict", "session-conflict", rootRunId: "run-conflict", runId: "run-conflict", body: Body("first")));

        InvalidDataException exception = Assert.Throws<InvalidDataException>(() => store.AppendNaradAuthorityEvent(
            "agent-1",
            "agent-1",
            Draft("evt-conflict", "session-conflict", rootRunId: "run-conflict", runId: "run-conflict", body: Body("changed"))));
        Assert.Contains("different canonical data", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AuthorityAppendRejectsControlWatchAndHydrateRecordClasses()
    {
        using NaradJournalTestDatabase temp = NaradJournalTestDatabase.Create();
        temp.EnsureReady();
        temp.InsertAgent("agent-1", "agent-1");
        var store = CreateStore(temp);

        Assert.Throws<NotSupportedException>(() => store.AppendNaradAuthorityEvent(
            "agent-1",
            "agent-1",
            Draft("evt-control", "session-control", recordClass: NaradRecordClasses.Control)));
        Assert.Throws<NotSupportedException>(() => store.AppendNaradAuthorityEvent(
            "agent-1",
            "agent-1",
            Draft("evt-watch", "session-control", recordClass: NaradRecordClasses.Watch)));
        Assert.Throws<NotSupportedException>(() => store.AppendNaradAuthorityEvent(
            "agent-1",
            "agent-1",
            Draft("evt-hydrate", "session-control", recordClass: NaradRecordClasses.Hydrate)));
    }

    [Fact]
    public void MirrorRejectsNonAuthorityRecordClasses()
    {
        using NaradJournalTestDatabase temp = NaradJournalTestDatabase.Create();
        temp.EnsureReady();
        temp.InsertAgent("agent-1", "agent-1");
        var store = CreateStore(temp);

        NotSupportedException exception = Assert.Throws<NotSupportedException>(() => store.MirrorNaradAuthorityEvent(
            "agent-1",
            MirroredEvent("evt-watch", "session.revision", "session-watch", recordClass: NaradRecordClasses.Watch)));
        Assert.Contains("not accepted", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void DatabaseRejectsNonAuthorityJournalRows()
    {
        using NaradJournalTestDatabase temp = NaradJournalTestDatabase.Create();
        temp.EnsureReady();
        temp.InsertAgent("agent-1", "agent-1");
        CreateStore(temp).AppendNaradAuthorityEvent(
            "agent-1",
            "agent-1",
            Draft("evt-bootstrap", "session-db", rootRunId: "run-db", runId: "run-db"));

        Assert.Throws<SqliteException>(() => temp.ExecuteAgentStoreSql(
            "agent-1",
            """
            INSERT INTO narad_runtime_journal
                (event_id, protocol_version, event_type, session_id, session_sequence, record_class, event_json, occurred_at)
            VALUES ('evt-watch-db', 'narad/v1', 'session.revision', 'session-db', 99, 'watch', '{}', '2026-08-05T00:00:00.0000000+00:00');
            """));
    }

    [Fact]
    public void ReservedEnvelopeKeysInDraftBodyAreRejected()
    {
        using NaradJournalTestDatabase temp = NaradJournalTestDatabase.Create();
        temp.EnsureReady();
        temp.InsertAgent("agent-1", "agent-1");
        var store = CreateStore(temp);

        ArgumentException exception = Assert.Throws<ArgumentException>(() => store.AppendNaradAuthorityEvent(
            "agent-1",
            "agent-1",
            Draft("evt-reserved", "session-reserved", body: ReservedKeyBody("sessionId"))));
        Assert.Contains("reserved envelope key", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void MirrorRejectsJsonColumnMismatch()
    {
        using NaradJournalTestDatabase temp = NaradJournalTestDatabase.Create();
        temp.EnsureReady();
        temp.InsertAgent("agent-1", "agent-1");
        var store = CreateStore(temp);

        NaradPersistedEvent mismatched = MirroredEvent("evt-json-mismatch", "session.revision", "session-json");
        mismatched = mismatched with
        {
            EventJson = """{"type":"session.revision","protocolVersion":"narad/v1","eventId":"evt-json-mismatch","timestamp":"2026-08-05T00:00:00.0000000+00:00","sessionId":"session-json","sessionSequence":999}""",
        };

        InvalidDataException exception = Assert.Throws<InvalidDataException>(() =>
            store.MirrorNaradAuthorityEvent("agent-1", mismatched));
        Assert.Contains("does not match indexed envelope column", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void InvalidEventTypeAndTimestampAreRejected()
    {
        using NaradJournalTestDatabase temp = NaradJournalTestDatabase.Create();
        temp.EnsureReady();
        temp.InsertAgent("agent-1", "agent-1");
        var store = CreateStore(temp);

        Assert.Throws<ArgumentException>(() => store.AppendNaradAuthorityEvent(
            "agent-1",
            "agent-1",
            Draft("evt-bad-type", "session-validation", eventType: "RunStarted")));
        Assert.Throws<ArgumentException>(() => store.AppendNaradAuthorityEvent(
            "agent-1",
            "agent-1",
            Draft("evt-bad-ts", "session-validation", occurredAt: "2026-08-05T00:00:00Z")));
    }

    [Fact]
    public void HistoryLimitAllowsPeekPastPublicPageSize()
    {
        NaradJournalValidation.ValidateHistoryLimit(NaradJournalValidation.MaxHistoryLimit);
        NaradJournalValidation.ValidateHistoryLimit(NaradJournalValidation.MaxHistoryPeekLimit);
        Assert.Throws<ArgumentOutOfRangeException>(() => NaradJournalValidation.ValidateHistoryLimit(0));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            NaradJournalValidation.ValidateHistoryLimit(NaradJournalValidation.MaxHistoryPeekLimit + 1));
    }

    [Fact]
    public void BoundedSessionHistoryReturnsContiguousWindow()
    {
        using NaradJournalTestDatabase temp = NaradJournalTestDatabase.Create();
        temp.EnsureReady();
        temp.InsertAgent("agent-1", "agent-1");
        var store = CreateStore(temp);
        for (int index = 1; index <= 5; index++)
        {
            store.AppendNaradAuthorityEvent(
                "agent-1",
                "agent-1",
                Draft($"evt-page-{index}", "session-page", rootRunId: "run-page", runId: "run-page"));
        }

        IReadOnlyList<NaradPersistedEvent> window = store.ListNaradSessionHistory("agent-1", "session-page", afterSessionSequence: 2, limit: 2);
        Assert.Equal([3L, 4L], window.Select(static evt => evt.SessionSequence));
        Assert.Empty(store.ListNaradSessionHistory("agent-1", "session-page", afterSessionSequence: 5, limit: 10));
        Assert.Equal(5, store.ListNaradSessionHistory(
            "agent-1",
            "session-page",
            afterSessionSequence: 0,
            limit: NaradJournalValidation.MaxHistoryPeekLimit).Count);
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            store.ListNaradSessionHistory("agent-1", "session-page", afterSessionSequence: 0, limit: 0));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            store.ListNaradSessionHistory(
                "agent-1",
                "session-page",
                afterSessionSequence: 0,
                limit: NaradJournalValidation.MaxHistoryPeekLimit + 1));
    }

    [Fact]
    public void WorkflowOrderingIsUniquePerWorkflow()
    {
        using NaradJournalTestDatabase temp = NaradJournalTestDatabase.Create();
        temp.EnsureReady();
        temp.InsertAgent("agent-1", "agent-1");
        var store = CreateStore(temp);

        NaradPersistedEvent started = store.AppendNaradAuthorityEvent(
            "agent-1",
            "agent-1",
            Draft("evt-workflow-1", "session-workflow", workflowId: "workflow-1", body: Body("started")));
        NaradPersistedEvent runStarted = store.AppendNaradAuthorityEvent(
            "agent-1",
            "agent-1",
            Draft(
                "evt-workflow-2",
                "session-workflow",
                workflowId: "workflow-1",
                rootRunId: "run-workflow",
                runId: "run-workflow",
                body: Body("run")));

        Assert.Equal(1L, started.WorkflowSequence);
        Assert.Equal(2L, runStarted.WorkflowSequence);
        Assert.Equal(2L, runStarted.SessionSequence);
    }

    [Fact]
    public void SessionHistoryRejectsWorkflowGaps()
    {
        using NaradJournalTestDatabase temp = NaradJournalTestDatabase.Create();
        temp.EnsureReady();
        temp.InsertAgent("agent-1", "agent-1");
        var store = CreateStore(temp);
        store.AppendNaradAuthorityEvent(
            "agent-1",
            "agent-1",
            Draft("evt-workflow-gap-1", "session-workflow-gap", workflowId: "workflow-gap"));

        temp.ExecuteAgentStoreSql(
            "agent-1",
            """
            INSERT INTO narad_runtime_journal
                (event_id, protocol_version, event_type, session_id, session_sequence,
                 workflow_id, workflow_sequence, record_class, event_json, occurred_at)
            VALUES
                ('evt-workflow-gap-3', 'narad/v1', 'session.revision', 'session-workflow-gap', 2,
                 'workflow-gap', 3, 'authority',
                 '{"type":"session.revision","protocolVersion":"narad/v1","eventId":"evt-workflow-gap-3","timestamp":"2026-08-05T00:00:02.0000000+00:00","sessionId":"session-workflow-gap","sessionSequence":2,"workflowId":"workflow-gap","workflowSequence":3}',
                 '2026-08-05T00:00:02.0000000+00:00');
            """);

        InvalidDataException exception = Assert.Throws<InvalidDataException>(() =>
            store.ListNaradSessionHistoryFull("agent-1", "session-workflow-gap"));
        Assert.Contains("workflowSequence", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void SessionAndRootHistoryRejectGapsAndCorruption()
    {
        using NaradJournalTestDatabase temp = NaradJournalTestDatabase.Create();
        temp.EnsureReady();
        temp.InsertAgent("agent-1", "agent-1");
        var store = CreateStore(temp);
        store.AppendNaradAuthorityEvent(
            "agent-1",
            "agent-1",
            Draft("evt-gap-1", "session-gap", rootRunId: "run-gap", runId: "run-gap"));

        temp.ExecuteAgentStoreSql(
            "agent-1",
            """
            INSERT INTO narad_runtime_journal
                (event_id, protocol_version, event_type, session_id, session_sequence,
                 root_run_id, run_id, sequence, record_class, event_json, occurred_at)
            VALUES
                ('evt-gap-3', 'narad/v1', 'run.finished', 'session-gap', 3,
                 'run-gap', 'run-gap', 3, 'authority',
                 '{"type":"run.finished","protocolVersion":"narad/v1","eventId":"evt-gap-3","timestamp":"2026-08-05T00:00:03.0000000+00:00","sessionId":"session-gap","sessionSequence":3,"rootRunId":"run-gap","runId":"run-gap","sequence":3}',
                 '2026-08-05T00:00:03.0000000+00:00');
            """);

        InvalidDataException sessionException = Assert.Throws<InvalidDataException>(() =>
            store.ListNaradSessionHistoryFull("agent-1", "session-gap"));
        Assert.Contains("gap or duplicate", sessionException.Message, StringComparison.Ordinal);

        temp.ExecuteAgentStoreSql("agent-1", "DELETE FROM narad_runtime_journal WHERE event_id = 'evt-gap-3';");
        temp.ExecuteAgentStoreSql(
            "agent-1",
            """
            INSERT INTO narad_runtime_journal
                (event_id, protocol_version, event_type, session_id, session_sequence,
                 root_run_id, run_id, sequence, record_class, event_json, occurred_at)
            VALUES
                ('evt-root-gap', 'narad/v1', 'run.finished', 'session-gap', 2,
                 'run-gap', 'run-gap', 3, 'authority',
                 '{"type":"run.finished","protocolVersion":"narad/v1","eventId":"evt-root-gap","timestamp":"2026-08-05T00:00:02.0000000+00:00","sessionId":"session-gap","sessionSequence":2,"rootRunId":"run-gap","runId":"run-gap","sequence":3}',
                 '2026-08-05T00:00:02.0000000+00:00');
            """);

        InvalidDataException rootException = Assert.Throws<InvalidDataException>(() =>
            store.ListNaradRootHistoryFull("agent-1", "run-gap"));
        Assert.Contains("gap or duplicate", rootException.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(NaradEventTypes.RunFinished)]
    [InlineData(NaradEventTypes.RunError)]
    [InlineData(NaradEventTypes.RunCancelled)]
    public void IsNaradRootRunAdmissionClosedReturnsTrueForNaradTerminalLifecycleEvents(string terminalEventType)
    {
        ArgumentNullException.ThrowIfNull(terminalEventType);
        using NaradJournalTestDatabase temp = NaradJournalTestDatabase.Create();
        temp.EnsureReady();
        temp.InsertAgent("agent-1", "agent-1");
        var store = CreateStore(temp);
        string rootRunId = $"run-{terminalEventType.Replace('.', '-')}";
        store.AppendNaradAuthorityEvent(
            "agent-1",
            "agent-1",
            Draft("evt-started", "session-admission", rootRunId: rootRunId, runId: rootRunId));
        store.AppendNaradAuthorityEvent(
            "agent-1",
            "agent-1",
            Draft("evt-terminal", "session-admission", eventType: terminalEventType, rootRunId: rootRunId, runId: rootRunId));

        Assert.True(store.IsNaradRootRunAdmissionClosed("agent-1", rootRunId));
    }

    [Theory]
    [InlineData("run.started")]
    [InlineData("run.paused")]
    [InlineData("session.revision")]
    public void IsNaradRootRunAdmissionClosedReturnsFalseForNonTerminalLifecycleEvents(string nonTerminalEventType)
    {
        ArgumentNullException.ThrowIfNull(nonTerminalEventType);
        using NaradJournalTestDatabase temp = NaradJournalTestDatabase.Create();
        temp.EnsureReady();
        temp.InsertAgent("agent-1", "agent-1");
        var store = CreateStore(temp);
        string rootRunId = $"run-{nonTerminalEventType.Replace('.', '-')}";
        store.AppendNaradAuthorityEvent(
            "agent-1",
            "agent-1",
            Draft("evt-latest", "session-admission", eventType: nonTerminalEventType, rootRunId: rootRunId, runId: rootRunId));

        Assert.False(store.IsNaradRootRunAdmissionClosed("agent-1", rootRunId));
    }

    [Fact]
    public void NaradSemanticQueriesExposeRevisionAdmissionAndRootListing()
    {
        using NaradJournalTestDatabase temp = NaradJournalTestDatabase.Create();
        temp.EnsureReady();
        temp.InsertAgent("agent-1", "agent-1");
        var store = CreateStore(temp);
        store.AppendNaradAuthorityEvent(
            "agent-1",
            "agent-1",
            Draft("evt-revision-1", "session-semantic", rootRunId: "run-semantic", runId: "run-semantic"));
        store.AppendNaradAuthorityEvent(
            "agent-1",
            "agent-1",
            Draft("evt-revision-2", "session-semantic", eventType: NaradEventTypes.RunFinished, rootRunId: "run-semantic", runId: "run-semantic"));

        Assert.Equal(2L, store.ReadNaradSessionProjectionRevision("agent-1", "session-semantic"));
        Assert.True(store.IsNaradRootRunAdmissionClosed("agent-1", "run-semantic"));
        Assert.Equal(["run-semantic"], store.ListNaradRootRunIdsForSession("agent-1", "session-semantic"));
        Assert.Equal("agent-1", store.FindNaradRootStorageLocatorKey("run-semantic"));
    }

    private static SqliteAgentDefinitionStore CreateStore(NaradJournalTestDatabase temp)
        => new(temp.Configuration, new KautukaDatabase(temp.Configuration));

    private static NaradAuthorityEventDraft Draft(
        string eventId,
        string sessionId,
        string recordClass = NaradRecordClasses.Authority,
        string? rootRunId = null,
        string? runId = null,
        string? workflowId = null,
        string? eventType = null,
        string? occurredAt = null,
        JsonElement? body = null)
        => new(
            eventId,
            eventType ?? (rootRunId is null ? "session.revision" : "run.started"),
            sessionId,
            body ?? Body(eventId),
            occurredAt ?? DateTimeOffset.UtcNow.ToString("O"),
            recordClass,
            NaradProtocolVersions.V1,
            rootRunId,
            runId,
            workflowId);

    private static NaradPersistedEvent MirroredEvent(
        string eventId,
        string eventType,
        string sessionId,
        long sessionSequence = 1,
        string recordClass = NaradRecordClasses.Authority,
        string? rootRunId = null,
        string? runId = null,
        long? rootSequence = null,
        string? workflowId = null,
        long? workflowSequence = null,
        JsonElement? body = null)
    {
        string occurredAt = DateTimeOffset.UtcNow.ToString("O");
        var envelope = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["type"] = eventType,
            ["protocolVersion"] = NaradProtocolVersions.V1,
            ["eventId"] = eventId,
            ["timestamp"] = occurredAt,
            ["sessionId"] = sessionId,
            ["sessionSequence"] = sessionSequence,
        };
        if (!string.IsNullOrWhiteSpace(workflowId) && workflowSequence.HasValue)
        {
            envelope["workflowId"] = workflowId;
            envelope["workflowSequence"] = workflowSequence.Value;
        }

        if (!string.IsNullOrWhiteSpace(rootRunId) && !string.IsNullOrWhiteSpace(runId) && rootSequence.HasValue)
        {
            envelope["rootRunId"] = rootRunId;
            envelope["runId"] = runId;
            envelope["sequence"] = rootSequence.Value;
        }

        if (body.HasValue && body.Value.ValueKind == JsonValueKind.Object)
        {
            foreach (JsonProperty property in body.Value.EnumerateObject())
            {
                envelope[property.Name] = JsonSerializer.Deserialize<object>(property.Value.GetRawText());
            }
        }

        string eventJson = JsonSerializer.Serialize(envelope);
        return new NaradPersistedEvent(
            eventId,
            NaradProtocolVersions.V1,
            eventType,
            sessionId,
            sessionSequence,
            recordClass,
            eventJson,
            occurredAt,
            rootRunId,
            runId,
            rootSequence,
            workflowId,
            workflowSequence);
    }

    private static JsonElement Body(string marker)
    {
        using JsonDocument document = JsonDocument.Parse($$"""{"marker":"{{marker}}"}""");
        return document.RootElement.Clone();
    }

    private static JsonElement ReservedKeyBody(string key)
    {
        using JsonDocument document = JsonDocument.Parse($$"""{"{{key}}":"conflict"}""");
        return document.RootElement.Clone();
    }

    private static JsonElement OversizeBody()
    {
        using JsonDocument document = JsonDocument.Parse($$"""{"payload":"{{new string('x', 256_050)}}"}""");
        return document.RootElement.Clone();
    }

    private sealed class NaradJournalTestDatabase : IDisposable
    {
        private readonly string directoryPath;

        private NaradJournalTestDatabase(string directoryPath, IConfiguration configuration)
        {
            this.directoryPath = directoryPath;
            Configuration = configuration;
        }

        public IConfiguration Configuration { get; }

        public static NaradJournalTestDatabase Create()
        {
            string directoryPath = Path.Combine(Path.GetTempPath(), "kautuka-narad-journal-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directoryPath);
            string ridFolder = OperatingSystem.IsWindows() ? "win-x64" : "linux-x64";
            string vecFileName = OperatingSystem.IsWindows() ? "vec0.dll" : "vec0.so";
            string sqliteVecPath = Path.GetFullPath(Path.Combine(
                AppContext.BaseDirectory,
                "..", "..", "..", "..", "..",
                "backend", "Kautuka.Gateway", "NativeAssets", "sqlite-vec", ridFolder, vecFileName));
            if (!File.Exists(sqliteVecPath))
            {
                sqliteVecPath = Path.GetFullPath(Path.Combine(
                    Directory.GetCurrentDirectory(),
                    "src", "backend", "Kautuka.Gateway", "NativeAssets", "sqlite-vec", ridFolder, vecFileName));
            }

            IConfiguration configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Kautuka:ContainerStateRoot"] = directoryPath,
                    ["Kautuka:SqliteVec:ExtensionPath"] = sqliteVecPath,
                })
                .Build();
            return new NaradJournalTestDatabase(directoryPath, configuration);
        }

        public void EnsureReady() => new KautukaDatabase(Configuration).EnsureReady();

        public void InsertAgent(string agentId, string storageLocatorKey)
        {
            using var connection = new SqliteConnection(new KautukaDatabase(Configuration).ConnectionString);
            connection.Open();
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO agents (id, scope, enabled, owner_user_id, storage_locator_key, created_at, updated_at)
                VALUES ($id, 'user', 1, 'user-1', $storageLocatorKey, $createdAt, $updatedAt);
                """;
            command.Parameters.AddWithValue("$id", agentId);
            command.Parameters.AddWithValue("$storageLocatorKey", storageLocatorKey);
            command.Parameters.AddWithValue("$createdAt", "2026-08-05T00:00:00.0000000+00:00");
            command.Parameters.AddWithValue("$updatedAt", "2026-08-05T00:00:00.0000000+00:00");
            command.ExecuteNonQuery();
        }

        public SqliteConnection OpenAgentStore(string storageLocatorKey)
        {
            string dataSource = Path.Combine(directoryPath, "agents", storageLocatorKey, "agent.db");
            Directory.CreateDirectory(Path.GetDirectoryName(dataSource)!);
            var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = dataSource,
                Mode = SqliteOpenMode.ReadWriteCreate,
                Pooling = false,
            }.ToString());
            connection.Open();
            return connection;
        }

        public void ExecuteAgentStoreSql(string storageLocatorKey, string sql)
        {
            using SqliteConnection connection = OpenAgentStore(storageLocatorKey);
            using SqliteCommand command = connection.CreateCommand();
#pragma warning disable CA2100
            command.CommandText = sql;
#pragma warning restore CA2100
            command.ExecuteNonQuery();
        }

        public int ReadOutboxCount(string storageLocatorKey)
        {
            using SqliteConnection connection = OpenAgentStore(storageLocatorKey);
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM runtime_event_outbox;";
            return Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture);
        }

        public void ForgetAgentStoreSchemaCache(string storageLocatorKey)
            => SqliteAgentStoreSchema.Forget(Path.Combine(directoryPath, "agents", storageLocatorKey, "agent.db"));

        public void WriteLegacySchemaMarker(string storageLocatorKey)
        {
            using SqliteConnection connection = OpenAgentStore(storageLocatorKey);
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = """
                DELETE FROM agent_store_schema;
                INSERT INTO agent_store_schema(schema_id, created_at)
                VALUES ('agent-store-2026-07-15.2', CURRENT_TIMESTAMP);
                """;
            command.ExecuteNonQuery();
        }

        public void RenameJournalToPreviousName(string storageLocatorKey)
        {
            using SqliteConnection connection = OpenAgentStore(storageLocatorKey);
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = """
                DROP INDEX IF EXISTS idx_narad_runtime_journal_canonical_root_sequence;
                DROP INDEX IF EXISTS idx_narad_runtime_journal_session_sequence;
                DROP INDEX IF EXISTS idx_narad_runtime_journal_root_sequence;
                DROP INDEX IF EXISTS idx_narad_runtime_journal_root_run;
                ALTER TABLE narad_runtime_journal RENAME TO agip_runtime_journal;
                CREATE INDEX idx_agip_runtime_journal_session_sequence
                    ON agip_runtime_journal(session_id, session_sequence);
                CREATE UNIQUE INDEX idx_agip_runtime_journal_canonical_root_sequence
                    ON agip_runtime_journal(root_run_id, sequence)
                    WHERE json_extract(event_json, '$.sourceEventId') IS NULL;
                CREATE INDEX idx_agip_runtime_journal_root_sequence
                    ON agip_runtime_journal(root_run_id, sequence);
                CREATE INDEX idx_agip_runtime_journal_root_run
                    ON agip_runtime_journal(root_run_id, run_id);
                """;
            command.ExecuteNonQuery();
        }

        public void DowngradeToolSurfaceTo202608071(string storageLocatorKey)
        {
            using SqliteConnection connection = OpenAgentStore(storageLocatorKey);
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = """
                DROP TABLE IF EXISTS agent_tool_surface_current;
                UPDATE agent_store_schema SET schema_id = 'agent-store-2026-08-07.1';
                """;
            command.ExecuteNonQuery();
        }

        public void Dispose()
        {
            try { Directory.Delete(directoryPath, recursive: true); }
            catch (IOException) { }
        }
    }
}
