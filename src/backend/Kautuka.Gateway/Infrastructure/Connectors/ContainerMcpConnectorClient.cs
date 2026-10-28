using Grpc.Core;

using Kautuka.Manager.Protos;

namespace Kautuka.Gateway.Infrastructure.Connectors;

/// <summary>
/// Abstracts containerized MCP connector operations and routes them to Kautuka.Manager.
/// Handles scope binding and error mapping for the Gateway layer.
/// </summary>
internal interface IContainerMcpConnectorClient
{
    /// <summary>
    /// Probe a containerized MCP connector for availability and tool count.
    /// </summary>
    Task<ContainerMcpProbeResult> ProbeMcpConnectorAsync(
        string connectorId,
        string image,
        string command,
        IReadOnlyList<string> args,
        string? bundlePath,
        string? serverType,
        IReadOnlyList<KeyValuePair<string, string>> environment,
        int timeoutSeconds,
        CancellationToken cancellationToken,
        string? userId = null);

    /// <summary>
    /// List tools available from a containerized MCP connector.
    /// </summary>
    Task<ContainerMcpToolListResult> ListMcpConnectorToolsAsync(
        string connectorId,
        IReadOnlyList<KeyValuePair<string, string>> environment,
        int timeoutSeconds,
        CancellationToken cancellationToken,
        bool allowInternalSystemConnector = false,
        string? userId = null);

    /// <summary>
    /// Invoke a tool on a containerized MCP connector.
    /// </summary>
    Task<ContainerMcpToolCallResult> CallMcpConnectorToolAsync(
        string connectorId,
        IReadOnlyList<KeyValuePair<string, string>> environment,
        string toolName,
        string toolInputJson,
        int timeoutSeconds,
        CancellationToken cancellationToken,
        bool allowInternalSystemConnector = false,
        string? userId = null);
}

/// <summary>
/// Result from probing a containerized MCP connector.
/// </summary>
internal sealed record ContainerMcpProbeResult(
    bool Ok,
    string Message,
    int ToolCount,
    IReadOnlyList<string> ToolNames,
    string? UnavailabilityReason);

/// <summary>
/// Result from listing tools on a containerized MCP connector.
/// </summary>
internal sealed record ContainerMcpToolListResult(
    bool Ok,
    string Message,
    int ToolCount,
    IReadOnlyList<string> ToolNames,
    string? UnavailabilityReason);

/// <summary>
/// Result from invoking a tool on a containerized MCP connector.
/// </summary>
internal sealed record ContainerMcpToolCallResult(
    bool Ok,
    string Message,
    string? ResultJson,
    string? UnavailabilityReason);

