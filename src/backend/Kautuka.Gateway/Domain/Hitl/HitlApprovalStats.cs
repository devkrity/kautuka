namespace Kautuka.Gateway.Domain.Hitl;

internal sealed record HitlApprovalStatsQuery(
    string? UserId,
    bool AdminAll,
    string? Since,
    string? AgentId,
    string? McpServerId,
    string? SkillId,
    string? ToolName = null,
    long? AgentDefinitionRevision = null,
    bool UngroupedRevisionOnly = false,
    IReadOnlyList<string>? ConnectorToolNames = null);

internal sealed class HitlApprovalStatRow
{
    public string ToolName { get; init; } = string.Empty;

    public string ToolType { get; init; } = string.Empty;

    public string? McpServerId { get; init; }

    public string? SkillId { get; init; }

    public long Approved { get; init; }

    public long Denied { get; init; }

    public string? LastDeniedAt { get; init; }

    public long AgentCount { get; init; }

    public long? AgentDefinitionRevision { get; init; }
}
