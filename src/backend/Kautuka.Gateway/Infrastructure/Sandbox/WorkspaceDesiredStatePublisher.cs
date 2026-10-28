using System.Globalization;
using System.Security.Cryptography;
using System.Text;

using Grpc.Core;

using Kautuka.Gateway.Domain.Sandbox;
using Kautuka.Manager.Protos;

namespace Kautuka.Gateway.Infrastructure.Sandbox;

internal readonly record struct DesiredStatePublishResult(bool Accepted, bool ManagerUnavailable)
{
    public static DesiredStatePublishResult Succeeded { get; } = new(true, false);

    public static DesiredStatePublishResult Unavailable { get; } = new(false, true);

    public static DesiredStatePublishResult Failed { get; } = new(false, false);
}

internal interface IWorkspaceDesiredStatePublisher
{
    DesiredStatePublishResult TryPublishCurrent(string reason);

    Task<DesiredStatePublishResult> TryPublishCurrentAsync(string reason, CancellationToken cancellationToken = default)
        => Task.FromResult(TryPublishCurrent(reason));
}

internal sealed class WorkspaceDesiredStatePublisher(
    Kautuka.Gateway.Domain.State.GatewayState gatewayState,
    WorkspaceRuntimeContextResolver resolver,
    IManagerGrpcService workspaceGrpcService,
    ILogger<WorkspaceDesiredStatePublisher> logger) : IWorkspaceDesiredStatePublisher
{
    public DesiredStatePublishResult TryPublishCurrent(string reason)
        => TryPublishCurrentAsync(reason).GetAwaiter().GetResult();

    public async Task<DesiredStatePublishResult> TryPublishCurrentAsync(string reason, CancellationToken cancellationToken = default)
    {
        try
        {
            WorkspaceDesiredStatePublisherLog.PushBegin(logger, reason);
            WorkspaceDesiredStatePayload state = resolver.ResolveDesiredState();
            WorkspaceDesiredStateSyncReason syncReason = ToSyncReason(reason);
            string contentHash = ComputeContentHash(state);
            long desiredStateVersion = ReserveDesiredStateVersion(contentHash);
            var request = new ManagerDesiredStateUpdateRequest(
                state,
                desiredStateVersion,
                syncReason,
                Guid.NewGuid().ToString("D"),
                gatewayState.DesiredStatePublisherEpoch);

            WorkspaceDesiredStatePublisherLog.ResolvedState(
                logger,
                reason,
                state.Workspaces.Count,
                state.ContainerConnectors.Count);

            WorkspaceDesiredStatePublisherLog.PushRpcStarting(logger, reason);
            DesiredStateUpdateResponse response = await workspaceGrpcService
                .UpdateDesiredStateAsync(request, cancellationToken)
                .ConfigureAwait(false);
            bool accepted = response.Outcome is DesiredStateUpdateOutcome.Applied or DesiredStateUpdateOutcome.Replayed;
            WorkspaceDesiredStatePublisherLog.PushRpcCompleted(logger, reason, accepted);

            if (!accepted)
            {
                SafeLogRejected(reason, response.Message);
                return DesiredStatePublishResult.Failed;
            }

            WorkspaceDesiredStatePublisherLog.PushSucceeded(logger, reason);

            return DesiredStatePublishResult.Succeeded;
        }
        catch (InvalidOperationException ex)
        {
            SafeLogFailed(ex, reason);
            return Classify(ex);
        }
        catch (RpcException ex)
        {
            SafeLogFailed(ex, reason);
            return Classify(ex);
        }
        catch (OperationCanceledException ex)
        {
            SafeLogFailed(ex, reason);
            return DesiredStatePublishResult.Failed;
        }
        catch (Microsoft.Data.Sqlite.SqliteException ex)
        {
            SafeLogFailed(ex, reason);
            return DesiredStatePublishResult.Failed;
        }
        catch (IOException ex)
        {
            SafeLogFailed(ex, reason);
            return DesiredStatePublishResult.Failed;
        }
    }

    internal static DesiredStatePublishResult Classify(Exception exception)
        => IsManagerUnavailable(exception)
            ? DesiredStatePublishResult.Unavailable
            : DesiredStatePublishResult.Failed;

    private static bool IsManagerUnavailable(Exception exception)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            if (current is RpcException rpc && rpc.StatusCode == StatusCode.Unavailable)
            {
                return true;
            }
        }

        return false;
    }

    private void SafeLogRejected(string reason, string message)
    {
        try
        {
            WorkspaceDesiredStatePublisherLog.PushRejected(logger, reason, message);
        }
        catch (Exception ex) when (IsLoggerDisposeRace(ex))
        {
        }
    }

    private void SafeLogFailed(Exception exception, string reason)
    {
        try
        {
            WorkspaceDesiredStatePublisherLog.PushFailed(logger, exception, reason);
        }
        catch (Exception ex) when (IsLoggerDisposeRace(ex))
        {
        }
    }

    private static bool IsLoggerDisposeRace(Exception exception)
        => exception is ObjectDisposedException
            || exception is AggregateException aggregateException
            && aggregateException.InnerExceptions.All(static inner => inner is ObjectDisposedException);

    private long ReserveDesiredStateVersion(string contentHash)
    {
        lock (gatewayState.SettingsWriteSyncRoot)
        {
            if (gatewayState.DesiredStateVersion <= 0 || !string.Equals(gatewayState.DesiredStateContentHash, contentHash, StringComparison.Ordinal))
            {
                long nextVersion = Math.Max(1, gatewayState.DesiredStateVersion + 1);
                gatewayState.PersistRawSettingsAuthoritatively(new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["desired_state.version"] = nextVersion.ToString(CultureInfo.InvariantCulture),
                    ["desired_state.content_hash"] = contentHash,
                });
                gatewayState.DesiredStateVersion = nextVersion;
                gatewayState.DesiredStateContentHash = contentHash;
            }

            return gatewayState.DesiredStateVersion;
        }
    }

    internal static string ComputeContentHash(WorkspaceDesiredStatePayload state)
    {
        var builder = new StringBuilder();
        ManagerRuntimeSettingsPayload runtime = state.RuntimeSettings;
        Append(builder, runtime.WorkspaceBaseRoot);
        Append(builder, runtime.Image);
        Append(builder, runtime.DefaultCpuLimit.ToString(CultureInfo.InvariantCulture));
        Append(builder, runtime.DefaultMemoryLimit);
        Append(builder, runtime.DefaultTimeoutSeconds.ToString(CultureInfo.InvariantCulture));
        Append(builder, runtime.TracesEndpoint ?? string.Empty);
        Append(builder, runtime.LogsEndpoint ?? string.Empty);
        Append(builder, runtime.MetricsEndpoint ?? string.Empty);
        Append(builder, runtime.OtlpProtocol ?? string.Empty);
        Append(builder, runtime.OtlpHeaders ?? string.Empty);

        foreach (WorkspaceRuntimeContext workspace in state.Workspaces
                     .OrderBy(static workspace => workspace.ScopeType, StringComparer.Ordinal)
                     .ThenBy(static workspace => workspace.ScopeId, StringComparer.Ordinal)
                     .ThenBy(static workspace => workspace.UserId ?? string.Empty, StringComparer.Ordinal))
        {
            Append(builder, workspace.ScopeType);
            Append(builder, workspace.ScopeId);
            Append(builder, workspace.UserId ?? string.Empty);
            Append(builder, workspace.HostWorkspacePath);
            Append(builder, workspace.ContainerWorkspacePath);
            Append(builder, workspace.Image);
            Append(builder, workspace.DefaultTimeoutSeconds.ToString(CultureInfo.InvariantCulture));

            foreach (string capability in workspace.BaseCapabilities)
            {
                Append(builder, capability);
            }

            foreach (KeyValuePair<string, string> pair in workspace.ContainerEnvironment.OrderBy(static pair => pair.Key, StringComparer.Ordinal))
            {
                Append(builder, pair.Key);
                Append(builder, pair.Value);
            }
        }

        foreach (WorkspaceMcpContainerConnectorPayload connector in state.ContainerConnectors.OrderBy(static connector => connector.ConnectorId, StringComparer.Ordinal))
        {
            Append(builder, connector.ConnectorId);
            Append(builder, connector.ServerId);
            Append(builder, connector.Image);
            Append(builder, connector.Command);
            Append(builder, connector.Enabled ? "1" : "0");
            Append(builder, connector.IsInternalSystemConnector ? "1" : "0");
            Append(builder, connector.BundlePath ?? string.Empty);
            Append(builder, connector.ServerType ?? string.Empty);
            Append(builder, connector.ReadinessTimeoutSeconds.ToString(CultureInfo.InvariantCulture));
            Append(builder, connector.JitCooldownSeconds.ToString(CultureInfo.InvariantCulture));
            Append(builder, connector.McpProtocolVersion ?? string.Empty);

            foreach (string arg in connector.Args)
            {
                Append(builder, arg);
            }

            foreach (KeyValuePair<string, string> pair in connector.Environment.OrderBy(static pair => pair.Key, StringComparer.Ordinal))
            {
                Append(builder, pair.Key);
                Append(builder, pair.Value);
            }
        }

        foreach (WorkspaceSandboxConnectorBindingsPayload binding in state.SandboxConnectorBindings
                     .OrderBy(static binding => binding.ScopeType, StringComparer.Ordinal)
                     .ThenBy(static binding => binding.ScopeId, StringComparer.Ordinal))
        {
            Append(builder, binding.ScopeType);
            Append(builder, binding.ScopeId);

            foreach (string connectorId in binding.ConnectorIds.OrderBy(static connectorId => connectorId, StringComparer.Ordinal))
            {
                Append(builder, connectorId);
            }
        }

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString())));
    }

    private static void Append(StringBuilder builder, string value)
    {
        builder.Append(value.Length.ToString(CultureInfo.InvariantCulture));
        builder.Append(':');
        builder.Append(value);
        builder.Append('|');
    }

    private static WorkspaceDesiredStateSyncReason ToSyncReason(string reason)
        => reason switch
        {
            "gateway-startup" or "gateway-startup-retry" => WorkspaceDesiredStateSyncReason.Bootstrap,
            "gateway-rehydrate-response" => WorkspaceDesiredStateSyncReason.RehydrateResponse,
            _ => WorkspaceDesiredStateSyncReason.Change,
        };
}

