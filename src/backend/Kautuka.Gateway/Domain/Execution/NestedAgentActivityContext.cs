namespace Kautuka.Gateway.Domain.Execution;

/// <summary>
/// Ambient, async-flowing channel that lets a nested "agent as a tool" invocation report the
/// child agent's completed <see cref="GatewayActivityEntry"/> entries back to the outer agent's
/// own turn, without threading them through the tool's return value (which becomes the function
/// result the model sees, and must stay a plain text/JSON payload).
///
/// The outer turn (<c>AgentChatService.Streaming.cs</c>) opens a capture scope before running its
/// own model turn; any nested <c>AgentToolInvocationOrchestrator</c> call started within that scope
/// (even several agent-as-tool levels deep) reports into the same list. The outer turn then merges
/// those entries into its own persisted <c>GatewayMessage.ActivityEntries</c>, so the parent
/// session's durable history is self-contained and never needs to read a child session's own
/// records to render nested tool calls.
/// </summary>
internal static class NestedAgentActivityContext
{
    private static readonly AsyncLocal<List<GatewayActivityEntry>?> current = new();

    public static IDisposable Capture(out List<GatewayActivityEntry> collected)
    {
        var list = new List<GatewayActivityEntry>();
        List<GatewayActivityEntry>? previous = current.Value;
        current.Value = list;
        collected = list;
        return new RestoreScope(previous);
    }

    public static void Report(IEnumerable<GatewayActivityEntry> entries)
    {
        current.Value?.AddRange(entries);
    }

    private sealed class RestoreScope(List<GatewayActivityEntry>? previous) : IDisposable
    {
        public void Dispose()
        {
            current.Value = previous;
        }
    }
}
