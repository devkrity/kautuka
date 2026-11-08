using Kautuka.Manager.Protos;
using Kautuka.Gateway.Domain.Stores;

namespace Kautuka.Gateway.Endpoints.Runtime;

internal static partial class GatewayNetworkAccessEndpointMappings
{
    internal static Func<NetworkAccessIdentityMessage, NetworkAccessIdentityDto> CreateIdentityDescriber(GatewayUser caller, IServiceProvider services)
    {
        var cache = new Dictionary<(string, string, string, string, string), NetworkAccessIdentityDto>();
        return value =>
        {
            var key = (value.ScopeType, value.UserId, value.AgentId, value.Role, value.ComponentId);
            if (!cache.TryGetValue(key, out NetworkAccessIdentityDto? result))
            {
                result = DescribeIdentity(value, caller, services);
                cache.Add(key, result);
            }
            return result;
        };
    }

    private static NetworkAccessIdentityDto DescribeIdentity(NetworkAccessIdentityMessage value, GatewayUser caller, IServiceProvider services)
    {
        IGatewayCatalogStore? catalog = services.GetService<IGatewayCatalogStore>();
        IGatewayUserStore? users = services.GetService<IGatewayUserStore>();
        GatewayAgent? agent = null;
        GatewayMcpServer? connector = null;
        GatewayUser? owner = null;
        catalog?.TryGetAgent(value.AgentId, out agent);
        if (agent is null && catalog is not null && value.ScopeType == "agent")
        {
            GatewayAgent[] matches = catalog.ListAgents()
                .Where(candidate => MatchesAgent(value, candidate) && GatewayOwnershipAccess.CanAccessAgent(caller, candidate))
                .Take(2).ToArray();
            if (matches.Length == 1) agent = matches[0];
        }
        if (value.Role is "connector" or "materializer") catalog?.TryGetMcpServer(ConnectorCatalogId(value), out connector);
        if (GatewayOwnershipAccess.IsAdmin(caller) || value.UserId == caller.Id) users?.TryGetUserById(value.UserId, out owner);
        return DescribeIdentity(value, caller, agent, connector, owner);
    }

    internal static NetworkAccessIdentityDto DescribeIdentity(NetworkAccessIdentityMessage value, GatewayUser caller,
        GatewayAgent? agent, GatewayMcpServer? connector, GatewayUser? owner)
    {
        bool admin = GatewayOwnershipAccess.IsAdmin(caller);
        if (!admin && (value.UserId != caller.Id || value.ScopeType == "core")) return Identity(value);
        string? agentName = agent is not null && MatchesAgent(value, agent) && GatewayOwnershipAccess.CanAccessAgent(caller, agent) ? agent.Name : null;
        string? connectorName = connector?.Id == ConnectorCatalogId(value) && GatewayOwnershipAccess.CanAccessConnector(caller, connector) ? connector.Name : null;
        string? userName = owner?.Id == value.UserId && (admin || owner.Id == caller.Id) ? owner.Username : null;
        string? displayName = value.Role switch
        {
            "materializer" when value.ScopeType == "core" && value.ComponentId == "image-acquisition" => "Container image downloads",
            "connector" or "materializer" => connectorName,
            "agent" => agentName,
            "core" when value.ScopeType == "core" => value.ComponentId switch
            {
                "gateway" => "Gateway",
                "webhost" => "Web interface",
                "channelhost" => "Channels",
                "credentialshost" => "Credentials",
                "kautuka-openobserve" => "Telemetry collector",
                _ => null,
            },
            "package" => "Package downloads",
            "telemetry" => "Telemetry collector",
            _ => null,
        };
        return new(value.ScopeType, value.UserId, value.AgentId, value.Role, value.ComponentId, displayName, agentName, userName);
    }

    private static bool MatchesAgent(NetworkAccessIdentityMessage value, GatewayAgent agent)
        => agent.Id == value.AgentId || (value.ScopeType == "agent"
            && (agent.OwnerUserId == value.UserId || agent.Scope == GatewayOwnershipScopes.System)
            && WorkspaceRuntimeContextResolver.BuildAgentWorkspaceScopeId(agent.Id, agent.OwnerUserId) == value.AgentId);

    private static string ConnectorCatalogId(NetworkAccessIdentityMessage value)
    {
        string prefix = value.UserId + "::";
        return value.ComponentId.StartsWith(prefix, StringComparison.Ordinal) ? value.ComponentId[prefix.Length..] : value.ComponentId;
    }
}
