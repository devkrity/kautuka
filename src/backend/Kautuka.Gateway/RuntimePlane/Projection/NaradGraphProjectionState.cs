using System.Text.Json;
using System.Text.Json.Nodes;

using Narad.Contracts;

namespace Kautuka.Gateway.RuntimePlane.Projection;

internal static class NaradGraphSchema
{
    public const string Version = "narad.graph/v1";
    public const string RootNodeId = "node-root";

    public static string ToolNodeId(string toolCallId) => $"node-{toolCallId}";

    public static string RunNodeId(string runId) => $"node-run-{runId}";

    public static string InterruptNodeId(string interruptId) => $"node-interrupt-{interruptId}";
}

internal sealed record NaradGraphApplyResult(
    IReadOnlyList<JsonObject> Patch,
    long BaseRevision,
    long Revision,
    bool RequiresInitialSnapshot,
    string RootRunId);

internal sealed record NaradGraphNode(string NodeId, string Label, string Status, string Kind);

internal sealed class NaradGraphProjectionState(string rootRunId)
{
    private readonly Dictionary<string, JsonObject> nodes = new(StringComparer.Ordinal);
    private readonly List<string> nodeOrder = [];
    private readonly List<JsonObject> edges = [];
    private readonly HashSet<string> edgeKeys = new(StringComparer.Ordinal);
    private long revision;
    private bool snapshotSent;
    private long lastSourceSequence;

    public string RootRunId => rootRunId;
    public long Revision => revision;
    public bool SnapshotSent => snapshotSent;
    public long LastSourceSequence => lastSourceSequence;

    public NaradGraphApplyResult? Apply(NaradPersistedEvent persistedEvent)
    {
        if (!string.Equals(persistedEvent.RootRunId, rootRunId, StringComparison.Ordinal))
        {
            return null;
        }

        if (persistedEvent.Sequence is > 0)
        {
            lastSourceSequence = Math.Max(lastSourceSequence, persistedEvent.Sequence.Value);
        }

        JsonElement body = NaradEventJsonParser.ParseBody(persistedEvent);
        IReadOnlyList<JsonObject> patch = persistedEvent.EventType switch
        {
            NaradEventTypes.RunStarted when IsRoot(persistedEvent) => ApplyRootStarted(body),
            NaradEventTypes.RunStarted => ApplyChildRunStarted(persistedEvent, body),
            NaradEventTypes.ToolCallStarted => ApplyToolStarted(persistedEvent, body),
            NaradEventTypes.ToolCallFinished => ApplyToolFinished(body),
            NaradEventTypes.InterruptRequested => ApplyInterruptRequested(body),
            NaradEventTypes.InterruptResolved => ApplyInterruptResolved(body),
            NaradEventTypes.RunPaused => ApplyRunPaused(persistedEvent),
            NaradEventTypes.RunResumed => ApplyRunResumed(persistedEvent),
            NaradEventTypes.RunFinished => ApplyRunTerminal(persistedEvent, "completed"),
            NaradEventTypes.RunError => ApplyRunTerminal(persistedEvent, "failed"),
            NaradEventTypes.RunCancelled => ApplyRunTerminal(persistedEvent, "cancelled"),
            NaradEventTypes.ProgressRecorded => [],
            _ => [],
        };

        if (patch.Count == 0)
        {
            return null;
        }

        long baseRevision = revision;
        revision++;
        return new NaradGraphApplyResult(
            patch,
            baseRevision,
            revision,
            // Until a snapshot is explicitly marked sent, the next hydrate must be a full
            // snapshot — never a baseRevision-N delta the client cannot chain.
            !snapshotSent,
            rootRunId);
    }

    public JsonObject BuildGraphSnapshot(string sessionId, DateTimeOffset timestamp)
    {
        snapshotSent = true;
        return BuildSnapshotRecord(sessionId, timestamp, revision);
    }