internal sealed class ContainerMcpConnectorClient(
    IManagerGrpcService workspaceGrpcService,
    WorkspaceRuntimeContextResolver contextResolver,
    ILogger<ContainerMcpConnectorClient> logger) : IContainerMcpConnectorClient
{
    private readonly IManagerGrpcService workspaceGrpcService = workspaceGrpcService ?? throw new ArgumentNullException(nameof(workspaceGrpcService));
    private readonly WorkspaceRuntimeContextResolver contextResolver = contextResolver ?? throw new ArgumentNullException(nameof(contextResolver));
    private readonly ILogger<ContainerMcpConnectorClient> logger = logger;

    public async Task<ContainerMcpProbeResult> ProbeMcpConnectorAsync(
        string connectorId,
        string image,
        string command,
        IReadOnlyList<string> args,
        string? bundlePath,
        string? serverType,
        IReadOnlyList<KeyValuePair<string, string>> environment,
        int timeoutSeconds,
        CancellationToken cancellationToken,
        string? userId = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectorId);
        ArgumentException.ThrowIfNullOrWhiteSpace(image);

        try
        {
            var runtimeSettings = contextResolver.ResolveRuntimeSettings();
            var runtimeMessage = ToRuntimeMessage(runtimeSettings);

            string scopedConnectorId = WorkspaceMcpConnectorIdentity.BuildScopedConnectorId(userId, connectorId);

            var request = new WorkspaceMcpContainerProbeRequest
            {
                Runtime = runtimeMessage,
                ConnectorId = scopedConnectorId,
                Image = image,
                Command = command,
                TimeoutSeconds = timeoutSeconds > 0 ? timeoutSeconds : 30,
                BundlePath = string.IsNullOrWhiteSpace(bundlePath) ? string.Empty : bundlePath.Trim(),
                ServerType = string.IsNullOrWhiteSpace(serverType) ? string.Empty : serverType.Trim(),
            };

            request.Args.AddRange(args);
            request.Environment.AddRange(environment.Select(pair => new WorkspaceKvPair { Key = pair.Key, Value = pair.Value }));

            var response = await workspaceGrpcService.ProbeMcpContainerAsync(request, cancellationToken).ConfigureAwait(false);

            return new ContainerMcpProbeResult(
                response.Ok,
                response.Message,
                response.ToolCount,
                [.. response.Tools.Select(t => t.Name)],
                null);
        }
        catch (RpcException ex)
        {
            ContainerMcpConnectorClientLog.ProbeFailed(logger, ex, connectorId);
            return new ContainerMcpProbeResult(false, ex.Status.Detail, 0, [], "rpc_error");
        }
        catch (InvalidOperationException ex)
        {
            ContainerMcpConnectorClientLog.ProbeFailed(logger, ex, connectorId);
            return new ContainerMcpProbeResult(false, ex.Message, 0, [], "unknown_error");
        }
    }

    public async Task<ContainerMcpToolListResult> ListMcpConnectorToolsAsync(
        string connectorId,
        IReadOnlyList<KeyValuePair<string, string>> environment,
        int timeoutSeconds,
        CancellationToken cancellationToken,
        bool allowInternalSystemConnector = false,
        string? userId = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectorId);

        try
        {
            var runtimeSettings = contextResolver.ResolveRuntimeSettings();
            var runtimeMessage = ToRuntimeMessage(runtimeSettings);

            string scopedConnectorId = WorkspaceMcpConnectorIdentity.BuildScopedConnectorId(userId, connectorId);

            var request = new WorkspaceMcpContainerCallRequest
            {
                Runtime = runtimeMessage,
                ConnectorId = scopedConnectorId,
                TimeoutSeconds = timeoutSeconds > 0 ? timeoutSeconds : 30,
                AllowInternalSystemConnector = allowInternalSystemConnector,
            };

            request.Environment.AddRange(environment.Select(pair => new WorkspaceKvPair { Key = pair.Key, Value = pair.Value }));

            var response = await workspaceGrpcService.ListMcpContainerToolsAsync(request, cancellationToken).ConfigureAwait(false);

            string message = response.Message;
            if (!response.Ok
                && response.UnavailabilityReason != WorkspaceMcpConnectorUnavailabilityReason.UnavailabilityReasonUnspecified)
            {
                message = $"{message} (reason: {response.UnavailabilityReason})";
            }

            return new ContainerMcpToolListResult(
                response.Ok,
                message,
                response.ToolCount,
                [.. response.Tools.Select(t => t.Name)],
                response.UnavailabilityReason.ToString());
        }
        catch (RpcException ex)
        {
            ContainerMcpConnectorClientLog.ListToolsFailed(logger, ex, connectorId);
            return new ContainerMcpToolListResult(false, ex.Status.Detail, 0, [], "rpc_error");
        }
        catch (InvalidOperationException ex)
        {
            ContainerMcpConnectorClientLog.ListToolsFailed(logger, ex, connectorId);
            return new ContainerMcpToolListResult(false, ex.Message, 0, [], "unknown_error");
        }
    }

    public async Task<ContainerMcpToolCallResult> CallMcpConnectorToolAsync(
        string connectorId,
        IReadOnlyList<KeyValuePair<string, string>> environment,
        string toolName,
        string toolInputJson,
        int timeoutSeconds,
        CancellationToken cancellationToken,
        bool allowInternalSystemConnector = false,
        string? userId = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectorId);
        ArgumentException.ThrowIfNullOrWhiteSpace(toolName);

        try
        {
            var runtimeSettings = contextResolver.ResolveRuntimeSettings();
            var runtimeMessage = ToRuntimeMessage(runtimeSettings);

            string scopedConnectorId = WorkspaceMcpConnectorIdentity.BuildScopedConnectorId(userId, connectorId);

            var request = new WorkspaceMcpContainerCallRequest
            {
                Runtime = runtimeMessage,
                ConnectorId = scopedConnectorId,
                ToolName = toolName,
                ToolInputJson = toolInputJson ?? "{}",
                TimeoutSeconds = timeoutSeconds > 0 ? timeoutSeconds : 30,
                AllowInternalSystemConnector = allowInternalSystemConnector,
            };

            request.Environment.AddRange(environment.Select(pair => new WorkspaceKvPair { Key = pair.Key, Value = pair.Value }));

            var response = await workspaceGrpcService.CallMcpContainerToolAsync(request, cancellationToken).ConfigureAwait(false);

            string message = response.Message;
            if (!response.Ok
                && response.UnavailabilityReason != WorkspaceMcpConnectorUnavailabilityReason.UnavailabilityReasonUnspecified)
            {
                message = $"{message} (reason: {response.UnavailabilityReason})";
            }

            return new ContainerMcpToolCallResult(
                response.Ok,
                message,
                response.ResultJson,
                response.UnavailabilityReason.ToString());
        }
        catch (RpcException ex)
        {
            ContainerMcpConnectorClientLog.CallToolFailed(logger, ex, connectorId, toolName);
            return new ContainerMcpToolCallResult(false, ex.Status.Detail, null, "rpc_error");
        }
        catch (InvalidOperationException ex)
        {
            ContainerMcpConnectorClientLog.CallToolFailed(logger, ex, connectorId, toolName);
            return new ContainerMcpToolCallResult(false, ex.Message, null, "unknown_error");
        }
    }

    private static ManagerRuntimeSettingsMessage ToRuntimeMessage(ManagerRuntimeSettingsPayload runtimeSettings)
        => new()
        {
            WorkspaceBaseRoot = runtimeSettings.WorkspaceBaseRoot,
            Image = runtimeSettings.Image,
            DefaultCpuLimit = runtimeSettings.DefaultCpuLimit,
            DefaultMemoryLimit = runtimeSettings.DefaultMemoryLimit,
            DefaultTimeoutSeconds = runtimeSettings.DefaultTimeoutSeconds,
            LogsEndpoint = runtimeSettings.LogsEndpoint ?? string.Empty,
            MetricsEndpoint = runtimeSettings.MetricsEndpoint ?? string.Empty,
            OtlpProtocol = runtimeSettings.OtlpProtocol ?? string.Empty,
            OtlpHeaders = runtimeSettings.OtlpHeaders ?? string.Empty,
        };
}

internal static partial class ContainerMcpConnectorClientLog
{
    [LoggerMessage(EventId = 1, Level = LogLevel.Warning, Message = "Container MCP connector probe failed for connector '{ConnectorId}'.")]
    public static partial void ProbeFailed(ILogger logger, Exception exception, string connectorId);

    [LoggerMessage(EventId = 2, Level = LogLevel.Warning, Message = "Container MCP connector tool listing failed for connector '{ConnectorId}'.")]
    public static partial void ListToolsFailed(ILogger logger, Exception exception, string connectorId);

    [LoggerMessage(EventId = 3, Level = LogLevel.Warning, Message = "Container MCP connector tool call failed for connector '{ConnectorId}' tool '{ToolName}'.")]
    public static partial void CallToolFailed(ILogger logger, Exception exception, string connectorId, string toolName);
}
