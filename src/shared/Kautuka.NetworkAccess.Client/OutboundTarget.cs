namespace Kautuka.NetworkAccess.Client;

public sealed record OutboundTarget(
    string Host,
    int Port,
    string Scheme = "https",
    string? Service = null,
    string? Purpose = null,
    string? UserId = null,
    string? AgentId = null,
    string? ConnectorId = null,
    bool Systemwide = false,
    string? CorrelationId = null,
    string ExecutionContext = "app")
{
    public OutboundTarget MergeContext(NetworkAccessScope? scope)
    {
        if (scope is null)
        {
            return this;
        }

        return this with
        {
            UserId = UserId ?? scope.UserId,
            AgentId = AgentId ?? scope.AgentId,
            ConnectorId = ConnectorId ?? scope.ConnectorId,
            Purpose = Purpose ?? scope.Purpose,
            Service = Service ?? scope.Service,
            Systemwide = Systemwide || scope.Systemwide,
            ExecutionContext = string.IsNullOrWhiteSpace(ExecutionContext) || ExecutionContext == "app"
                ? scope.ExecutionContext
                : ExecutionContext,
        };
    }
}