    public static JsonObject? BuildGraphDelta(
        string sessionId,
        DateTimeOffset timestamp,
        string rootRunId,
        NaradGraphApplyResult applyResult,
        long lastSourceSequence)
        => new()
        {
            ["type"] = NaradEventTypes.GraphDelta,
            ["protocolVersion"] = NaradProtocolVersions.V1,
            ["timestamp"] = timestamp.ToString("O"),
            ["sessionId"] = sessionId,
            ["scope"] = "run",
            ["rootRunId"] = rootRunId,
            ["graphSchemaVersion"] = NaradGraphSchema.Version,
            ["baseRevision"] = applyResult.BaseRevision,
            ["revision"] = applyResult.Revision,
            ["sourceRunCursor"] = new JsonObject
            {
                ["rootRunId"] = rootRunId,
                ["sequence"] = Math.Max(1, lastSourceSequence),
            },
            ["patch"] = new JsonArray([.. applyResult.Patch]),
        };

    public JsonObject BuildAttachmentSnapshot(string sessionId, DateTimeOffset timestamp)
        => BuildSnapshotRecord(sessionId, timestamp, revision);

    private JsonObject BuildSnapshotRecord(string sessionId, DateTimeOffset timestamp, long snapshotRevision)
        => new()
        {
            ["type"] = NaradEventTypes.GraphSnapshot,
            ["protocolVersion"] = NaradProtocolVersions.V1,
            ["timestamp"] = timestamp.ToString("O"),
            ["sessionId"] = sessionId,
            ["scope"] = "run",
            ["rootRunId"] = rootRunId,
            ["graphSchemaVersion"] = NaradGraphSchema.Version,
            ["revision"] = snapshotRevision,
            ["sourceRunCursor"] = BuildSourceCursor(),
            ["rootNodeId"] = NaradGraphSchema.RootNodeId,
            ["nodes"] = SerializeNodes(),
            ["edges"] = SerializeEdges(),
        };

    private JsonObject BuildSourceCursor()
        => new()
        {
            ["rootRunId"] = rootRunId,
            ["sequence"] = Math.Max(1, lastSourceSequence),
        };

    private IReadOnlyList<JsonObject> ApplyRootStarted(JsonElement body)
    {
        string label = NaradEventJsonParser.String(body, "trigger") ?? "Run";
        UpsertNode(NaradGraphSchema.RootNodeId, label, "running", kind: "run");
        return [PatchAddNode(NaradGraphSchema.RootNodeId, label, "running", kind: "run")];
    }

    private List<JsonObject> ApplyChildRunStarted(NaradPersistedEvent persistedEvent, JsonElement body)
    {
        string runId = persistedEvent.RunId ?? string.Empty;
        string nodeId = NaradGraphSchema.RunNodeId(runId);
        string? parentToolCallId = NaradEventJsonParser.String(body, "parentToolCallId");
        string label = NaradEventJsonParser.String(body, "trigger") ?? "Nested run";
        UpsertNode(nodeId, label, "running", kind: "run");
        var patch = new List<JsonObject> { PatchAddNode(nodeId, label, "running", kind: "run") };
        if (!string.IsNullOrWhiteSpace(parentToolCallId))
        {
            string parentNodeId = NaradGraphSchema.ToolNodeId(parentToolCallId);
            if (TryAddEdge(parentNodeId, nodeId, "parent-child"))
            {
                patch.Add(PatchAddEdge(parentNodeId, nodeId, "parent-child"));
            }
        }

        return patch;
    }

    private List<JsonObject> ApplyToolStarted(NaradPersistedEvent persistedEvent, JsonElement body)
    {
        string? toolCallId = NaradEventJsonParser.String(body, "toolCallId");
        string? toolName = NaradEventJsonParser.String(body, "toolName");
        if (string.IsNullOrWhiteSpace(toolCallId))
        {
            return [];
        }

        string nodeId = NaradGraphSchema.ToolNodeId(toolCallId);
        UpsertNode(nodeId, toolName ?? toolCallId, "running", kind: "tool");
        string parentNodeId = ResolveRunNodeId(persistedEvent);
        var patch = new List<JsonObject> { PatchAddNode(nodeId, toolName ?? toolCallId, "running", kind: "tool") };
        if (TryAddEdge(parentNodeId, nodeId, "sequence"))
        {
            patch.Add(PatchAddEdge(parentNodeId, nodeId, "sequence"));
        }

        return patch;
    }

