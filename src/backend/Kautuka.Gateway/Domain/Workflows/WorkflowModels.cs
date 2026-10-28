using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Kautuka.Gateway.Domain.Workflows;

internal static class WorkflowInstanceStatuses
{
    public const string Running = "running";
    public const string Paused = "paused";
    public const string Finished = "finished";
    public const string Error = "error";
    public const string Cancelled = "cancelled";

    public static bool IsTerminal(string status) =>
        status is Finished or Error or Cancelled;
}

internal static class WorkflowStepAttemptStatuses
{
    public const string Pending = "pending";
    public const string Running = "running";
    public const string Finished = "finished";
    public const string Failed = "failed";
    public const string Cancelled = "cancelled";
}

internal sealed record GatewayWorkflowMap(string From, string To);

internal sealed record GatewayWorkflowNode(
    string NodeId,
    string AgentId,
    string PromptTemplate,
    string OutputKind,
    JsonObject? OutputSchema,
    GatewayWorkflowMap[] Maps,
    string SideEffects,
    bool MemoryWrites,
    bool RequiresReview,
    int MaxRetries,
    GatewayWorkflowSwitchCase[]? SwitchCases = null,
    string? DefaultGoTo = null)
{
    public GatewayWorkflowSwitchCase[] EffectiveSwitchCases => SwitchCases ?? [];

    public bool HasSwitch => EffectiveSwitchCases.Length > 0 || !string.IsNullOrWhiteSpace(DefaultGoTo);
}

internal sealed record GatewayWorkflowGraph(
    JsonObject InputSchema,
    JsonObject WorkingSchema,
    JsonObject OutputSchema,
    GatewayWorkflowNode[] Nodes);

internal sealed record GatewayWorkflowStepTestResponse(JsonObject? Results, string? Error, string[] DroppedKeys);

internal sealed record GatewayWorkflowDefinition(
    string Id,
    string OwnerUserId,
    string Name,
    bool Governed,
    int Revision,
    JsonObject InputSchema,
    JsonObject WorkingSchema,
    JsonObject OutputSchema,
    GatewayWorkflowNode[] Nodes,
    string CreatedAt,
    string UpdatedAt,
    bool Archived = false,
    bool HasRuns = false,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Diagram = null);

internal sealed record GatewayWorkflowStepAttempt(
    string Id,
    string InstanceId,
    string NodeId,
    int Attempt,
    string RunId,
    string? SessionId,
    string Status,
    string? FailureCode,
    string? StartedAt,
    string? EndedAt,
    string CreatedAt)
{
    public string? ReviewNote { get; init; }
}

internal sealed record GatewayWorkflowInstance(
    string Id,
    string WorkflowId,
    int DefinitionRevision,
    string OwnerUserId,
    string? CorrelationKey,
    string Status,
    bool StopRequested,
    string? CurrentNodeId,
    JsonObject Envelope,
    string EnvelopeHash,
    int EnvelopeRevision,
    GatewayWorkflowDefinition Snapshot,
    GatewayWorkflowStepAttempt[] Steps,
    string? FailureCode,
    string? Error,
    string CreatedAt,
    string UpdatedAt,
    string? StartedAt,
    string? EndedAt);

internal sealed record WorkflowArtifact(
    string Id,
    string InstanceId,
    string Sha256,
    string Name,
    string StoragePath,
    string CreatedAt);

/// <summary>
/// HTTP shape for instances. Envelope and snapshot are <see cref="JsonElement"/> so Native AOT
/// source-gen does not have to compose nested <see cref="JsonObject"/> graphs.
/// </summary>
internal sealed record GatewayWorkflowInstanceResponse(
    string Id,
    string WorkflowId,
    int DefinitionRevision,
    string OwnerUserId,
    string? CorrelationKey,
    string Status,
    bool StopRequested,
    string? CurrentNodeId,
    JsonElement Envelope,
    string EnvelopeHash,
    int EnvelopeRevision,
    JsonElement Snapshot,
    GatewayWorkflowStepAttempt[] Steps,
    string? FailureCode,
    string? Error,
    string CreatedAt,
    string UpdatedAt,
    string? StartedAt,
    string? EndedAt);

internal static class WorkflowDefinitionCodec
{
    public static WorkflowDefinitionDraft ToDraft(GatewayWorkflowDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        return new WorkflowDefinitionDraft(
            definition.Id,
            definition.Name,
            definition.Governed,
            definition.InputSchema,
            definition.WorkingSchema,
            definition.OutputSchema,
            [.. definition.Nodes.Select(ToNode)]);
    }

    public static GatewayWorkflowGraph ToGraph(GatewayWorkflowDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        return new GatewayWorkflowGraph(
            definition.InputSchema,
            definition.WorkingSchema,
            definition.OutputSchema,
            definition.Nodes);
    }

    public static GatewayWorkflowDefinition FromGraph(
        string id,
        string ownerUserId,
        string name,
        bool governed,
        int revision,
        GatewayWorkflowGraph graph,
        string createdAt,
        string updatedAt,
        bool archived = false)
    {
        ArgumentNullException.ThrowIfNull(graph);
        return new GatewayWorkflowDefinition(
            id,
            ownerUserId,
            name,
            governed,
            revision,
            graph.InputSchema,
            graph.WorkingSchema,
            graph.OutputSchema,
            graph.Nodes,
            createdAt,
            updatedAt,
            archived);
    }

    public static WorkflowOutputKind ParseOutputKind(string? value)
    {
        if (string.Equals(value, "text", StringComparison.OrdinalIgnoreCase))
        {
            return WorkflowOutputKind.Text;
        }

        return WorkflowOutputKind.Schema;
    }

    public static WorkflowSideEffects ParseSideEffects(string? value)
    {
        if (string.Equals(value, "idempotent", StringComparison.OrdinalIgnoreCase))
        {
            return WorkflowSideEffects.Idempotent;
        }

        if (string.Equals(value, "external", StringComparison.OrdinalIgnoreCase))
        {
            return WorkflowSideEffects.External;
        }

        return WorkflowSideEffects.None;
    }

    public static string FormatOutputKind(WorkflowOutputKind kind)
        => kind == WorkflowOutputKind.Text ? "text" : "schema";

    public static string FormatSideEffects(WorkflowSideEffects sideEffects)
        => sideEffects switch
        {
            WorkflowSideEffects.Idempotent => "idempotent",
            WorkflowSideEffects.External => "external",
            _ => "none",
        };

    private static WorkflowNodeDefinition ToNode(GatewayWorkflowNode node)
        => new(
            node.NodeId,
            node.AgentId,
            node.PromptTemplate,
            ParseOutputKind(node.OutputKind),
            node.OutputSchema,
            [.. node.Maps.Select(static map => new WorkflowStateMap(map.From, map.To))],
            ParseSideEffects(node.SideEffects),
            node.MemoryWrites,
            node.RequiresReview,
            node.MaxRetries,
            node.EffectiveSwitchCases,
            node.DefaultGoTo);
}
