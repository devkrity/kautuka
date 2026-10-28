using Narad.Contracts;

namespace Kautuka.Gateway.Domain.Execution;

/// <summary>
/// Parent AAAT completion is authoritative once the parent tool terminal is journaled
/// (or the root run itself is already terminal). Re-streaming after that doubles terminals.
/// </summary>
internal static class AaatParentCompletionAuthority
{
    public static bool HasAuthoritativeParentCompletion(
        IReadOnlyList<NaradPersistedEvent> history,
        string rootRunId,
        string parentToolCallId)
    {
        if (string.IsNullOrWhiteSpace(rootRunId) || string.IsNullOrWhiteSpace(parentToolCallId))
        {
            return false;
        }

        foreach (NaradPersistedEvent runtimeEvent in history)
        {
            if (string.Equals(runtimeEvent.EventType, NaradEventTypes.ToolCallFinished, StringComparison.Ordinal)
                && string.Equals(
                    NaradPersistedEventReader.String(runtimeEvent, "toolCallId"),
                    parentToolCallId,
                    StringComparison.Ordinal))
            {
                return true;
            }

            if (string.Equals(runtimeEvent.RunId, rootRunId, StringComparison.Ordinal)
                && NaradRunLifecycle.IsTerminal(runtimeEvent.EventType))
            {
                return true;
            }
        }

        return false;
    }
}
