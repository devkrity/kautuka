namespace Kautuka.Manager.Hosting;

// All [LoggerMessage] source-generated log methods for WorkspaceContainerSupervisorWorker.
internal sealed partial class WorkspaceContainerSupervisorWorker
{
    [LoggerMessage(EventId = 45, Level = LogLevel.Information,
        Message = "Starting reconcile cycle. Ready={IsReady} DesiredWorkspaces={DesiredWorkspaceCount} Image={Image} BaseRoot={BaseRoot}")]
    private static partial void LogReconcileStarting(ILogger logger, bool isReady, int desiredWorkspaceCount, string image, string baseRoot);

    [LoggerMessage(EventId = 1, Level = LogLevel.Warning, Message = "Workspace container reconcile loop failed.")]
    private static partial void LogReconcileLoopFailed(ILogger logger, Exception exception);

    [LoggerMessage(EventId = 5, Level = LogLevel.Warning, Message = "Failed to ensure sandbox container for agent {AgentId}.")]
    private static partial void LogEnsureAgentSandboxFailed(ILogger logger, Exception exception, string agentId);

    [LoggerMessage(EventId = 46, Level = LogLevel.Warning, Message = "Manager runtime adapter did not reach ready state during reconcile.")]
    private static partial void LogRuntimeNotReady(ILogger logger);

    [LoggerMessage(EventId = 47, Level = LogLevel.Warning, Message = "Manager sandbox image preparation failed: {Message}")]
    private static partial void LogSandboxImagePreparationFailed(ILogger logger, string message);

    [LoggerMessage(EventId = 48, Level = LogLevel.Information, Message = "Manager readiness gate signalled. Proceeding with platform and workspace container reconcile.")]
    private static partial void LogReadinessGateSignalled(ILogger logger);

    [LoggerMessage(EventId = 49, Level = LogLevel.Information, Message = "Ensuring sandbox container for agent {AgentId} using image {Image} at host workspace {HostWorkspacePath}.")]
    private static partial void LogEnsuringAgentSandbox(ILogger logger, string agentId, string image, string hostWorkspacePath);

    [LoggerMessage(EventId = 50, Level = LogLevel.Information, Message = "Sandbox container for agent {AgentId} is ready as {ContainerName}. Running={Running}")]
    private static partial void LogAgentSandboxEnsured(ILogger logger, string agentId, string containerName, bool running);

    [LoggerMessage(EventId = 37, Level = LogLevel.Debug,
        Message = "Reconcile triggered by: {Trigger}. Running reconcile cycle.")]
    private static partial void LogReconcileTriggered(ILogger logger, string trigger);

    [LoggerMessage(EventId = 39, Level = LogLevel.Debug,
        Message = "No live Gateway-supplied desired state is available yet. Reconciling Manager-owned sidecars only and skipping workspace/container reconcile.")]
    private static partial void LogDesiredStateUnavailable(ILogger logger);

    [LoggerMessage(EventId = 41, Level = LogLevel.Information,
        Message = "Manager is not ready yet. Retrying runtime recovery in {RetrySeconds}s.")]
    private static partial void LogRecoveryRetryScheduled(ILogger logger, int retrySeconds);

    [LoggerMessage(EventId = 51, Level = LogLevel.Information,
        Message = "Registered desired MCP container connector {ConnectorId} with image {Image} and JIT cooldown {JitCooldownSeconds}s.")]
    private static partial void LogMcpConnectorDesired(ILogger logger, string connectorId, string image, int jitCooldownSeconds);

    [LoggerMessage(EventId = 52, Level = LogLevel.Information,
        Message = "Removing stale MCP container connector {ConnectorId} from tracking.")]
    private static partial void LogMcpConnectorRemoved(ILogger logger, string connectorId);

    [LoggerMessage(EventId = 53, Level = LogLevel.Debug,
        Message = "Skipped marking desired-state version {Version} as reconciled because a newer accepted version is now present.")]
    private static partial void LogReconcileCursorAdvancedByNewerVersion(ILogger logger, long version);

    [LoggerMessage(EventId = 54, Level = LogLevel.Information,
        Message = "Reconcile {Phase} phase completed in {ElapsedMilliseconds}ms. Items={ItemCount} MaxParallelism={MaxParallelism}")]
    private static partial void LogReconcilePhaseCompleted(ILogger logger, string phase, long elapsedMilliseconds, int itemCount, int maxParallelism);
}
