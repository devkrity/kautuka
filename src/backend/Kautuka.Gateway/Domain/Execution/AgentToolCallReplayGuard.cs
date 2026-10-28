using System.Text.Json;

using Narad.Contracts;

namespace Kautuka.Gateway.Domain.Execution;

internal static class AgentToolCallReplayGuard
{
    public static bool IsAlreadyStarted(IRuntimeEventHistoryReader? historyReader, NaradEventContext context, GatewayChatToolCall toolCall)
    {
        NaradPersistedEvent? prior = historyReader?.ReadRootHistory(context.RootRunId).LastOrDefault(item => item.RunId == context.RunId
            && item.EventType is NaradEventTypes.ToolCallStarted or NaradEventTypes.ToolCallFinished
            && NaradPersistedEventReader.String(NaradPersistedEventReader.ParseBody(item), "toolCallId") == toolCall.ToolCallId);
        if (prior is null) return false;
        JsonElement body = NaradPersistedEventReader.ParseBody(prior);
        JsonElement proposed = AgentExecutionNaradToolBodies.ToolCallStarted(toolCall);
        if (prior.EventType != NaradEventTypes.ToolCallStarted
            || NaradPersistedEventReader.String(body, "toolName") != toolCall.ToolName
            || !JsonElement.DeepEquals(body.GetProperty("args"), proposed.GetProperty("args")))
            throw new InvalidOperationException("Tool call identity cannot be reused with different arguments or after completion.");
        return true;
    }
}