    private IReadOnlyList<JsonObject> ApplyToolFinished(JsonElement body)
    {
        string? toolCallId = NaradEventJsonParser.String(body, "toolCallId");
        string? outcome = NaradEventJsonParser.String(body, "outcome");
        if (string.IsNullOrWhiteSpace(toolCallId))
        {
            return [];
        }

        string status = outcome switch
        {
            "success" => "completed",
            "cancelled" => "cancelled",
            _ => "failed",
        };
        return PatchNodeStatus(NaradGraphSchema.ToolNodeId(toolCallId), status);
    }

    private List<JsonObject> ApplyInterruptRequested(JsonElement body)
    {
        string? interruptId = NaradEventJsonParser.String(body, "interruptId");
        string? toolCallId = NaradEventJsonParser.String(body, "toolCallId");
        if (string.IsNullOrWhiteSpace(interruptId))
        {
            return [];
        }

        var patch = new List<JsonObject>();
        if (!string.IsNullOrWhiteSpace(toolCallId))
        {
            patch.AddRange(PatchNodeStatus(NaradGraphSchema.ToolNodeId(toolCallId), "waiting"));
        }

        string approvalNodeId = NaradGraphSchema.InterruptNodeId(interruptId);
        string? toolName = NaradEventJsonParser.String(body, "toolName");
        string approvalLabel = string.IsNullOrWhiteSpace(toolName)
            ? "Approval"
            : $"Approval · {toolName}";
        UpsertNode(approvalNodeId, approvalLabel, "waiting", kind: "approval");
        patch.Add(PatchAddNode(approvalNodeId, approvalLabel, "waiting", kind: "approval"));
        if (!string.IsNullOrWhiteSpace(toolCallId)
            && TryAddEdge(NaradGraphSchema.ToolNodeId(toolCallId), approvalNodeId, "sequence"))
        {
            patch.Add(PatchAddEdge(NaradGraphSchema.ToolNodeId(toolCallId), approvalNodeId, "sequence"));
        }

        return patch;
    }

    private IReadOnlyList<JsonObject> ApplyInterruptResolved(JsonElement body)
    {
        string? interruptId = NaradEventJsonParser.String(body, "interruptId");
        string? decision = NaradEventJsonParser.String(body, "decision");
        if (string.IsNullOrWhiteSpace(interruptId))
        {
            return [];
        }

        string status = decision switch
        {
            "approve" or "approved" => "completed",
            "cancel" or "cancelled" => "cancelled",
            _ => "failed",
        };
        return PatchNodeStatus(NaradGraphSchema.InterruptNodeId(interruptId), status);
    }

    private IReadOnlyList<JsonObject> ApplyRunPaused(NaradPersistedEvent persistedEvent)
        => PatchNodeStatus(ResolveRunNodeId(persistedEvent), "waiting");

    private IReadOnlyList<JsonObject> ApplyRunResumed(NaradPersistedEvent persistedEvent)
        => PatchNodeStatus(ResolveRunNodeId(persistedEvent), "running");

    private IReadOnlyList<JsonObject> ApplyRunTerminal(NaradPersistedEvent persistedEvent, string status)
        => PatchNodeStatus(ResolveRunNodeId(persistedEvent), status);

    private static string ResolveRunNodeId(NaradPersistedEvent persistedEvent)
        => IsRoot(persistedEvent)
            ? NaradGraphSchema.RootNodeId
            : NaradGraphSchema.RunNodeId(persistedEvent.RunId ?? persistedEvent.RootRunId ?? string.Empty);

