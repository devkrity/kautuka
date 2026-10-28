using Narad.Contracts;

namespace Kautuka.Gateway.Domain.Execution;

internal static class SessionProjectionRevision
{
    public static IReadOnlyList<string> ListRootRunIdsForSession(
        IEnumerable<NaradPersistedEvent> events,
        string sessionId)
    {
        var ordered = new List<(string RootRunId, int FirstIndex)>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        int index = 0;
        foreach (NaradPersistedEvent persistedEvent in events)
        {
            if (BelongsToThreadScope(persistedEvent, sessionId)
                && string.Equals(persistedEvent.RunId, persistedEvent.RootRunId, StringComparison.Ordinal)
                && !string.IsNullOrWhiteSpace(persistedEvent.RootRunId)
                && seen.Add(persistedEvent.RootRunId))
            {
                ordered.Add((persistedEvent.RootRunId, index));
            }

            index++;
        }

        return ordered
            .OrderBy(item => item.FirstIndex)
            .Select(item => item.RootRunId)
            .ToArray();
    }

    public static long ReadRevision(
        IEnumerable<NaradPersistedEvent> events,
        string sessionId,
        Func<string, bool> isRootAdmissionClosed)
    {
        IReadOnlyList<string> rootRunIds = ListRootRunIdsForSession(events, sessionId);
        if (rootRunIds.Count == 0)
        {
            return 0;
        }

        string? scopeRootRunId = null;
        for (int index = rootRunIds.Count - 1; index >= 0; index--)
        {
            string candidate = rootRunIds[index];
            if (!isRootAdmissionClosed(candidate))
            {
                scopeRootRunId = candidate;
                break;
            }
        }

        scopeRootRunId ??= rootRunIds[^1];
        return ReadRevisionForRoot(events, sessionId, scopeRootRunId);
    }

    public static long ReadRevisionForRoot(
        IEnumerable<NaradPersistedEvent> events,
        string sessionId,
        string rootRunId)
        => events
            .Where(persistedEvent => string.Equals(persistedEvent.RootRunId, rootRunId, StringComparison.Ordinal)
                && BelongsToThreadScope(persistedEvent, sessionId))
            .Select(persistedEvent => persistedEvent.Sequence ?? persistedEvent.SessionSequence)
            .DefaultIfEmpty(0L)
            .Max();

    private static bool BelongsToThreadScope(NaradPersistedEvent persistedEvent, string sessionId)
    {
        if (string.Equals(persistedEvent.SessionId, sessionId, StringComparison.Ordinal))
        {
            return true;
        }

        string? parentSessionId = NaradPersistedEventReader.String(persistedEvent, "parentSessionId");
        return string.Equals(parentSessionId, sessionId, StringComparison.Ordinal);
    }
}
