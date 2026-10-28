using System.Diagnostics;

using Grpc.Core;

using Kautuka.Gateway.Domain.Sandbox;
using Kautuka.Gateway.Infrastructure.Observability;
using Kautuka.Manager.Protos;

namespace Kautuka.Gateway.Infrastructure.Sandbox;

/// <summary>
/// AOT-compatible gRPC client for the Kautuka.Manager service.
/// Wraps the proto-first generated <see cref="ManagerService.ManagerServiceClient"/>.
/// </summary>
internal sealed class ManagerGrpcServiceClient(CallInvoker callInvoker, string managerGrpcAddress) : IManagerGrpcService
{
    internal static readonly TimeSpan DesiredStateMaterializationDeadline = TimeSpan.FromMinutes(31);
    private readonly ManagerService.ManagerServiceClient client = new(callInvoker);

    public Task<WorkspaceBoolResponse> PingAsync(WorkspacePingRequest request, CancellationToken cancellationToken = default)
        => Call("Ping", () => client.PingAsync(request, headers: KautukaGrpcCorrelationMetadata.CreateClientHeaders(), cancellationToken: cancellationToken).ResponseAsync);

    public Task<ManagerStatusResponse> GetHostStatusAsync(WorkspacePingRequest request, CancellationToken cancellationToken = default)
        => Call("GetHostStatus", () => client.GetHostStatusAsync(request, headers: KautukaGrpcCorrelationMetadata.CreateClientHeaders(), cancellationToken: cancellationToken).ResponseAsync);

    public Task<ManagerRuntimeContainersResponse> ListRuntimeContainersAsync(WorkspacePingRequest request, CancellationToken cancellationToken = default)
        => Call("ListRuntimeContainers", () => client.ListRuntimeContainersAsync(request, headers: KautukaGrpcCorrelationMetadata.CreateClientHeaders(), cancellationToken: cancellationToken).ResponseAsync);

    public Task<ManagerPkgsProxyCacheStatsResponse> GetPkgsProxyCacheStatsAsync(WorkspacePingRequest request, CancellationToken cancellationToken = default)
        => Call(
            "GetPkgsProxyCacheStats",
            () => client.GetPkgsProxyCacheStatsAsync(
                request,
                headers: KautukaGrpcCorrelationMetadata.CreateClientHeaders(),
                deadline: DateTime.UtcNow.AddSeconds(30),
                cancellationToken: cancellationToken).ResponseAsync);

    public Task<WorkspaceContextResponse> DescribeWorkspaceAsync(WorkspaceResolvedRequest request, CancellationToken cancellationToken = default)
        => Call("DescribeWorkspace", () => client.DescribeWorkspaceAsync(request, headers: KautukaGrpcCorrelationMetadata.CreateClientHeaders(), cancellationToken: cancellationToken).ResponseAsync);

    public Task<WorkspaceContextResponse> RebuildWorkspaceAsync(WorkspaceResolvedRequest request, CancellationToken cancellationToken = default)
        => Call("RebuildWorkspace", () => client.RebuildWorkspaceAsync(request, headers: KautukaGrpcCorrelationMetadata.CreateClientHeaders(), deadline: DateTime.UtcNow.AddMinutes(5), cancellationToken: cancellationToken).ResponseAsync);

    public Task<WorkspaceDeleteResponse> DeleteWorkspaceAsync(WorkspaceResolvedRequest request, CancellationToken cancellationToken = default)
        => Call("DeleteWorkspace", () => client.DeleteWorkspaceAsync(request, headers: KautukaGrpcCorrelationMetadata.CreateClientHeaders(), deadline: DateTime.UtcNow.AddMinutes(2), cancellationToken: cancellationToken).ResponseAsync);

    public Task<WorkspaceSandboxResponse> SpawnSandboxAsync(WorkspaceResolvedRequest request, CancellationToken cancellationToken = default)
        => Call("SpawnSandbox", () => client.SpawnSandboxAsync(request, headers: KautukaGrpcCorrelationMetadata.CreateClientHeaders(), deadline: DateTime.UtcNow.AddMinutes(5), cancellationToken: cancellationToken).ResponseAsync);

    public Task<WorkspaceSandboxResponse> InspectSandboxAsync(WorkspaceResolvedRequest request, CancellationToken cancellationToken = default)
        => Call("InspectSandbox", () => client.InspectSandboxAsync(request, headers: KautukaGrpcCorrelationMetadata.CreateClientHeaders(), cancellationToken: cancellationToken).ResponseAsync);

