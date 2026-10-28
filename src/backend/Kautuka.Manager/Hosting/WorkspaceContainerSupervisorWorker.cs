using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading.Channels;

using Kautuka.Manager.RuntimeContainer.Configuration;
using Kautuka.Manager.RuntimeContainer.Hosting;
using Kautuka.Manager.RuntimeContainer.Sandbox;
using Kautuka.Manager.RuntimeContainer.Sandbox.Models;
using Kautuka.Telemetry.Correlation;

using Microsoft.Extensions.Options;

namespace Kautuka.Manager.Hosting;

/// <summary>
/// Background service that supervises runtime readiness and ensures agent sandbox
/// containers are running. Signals <see cref="WorkspaceReadinessGate"/> once the container
/// runtime can run containers. Sandbox startup continues after that signal.
/// </summary>
internal sealed partial class WorkspaceContainerSupervisorWorker(
    WorkspaceDesiredStateStore desiredStateStore,
    IManagerRuntimeAdapter runtimeAdapter,
    WorkspaceSidecarSupervisor sidecarSupervisor,
    WorkspaceContainerConnectorExecutionService connectorExecutionService,
    WorkspaceRuntimePaths runtimePaths,
    WorkspaceReadinessGate readinessGate,
    WorkspaceExecTracker execTracker,
    IOptions<ManagerContainerStartupOptions> startupOptions,
    ILogger<WorkspaceContainerSupervisorWorker> logger) : BackgroundService
{
    /// <summary>
    /// Maximum time between drift audits when no exec activity is observed.
    /// An idle signal from <see cref="WorkspaceExecTracker"/> can trigger a reconcile sooner.
    /// </summary>
    private static readonly TimeSpan driftAuditInterval = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan recoveryRetryInterval = TimeSpan.FromSeconds(10);

    // Tracks every container spawned during reconcile so StopAsync can shut them all down.
    private readonly ConcurrentDictionary<string, byte> trackedContainers = new(StringComparer.OrdinalIgnoreCase);

    // Tracks container connector lifecycle and JIT cooldown state: connector_id -> (container_name, last_startup_time)
    private readonly ConcurrentDictionary<string, (string? ContainerName, DateTime LastStartupTime)> mcpContainerConnectors =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly WorkspaceReadinessGate readinessGate = readinessGate;
    private readonly ChannelReader<byte> idleTrigger = execTracker.IdleTrigger;
    private readonly ChannelReader<WorkspaceReconcileTriggerKind> desiredStateTrigger = desiredStateStore.Updates;
    private readonly ManagerContainerStartupOptions startupOptions = startupOptions.Value;
    private readonly IManagerRuntimeAdapter runtimeAdapter = runtimeAdapter;
    private readonly WorkspaceSidecarSupervisor sidecarSupervisor = sidecarSupervisor;
    private readonly WorkspaceRuntimePaths runtimePaths = runtimePaths;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Initial reconcile on startup — gates gRPC until the machine is confirmed ready.
        await TryReconcileAsync(WorkspaceReconcileTriggerKind.HostLifecycleReconcile, stoppingToken).ConfigureAwait(false);

        // Wait for whichever comes first: the 30-minute periodic timer, or an idle signal
        // from WorkspaceExecTracker (fired when the last concurrent Exec call completes).
        // This ensures drift is audited promptly after real workload rather than on a fixed clock.
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                WorkspaceReconcileTriggerKind trigger = await WaitForNextReconcileTriggerAsync(stoppingToken).ConfigureAwait(false);

                if (stoppingToken.IsCancellationRequested)
                    break;

                LogReconcileTriggered(logger, trigger.ToString());

                await TryReconcileAsync(trigger, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                LogReconcileLoopFailed(logger, ex);
            }
        }
    }

    private async Task<WorkspaceReconcileTriggerKind> WaitForNextReconcileTriggerAsync(CancellationToken stoppingToken)
    {
        using var triggerCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);

        TimeSpan nextDelay = readinessGate.IsReady ? driftAuditInterval : recoveryRetryInterval;
        if (!readinessGate.IsReady)
        {
            LogRecoveryRetryScheduled(logger, (int)nextDelay.TotalSeconds);
        }

        Task delayTask = Task.Delay(nextDelay, triggerCts.Token);
        Task<byte> idleTask = idleTrigger.ReadAsync(triggerCts.Token).AsTask();
        Task<WorkspaceReconcileTriggerKind> desiredStateTask = desiredStateTrigger.ReadAsync(triggerCts.Token).AsTask();

        Task completed = await Task.WhenAny(delayTask, idleTask, desiredStateTask).ConfigureAwait(false);
        WorkspaceReconcileTriggerKind trigger = ResolveReconcileTrigger(completed, idleTask, desiredStateTask, readinessGate.IsReady);

        await triggerCts.CancelAsync().ConfigureAwait(false);
        await ObserveCanceledWaitAsync(delayTask).ConfigureAwait(false);
        await ObserveCanceledWaitAsync(idleTask).ConfigureAwait(false);
        await ObserveCanceledWaitAsync(desiredStateTask).ConfigureAwait(false);

        return trigger;
    }

    private static WorkspaceReconcileTriggerKind ResolveReconcileTrigger(
        Task completed,
        Task<byte> idleTask,
        Task<WorkspaceReconcileTriggerKind> desiredStateTask,
        bool isReady)
    {
        if (ReferenceEquals(completed, desiredStateTask) && desiredStateTask.IsCompletedSuccessfully)
        {
            return desiredStateTask.Result;
        }

        if (ReferenceEquals(completed, idleTask) && idleTask.IsCompletedSuccessfully)
        {
            return WorkspaceReconcileTriggerKind.ExecIdle;
        }

        return isReady
            ? WorkspaceReconcileTriggerKind.DriftAuditTimer
            : WorkspaceReconcileTriggerKind.RecoveryRetry;
    }

    private static async Task ObserveCanceledWaitAsync(Task task)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            _ = task.IsCanceled;
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken).ConfigureAwait(false);
        string[] names = await runtimeAdapter.GetManagedContainerNamesAsync(
            [.. trackedContainers.Keys, .. sidecarSupervisor.ManagedContainerNames],
            CancellationToken.None).ConfigureAwait(false);
        await runtimeAdapter.StopContainersAsync(names, CancellationToken.None).ConfigureAwait(false);
    }

    private async Task ReconcileAsync(WorkspaceReconcileTriggerKind triggerKind, CancellationToken cancellationToken)
    {
        using IDisposable scope = logger.BeginKautukaCorrelationScope(("SupervisorOperation", "reconcile"), ("TriggerKind", triggerKind.ToString()));

        // A reconcile that is still starting agent sandboxes must not close the gate.
        // Tool listing only needs a live container runtime, and the caller's deadline is
        // the wait bound. The gate is closed again only when that runtime is unavailable.
        WorkspaceDesiredStateSnapshot snapshot = desiredStateStore.Snapshot;
        if (snapshot.AcceptedVersion > 0)
        {
            _ = desiredStateStore.TryMarkReconcileStarted(snapshot.AcceptedVersion);
        }
        ManagerRuntimeSettings? runtimeSettings = snapshot.RuntimeSettings;
        bool hasLiveDesiredState = runtimeSettings is not null;
        runtimeSettings ??= CreateSidecarOnlyRuntimeSettings();

        runtimeAdapter.ApplyRuntimeSettings(runtimeSettings);
        LogReconcileStarting(
            logger,
            readinessGate.IsReady,
            snapshot.DesiredWorkspaces.Count,
            runtimeSettings.Sandbox.Image ?? string.Empty,
            runtimeSettings.Workspace.BaseRoot ?? string.Empty);

        // Gate eager container startup on runtime readiness.
        // Invariant: runtime must be available and workspace access verified
        // and workspace grant confirmed before any container is started or inspected.
        if (!await runtimeAdapter.EnsureReadyAsync(runtimeSettings, cancellationToken).ConfigureAwait(false))
        {
            LogRuntimeNotReady(logger);
            readinessGate.Reset();
            MarkFailed(snapshot, "runtime_unavailable");
            return;
        }

        // Ensure Podman sandbox network exists before any container operations
        await runtimeAdapter.EnsureNetworkAsync("kautuka-sandbox", cancellationToken).ConfigureAwait(false);

        if (!await sidecarSupervisor.EnsureReadyAsync(runtimeSettings, cancellationToken).ConfigureAwait(false))
        {
            if (runtimePaths.RequireSidecars)
            {
                logger.LogWarning("Workspace sidecars are not ready and strict sidecar mode is enabled.");
                readinessGate.Reset();
                MarkFailed(snapshot, "required_sidecar_unavailable");
                return;
            }

            logger.LogWarning("Workspace sidecars are not ready; continuing because strict sidecar mode is disabled.");
        }

        ManagedContainerReconcileResult sidecarOrphanResult = await runtimeAdapter.RemoveOrphanedManagedContainersAsync(
            "sidecar",
            sidecarSupervisor.ManagedContainerNames,
            cancellationToken).ConfigureAwait(false);
        if (!sidecarOrphanResult.Succeeded)
        {
            MarkFailed(snapshot, sidecarOrphanResult.FailureClass ?? "sidecar_orphan_reconcile_failed");
            return;
        }

        ApplianceHostExposureAssert.LogFindings(logger);

        if (!hasLiveDesiredState)
        {
            LogDesiredStateUnavailable(logger);
            return;
        }

        // Connector tool listing starts its own container. It does not wait for every
        // agent sandbox, or for the sandbox image build that follows.
        SignalRuntimeReady();

        Stopwatch sandboxImageStopwatch = Stopwatch.StartNew();
        string? sandboxImageError = await runtimeAdapter.PrepareSandboxImageAsync(runtimeSettings.Sandbox, cancellationToken).ConfigureAwait(false);
        sandboxImageStopwatch.Stop();
        LogReconcilePhaseCompleted(logger, "sandbox-image", sandboxImageStopwatch.ElapsedMilliseconds, 1, 1);
        if (!string.IsNullOrWhiteSpace(sandboxImageError))
        {
            LogSandboxImagePreparationFailed(logger, sandboxImageError);
            MarkFailed(snapshot, "sandbox_image_failed");
            return;
        }

        ContainerContext[] desiredWorkspaces = [.. snapshot.DesiredWorkspaces
            .GroupBy(
                static workspace => workspace.ScopeId + "\n" + (workspace.UserId ?? string.Empty),
                StringComparer.OrdinalIgnoreCase)
            .Select(static group => group.First())];

        string[] desiredContainerNames = [.. desiredWorkspaces.Select(PodmanContainerTool.BuildContainerName)];
        ManagedContainerReconcileResult orphanResult = await runtimeAdapter.RemoveOrphanedManagedContainersAsync(
            "agent",
            desiredContainerNames,
            cancellationToken).ConfigureAwait(false);
        if (!orphanResult.Succeeded)
        {
            MarkFailed(snapshot, orphanResult.FailureClass ?? "orphan_container_reconcile_failed");
            return;
        }

        string[] desiredConnectorNames = [.. connectorExecutionService
            .DesiredIsolatedContainerNames(snapshot.DesiredContainerConnectors)
            .Concat(connectorExecutionService.TrackedContainerNames)
            .Distinct(StringComparer.OrdinalIgnoreCase)];
        ManagedContainerReconcileResult connectorOrphanResult = await runtimeAdapter.RemoveOrphanedManagedContainersAsync(
            "connector",
            desiredConnectorNames,
            cancellationToken).ConfigureAwait(false);
        if (!connectorOrphanResult.Succeeded)
        {
            MarkFailed(snapshot, connectorOrphanResult.FailureClass ?? "connector_orphan_reconcile_failed");
            return;
        }

        var desiredContainerNameSet = new HashSet<string>(desiredContainerNames, StringComparer.OrdinalIgnoreCase);
        foreach (string trackedContainer in trackedContainers.Keys.Where(name => !desiredContainerNameSet.Contains(name)))
        {
            trackedContainers.TryRemove(trackedContainer, out _);
        }

        int sandboxMaxParallelism = startupOptions.GetEffectiveSandboxMaxConcurrency();
        Dictionary<string, IReadOnlyList<string>> sandboxConnectorIdsByWorkspace = snapshot.DesiredSandboxConnectorBindings
            .ToDictionary(
                static binding => BuildWorkspaceKey(binding.ScopeType, binding.ScopeId),
                static binding => (IReadOnlyList<string>)binding.ConnectorIds,
                StringComparer.OrdinalIgnoreCase);
        Stopwatch sandboxStartupStopwatch = Stopwatch.StartNew();
        var sandboxFailures = new ConcurrentQueue<string>();
        await Parallel.ForEachAsync(
            desiredWorkspaces,
            new ParallelOptions
            {
                MaxDegreeOfParallelism = sandboxMaxParallelism,
                CancellationToken = cancellationToken,
            },
            async (desiredWorkspace, ct) =>
            {
                try
                {
                    string sandboxImage = runtimeAdapter.ResolveSandboxImage(desiredWorkspace.Image);
                    LogEnsuringAgentSandbox(logger, desiredWorkspace.ScopeId, sandboxImage, desiredWorkspace.HostWorkspacePath);

                    sandboxConnectorIdsByWorkspace.TryGetValue(BuildWorkspaceKey(desiredWorkspace.ScopeType, desiredWorkspace.ScopeId), out IReadOnlyList<string>? connectorIds);
                    connectorIds ??= [];
                    ContainerContext resolvedWorkspace = desiredWorkspace with { Image = sandboxImage };
                    ContainerContext versionedWorkspace = resolvedWorkspace with
                    {
                        DesiredStateVersion = snapshot.AcceptedVersion,
                        SpecFingerprint = WorkspaceContainerSpecFingerprint.Compute(resolvedWorkspace, connectorIds),
                    };
                    var session = await connectorExecutionService
                        .EnsureAgentSandboxProvisionedAsync(versionedWorkspace, connectorIds, ct)
                        .ConfigureAwait(false);
                    trackedContainers.TryAdd(session.ContainerName, 0);
                    LogAgentSandboxEnsured(logger, desiredWorkspace.ScopeId, session.ContainerName, session.Running);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    sandboxFailures.Enqueue(desiredWorkspace.ScopeId);
                    LogEnsureAgentSandboxFailed(logger, ex, desiredWorkspace.ScopeId);
                }
            }).ConfigureAwait(false);
        sandboxStartupStopwatch.Stop();
        LogReconcilePhaseCompleted(logger, "sandbox-startup", sandboxStartupStopwatch.ElapsedMilliseconds, desiredWorkspaces.Length, sandboxMaxParallelism);

        if (!sandboxFailures.IsEmpty)
        {
            MarkFailed(snapshot, "sandbox_provision_failed");
            return;
        }

        // Reconcile containerized MCP connectors (Phase 2c: JIT startup with cooldown)
        await ReconcileContainerConnectorsAsync(snapshot.DesiredContainerConnectors, cancellationToken).ConfigureAwait(false);
        IReadOnlyList<string> connectorNotes = await connectorExecutionService.ReconcileDesiredConnectorsAsync(
            snapshot.DesiredContainerConnectors,
            cancellationToken).ConfigureAwait(false);
        desiredStateStore.ReplaceDriftActions([
            .. sidecarSupervisor.TakeDriftNotes(),
            .. connectorExecutionService.TakeSandboxDriftNotes(),
            .. connectorNotes,
        ]);

        if (!desiredStateStore.TryMarkReconciled(snapshot.AcceptedVersion, triggerKind, DateTimeOffset.UtcNow))
        {
            LogReconcileCursorAdvancedByNewerVersion(logger, snapshot.AcceptedVersion);
            return;
        }

        SignalRuntimeReady();
    }

    private void SignalRuntimeReady()
    {
        if (readinessGate.IsReady)
        {
            return;
        }

        readinessGate.SignalReady();
        LogReadinessGateSignalled(logger);
    }

    private async Task TryReconcileAsync(WorkspaceReconcileTriggerKind triggerKind, CancellationToken cancellationToken)
    {
        try
        {
            await ReconcileAsync(triggerKind, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            WorkspaceDesiredStateSnapshot snapshot = desiredStateStore.Snapshot;
            MarkFailed(snapshot, ClassifyFailure(ex));
            LogReconcileLoopFailed(logger, ex);
        }
    }

    private void MarkFailed(WorkspaceDesiredStateSnapshot snapshot, string failureClass)
    {
        if (snapshot.AcceptedVersion > 0)
        {
            _ = desiredStateStore.TryMarkReconcileFailed(snapshot.AcceptedVersion, failureClass);
        }
    }

    private static string ClassifyFailure(Exception exception)
        => exception switch
        {
            IOException => "io_failure",
            UnauthorizedAccessException => "access_denied",
            TimeoutException => "timeout",
            _ => "reconcile_failure",
        };

    private static ManagerRuntimeSettings CreateSidecarOnlyRuntimeSettings()
        => new(
            new WorkspaceDefinition(),
            new SandboxRuntimeDefinition(),
            new WorkspaceTelemetryDefinition());

    private Task ReconcileContainerConnectorsAsync(
        IReadOnlyList<WorkspaceMcpContainerConnectorDefinition> desiredConnectors,
        CancellationToken cancellationToken)
    {
        // Phase 2c: Track desired container connector state
        // Full lifecycle management (startup, readiness checks, shutdown) deferred to Phase 3
        // For now: update tracking and log desired state

        var desiredConnectorIds = new HashSet<string>(desiredConnectors.Select(c => c.ConnectorId), StringComparer.OrdinalIgnoreCase);

        // Remove connectors no longer in desired state
        var staleConnectorIds = mcpContainerConnectors.Keys
            .Where(id => !desiredConnectorIds.Contains(id))
            .ToList();
        foreach (var staleId in staleConnectorIds)
        {
            mcpContainerConnectors.TryRemove(staleId, out _);
            _ = desiredStateStore.TryTransitionConnectorState(
                staleId,
                WorkspaceMcpContainerLifecycleState.Deleted,
                "removed-from-desired-state");
            LogMcpConnectorRemoved(logger, staleId);
        }

        // Register/update desired connectors
        foreach (var connector in desiredConnectors)
        {
            if (cancellationToken.IsCancellationRequested) break;
            if (connector.Enabled)
            {
                mcpContainerConnectors.AddOrUpdate(
                    connector.ConnectorId,
                    (null, DateTime.MinValue),
                    (_, existing) => existing);

                WorkspaceMcpContainerRuntimeStatus? status = desiredStateStore.GetConnectorStatus(connector.ConnectorId);
                if (status is null || status.State == WorkspaceMcpContainerLifecycleState.Imported)
                {
                    _ = desiredStateStore.TryTransitionConnectorState(
                        connector.ConnectorId,
                        WorkspaceMcpContainerLifecycleState.Materialized,
                        "connector-materialized");
                }

                LogMcpConnectorDesired(logger, connector.ConnectorId, connector.Image, connector.JitCooldownSeconds);
            }
        }

        return Task.CompletedTask;
    }

    private static string BuildWorkspaceKey(string scopeType, string scopeId)
        => string.Join('|', scopeType, scopeId);
}
