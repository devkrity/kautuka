using System.Text.Json;

using Narad.Contracts;

namespace Kautuka.Gateway.Domain.Execution;

internal sealed record NaradOpenInterrupt(string InterruptId, string WaitId, string? ToolCallId);

internal sealed record NaradRunOpenState(
    IReadOnlyList<NaradOpenInterrupt> OpenInterrupts,
    bool RootPaused,
    IReadOnlyList<string> OpenToolCallIds,
    IReadOnlyList<string> OpenChildRunIds)
{
    public bool HasBlockingState =>
        OpenInterrupts.Count > 0
        || RootPaused
        || OpenToolCallIds.Count > 0
        || OpenChildRunIds.Count > 0;
}

internal static class NaradRunHistoryState
{
    public static NaradRunOpenState Analyze(IReadOnlyList<NaradPersistedEvent> history)
    {
        var resolvedInterruptIds = new HashSet<string>(StringComparer.Ordinal);
        var openInterrupts = new Dictionary<string, NaradOpenInterrupt>(StringComparer.Ordinal);
        var startedTools = new HashSet<string>(StringComparer.Ordinal);
        var finishedTools = new HashSet<string>(StringComparer.Ordinal);
        var childRunStarted = new HashSet<string>(StringComparer.Ordinal);
        var childRunTerminal = new Dictionary<string, bool>(StringComparer.Ordinal);
        string? rootLifecycle = null;

        foreach (NaradPersistedEvent persistedEvent in history)
        {
            if (!string.IsNullOrWhiteSpace(persistedEvent.RunId)
                && !string.IsNullOrWhiteSpace(persistedEvent.RootRunId)
                && !string.Equals(persistedEvent.RunId, persistedEvent.RootRunId, StringComparison.Ordinal))
            {
                string childRunId = persistedEvent.RunId;
                if (string.Equals(persistedEvent.EventType, NaradEventTypes.RunStarted, StringComparison.Ordinal))
                {
                    childRunStarted.Add(childRunId);
                    childRunTerminal.TryAdd(childRunId, false);
                }
                else if (NaradRunLifecycle.IsTerminal(persistedEvent.EventType))
                {
                    childRunTerminal[childRunId] = true;
                }

                continue;
            }

            switch (persistedEvent.EventType)
            {
                case NaradEventTypes.InterruptRequested:
                    JsonElement requestedBody = NaradPersistedEventReader.ParseBody(persistedEvent);
                    string interruptId = NaradPersistedEventReader.String(requestedBody, "interruptId") ?? string.Empty;
                    string waitId = NaradPersistedEventReader.String(requestedBody, "waitId") ?? string.Empty;
                    string? toolCallId = NaradPersistedEventReader.String(requestedBody, "toolCallId");
                    if (!string.IsNullOrWhiteSpace(interruptId))
                    {
                        openInterrupts[interruptId] = new NaradOpenInterrupt(interruptId, waitId, toolCallId);
                    }

                    break;
                case NaradEventTypes.InterruptResolved:
                    JsonElement resolvedBody = NaradPersistedEventReader.ParseBody(persistedEvent);
                    string resolvedInterruptId = NaradPersistedEventReader.String(resolvedBody, "interruptId") ?? string.Empty;
                    if (!string.IsNullOrWhiteSpace(resolvedInterruptId))
                    {
                        resolvedInterruptIds.Add(resolvedInterruptId);
                        openInterrupts.Remove(resolvedInterruptId);
                    }

                    break;
                case NaradEventTypes.ToolCallStarted:
                    string startedToolCallId = ReadToolCallId(persistedEvent);
                    if (!string.IsNullOrWhiteSpace(startedToolCallId))
                    {
                        startedTools.Add(startedToolCallId);
                    }

                    break;
                case NaradEventTypes.ToolCallFinished:
                    string finishedToolCallId = ReadToolCallId(persistedEvent);
                    if (!string.IsNullOrWhiteSpace(finishedToolCallId))
                    {
                        finishedTools.Add(finishedToolCallId);
                    }

                    break;
                default:
                    if (NaradRunLifecycle.IsLifecycleEvent(persistedEvent.EventType))
                    {
                        rootLifecycle = persistedEvent.EventType;
                    }

                    break;
            }
        }

        IReadOnlyList<NaradOpenInterrupt> stillOpenInterrupts = openInterrupts.Values
            .Where(interrupt => !resolvedInterruptIds.Contains(interrupt.InterruptId))
            .ToArray();
        bool rootPaused = string.Equals(rootLifecycle, NaradEventTypes.RunPaused, StringComparison.Ordinal);
        IReadOnlyList<string> openTools = startedTools
            .Except(finishedTools, StringComparer.Ordinal)
            .ToArray();
        IReadOnlyList<string> openChildren = childRunStarted
            .Where(childRunId => !childRunTerminal.TryGetValue(childRunId, out bool terminal) || !terminal)
            .ToArray();
        return new NaradRunOpenState(stillOpenInterrupts, rootPaused, openTools, openChildren);
    }

    private static string ReadToolCallId(NaradPersistedEvent persistedEvent)
        => NaradPersistedEventReader.String(NaradPersistedEventReader.ParseBody(persistedEvent), "toolCallId") ?? string.Empty;
}
