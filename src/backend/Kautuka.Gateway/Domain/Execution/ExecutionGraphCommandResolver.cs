using System.Text.Json.Nodes;

using Kautuka.Gateway.RuntimePlane.Projection;

using Narad.Contracts;

namespace Kautuka.Gateway.Domain.Execution;

internal sealed record ExecutionGraphCommandNode(
    NaradGraphNode Node,
    bool CanCancel,
    bool CanRetry,
    string? RootRunId);

internal static class ExecutionGraphCommandResolver
{
    private static readonly HashSet<string> cancellableStatuses = new(StringComparer.Ordinal)
    {
        "running",
        "waiting",
        "pending",
    };

    private static readonly HashSet<string> retryableStatuses = new(StringComparer.Ordinal)
    {
        "failed",
        "cancelled",
    };

    public static NaradGraphProjectionState BuildGraph(string rootRunId, IReadOnlyList<NaradPersistedEvent> history)
        => NaradGraphProjectionBuilder.Build(rootRunId, history);

    public static string? FindLatestWaitingRootRunId(IReadOnlyList<NaradPersistedEvent> sessionHistory, string threadId)
    {
        _ = threadId;
        foreach (NaradPersistedEvent persistedEvent in sessionHistory.OrderByDescending(static item => item.SessionSequence))
        {
            if (string.IsNullOrWhiteSpace(persistedEvent.RootRunId))
            {
                continue;
            }

            if (persistedEvent.EventType is NaradEventTypes.RunPaused)
            {
                return persistedEvent.RootRunId;
            }
        }

        return null;
    }

    public static ExecutionGraphCommandNode? TryResolveNode(
        NaradGraphProjectionState graph,
        string nodeId,
        string rootRunId)
    {
        JsonObject snapshot = graph.BuildAttachmentSnapshot(string.Empty, DateTimeOffset.UtcNow);
        if (snapshot["nodes"] is not JsonArray nodes)
        {
            return null;
        }

        JsonNode? nodeJson = nodes.FirstOrDefault(node => string.Equals(
            node?["id"]?.GetValue<string>(),
            nodeId,
            StringComparison.Ordinal));
        if (nodeJson is null)
        {
            return null;
        }

        NaradGraphNode node = ParseNode(nodeJson);
        bool canCancel = cancellableStatuses.Contains(node.Status)
            && node.Kind is "run" or "tool";
        bool canRetry = retryableStatuses.Contains(node.Status)
            && node.Kind == "interrupt";
        return new ExecutionGraphCommandNode(node, canCancel, canRetry, rootRunId);
    }

    public static ExecutionGraphCommandNode? TryResolveTarget(
        NaradGraphProjectionState graph,
        string targetType,
        string targetId,
        string rootRunId)
    {
        string nodeId = targetType switch
        {
            "tool" => NaradGraphSchema.ToolNodeId(targetId),
            "run" => NaradGraphSchema.RunNodeId(targetId),
            "interrupt" => NaradGraphSchema.InterruptNodeId(targetId),
            _ => targetId,
        };
        return TryResolveNode(graph, nodeId, rootRunId);
    }

    private static NaradGraphNode ParseNode(JsonNode nodeJson)
    {
        string nodeId = nodeJson["id"]?.GetValue<string>() ?? string.Empty;
        string label = nodeJson["label"]?.GetValue<string>() ?? nodeId;
        string status = nodeJson["status"]?.GetValue<string>() ?? "unknown";
        string kind = nodeId switch
        {
            NaradGraphSchema.RootNodeId => "run",
            _ when nodeId.StartsWith("node-run-", StringComparison.Ordinal) => "run",
            _ when nodeId.StartsWith("node-interrupt-", StringComparison.Ordinal) => "interrupt",
            _ => "tool",
        };
        return new NaradGraphNode(nodeId, label, status, kind);
    }
}