    private void UpsertNode(string nodeId, string label, string status, string kind = "tool")
    {
        if (!nodes.ContainsKey(nodeId))
        {
            nodeOrder.Add(nodeId);
        }

        nodes[nodeId] = new JsonObject
        {
            ["id"] = nodeId,
            ["nodeId"] = nodeId,
            ["label"] = label,
            ["status"] = status,
            ["kind"] = kind,
        };
    }

    private IReadOnlyList<JsonObject> PatchNodeStatus(string nodeId, string status)
    {
        if (!nodes.TryGetValue(nodeId, out JsonObject? existing))
        {
            return [];
        }

        existing["status"] = status;
        // Replace the whole nodes array instead of /nodes/{index}/status. Index patches break
        // as soon as a client graph diverges slightly from the server nodeOrder (lost delta,
        // reordered fold, or attach snapshot race) and surface as "Graph hydrate rejected."
        return
        [
            new JsonObject
            {
                ["op"] = "replace",
                ["path"] = "/nodes",
                ["value"] = SerializeNodes(),
            },
        ];
    }

    private bool TryAddEdge(string fromNodeId, string toNodeId, string kind)
    {
        string edgeId = $"{fromNodeId}->{toNodeId}:{kind}";
        if (!edgeKeys.Add(edgeId))
        {
            return false;
        }

        edges.Add(new JsonObject
        {
            ["id"] = edgeId,
            ["edgeId"] = edgeId,
            ["from"] = fromNodeId,
            ["to"] = toNodeId,
            ["fromNodeId"] = fromNodeId,
            ["toNodeId"] = toNodeId,
            ["kind"] = kind,
        });
        return true;
    }

    private JsonArray SerializeNodes()
        // Deep-clone: JsonNode instances can only have one parent. Status patches and
        // snapshots both call SerializeNodes; reusing owned nodes throws
        // "The node already has a parent" and takes down the Narad stream (HTTP 500).
        => new([.. nodeOrder.Select(nodeId => CloneJsonObject(nodes[nodeId]))]);

    private JsonArray SerializeEdges()
        => new([.. edges.Select(CloneJsonObject)]);

    private static JsonObject CloneJsonObject(JsonObject source)
        => JsonNode.Parse(source.ToJsonString())!.AsObject();

    private static JsonObject PatchAddNode(string nodeId, string label, string status, string kind = "tool")
        => new()
        {
            ["op"] = "add",
            ["path"] = "/nodes/-",
            ["value"] = new JsonObject
            {
                ["id"] = nodeId,
                ["nodeId"] = nodeId,
                ["label"] = label,
                ["status"] = status,
                ["kind"] = kind,
            },
        };

    private static JsonObject PatchAddEdge(string fromNodeId, string toNodeId, string kind)
        => new()
        {
            ["op"] = "add",
            ["path"] = "/edges/-",
            ["value"] = new JsonObject
            {
                ["id"] = $"{fromNodeId}->{toNodeId}:{kind}",
                ["edgeId"] = $"{fromNodeId}->{toNodeId}:{kind}",
                ["from"] = fromNodeId,
                ["to"] = toNodeId,
                ["fromNodeId"] = fromNodeId,
                ["toNodeId"] = toNodeId,
                ["kind"] = kind,
            },
        };

    private static bool IsRoot(NaradPersistedEvent persistedEvent)
        => string.Equals(persistedEvent.RunId, persistedEvent.RootRunId, StringComparison.Ordinal);
}

internal static class NaradGraphProjectionBuilder
{
    public static NaradGraphProjectionState Build(string rootRunId, IEnumerable<NaradPersistedEvent> rootHistory)
    {
        var graph = new NaradGraphProjectionState(rootRunId);
        foreach (NaradPersistedEvent persistedEvent in rootHistory
                     .Where(item => string.Equals(item.RootRunId, rootRunId, StringComparison.Ordinal))
                     .OrderBy(item => item.Sequence ?? 0))
        {
            _ = graph.Apply(persistedEvent);
        }

        return graph;
    }
}