internal static partial class WorkspaceDesiredStatePublisherLog
{
    [LoggerMessage(EventId = 7, Level = LogLevel.Debug,
        Message = "[GatewayStartup] Desired-state publish begin. reason={Reason}")]
    public static partial void PushBegin(ILogger logger, string reason);

    [LoggerMessage(EventId = 3, Level = LogLevel.Debug,
        Message = "[GatewayStartup] Desired-state resolved. reason={Reason} workspaces={Workspaces} connectors={Connectors}")]
    public static partial void ResolvedState(ILogger logger, string reason, int workspaces, int connectors);

    [LoggerMessage(EventId = 4, Level = LogLevel.Debug,
        Message = "[GatewayStartup] Desired-state gRPC update starting. reason={Reason}")]
    public static partial void PushRpcStarting(ILogger logger, string reason);

    [LoggerMessage(EventId = 5, Level = LogLevel.Debug,
        Message = "[GatewayStartup] Desired-state gRPC update completed. reason={Reason} ok={Ok}")]
    public static partial void PushRpcCompleted(ILogger logger, string reason, bool ok);

    [LoggerMessage(EventId = 6, Level = LogLevel.Information,
        Message = "[GatewayStartup] Desired-state publish succeeded. reason={Reason}")]
    public static partial void PushSucceeded(ILogger logger, string reason);

    [LoggerMessage(EventId = 1, Level = LogLevel.Warning,
        Message = "Workspace desired state push rejected during {Reason}: {Message}")]
    public static partial void PushRejected(ILogger logger, string reason, string message);

    [LoggerMessage(EventId = 2, Level = LogLevel.Warning,
        Message = "Workspace desired state push failed during {Reason}.")]
    public static partial void PushFailed(ILogger logger, Exception exception, string reason);
}
