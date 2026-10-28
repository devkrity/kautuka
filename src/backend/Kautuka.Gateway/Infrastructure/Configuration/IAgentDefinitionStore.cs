using System.Text.Json.Nodes;

using Kautuka.Gateway.Domain.Memory;

using Narad.Contracts;

namespace Kautuka.Gateway.Infrastructure.Configuration;

internal interface IAgentDefinitionStore
{
    string GetStorageLocatorKey(string agentId);

    bool HasActiveRunForSession(string storageLocatorKey, string sessionId, string? exceptRootRunId = null)
        => ListNaradSessionHistoryFull(storageLocatorKey, sessionId)
            .Where(item => item.RunId == item.RootRunId && !string.IsNullOrWhiteSpace(item.RootRunId)
                && NaradEventTypes.IsRunLifecycleEventType(item.EventType))
            .GroupBy(item => item.RootRunId, StringComparer.Ordinal)
            .Any(group => group.Key != exceptRootRunId && !NaradEventTypes.IsRootRunTerminalEventType(group.OrderBy(item => item.SessionSequence).Last().EventType));

    string? FindRunUserMessage(string storageLocatorKey, string sessionId, string runId)
        => ListNaradSessionHistoryFull(storageLocatorKey, sessionId)
            .Where(item => item.EventType == NaradEventTypes.MessageUserRecorded && item.RunId == runId)
            .Select(item => Kautuka.Gateway.Domain.Execution.NaradPersistedEventReader.String(item, "content"))
            .FirstOrDefault();

    long SaveDefinition(string storageLocatorKey, AgentDefinition definition, AgentDefinitionSnapshotMetadata? snapshotMetadata = null);

    AgentDefinition? TryLoadLatestDefinition(string storageLocatorKey);

    AgentDefinition? TryLoadLatestDefinition(string storageLocatorKey, out long revision)
    {
        revision = 0;
        return TryLoadLatestDefinition(storageLocatorKey);
    }

    IReadOnlyList<AgentDefinitionRevision> ListDefinitionRevisions(string storageLocatorKey)
        => [];

    IReadOnlyList<AgentDefinitionSettingSnapshot> ListDefinitionSettings(string storageLocatorKey, long revision)
        => [];

    IReadOnlyList<AgentDefinitionSettingSnapshot> ListAllDefinitionSettings(string storageLocatorKey)
        => [];

    NaradPersistedEvent AppendNaradAuthorityEvent(
        string rootStorageLocatorKey,
        string sourceStorageLocatorKey,
        NaradAuthorityEventDraft draft);

    /// <summary>
    /// Projects a child-session AAAT event onto the parent conversation as a session-lane row:
    /// new parent <c>sessionSequence</c>/<c>eventId</c>, lineage in JSON (<c>runId</c>/<c>rootRunId</c>/
    /// <c>parent*</c>), no run-tree <c>sequence</c>. Marked with <c>sourceEventId</c>.
    /// </summary>
    NaradPersistedEvent AppendNaradParentSessionProjection(
        string rootStorageLocatorKey,
        NaradPersistedEvent sourceEvent,
        string parentSessionId);

    void MirrorNaradAuthorityEvent(string sourceStorageLocatorKey, NaradPersistedEvent persistedEvent);

    IReadOnlyList<NaradPersistedEvent> ListNaradSessionHistory(
        string storageLocatorKey,
        string sessionId,
        long afterSessionSequence,
        int limit);

    IReadOnlyList<NaradPersistedEvent> ListNaradSessionHistoryFull(string storageLocatorKey, string sessionId);

    IReadOnlyList<NaradPersistedEvent> ListNaradSessionHistory(string storageLocatorKey, string sessionId);

    IReadOnlyList<NaradPersistedEvent> ListNaradRootHistory(
        string storageLocatorKey,
        string rootRunId,
        long afterRootSequence,
        int limit);

