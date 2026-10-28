using System.Text.Json;

using Kautuka.Gateway.Domain.Execution;

namespace Kautuka.Gateway.Infrastructure.Agents;

/// <summary>
/// Recovers HITL grouping keys without changing the MAF interrupt shape.
/// MCP server id is known at wrap time but is not present on packed TARC;
/// this lookup uses the same approval policy the wrap used.
/// </summary>
internal static class HitlApprovalIdentity
{
    public static string? ResolveMcpServerId(PlatformConfiguration config, string agentId, string toolName)
    {
        if (string.IsNullOrWhiteSpace(agentId)
            || string.IsNullOrWhiteSpace(toolName)
            || !string.Equals(ActionIdentity.ResolveKind(toolName), "connector", StringComparison.Ordinal))
        {
            return null;
        }

        AgentDefinition? agent = config.Agents.FirstOrDefault(candidate =>
            string.Equals(candidate.Id, agentId, StringComparison.Ordinal));
        if (agent is null)
        {
            return null;
        }

        IReadOnlyList<string> boundIds = [.. agent.McpBindings
            .Select(static binding => binding.ConnectorId)
            .Where(static id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.OrdinalIgnoreCase)];

        List<string> gatedMatches = [];
        foreach (string serverId in boundIds)
        {
            ToolApprovalDirective directive = ToolExecutionPolicyService.EvaluateMcpToolApprovalCore(
                agent.ApprovalPolicy,
                serverId,
                toolName);
            if (directive.Include && directive.RequiresApproval)
            {
                gatedMatches.Add(serverId);
            }
        }

        if (gatedMatches.Count == 1)
        {
            return gatedMatches[0];
        }

        if (gatedMatches.Count > 1)
        {
            return null;
        }

        List<string> discoveredMatches = [];
        foreach (string serverId in boundIds)
        {
            McpServerDefinition? server = config.McpServers.FirstOrDefault(candidate =>
                string.Equals(candidate.Id, serverId, StringComparison.Ordinal));
            if (server?.ToolNames is { Count: > 0 }
                && server.ToolNames.Contains(toolName, StringComparer.OrdinalIgnoreCase))
            {
                discoveredMatches.Add(serverId);
            }
        }

        if (discoveredMatches.Count == 1)
        {
            return discoveredMatches[0];
        }

        // Unique owner across the catalog when binding-scoped discovery is empty/ambiguous.
        List<string> catalogMatches = [];
        foreach (McpServerDefinition server in config.McpServers)
        {
            if (server.ToolNames is { Count: > 0 }
                && server.ToolNames.Contains(toolName, StringComparer.OrdinalIgnoreCase)
                && !string.IsNullOrWhiteSpace(server.Id))
            {
                catalogMatches.Add(server.Id);
            }
        }

        return catalogMatches.Count == 1 ? catalogMatches[0] : null;
    }

    public static string? TryReadSkillId(string? argumentsJson)
    {
        if (string.IsNullOrWhiteSpace(argumentsJson))
        {
            return null;
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(argumentsJson);
            if (!document.RootElement.TryGetProperty("skillId", out JsonElement skillIdElement))
            {
                return null;
            }

            string? skillId = skillIdElement.GetString();
            return string.IsNullOrWhiteSpace(skillId) ? null : skillId;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
