using Kautuka.Manager.Protos;

namespace Kautuka.Gateway.Infrastructure.Agents;

internal static class RemoteMcpNetworkAuthorization
{
    internal const string RemoteConnectorRole = "remote-connector";
    internal const string ManagementAgentId = "management";

    internal static NetworkAccessIdentityMessage CreateIdentity(string scopeType, string userId, string agentId, string connectorId)
    {
        if (scopeType == "agent")
        {
            return new NetworkAccessIdentityMessage
            {
                ScopeType = "agent",
                UserId = userId,
                AgentId = agentId,
                Role = "agent",
                ComponentId = agentId,
            };
        }

        if (scopeType != "management")
            throw new ArgumentException("Remote MCP network authorization requires an agent or management scope.", nameof(scopeType));

        return new NetworkAccessIdentityMessage
        {
            ScopeType = "management",
            UserId = userId,
            AgentId = ManagementAgentId,
            Role = RemoteConnectorRole,
            ComponentId = WorkspaceMcpConnectorIdentity.BuildScopedConnectorId(userId, connectorId),
        };
    }

    internal static Func<Uri, string?, CancellationToken, Task<RemoteConnectorNetworkResponse>> CreateCheck(
        IManagerGrpcService manager,
        string userId,
        bool isAdmin,
        NetworkAccessIdentityMessage identity)
    {
        ArgumentNullException.ThrowIfNull(manager);
        ArgumentNullException.ThrowIfNull(identity);
        return (endpoint, pinnedIpv4, cancellationToken) => manager.CheckRemoteConnectorNetworkAsync(
            new RemoteConnectorNetworkRequest
            {
                Actor = new NetworkAccessActorMessage { UserId = userId, IsAdmin = isAdmin },
                Identity = identity.Clone(),
                Scheme = endpoint.Scheme,
                Hostname = endpoint.IdnHost,
                Port = endpoint.Port,
                PinnedIpv4 = pinnedIpv4 ?? string.Empty,
            }, cancellationToken);
    }
}

internal sealed class RemoteMcpNetworkRouteState
{
    private readonly object sync = new();
    private readonly bool legacyAllowPrivateDestination;
    private string? routeBinding;
    private bool? authorizedMode;
    private bool allowPrivateDestination;

    internal RemoteMcpNetworkRouteState(bool legacyAllowPrivateDestination)
    {
        this.legacyAllowPrivateDestination = legacyAllowPrivateDestination;
        allowPrivateDestination = legacyAllowPrivateDestination;
    }

    internal bool AllowPrivateDestination
    {
        get { lock (sync) return allowPrivateDestination; }
    }

    internal void Accept(RemoteConnectorNetworkResponse response)
    {
        lock (sync)
        {
            if (authorizedMode is bool previousMode && previousMode != response.Enabled)
                throw new RemoteMcpConfigurationException("Remote MCP network authorization mode changed for this connection. Reconnect and retry.");

            if (!response.Enabled)
            {
                authorizedMode = false;
                allowPrivateDestination = legacyAllowPrivateDestination;
                return;
            }

            if (!string.Equals(response.Decision, "allowed", StringComparison.Ordinal)
                || string.IsNullOrWhiteSpace(response.RouteBinding))
                throw new RemoteMcpConfigurationException("Remote MCP network authorization is incomplete. No request was sent.");

            if (routeBinding is null)
                routeBinding = response.RouteBinding;
            else if (!string.Equals(routeBinding, response.RouteBinding, StringComparison.Ordinal))
                throw new RemoteMcpConfigurationException("Remote MCP network permissions changed for this connection. Reconnect and retry.");

            authorizedMode = true;
            allowPrivateDestination = response.AllowPrivateNetwork;
        }
    }
}