    IReadOnlyList<NaradPersistedEvent> ListNaradRootHistoryFull(string storageLocatorKey, string rootRunId);

    IReadOnlyList<NaradPersistedEvent> ListNaradRootHistory(string storageLocatorKey, string rootRunId);

    long ReadNaradSessionProjectionRevision(string storageLocatorKey, string sessionId);

    bool IsNaradRootRunAdmissionClosed(string storageLocatorKey, string rootRunId);

    IReadOnlyList<string> ListNaradRootRunIdsForSession(string storageLocatorKey, string sessionId);

    IReadOnlyList<string> ListInterruptedNaradRootRuns(string startedBefore);

    string? FindNaradRootStorageLocatorKey(string rootRunId);

    string? FindNaradRootRunIdForRun(string storageLocatorKey, string runId);

    IReadOnlyList<NaradRuntimeEventOutboxEntry> ListPendingNaradEventOutbox(string rootStorageLocatorKey, int limit);

    IReadOnlyList<NaradRuntimeEventOutboxEntry> ClaimNaradEventOutbox(
        string rootStorageLocatorKey,
        string leaseOwner,
        TimeSpan leaseDuration,
        int limit);

    void MarkNaradEventPublished(string rootStorageLocatorKey, string eventId, string leaseOwner);

    void MarkNaradEventMirrored(string rootStorageLocatorKey, string eventId, string leaseOwner);

    void RecordNaradEventOutboxFailure(string rootStorageLocatorKey, string eventId, string leaseOwner, string safeError);

    IReadOnlyList<NaradRuntimeEventOutboxDeadLetter> ListNaradEventOutboxDeadLetters(string rootStorageLocatorKey, int limit);

    bool RetryNaradEventOutboxDeadLetter(string rootStorageLocatorKey, string eventId);

    bool DismissNaradEventOutboxDeadLetter(string rootStorageLocatorKey, string eventId);

    IReadOnlyList<string> ListNaradEventOutboxStorageLocatorKeys();

    IReadOnlyList<string> ListNaradEventOutboxStorageLocatorKeys(IReadOnlySet<string> excludedStorageLocatorKeys)
        => [.. ListNaradEventOutboxStorageLocatorKeys().Where(key => !excludedStorageLocatorKeys.Contains(key))];

    void UpsertMemoryEmbeddings(string storageLocatorKey, IReadOnlyList<AgentMemoryEmbeddingRecord> embeddings);

    void DeleteMemoryEmbedding(string storageLocatorKey, string factId);

    IReadOnlyDictionary<string, AgentMemoryEmbeddingState> ListMemoryEmbeddingStates(string storageLocatorKey);

    IReadOnlyList<AgentMemorySearchHit> SearchMemoryEmbeddings(string storageLocatorKey, string modelKey, ReadOnlySpan<float> queryVector, int limit);

    bool DeleteStore(string storageLocatorKey);
}

internal interface IAtomicAgentDefinitionStore : IAgentDefinitionStore
{
    bool SupportsAtomicRegistryTransactions { get; }

    long SaveDefinitionAndRegistryAtomically(AgentRegistryEntry registryEntry, AgentDefinition definition, AgentDefinitionSnapshotMetadata? snapshotMetadata = null);

    bool DeleteStoreAndRegistryAtomically(string agentId, string storageLocatorKey);
}

internal sealed record AgentDefinitionSnapshotMetadata(
    string? ModelName = null,
    string? ModelProviderId = null,
    IReadOnlyDictionary<string, string>? ConnectorNames = null,
    IReadOnlyDictionary<string, string>? SkillNames = null);

internal sealed record AgentDefinitionRevision(long Revision, string CreatedAt);

internal sealed record AgentDefinitionSettingSnapshot(
    long Revision,
    string SettingId,
    string SettingName,
    string ValueJson);

internal sealed record AgentDefinitionSettingChange(
    string SettingId,
    string SettingName,
    JsonNode? Before,
    JsonNode? After);
