namespace Kautuka.Gateway.Infrastructure.Configuration;

/// <summary>
/// Authoritative store for agent run records. GatewayState.Runs is a reconstructible projection.
/// </summary>
internal interface IAgentRuntimeRunRepository
{
    IReadOnlyList<GatewayRun> ListRuntimeRuns(string storageLocatorKey);

    void CreateRuntimeRun(string storageLocatorKey, GatewayRun run);

    void UpdateRuntimeRun(string storageLocatorKey, GatewayRun run);
}
