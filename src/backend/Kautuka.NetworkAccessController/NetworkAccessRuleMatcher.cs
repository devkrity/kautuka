namespace Kautuka.NetworkAccessController;

internal static class NetworkAccessRuleMatcher
{
    public static IReadOnlyList<NetworkAccessRule> FindApplicable(
        NetworkAccessPolicyState state,
        string? userId,
        string? agentId,
        string? connectorId,
        string destKind,
        string destination,
        int port,
        bool allowAnyPublic = false,
        bool includeSystemDomainFallback = false)
    {
        var matches = new List<NetworkAccessRule>();
        string? baseDomain = null;
        foreach (NetworkAccessRule rule in state.Rules)
        {
            if (IsPaused(state, rule))
                continue;
            bool targetMatches = rule.ScopeKind switch
            {
                "system" => true,
                "agent" => userId is not null && agentId is not null
                    && rule.UserId == userId && rule.ConnectorId is null
                    && (rule.AgentId == agentId || (includeSystemDomainFallback && userId == "system"
                        && rule.AgentId == "system" && rule.DestKind == "domain")),
                "user" => userId is not null && rule.UserId == userId && rule.AgentId is null
                    && (rule.ConnectorId is null || (connectorId is not null
                        && ConnectorForUser(rule.ConnectorId, userId) == ConnectorForUser(connectorId, userId))),
                _ => false,
            };
            if (!targetMatches)
                continue;

            bool exact = rule.DestKind == destKind && rule.Destination == destination && rule.Port == port;
            bool anyPublic = allowAnyPublic && rule.DestKind == "any-public" && rule.ConnectorId is not null;
            bool domainMatch = false;
            if (rule.DestKind == "domain" && destKind == "host" && rule.Port == port
                && (destination == rule.Destination || (destination.Length > rule.Destination.Length
                    && destination[destination.Length - rule.Destination.Length - 1] == '.'
                    && destination.EndsWith(rule.Destination, StringComparison.Ordinal))))
            {
                baseDomain ??= NetworkRegistrableDomain.TryGet(destination, out string domain) ? domain : string.Empty;
                domainMatch = baseDomain == rule.Destination;
            }
            if (exact || anyPublic || domainMatch)
                matches.Add(rule);
        }
        return matches;
    }

    public static bool IsPaused(NetworkAccessPolicyState state, NetworkAccessRule rule)
        => rule.Allowed && rule.ScopeKind is ("user" or "agent")
            && rule.UserId is not null
            && rule.UserId == rule.DecidedBy
            && !rule.DecidedByAdmin
            && !state.UserPrivileges.Any(p => p.UserId == rule.UserId && p.CanApproveOwn);

    internal static string ConnectorForUser(string connectorId, string userId)
    {
        string prefix = userId + "::";
        return connectorId.StartsWith(prefix, StringComparison.Ordinal) ? connectorId[prefix.Length..] : connectorId;
    }
}
