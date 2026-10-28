using System.Text.Json;

using Narad.Contracts;

namespace Kautuka.Gateway.Domain.Execution;

internal sealed class ChildRunToolOutcomeTracker(string? ignoredTerminalToolCallId = null)
{
    private readonly Dictionary<string, ToolAttempt> attemptsByCallId = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ToolFailure> unresolvedFailuresByCallId = new(StringComparer.Ordinal);
    private long observationOrdinal;

    public bool HasUnresolvedFailures => unresolvedFailuresByCallId.Count > 0;

    public string DescribeUnresolvedFailures()
        => string.Join(", ", unresolvedFailuresByCallId.Values
            .Select(static failure => failure.ToolName)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal));

    public void Observe(NaradPersistedEvent persistedEvent)
    {
        if (IsIgnoredTerminal(persistedEvent))
        {
            return;
        }

        long currentOrdinal = ++observationOrdinal;
        JsonElement body = NaradPersistedEventReader.ParseBody(persistedEvent);
        if (string.Equals(persistedEvent.EventType, NaradEventTypes.ToolCallStarted, StringComparison.Ordinal))
        {
            GatewayChatToolCall toolCall = JsonSerializer.Deserialize(body, KautukaJsonContext.Default.GatewayChatToolCall)
                ?? throw new InvalidOperationException("Tool call payload is invalid.");
            attemptsByCallId[toolCall.ToolCallId] = new ToolAttempt(toolCall.ToolName, currentOrdinal);
            return;
        }

        if (string.Equals(persistedEvent.EventType, NaradEventTypes.ToolCallFinished, StringComparison.Ordinal))
        {
            string? outcome = ReadString(body, "outcome");
            if (string.Equals(outcome, "success", StringComparison.Ordinal))
            {
                GatewayChatToolResult toolResult = JsonSerializer.Deserialize(body, KautukaJsonContext.Default.GatewayChatToolResult)
                    ?? throw new InvalidOperationException("Tool result payload is invalid.");
                if (attemptsByCallId.TryGetValue(toolResult.ToolCallId, out ToolAttempt? completedAttempt))
                {
                    foreach (string failedCallId in unresolvedFailuresByCallId
                        .Where(pair => string.Equals(pair.Value.ToolName, completedAttempt.ToolName, StringComparison.Ordinal)
                            && pair.Value.TerminalOrdinal < completedAttempt.StartOrdinal)
                        .Select(static pair => pair.Key)
                        .ToArray())
                    {
                        unresolvedFailuresByCallId.Remove(failedCallId);
                    }
                }
            }
            else if (!string.Equals(outcome, "cancelled", StringComparison.Ordinal))
            {
                RecordFailure(body, persistedEvent.EventId, currentOrdinal);
            }

            return;
        }
    }

    private void RecordFailure(JsonElement body, string fallbackCallId, long currentOrdinal)
    {
        string callId = ReadString(body, "toolCallId") ?? fallbackCallId;
        string toolName = ReadString(body, "toolName")
            ?? (attemptsByCallId.TryGetValue(callId, out ToolAttempt? attempt) ? attempt.ToolName : callId);
        unresolvedFailuresByCallId[callId] = new ToolFailure(toolName, currentOrdinal);
    }

    private bool IsIgnoredTerminal(NaradPersistedEvent persistedEvent)
    {
        if (string.IsNullOrWhiteSpace(ignoredTerminalToolCallId)
            || !string.Equals(persistedEvent.EventType, NaradEventTypes.ToolCallFinished, StringComparison.Ordinal))
        {
            return false;
        }

        JsonElement body = NaradPersistedEventReader.ParseBody(persistedEvent);
        string? outcome = ReadString(body, "outcome");
        if (!string.Equals(outcome, "cancelled", StringComparison.Ordinal)
            && !string.Equals(outcome, "error", StringComparison.Ordinal))
        {
            return false;
        }

        return string.Equals(ReadString(body, "toolCallId"), ignoredTerminalToolCallId, StringComparison.Ordinal);
    }

    private static string? ReadString(JsonElement payload, string propertyName)
        => payload.ValueKind == JsonValueKind.Object
            && payload.TryGetProperty(propertyName, out JsonElement value)
            && value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;

    private sealed record ToolAttempt(string ToolName, long StartOrdinal);

    private sealed record ToolFailure(string ToolName, long TerminalOrdinal);
}
