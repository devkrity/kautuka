namespace Kautuka.NetworkAccess.Client;

/// <summary>
/// Immutable snapshot of contextual ambient identity for outbound network access checks.
/// </summary>
public sealed record NetworkAccessScope(
    string? UserId,
    string? AgentId = null,
    string? ConnectorId = null,
    string? Purpose = null,
    string? Service = null,
    bool Systemwide = false,
    string ExecutionContext = "app");

/// <summary>
/// Provides an AsyncLocal ambient scope for attributing outbound HTTP/WebSocket requests to users, agents, and connectors.
/// </summary>
public static class NetworkAccessContext
{
    private static readonly AsyncLocal<NetworkAccessScope?> currentScope = new();

    public static NetworkAccessScope? Current => currentScope.Value;

    public static IDisposable BeginScope(
        string? userId,
        string? agentId = null,
        string? connectorId = null,
        string? purpose = null,
        string? service = null,
        bool systemwide = false,
        string executionContext = "app")
    {
        var scope = new NetworkAccessScope(
            UserId: userId,
            AgentId: agentId,
            ConnectorId: connectorId,
            Purpose: purpose,
            Service: service,
            Systemwide: systemwide,
            ExecutionContext: string.IsNullOrWhiteSpace(executionContext) ? "app" : executionContext);

        return BeginScope(scope);
    }

    public static IDisposable BeginScope(NetworkAccessScope scope)
    {
        ArgumentNullException.ThrowIfNull(scope);
        NetworkAccessScope? previous = currentScope.Value;
        currentScope.Value = scope;
        return new ScopeRestorer(previous);
    }

    private sealed class ScopeRestorer(NetworkAccessScope? previous) : IDisposable
    {
        private bool disposed;

        public void Dispose()
        {
            if (!disposed)
            {
                currentScope.Value = previous;
                disposed = true;
            }
        }
    }
}