    public Task<WorkspaceExecutionResponse> ExecAsync(WorkspaceExecRequest request, CancellationToken cancellationToken = default)
        => Call("Exec", () => client.ExecAsync(request, headers: KautukaGrpcCorrelationMetadata.CreateClientHeaders(), deadline: DateTime.UtcNow.AddSeconds(BoundedTimeout(request.TimeoutSeconds, 30, 3600) + 15), cancellationToken: cancellationToken).ResponseAsync);

    public Task<WorkspaceBoolResponse> StopSandboxAsync(WorkspaceResolvedRequest request, CancellationToken cancellationToken = default)
        => Call("StopSandbox", () => client.StopSandboxAsync(request, headers: KautukaGrpcCorrelationMetadata.CreateClientHeaders(), deadline: DateTime.UtcNow.AddMinutes(2), cancellationToken: cancellationToken).ResponseAsync);

    public Task<WorkspaceBoolResponse> PrimeWorkspaceAsync(WorkspaceResolvedRequest request, CancellationToken cancellationToken = default)
        => Call("PrimeWorkspace", () => client.PrimeWorkspaceAsync(request, headers: KautukaGrpcCorrelationMetadata.CreateClientHeaders(), deadline: DateTime.UtcNow.AddMinutes(5), cancellationToken: cancellationToken).ResponseAsync);

    public Task<WorkspaceMcpProbeResponse> ProbeMcpContainerAsync(WorkspaceMcpContainerProbeRequest request, CancellationToken cancellationToken = default)
        => Call("ProbeMcpContainer", () => client.ProbeMcpContainerAsync(request, headers: KautukaGrpcCorrelationMetadata.CreateClientHeaders(), deadline: DateTime.UtcNow.AddSeconds(BoundedTimeout(request.TimeoutSeconds, 30, 600) + 15), cancellationToken: cancellationToken).ResponseAsync);

    public Task<WorkspaceMcpContainerListToolsResponse> ListMcpContainerToolsAsync(WorkspaceMcpContainerCallRequest request, CancellationToken cancellationToken = default)
        => Call("ListMcpContainerTools", () => client.ListMcpContainerToolsAsync(request, headers: KautukaGrpcCorrelationMetadata.CreateClientHeaders(), deadline: DateTime.UtcNow.AddSeconds(BoundedTimeout(request.TimeoutSeconds, 30, 600) + 15), cancellationToken: cancellationToken).ResponseAsync);

    public Task<WorkspaceMcpContainerCallResponse> CallMcpContainerToolAsync(WorkspaceMcpContainerCallRequest request, CancellationToken cancellationToken = default)
        => Call("CallMcpContainerTool", () => client.CallMcpContainerToolAsync(request, headers: KautukaGrpcCorrelationMetadata.CreateClientHeaders(), deadline: DateTime.UtcNow.AddSeconds(BoundedTimeout(request.TimeoutSeconds, 30, 3600) + 15), cancellationToken: cancellationToken).ResponseAsync);

    public Task<DesiredStateUpdateResponse> UpdateDesiredStateAsync(ManagerDesiredStateUpdateRequest request, CancellationToken cancellationToken = default)
        => Call("UpdateDesiredState", () => client.UpdateDesiredStateAsync(
            ToMessage(request),
            BuildDesiredStateCallOptions(DateTime.UtcNow, cancellationToken)).ResponseAsync);

    internal static CallOptions BuildDesiredStateCallOptions(DateTime utcNow, CancellationToken cancellationToken)
        => new(
            KautukaGrpcCorrelationMetadata.CreateClientHeaders(),
            deadline: utcNow.Add(DesiredStateMaterializationDeadline),
            cancellationToken: cancellationToken);

    private static WorkspaceDesiredStateUpdate ToMessage(ManagerDesiredStateUpdateRequest request)
    {
        var message = new WorkspaceDesiredStateUpdate
        {
            Runtime = ToMessage(request.DesiredState.RuntimeSettings),
            DesiredStateVersion = request.DesiredStateVersion,
            SyncReason = request.SyncReason,
            RequestId = request.RequestId,
            PublisherEpoch = request.PublisherEpoch,
        };

        message.DesiredWorkspaces.AddRange(request.DesiredState.Workspaces.Select(ToMessage));
        message.ContainerConnectors.AddRange(request.DesiredState.ContainerConnectors.Select(ToMessage));
        message.SandboxConnectorBindings.AddRange(request.DesiredState.SandboxConnectorBindings.Select(ToMessage));
        return message;
    }

