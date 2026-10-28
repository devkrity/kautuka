namespace Kautuka.Gateway.Infrastructure.Configuration;

/// <summary>
/// Controls which tool calls require human approval before execution.
/// Serialised as camelCase JSON in the approvalConfigJson column on agents.
/// </summary>
internal sealed class ApprovalPolicy
{
    /// <summary>
    /// List of MCP server IDs whose tools always require approval.
    /// </summary>
    public IReadOnlyList<string> RequireMcpIds { get; init; } = [];

    /// <summary>Seconds before a pending approval request is auto-rejected. 0 = no timeout.</summary>
    public int TimeoutSeconds { get; init; } = 300;

    /// <summary>
    /// Per-connector function filters. When a connector ID appears here AND in <see cref="RequireMcpIds"/>,
    /// only tool calls matching one of the glob patterns require approval instead of all tools.
    /// </summary>
    public IReadOnlyDictionary<string, IReadOnlyList<string>> RequireMcpFunctionFilters { get; init; } = new Dictionary<string, IReadOnlyList<string>>();

    /// <summary>
    /// Per-connector disabled tool lists. Tools listed here are hidden from the agent entirely
    /// and will never be called, regardless of approval settings.
    /// </summary>
    public IReadOnlyDictionary<string, IReadOnlyList<string>> DisabledMcpFunctions { get; init; } = new Dictionary<string, IReadOnlyList<string>>();

    /// <summary>
    /// Agent IDs that this agent (acting as caller) is permitted to invoke as tools.
    /// Only agents with <c>ExposedAsTool = true</c> are actually wired up at runtime.
    /// </summary>
    public IReadOnlyList<string> AgentToolIds { get; init; } = [];

    /// <summary>
    /// Subset of <see cref="AgentToolIds"/> whose invocations require human approval
    /// before the target agent runs (ask-first mode).
    /// </summary>
    public IReadOnlyList<string> ApprovalRequiredAgentToolIds { get; init; } = [];
}
