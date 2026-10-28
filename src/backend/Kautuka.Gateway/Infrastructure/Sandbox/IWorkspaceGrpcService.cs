using Kautuka.Manager.Protos;

namespace Kautuka.Gateway.Infrastructure.Sandbox;

internal sealed record ManagerDesiredStateUpdateRequest(
    WorkspaceDesiredStatePayload DesiredState,
    long DesiredStateVersion,
    WorkspaceDesiredStateSyncReason SyncReason,
    string RequestId,
    string PublisherEpoch);

/// <summary>
/// Abstraction over the Kautuka.Manager gRPC service used by Gateway.
/// Implemented by <see cref="ManagerGrpcServiceClient"/>.
/// </summary>
internal interface IManagerGrpcService
{
    Task<WorkspaceBoolResponse> PingAsync(WorkspacePingRequest request, CancellationToken cancellationToken = default);
    Task<ManagerStatusResponse> GetHostStatusAsync(WorkspacePingRequest request, CancellationToken cancellationToken = default);
    Task<ManagerRuntimeContainersResponse> ListRuntimeContainersAsync(WorkspacePingRequest request, CancellationToken cancellationToken = default);
    Task<ManagerPkgsProxyCacheStatsResponse> GetPkgsProxyCacheStatsAsync(WorkspacePingRequest request, CancellationToken cancellationToken = default)
        => Task.FromException<ManagerPkgsProxyCacheStatsResponse>(new NotSupportedException());
    Task<WorkspaceContextResponse> DescribeWorkspaceAsync(WorkspaceResolvedRequest request, CancellationToken cancellationToken = default);
    Task<WorkspaceContextResponse> RebuildWorkspaceAsync(WorkspaceResolvedRequest request, CancellationToken cancellationToken = default);
    Task<WorkspaceDeleteResponse> DeleteWorkspaceAsync(WorkspaceResolvedRequest request, CancellationToken cancellationToken = default);
    Task<WorkspaceSandboxResponse> SpawnSandboxAsync(WorkspaceResolvedRequest request, CancellationToken cancellationToken = default);
    Task<WorkspaceSandboxResponse> InspectSandboxAsync(WorkspaceResolvedRequest request, CancellationToken cancellationToken = default);
    Task<WorkspaceExecutionResponse> ExecAsync(WorkspaceExecRequest request, CancellationToken cancellationToken = default);
    Task<WorkspaceBoolResponse> StopSandboxAsync(WorkspaceResolvedRequest request, CancellationToken cancellationToken = default);
    Task<WorkspaceBoolResponse> PrimeWorkspaceAsync(WorkspaceResolvedRequest request, CancellationToken cancellationToken = default);
    Task<WorkspaceMcpProbeResponse> ProbeMcpContainerAsync(WorkspaceMcpContainerProbeRequest request, CancellationToken cancellationToken = default);
    Task<WorkspaceMcpContainerListToolsResponse> ListMcpContainerToolsAsync(WorkspaceMcpContainerCallRequest request, CancellationToken cancellationToken = default);
    Task<WorkspaceMcpContainerCallResponse> CallMcpContainerToolAsync(WorkspaceMcpContainerCallRequest request, CancellationToken cancellationToken = default);
    Task<DesiredStateUpdateResponse> UpdateDesiredStateAsync(ManagerDesiredStateUpdateRequest request, CancellationToken cancellationToken = default);
}
