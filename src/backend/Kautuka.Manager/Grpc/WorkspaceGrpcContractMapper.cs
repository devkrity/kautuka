using Kautuka.Manager.Protos;
using Kautuka.Manager.RuntimeContainer.Configuration;
using Kautuka.Manager.RuntimeContainer.Hosting;
using Kautuka.Manager.RuntimeContainer.Sandbox.Models;

namespace Kautuka.Manager.Grpc;

internal static class WorkspaceGrpcContractMapper
{
    public static ContainerContext ResolveWorkspace(WorkspaceDesiredStateStore store, WorkspaceContextMessage? message)
    {
        ArgumentNullException.ThrowIfNull(store);
        if (message is null)
        {
            throw new InvalidOperationException("Workspace identity is missing.");
        }

        if (!store.TryResolveWorkspace(message.ScopeType, message.ScopeId, message.UserId, out ContainerContext? workspace))
        {
            throw new InvalidOperationException("Workspace identity is not present in accepted desired state.");
        }

        return workspace!;
    }

    public static ContainerContext FromDesiredState(WorkspaceContextMessage? message, ManagerRuntimeSettings runtimeSettings)
    {
        ArgumentNullException.ThrowIfNull(runtimeSettings);
        if (message is null)
        {
            throw new InvalidOperationException("Workspace context payload is missing.");
        }

        return new ContainerContext(
            message.ScopeType,
            message.ScopeId,
            NormalizeOptional(message.UserId),
            message.HostWorkspacePath,
            message.ContainerWorkspacePath,
            message.Image,
            runtimeSettings.Sandbox.DefaultCpus,
            runtimeSettings.Sandbox.DefaultMemory ?? string.Empty,
            message.DefaultTimeoutSeconds,
            [.. message.BaseCapabilities],
            message.ContainerEnvironment.ToDictionary(static pair => pair.Key, static pair => pair.Value, StringComparer.OrdinalIgnoreCase));
    }

    public static ManagerRuntimeSettings FromRuntime(ManagerRuntimeSettingsMessage? message)
    {
        if (message is null)
        {
            throw new InvalidOperationException("Workspace runtime settings payload is missing.");
        }

        return new ManagerRuntimeSettings(
            new WorkspaceDefinition { BaseRoot = message.WorkspaceBaseRoot },
            new SandboxRuntimeDefinition
            {
                Image = message.Image,
                DefaultCpus = message.DefaultCpuLimit,
                DefaultMemory = message.DefaultMemoryLimit,
                DefaultTimeout = message.DefaultTimeoutSeconds,
            },
            new WorkspaceTelemetryDefinition
            {
                TracesEndpoint = NormalizeOptional(message.TracesEndpoint),
                LogsEndpoint = NormalizeOptional(message.LogsEndpoint),
                MetricsEndpoint = NormalizeOptional(message.MetricsEndpoint),
                OtlpProtocol = NormalizeOptional(message.OtlpProtocol),
                OtlpHeaders = NormalizeOptional(message.OtlpHeaders),
            });
    }

    public static Dictionary<string, string> ToEnvironment(IEnumerable<WorkspaceKvPair> environment)
        => environment
            .Where(static pair => !string.IsNullOrWhiteSpace(pair.Key))
            .ToDictionary(static pair => pair.Key, static pair => pair.Value, StringComparer.Ordinal);

    public static WorkspaceMcpContainerConnectorDefinition FromProbe(WorkspaceMcpContainerProbeRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return new WorkspaceMcpContainerConnectorDefinition(
            string.IsNullOrWhiteSpace(request.ConnectorId) ? "probe-container" : request.ConnectorId.Trim(),
            request.Image?.Trim() ?? string.Empty,
            request.Command?.Trim() ?? string.Empty,
            [.. request.Args.Where(static arg => !string.IsNullOrWhiteSpace(arg)).Select(static arg => arg.Trim())],
            true,
            false,
            string.IsNullOrWhiteSpace(request.BundlePath) ? null : request.BundlePath.Trim(),
            string.IsNullOrWhiteSpace(request.ServerType) ? null : request.ServerType.Trim(),
            [.. ToEnvironment(request.Environment)],
            request.TimeoutSeconds <= 0 ? 30 : request.TimeoutSeconds,
            600,
            string.IsNullOrWhiteSpace(request.McpProtocolVersion) ? null : request.McpProtocolVersion.Trim());
    }

    public static WorkspaceContextMessage ToContract(ContainerContext context)
        => new()
        {
            ScopeType = context.ScopeType,
            ScopeId = context.ScopeId,
            UserId = context.UserId ?? string.Empty,
            HostWorkspacePath = context.HostWorkspacePath,
            ContainerWorkspacePath = context.ContainerWorkspacePath,
            Image = context.Image ?? string.Empty,
            DefaultTimeoutSeconds = context.DefaultTimeoutSeconds,
            BaseCapabilities = { context.BaseCapabilities },
            ContainerEnvironment = { context.ContainerEnvironment.Select(static pair => new WorkspaceKvPair { Key = pair.Key, Value = pair.Value }) },
        };

    public static WorkspaceSandboxMessage ToContract(OciSandboxSession session)
        => new()
        {
            ContainerName = session.ContainerName,
            Running = session.Running,
            ScopeType = session.ScopeType,
            ScopeId = session.ScopeId,
            UserId = session.UserId ?? string.Empty,
        };

    public static WorkspaceExecutionMessage ToContract(ContainerExecutionResult result)
        => new()
        {
            Status = result.Status,
            ExitCode = result.ExitCode,
            StdOut = result.StdOut,
            StdErr = result.StdErr,
            ContainerName = result.ContainerName ?? string.Empty,
        };

    private static string? NormalizeOptional(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value;
}
