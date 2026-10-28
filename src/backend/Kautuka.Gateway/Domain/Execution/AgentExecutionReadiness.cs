namespace Kautuka.Gateway.Domain.Execution;

internal interface IAgentExecutionDependencyProbe
{
    string Dependency { get; }

    Task<AgentExecutionDependencyStatus> CheckAsync(CancellationToken cancellationToken);
}

internal sealed record AgentExecutionDependencyStatus(
    string Dependency,
    bool Ready,
    string Code,
    string Message,
    string? Detail = null);

internal sealed record AgentExecutionReadiness(
    bool Ready,
    string Code,
    string Message,
    IReadOnlyList<AgentExecutionDependencyStatus> Dependencies);

internal interface IAgentExecutionReadinessService
{
    Task<AgentExecutionReadiness> CheckAsync(CancellationToken cancellationToken);
}

internal sealed class AgentExecutionReadinessService(
    IEnumerable<IAgentExecutionDependencyProbe> dependencyProbes) : IAgentExecutionReadinessService
{
    private readonly IAgentExecutionDependencyProbe[] dependencyProbes =
        [.. dependencyProbes.OrderBy(static probe => probe.Dependency, StringComparer.Ordinal)];

    public async Task<AgentExecutionReadiness> CheckAsync(CancellationToken cancellationToken)
    {
        Task<AgentExecutionDependencyStatus>[] checks =
            [.. dependencyProbes.Select(probe => probe.CheckAsync(cancellationToken))];
        AgentExecutionDependencyStatus[] statuses = await Task.WhenAll(checks).ConfigureAwait(false);
        AgentExecutionDependencyStatus? unavailable = statuses.FirstOrDefault(static status => !status.Ready);

        return unavailable is null
            ? new AgentExecutionReadiness(
                Ready: true,
                Code: "READY",
                Message: "Agent execution dependencies are ready.",
                Dependencies: statuses)
            : new AgentExecutionReadiness(
                Ready: false,
                Code: unavailable.Code,
                Message: unavailable.Message,
                Dependencies: statuses);
    }
}
