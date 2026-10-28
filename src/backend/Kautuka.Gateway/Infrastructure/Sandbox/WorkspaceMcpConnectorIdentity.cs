namespace Kautuka.Gateway.Infrastructure.Sandbox;

internal static class WorkspaceMcpConnectorIdentity
{
    internal const string SystemUserScope = "system";
    private const string connectorSeparator = "::";

    public static string BuildScopedConnectorId(string? userId, string connectorId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectorId);

        string normalizedUser = string.IsNullOrWhiteSpace(userId)
            ? SystemUserScope
            : userId.Trim();

        return $"{normalizedUser}{connectorSeparator}{connectorId.Trim()}";
    }
}
