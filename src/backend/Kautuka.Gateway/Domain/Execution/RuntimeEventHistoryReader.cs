using Narad.Contracts;

namespace Kautuka.Gateway.Domain.Execution;

/// <summary>Reads the authoritative root-agent Narad journal. Child stores are mirrors, not history sources.</summary>
internal sealed class RuntimeEventHistoryReader(IAgentDefinitionStore agentDefinitionStore) : IRuntimeEventHistoryReader
{
    public IReadOnlyList<NaradPersistedEvent> ReadRootHistory(string rootRunId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootRunId);

        string? storageLocatorKey = agentDefinitionStore.FindNaradRootStorageLocatorKey(rootRunId);
        if (storageLocatorKey is null)
        {
            return [];
        }

        IReadOnlyList<NaradPersistedEvent> events = agentDefinitionStore.ListNaradRootHistoryFull(storageLocatorKey, rootRunId);
        long? previousSequence = null;
        foreach (NaradPersistedEvent persistedEvent in events)
        {
            if (!string.Equals(persistedEvent.RootRunId, rootRunId, StringComparison.Ordinal)
                || persistedEvent.Sequence is null or <= 0
                || previousSequence is not null && persistedEvent.Sequence <= previousSequence.Value)
            {
                throw new InvalidOperationException($"Root history '{rootRunId}' is not ordered by root sequence.");
            }

            previousSequence = persistedEvent.Sequence;
        }

        return events;
    }

    public IReadOnlyList<NaradPersistedEvent> ReadSessionHistory(string rootAgentId, string sessionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootAgentId);
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);

        string storageLocatorKey = agentDefinitionStore.GetStorageLocatorKey(rootAgentId);
        IReadOnlyList<string> rootRunIds = agentDefinitionStore.ListNaradRootRunIdsForSession(storageLocatorKey, sessionId);
        var history = new List<NaradPersistedEvent>();
        foreach (string rootRunId in rootRunIds)
        {
            history.AddRange(ReadRootHistory(rootRunId));
        }

        return history;
    }
}