    private static WorkspaceSandboxConnectorBindingsMessage ToMessage(WorkspaceSandboxConnectorBindingsPayload binding)
    {
        var message = new WorkspaceSandboxConnectorBindingsMessage
        {
            ScopeType = binding.ScopeType,
            ScopeId = binding.ScopeId,
        };

        message.ConnectorIds.AddRange(binding.ConnectorIds);
        return message;
    }

    private static ManagerRuntimeSettingsMessage ToMessage(ManagerRuntimeSettingsPayload runtimeSettings)
        => new()
        {
            WorkspaceBaseRoot = runtimeSettings.WorkspaceBaseRoot,
            Image = runtimeSettings.Image,
            DefaultCpuLimit = runtimeSettings.DefaultCpuLimit,
            DefaultMemoryLimit = runtimeSettings.DefaultMemoryLimit,
            DefaultTimeoutSeconds = runtimeSettings.DefaultTimeoutSeconds,
            TracesEndpoint = runtimeSettings.TracesEndpoint ?? string.Empty,
            LogsEndpoint = runtimeSettings.LogsEndpoint ?? string.Empty,
            MetricsEndpoint = runtimeSettings.MetricsEndpoint ?? string.Empty,
            OtlpProtocol = runtimeSettings.OtlpProtocol ?? string.Empty,
            OtlpHeaders = runtimeSettings.OtlpHeaders ?? string.Empty,
        };

    private static WorkspaceContextMessage ToMessage(WorkspaceRuntimeContext workspace)
    {
        var message = new WorkspaceContextMessage
        {
            ScopeType = workspace.ScopeType,
            ScopeId = workspace.ScopeId,
            UserId = workspace.UserId ?? string.Empty,
            HostWorkspacePath = workspace.HostWorkspacePath,
            ContainerWorkspacePath = workspace.ContainerWorkspacePath,
            Image = workspace.Image,
            DefaultTimeoutSeconds = workspace.DefaultTimeoutSeconds,
        };

        message.BaseCapabilities.AddRange(workspace.BaseCapabilities);
        message.ContainerEnvironment.AddRange(workspace.ContainerEnvironment.Select(static pair => new WorkspaceKvPair
        {
            Key = pair.Key,
            Value = pair.Value,
        }));

        return message;
    }

    private static WorkspaceMcpContainerConnectorMessage ToMessage(WorkspaceMcpContainerConnectorPayload connector)
    {
        var message = new WorkspaceMcpContainerConnectorMessage
        {
            ConnectorId = connector.ConnectorId,
            Image = connector.Image,
            Command = connector.Command,
            ReadinessTimeoutSeconds = connector.ReadinessTimeoutSeconds,
            JitCooldownSeconds = connector.JitCooldownSeconds,
            Enabled = connector.Enabled,
            BundlePath = connector.BundlePath ?? string.Empty,
            ServerType = connector.ServerType ?? string.Empty,
            IsInternalSystemConnector = connector.IsInternalSystemConnector,
            McpProtocolVersion = connector.McpProtocolVersion ?? string.Empty,
        };

        message.Args.AddRange(connector.Args);
        message.Environment.AddRange(connector.Environment.Select(static pair => new WorkspaceKvPair
        {
            Key = pair.Key,
            Value = pair.Value,
        }));

        return message;
    }

    private async Task<T> Call<T>(string methodName, Func<Task<T>> invoke)
    {
        using Activity? activity = AgentRunHostCallTracer.StartGrpcClientActivity(
            KautukaActivitySources.Component.Manager,
            methodName,
            managerGrpcAddress);

        try
        {
            T response = await invoke().ConfigureAwait(false);
            AgentRunHostCallTracer.MarkSuccess(activity);
            return response;
        }
        catch (RpcException ex) when (ex.StatusCode == StatusCode.Unavailable)
        {
            AgentRunHostCallTracer.MarkFailure(activity, ex);
            throw new InvalidOperationException(
                $"Kautuka.Manager gRPC endpoint '{managerGrpcAddress}' is unavailable. Ensure Kautuka.Manager is running and listening.", ex);
        }
        catch (Exception ex)
        {
            AgentRunHostCallTracer.MarkFailure(activity, ex);
            throw;
        }
    }

    private static int BoundedTimeout(int requestedSeconds, int defaultSeconds, int maximumSeconds)
        => Math.Clamp(requestedSeconds > 0 ? requestedSeconds : defaultSeconds, 1, maximumSeconds);
}
