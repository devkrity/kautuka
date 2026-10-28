using System.Globalization;

using Microsoft.Extensions.AI;

namespace Kautuka.Gateway.Domain.Execution;

internal static class ToolCallOutcomeReducer
{
    public static void RecordStart(
        List<GatewayToolCall> toolCalls,
        string toolCallId,
        string toolName,
        string? agentId,
        string? argumentsJson)
    {
        int index = toolCalls.FindIndex(call => string.Equals(call.ToolCallId, toolCallId, StringComparison.Ordinal));
        GatewayToolCall? existing = index >= 0 ? toolCalls[index] : null;
        var snapshot = new GatewayToolCall(
            toolCallId,
            toolName,
            agentId,
            argumentsJson,
            existing?.ResultJson,
            IsTerminal(existing?.Status) ? existing!.Status : "calling",
            ActionIdentity.ResolveKind(toolName),
            StartedAt: existing?.StartedAt ?? UtcNow(),
            CompletedAt: existing?.CompletedAt,
            AttemptOrdinal: existing is { AttemptOrdinal: > 0 } ? existing.AttemptOrdinal : NextOrdinal(toolCalls),
            TerminalOrdinal: existing?.TerminalOrdinal);
        if (index >= 0)
        {
            toolCalls[index] = snapshot;
        }
        else
        {
            toolCalls.Add(snapshot);
        }
    }

    public static bool RecordResult(List<GatewayToolCall> toolCalls, string toolCallId, string? resultJson)
        => RecordTerminal(toolCalls, toolCallId, resultJson, "done");

    public static bool RecordFailure(List<GatewayToolCall> toolCalls, string toolCallId, string failureJson)
        => RecordTerminal(toolCalls, toolCallId, failureJson, "failed");

    public static bool TryGetFailure(FunctionResultContent result, out Exception? exception)
    {
        exception = result.Exception;
        return exception is not null;
    }

    public static bool IsTerminal(string? status) => status is "done" or "failed";

    public static bool HasUnrecoveredFailure(IEnumerable<GatewayToolCall> toolCalls)
    {
        GatewayToolCall[] attempts = [.. toolCalls.Where(static call => !string.IsNullOrWhiteSpace(call.ToolName))];
        return attempts
            .Where(static call => string.Equals(call.Status, "failed", StringComparison.Ordinal))
            .Any(failed => !attempts.Any(retry => string.Equals(retry.ToolName, failed.ToolName, StringComparison.Ordinal)
                    && string.Equals(retry.Status, "done", StringComparison.Ordinal)
                    && StartedAfterFailure(retry, failed)));
    }

    private static bool RecordTerminal(
        List<GatewayToolCall> toolCalls,
        string toolCallId,
        string? resultJson,
        string status)
    {
        int index = toolCalls.FindIndex(call => string.Equals(call.ToolCallId, toolCallId, StringComparison.Ordinal));
        if (index < 0 || IsTerminal(toolCalls[index].Status))
        {
            return false;
        }

        GatewayToolCall existing = toolCalls[index];
        toolCalls[index] = existing with
        {
            ResultJson = resultJson,
            Status = status,
            CompletedAt = existing.CompletedAt ?? UtcNow(),
            TerminalOrdinal = existing.TerminalOrdinal ?? NextOrdinal(toolCalls),
        };
        return true;
    }

    private static long NextOrdinal(IEnumerable<GatewayToolCall> toolCalls)
        => toolCalls.Select(static call => Math.Max(call.AttemptOrdinal, call.TerminalOrdinal ?? 0))
            .DefaultIfEmpty(0)
            .Max() + 1;

    private static bool StartedAfterFailure(GatewayToolCall retry, GatewayToolCall failure)
    {
        if (retry.AttemptOrdinal > 0 && failure.TerminalOrdinal is > 0)
        {
            return retry.AttemptOrdinal > failure.TerminalOrdinal;
        }

        return DateTimeOffset.TryParse(retry.StartedAt, CultureInfo.InvariantCulture, out DateTimeOffset retryStarted)
            && DateTimeOffset.TryParse(failure.CompletedAt, CultureInfo.InvariantCulture, out DateTimeOffset failureCompleted)
            && retryStarted > failureCompleted;
    }

    private static string UtcNow() => DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture);
}
