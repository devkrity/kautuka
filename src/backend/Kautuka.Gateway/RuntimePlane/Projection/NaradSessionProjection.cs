using System.Text.Json;
using System.Text.Json.Nodes;

using Narad.Contracts;

namespace Kautuka.Gateway.RuntimePlane.Projection;

internal sealed record NaradPendingInterrupt(
    string InterruptId,
    string WaitId,
    string? Kind,
    string? ToolCallId,
    long SessionSequence,
    string RootRunId);

internal sealed record NaradActiveRunState(
    string RootRunId,
    string Lifecycle,
    bool IsPaused,
    bool IsTerminal,
    long LatestRunSequence,
    long LatestSessionSequence);

internal sealed class NaradSessionProjection
{
    private readonly Dictionary<string, NaradGraphProjectionState> graphs = new(StringComparer.Ordinal);
    private readonly List<NaradPendingInterrupt> pendingInterrupts = [];
    private readonly List<NaradProgressEntry> progressEntries = [];
    private NaradActiveRunState? activeRun;
    private long lastSessionSequence;

    public IReadOnlyList<NaradPendingInterrupt> PendingInterrupts => pendingInterrupts;
    public NaradActiveRunState? ActiveRun => activeRun;
    public IReadOnlyList<NaradProgressEntry> ProgressEntries => progressEntries;
    public long LastSessionSequence => lastSessionSequence;

    public static NaradSessionProjection Fold(
        string sessionId,
        IEnumerable<NaradPersistedEvent> sessionHistory,
        Func<string, bool>? isRootAdmissionClosed = null)
    {
        var projection = new NaradSessionProjection();
        projection.ApplyHistory(sessionId, sessionHistory, isRootAdmissionClosed);
        return projection;
    }

    public void ApplyHistory(
        string sessionId,
        IEnumerable<NaradPersistedEvent> sessionHistory,
        Func<string, bool>? isRootAdmissionClosed = null)
    {
        foreach (NaradPersistedEvent persistedEvent in sessionHistory.OrderBy(static item => item.SessionSequence))
        {
            ApplyEvent(sessionId, persistedEvent, isRootAdmissionClosed);
        }

        progressEntries.Clear();
        progressEntries.AddRange(NaradProgressLedger.Fold(sessionHistory));
    }

    public NaradGraphApplyResult? ApplyEvent(
        string sessionId,
        NaradPersistedEvent persistedEvent,
        Func<string, bool>? isRootAdmissionClosed = null)
    {
        _ = sessionId;
        lastSessionSequence = Math.Max(lastSessionSequence, persistedEvent.SessionSequence);

        NaradGraphApplyResult? graphResult = null;
        if (TryResolveGraphAuthorityEvent(persistedEvent, out NaradPersistedEvent graphAuthority))
        {
            NaradGraphProjectionState graph = graphs.GetOrAdd(
                graphAuthority.RootRunId!,
                static rootRunId => new NaradGraphProjectionState(rootRunId));
            graphResult = graph.Apply(graphAuthority);
        }

        JsonElement body = NaradEventJsonParser.ParseBody(persistedEvent);
        switch (persistedEvent.EventType)
        {
            case NaradEventTypes.InterruptRequested:
                {
                    string? interruptId = NaradEventJsonParser.String(body, "interruptId");
                    if (!string.IsNullOrWhiteSpace(interruptId))
                    {
                        pendingInterrupts.RemoveAll(item => string.Equals(item.InterruptId, interruptId, StringComparison.Ordinal));
                        pendingInterrupts.Add(new NaradPendingInterrupt(
                            interruptId,
                            NaradEventJsonParser.String(body, "waitId") ?? string.Empty,
                            NaradEventJsonParser.String(body, "kind"),
                            NaradEventJsonParser.String(body, "toolCallId"),
                            persistedEvent.SessionSequence,
                            persistedEvent.RootRunId ?? string.Empty));
                    }

                    break;
                }
            case NaradEventTypes.InterruptResolved:
                {
                    string? interruptId = NaradEventJsonParser.String(body, "interruptId");
                    if (!string.IsNullOrWhiteSpace(interruptId))
                    {
                        pendingInterrupts.RemoveAll(item => string.Equals(item.InterruptId, interruptId, StringComparison.Ordinal));
                    }

                    break;
                }
            case NaradEventTypes.ProgressRecorded:
                break;
            default:
                if (NaradEventTypes.IsRunLifecycleEventType(persistedEvent.EventType)
                    && string.Equals(persistedEvent.RunId, persistedEvent.RootRunId, StringComparison.Ordinal)
                    && !string.IsNullOrWhiteSpace(persistedEvent.RootRunId))
                {
                    activeRun = new NaradActiveRunState(
                        persistedEvent.RootRunId,
                        persistedEvent.EventType,
                        string.Equals(persistedEvent.EventType, NaradEventTypes.RunPaused, StringComparison.Ordinal),
                        NaradEventTypes.IsRootRunTerminalEventType(persistedEvent.EventType),
                        persistedEvent.Sequence ?? 0,
                        persistedEvent.SessionSequence);
                }

                break;
        }

        if (isRootAdmissionClosed is not null && activeRun is not null && isRootAdmissionClosed(activeRun.RootRunId))
        {
            if (activeRun.IsTerminal)
            {
                activeRun = null;
            }
        }

        return graphResult;
    }

