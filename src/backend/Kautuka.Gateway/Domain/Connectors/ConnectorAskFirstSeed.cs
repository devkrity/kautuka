namespace Kautuka.Gateway.Domain.Connectors;

/// <summary>
/// Adds an Ask me first tool once. A later Allowed or Blocked choice is left alone.
/// </summary>
internal static class ConnectorAskFirstSeed
{
    public static GatewayApprovalConfigPayload? Next(
        GatewayApprovalConfigPayload current,
        string connectorId,
        string toolName,
        ISet<string> seededAgentIds,
        string agentId)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(seededAgentIds);
        if (seededAgentIds.Contains(agentId))
        {
            return null;
        }

        seededAgentIds.Add(agentId);
        if (Contains(current.DisabledMcpFunctions, connectorId, toolName)
            || Contains(current.RequireMcpFunctionFilters, connectorId, toolName))
        {
            return null;
        }

        var filters = new Dictionary<string, string[]>(current.RequireMcpFunctionFilters, StringComparer.OrdinalIgnoreCase);
        filters.TryGetValue(connectorId, out string[]? existing);
        var tools = new List<string>(existing ?? []);
        if (!tools.Contains(toolName, StringComparer.Ordinal))
        {
            tools.Add(toolName);
        }

        filters[connectorId] = [.. tools];
        return new GatewayApprovalConfigPayload
        {
            RequireMcpIds = current.RequireMcpIds,
            TimeoutSeconds = current.TimeoutSeconds,
            RequireMcpFunctionFilters = filters,
            DisabledMcpFunctions = current.DisabledMcpFunctions,
            AgentToolIds = current.AgentToolIds,
            ApprovalRequiredAgentToolIds = current.ApprovalRequiredAgentToolIds,
        };
    }

    private static bool Contains(Dictionary<string, string[]> map, string connectorId, string toolName)
    {
        foreach ((string key, string[] tools) in map)
        {
            if (!string.Equals(key, connectorId, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            foreach (string tool in tools)
            {
                if (string.Equals(tool, toolName, StringComparison.Ordinal))
                {
                    return true;
                }
            }
        }

        return false;
    }
}
