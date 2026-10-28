using Kautuka.Gateway.Domain.Stores;

namespace Kautuka.Gateway.Domain.Execution;

internal static class RunUserMessageResolver
{
    public static string? Resolve(IAgentDefinitionStore definitions, IGatewaySessionStore sessions,
        string agentId, string sessionId, string? runId, string toolCallId)
    {
        if (!string.IsNullOrWhiteSpace(runId))
        {
            string? text = definitions.FindRunUserMessage(definitions.GetStorageLocatorKey(agentId), sessionId, runId);
            if (text is not null) return text;
        }
        // Legacy transcripts can correlate an exact pending tool to its preceding
        // user turn. Never borrow the latest user turn from another run.
        if (!sessions.TryGetSession(sessionId, out GatewaySession? session) || session is null) return null;
        string? candidate = null;
        bool awaitingAssistant = false;
        bool ambiguous = false;
        foreach (GatewayMessage message in session.Messages)
        {
            if (message.Internal) continue;
            if (message.Role == "user")
            {
                ambiguous |= awaitingAssistant;
                candidate = message.Content;
                awaitingAssistant = true;
            }
            if (message.ToolCalls?.Any(call => string.Equals(call.ToolCallId, toolCallId, StringComparison.Ordinal)) is true)
                return ambiguous ? null : candidate;
            if (message.Role == "assistant") awaitingAssistant = false;
        }
        return null;
    }
}