    public NaradGraphProjectionState? GetGraph(string rootRunId)
        => graphs.TryGetValue(rootRunId, out NaradGraphProjectionState? graph) ? graph : null;

    public IEnumerable<NaradGraphProjectionState> EnumerateGraphs() => graphs.Values;

    public static JsonObject BuildSessionRevisionWatch(
        string sessionId,
        NaradPersistedEvent persistedEvent)
        => NaradWatchRevisionBuilder.BuildSessionRevision(
            sessionId,
            BuildSessionWatchEventId(sessionId, persistedEvent.SessionSequence),
            ParseSourceTimestamp(persistedEvent),
            persistedEvent.SessionSequence,
            persistedEvent.SessionSequence);

    public static string BuildSessionWatchEventId(string sessionId, long sessionSequence)
        => $"session-revision:{sessionId}:{sessionSequence}";

    public static JsonObject BuildMessagesSnapshot(string sessionId, IReadOnlyList<NaradPersistedEvent> sessionHistory, DateTimeOffset timestamp)
        => NaradMessagesSnapshotBuilder.Build(sessionId, sessionHistory, timestamp);

    private static DateTimeOffset ParseSourceTimestamp(NaradPersistedEvent persistedEvent)
        => DateTimeOffset.TryParse(persistedEvent.OccurredAt, out DateTimeOffset parsed)
            ? parsed
            : DateTimeOffset.UtcNow;

    /// <summary>
    /// Parent AAAT rows store lineage in event_json while journal run-tree columns stay null.
    /// Graph projection must consume those rows or live graph deltas fall behind session-lane facts.
    /// </summary>
    private static bool TryResolveGraphAuthorityEvent(
        NaradPersistedEvent persistedEvent,
        out NaradPersistedEvent graphAuthority)
    {
        if (!string.IsNullOrWhiteSpace(persistedEvent.RootRunId))
        {
            graphAuthority = persistedEvent;
            return true;
        }

        JsonElement root = NaradEventJsonParser.ParseEnvelope(persistedEvent);
        if (!NaradEventJsonParser.IsParentConversationProjection(root))
        {
            graphAuthority = persistedEvent;
            return false;
        }

        string? rootRunId = NaradEventJsonParser.String(root, "rootRunId");
        if (string.IsNullOrWhiteSpace(rootRunId))
        {
            graphAuthority = persistedEvent;
            return false;
        }

        string? runId = NaradEventJsonParser.String(root, "runId");
        graphAuthority = persistedEvent with
        {
            RootRunId = rootRunId,
            RunId = runId,
            Sequence = null,
        };
        return true;
    }
}

internal static class NaradWatchRevisionBuilder
{
    public static JsonObject BuildSessionRevision(
        string sessionId,
        string watchEventId,
        DateTimeOffset timestamp,
        long revision,
        long sessionSequence)
        => new()
        {
            ["type"] = NaradEventTypes.SessionRevision,
            ["protocolVersion"] = NaradProtocolVersions.V1,
            ["eventId"] = watchEventId,
            ["timestamp"] = timestamp.ToString("O"),
            ["sessionId"] = sessionId,
            ["revision"] = revision,
            ["cursor"] = new JsonObject
            {
                ["kind"] = "session",
                ["sessionSequence"] = sessionSequence,
            },
        };
}

internal static class DictionaryExtensions
{
    public static TValue GetOrAdd<TKey, TValue>(this Dictionary<TKey, TValue> dictionary, TKey key, Func<TKey, TValue> factory)
        where TKey : notnull
    {
        if (!dictionary.TryGetValue(key, out TValue? value))
        {
            value = factory(key);
            dictionary[key] = value;
        }

        return value;
    }
}
