using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

using Kautuka.Manager.Protos;
using Kautuka.Manager.RuntimeContainer.Sandbox;
using Kautuka.Manager.RuntimeContainer.Sandbox.Models;

using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace Kautuka.Manager.RuntimeContainer.Hosting;

/// <summary>
/// Executes MCP operations against containerized connector sidecars.
/// </summary>
public sealed class WorkspaceContainerConnectorExecutionService(
    WorkspaceDesiredStateStore desiredStateStore,
    PodmanCommandRunner podmanCommandRunner,
    McpbImageMaterializer mcpbImageMaterializer,
    IContainerTool containerTool,
    ILogger<WorkspaceContainerConnectorExecutionService> logger,
    NetworkAccess.WorkloadAddressAllocator? addressAllocator = null,
    Kautuka.NetworkAccessController.IWorkloadRegistry? workloadRegistry = null,
    NetworkAccess.WorkloadPostStartVerifier? postStartVerifier = null,
    NetworkAccess.ImageCompatibilityStore? imageCompatibilityStore = null,
    NetworkAccess.WorkloadLaunchGate? workloadLaunchGate = null,
    Kautuka.NetworkAccessController.NetworkAccessPolicyStore? probePolicyStore = null,
    string stdioCommand = PodmanCommandRunner.BinaryName,
    IReadOnlyList<string>? stdioArgumentsPrefix = null,
    IHostApplicationLifetime? hostLifetime = null) : IAsyncDisposable
{
    private const string agentSandboxInstallRoot = "/home/node/.kautuka/connectors";
    private const string agentSandboxManifestPath = "/home/node/.kautuka/connectors/.installed.json";
    private const int agentSandboxNormalizationVersion = 3;

    private readonly ConcurrentDictionary<string, string> trackedConnectorContainers = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConnectorCooldownScheduler connectorCooldowns = new();
    private readonly AsyncKeyedLockPool connectorLocks = new();
    private readonly AsyncKeyedLockPool sandboxProvisionLocks = new();
    private readonly IContainerTool containerTool = containerTool;
    private readonly NetworkAccess.WorkloadAddressAllocator? addressAllocator = addressAllocator;
    private readonly Kautuka.NetworkAccessController.IWorkloadRegistry? workloadRegistry = workloadRegistry;
    private readonly NetworkAccess.WorkloadPostStartVerifier? postStartVerifier = postStartVerifier;
    private readonly NetworkAccess.ImageCompatibilityStore? imageCompatibilityStore = imageCompatibilityStore;
    private readonly NetworkAccess.WorkloadLaunchGate? workloadLaunchGate = workloadLaunchGate;
    private readonly Action<string>? clearProbeAuthorization = CreateProbeClearAction(probePolicyStore);

    public IReadOnlyCollection<string> TrackedContainerNames
        => [.. trackedConnectorContainers.Values.Distinct(StringComparer.OrdinalIgnoreCase)];

    public bool IsTrackedContainerName(string name)
        => trackedConnectorContainers.Values.Contains(name, StringComparer.OrdinalIgnoreCase);

    public IReadOnlyCollection<string> DesiredIsolatedContainerNames(
        IReadOnlyList<WorkspaceMcpContainerConnectorDefinition> connectors)
    {
        ArgumentNullException.ThrowIfNull(connectors);
        ContainerContext[] workspaces = [.. desiredStateStore.Snapshot.DesiredWorkspaces
            .Where(static workspace => !string.IsNullOrWhiteSpace(workspace.UserId))
            .DistinctBy(static workspace => (workspace.ScopeType, workspace.ScopeId, workspace.UserId))];
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (WorkspaceMcpContainerConnectorDefinition connector in connectors.Where(static item => item.Enabled && !item.IsInternalSystemConnector))
        {
            foreach (ContainerContext workspace in workspaces)
            {
                if (!string.Equals(workspace.ScopeType, "agent", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                names.Add(BuildContainerName(connector.ConnectorId, WorkspaceMcpExecutionContext.Isolated, workspace));
                if (string.Equals(workspace.ScopeType, "agent", StringComparison.OrdinalIgnoreCase))
                {
                    names.Add(BuildContainerName(connector.ConnectorId, WorkspaceMcpExecutionContext.IsolatedWithAgentFiles, workspace));
                }
            }
        }

        return [.. names];
    }

    public async Task<IReadOnlyList<string>> ReconcileDesiredConnectorsAsync(
        IReadOnlyList<WorkspaceMcpContainerConnectorDefinition> connectors,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connectors);
        WorkspaceDesiredStateSnapshot authoritativeSnapshot = desiredStateStore.Snapshot;
        var notes = new List<string>();
        ContainerContext[] workspaces = [.. authoritativeSnapshot.DesiredWorkspaces
            .Where(static workspace => !string.IsNullOrWhiteSpace(workspace.UserId))
            .DistinctBy(static workspace => (workspace.ScopeType, workspace.ScopeId, workspace.UserId))];
        if (workspaces.Length == 0)
        {
            return notes;
        }

        foreach (WorkspaceMcpContainerConnectorDefinition connector in connectors.Where(static item => item.Enabled && !item.IsInternalSystemConnector))
        {
            foreach (ContainerContext workspace in workspaces)
            {
                if (!string.Equals(workspace.ScopeType, "agent", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                WorkspaceMcpExecutionContext[] executionContexts = [WorkspaceMcpExecutionContext.Isolated, WorkspaceMcpExecutionContext.IsolatedWithAgentFiles];
                foreach (WorkspaceMcpExecutionContext executionContext in executionContexts)
                {
                    string containerName = BuildContainerName(connector.ConnectorId, executionContext, workspace);
                    string runtimeKey = BuildRuntimeKey(connector.ConnectorId, executionContext, workspace);
                    try
                    {
                        if (await PodmanContainerInspector.ReadAsync(podmanCommandRunner, containerName, cancellationToken).ConfigureAwait(false) is null)
                        {
                            continue;
                        }
                    }
                    catch (ContainerInspectionUnavailableException ex)
                    {
                        notes.Add("Connector " + connector.ConnectorId + " inspection is unavailable. Reconciliation is deferred.");
                        logger.LogWarning(ex, "[ContainerConnector] Reconcile deferred for connector '{ConnectorId}'.", connector.ConnectorId);
                        continue;
                    }

                    WorkspaceMcpContainerConnectorDefinition runtimeConnector;
                    NetworkAccess.ImageCompatProfile? compatProfile;
                    try
                    {
                        workloadLaunchGate?.EnsureLaunchAllowed(workspace.UserId);
                        NetworkAccess.IWorkloadLaunchLease preparationLease = workloadLaunchGate is null
                            ? NoopLaunchLease.Instance
                            : await workloadLaunchGate.AcquireLaunchLeaseAsync(workspace.UserId, cancellationToken).ConfigureAwait(false);
                        await using var preparationLeaseScope = preparationLease.ConfigureAwait(false);
                        using CancellationTokenSource preparationCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                            cancellationToken,
                            preparationLease.CancellationToken);
                        bool isBraked = workloadLaunchGate?.IsBraked(workspace.UserId) ?? true;
                        runtimeConnector = await PrepareRuntimeConnectorAsync(connector, isBraked, preparationCancellation.Token).ConfigureAwait(false);
                        ImageCompatibilityResolution imageResolution = await ResolveImageCompatibilityAsync(runtimeConnector.Image, preparationCancellation.Token).ConfigureAwait(false);
                        runtimeConnector = runtimeConnector with { Image = imageResolution.Image };
                        compatProfile = imageResolution.Profile;
                    }
                    catch (Exception ex) when (ex is InvalidOperationException or IOException)
                    {
                        notes.Add("Connector " + connector.ConnectorId + " could not be prepared for inspection.");
                        logger.LogWarning(ex, "[ContainerConnector] Reconcile could not prepare connector '{ConnectorId}'.", connector.ConnectorId);
                        continue;
                    }

                    ConnectorContainerSpecBuilder.ConnectorIdentity identity = ConnectorContainerSpecBuilder.ResolveIdentity(runtimeConnector, workspace, purpose: null);
                    WorkspaceMcpContainerConnectorDefinition expectedConnector = runtimeConnector with
                    {
                        SpecFingerprint = ConnectorContainerSpecBuilder.ComputeWorkloadFingerprint(
                            runtimeConnector,
                            identity,
                            executionContext,
                            compatProfile),
                    };
                    ContainerContext? mountedWorkspace = ResolveMountedWorkspace(executionContext, workspace);
                    ConnectorContainerInspection inspection;
                    try
                    {
                        inspection = await InspectContainerStateAsync(containerName, expectedConnector, mountedWorkspace, cancellationToken).ConfigureAwait(false);
                    }
                    catch (ContainerInspectionUnavailableException ex)
                    {
                        notes.Add("Connector " + connector.ConnectorId + " inspection is unavailable. Reconciliation is deferred.");
                        logger.LogWarning(ex, "[ContainerConnector] Reconcile deferred for connector '{ConnectorId}'.", connector.ConnectorId);
                        continue;
                    }
                    if (inspection.Exists && inspection.IsRunning && inspection.Matches(expectedConnector))
                    {
                        try
                        {
                            await EnsureConnectorContainerRunningAsync(
                                expectedConnector,
                                runtimeKey,
                                containerName,
                                executionContext,
                                mountedWorkspace,
                                cancellationToken,
                                identityContext: workspace,
                                authoritativeSnapshot: authoritativeSnapshot).ConfigureAwait(false);
                            await ResetConnectorCooldownAsync(expectedConnector, runtimeKey, containerName).ConfigureAwait(false);
                        }
                        catch (Exception ex) when (ex is InvalidOperationException or IOException)
                        {
                            notes.Add("Connector " + connector.ConnectorId + " could not be re-verified after Manager restart.");
                            logger.LogWarning(ex, "[ContainerConnector] Reconcile did not re-verify connector '{ConnectorId}'.", connector.ConnectorId);
                        }

                        continue;
                    }

                    if (!inspection.Exists || !inspection.IsRunning)
                    {
                        continue;
                    }

                    string reason = inspection.Drift ?? "its spec label changed";
                    try
                    {
                        await EnsureConnectorContainerRunningAsync(
                            expectedConnector,
                            runtimeKey,
                            containerName,
                            executionContext,
                            mountedWorkspace,
                            cancellationToken,
                            identityContext: workspace,
                            authoritativeSnapshot: authoritativeSnapshot).ConfigureAwait(false);
                        await ResetConnectorCooldownAsync(expectedConnector, runtimeKey, containerName).ConfigureAwait(false);
                        notes.Add("Connector " + connector.ConnectorId + " was replaced because " + reason + ".");
                    }
                    catch (Exception ex) when (ex is InvalidOperationException or IOException)
                    {
                        notes.Add("Connector " + connector.ConnectorId + " could not be replaced.");
                        logger.LogWarning(ex, "[ContainerConnector] Reconcile did not replace connector '{ConnectorId}'.", connector.ConnectorId);
                    }
                }
            }
        }

        return notes;
    }

    public IReadOnlyList<string> TakeSandboxDriftNotes()
        => containerTool.TakeDriftNotes();

    public async Task ApplyDesiredStateUpdateAsync(
        IReadOnlyList<WorkspaceMcpContainerConnectorDefinition> previousConnectors,
        IReadOnlyList<WorkspaceMcpContainerConnectorDefinition> nextConnectors,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(previousConnectors);
        ArgumentNullException.ThrowIfNull(nextConnectors);
        cancellationToken.ThrowIfCancellationRequested();

        Dictionary<string, WorkspaceMcpContainerConnectorDefinition> nextById = nextConnectors
            .ToDictionary(static connector => connector.ConnectorId, StringComparer.OrdinalIgnoreCase);

        foreach (WorkspaceMcpContainerConnectorDefinition previousConnector in previousConnectors)
        {
            if (!nextById.TryGetValue(previousConnector.ConnectorId, out WorkspaceMcpContainerConnectorDefinition? nextConnector))
            {
                await StopTrackedConnectorInstancesAsync(previousConnector.ConnectorId, "connector-removed").ConfigureAwait(false);
                continue;
            }

            if (!RequiresContainerRecycle(previousConnector, nextConnector))
            {
                continue;
            }

            await StopTrackedConnectorInstancesAsync(previousConnector.ConnectorId, "connector-updated").ConfigureAwait(false);
        }

        // Connector images are materialized on execution. Building every image while
        // accepting desired state blocks Gateway startup on registry/build failures.
    }

    public async Task<OciSandboxSession> EnsureAgentSandboxProvisionedAsync(
        ContainerContext workspaceContext,
        IReadOnlyList<string> desiredConnectorIds,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(workspaceContext);
        ArgumentNullException.ThrowIfNull(desiredConnectorIds);

        workloadLaunchGate?.EnsureLaunchAllowed(workspaceContext.UserId);
        NetworkAccess.IWorkloadLaunchLease lease = workloadLaunchGate is null
            ? NoopLaunchLease.Instance
            : await workloadLaunchGate.AcquireLaunchLeaseAsync(workspaceContext.UserId, cancellationToken).ConfigureAwait(false);
        await using var leaseScope = lease.ConfigureAwait(false);
        using CancellationTokenSource launchCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lease.CancellationToken);
        return await EnsureAgentSandboxProvisionedUnderLeaseAsync(workspaceContext, desiredConnectorIds, launchCancellation.Token).ConfigureAwait(false);
    }

    private async Task<OciSandboxSession> EnsureAgentSandboxProvisionedUnderLeaseAsync(
        ContainerContext workspaceContext,
        IReadOnlyList<string> desiredConnectorIds,
        CancellationToken cancellationToken)
    {
        string sandboxProvisionKey = BuildSandboxProvisionKey(workspaceContext);
        bool isBraked = workloadLaunchGate?.IsBraked(workspaceContext.UserId) ?? true;
        await using var sandboxProvisionLease = (await sandboxProvisionLocks
            .AcquireAsync(sandboxProvisionKey, cancellationToken)
            .ConfigureAwait(false)).ConfigureAwait(false);
        AgentSandboxInstalledManifest desiredManifest = await BuildDesiredAgentSandboxManifestAsync(desiredConnectorIds, isBraked, cancellationToken).ConfigureAwait(false);
        OciSandboxSession session = await containerTool.SpawnSandboxAsync(workspaceContext, cancellationToken).ConfigureAwait(false);
        AgentSandboxInstalledManifest? installedManifest = await TryReadAgentSandboxManifestAsync(workspaceContext, cancellationToken).ConfigureAwait(false);

        if (AgentSandboxInstallContract.Equals(installedManifest, desiredManifest))
        {
            return session;
        }

        await containerTool.CloseSandboxAsync(workspaceContext, cancellationToken).ConfigureAwait(false);
        session = await containerTool.SpawnSandboxAsync(workspaceContext, cancellationToken).ConfigureAwait(false);

        try
        {
            await ResetAgentSandboxInstallRootAsync(workspaceContext, cancellationToken).ConfigureAwait(false);

            foreach (AgentSandboxInstalledContract contract in desiredManifest.Contracts)
            {
                await InstallAgentSandboxConnectorAsync(workspaceContext, session.ContainerName, contract, cancellationToken).ConfigureAwait(false);
            }

            await WriteAgentSandboxManifestAsync(workspaceContext, desiredManifest, cancellationToken).ConfigureAwait(false);
            return session;
        }
        catch
        {
            try
            {
                await containerTool.CloseSandboxAsync(workspaceContext, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "[ContainerConnector] Failed to tear down sandbox '{ScopeType}/{ScopeId}' after provisioning failure", workspaceContext.ScopeType, workspaceContext.ScopeId);
            }

            throw;
        }
    }

    /// <summary>
    /// Probes a containerized MCP connector without executing tools.
    /// </summary>
    public async Task<(bool Ok, IReadOnlyList<McpClientTool> Tools, WorkspaceMcpConnectorUnavailabilityReason? UnavailabilityReason, string? FailureMessage, string? ImageDigest)> ProbeConnectorAsync(
        string connectorId,
        int timeoutSeconds,
        CancellationToken cancellationToken,
        string? userId = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectorId);

        WorkspaceMcpContainerConnectorDefinition connector = ResolveConnectorDefinition(connectorId);
        return await ProbeConnectorAsync(connector, timeoutSeconds, cancellationToken, userId).ConfigureAwait(false);
    }

    public async Task<(bool Ok, IReadOnlyList<McpClientTool> Tools, WorkspaceMcpConnectorUnavailabilityReason? UnavailabilityReason, string? FailureMessage, string? ImageDigest)> ProbeConnectorAsync(
        WorkspaceMcpContainerConnectorDefinition connector,
        int timeoutSeconds,
        CancellationToken cancellationToken,
        string? userId = null,
        bool saveDiscovery = false)
    {
        ArgumentNullException.ThrowIfNull(connector);

        WorkspaceDesiredStateSnapshot authoritativeSnapshot = desiredStateStore.Snapshot;
        string connectorId = connector.ConnectorId;
        bool containerPrepared = false;
        EphemeralWorkload? ephemeralWorkload = null;
        TransportLaunch? transportLaunch = null;
        ContainerContext? probeContext = !string.IsNullOrWhiteSpace(userId)
            ? new ContainerContext("user", userId, userId, "/tmp", "/tmp", string.Empty, 0, string.Empty, 0, [], new Dictionary<string, string>())
            : null;

        try
        {
            WorkspaceMcpContainerConnectorDefinition effectiveConnector = BuildEffectiveConnectorDefinition(
                connector,
                environmentOverrides: null,
                timeoutSeconds,
                enabledOverride: false);

            if (logger.IsEnabled(LogLevel.Information))
            {
                logger.LogInformation("[ContainerConnector] Probing connector '{ConnectorId}' with {TimeoutSeconds}s timeout", effectiveConnector.ConnectorId, timeoutSeconds);
            }

            _ = desiredStateStore.TryTransitionConnectorState(
                effectiveConnector.ConnectorId,
                WorkspaceMcpContainerLifecycleState.Starting,
                "probe-start");

            transportLaunch = await CreateTransportCoreAsync(effectiveConnector, requireEnabled: false, workspaceContext: probeContext, isProbe: true, saveDiscovery: saveDiscovery, cancellationToken: cancellationToken, authoritativeSnapshot: authoritativeSnapshot).ConfigureAwait(false);
            ephemeralWorkload = transportLaunch.EphemeralWorkload;
            containerPrepared = true;
            var tools = await ListMcpToolsAsync(transportLaunch, ClientOptionsFor(effectiveConnector.McpProtocolVersion), cancellationToken).ConfigureAwait(false);

            _ = desiredStateStore.TryTransitionConnectorState(
                effectiveConnector.ConnectorId,
                WorkspaceMcpContainerLifecycleState.Ready,
                "probe-ready");

            return (true, tools, null, null, transportLaunch.ImageDigest);
        }
        catch (OperationCanceledException)
        {
            logger.LogWarning("[ContainerConnector] Probe timed out for connector '{ConnectorId}'", connectorId);
            return (false, [], WorkspaceMcpConnectorUnavailabilityReason.StartupTimeout, "Container connector probe timed out.", null);
        }
        catch (InvalidOperationException ex)
        {
            MarkConnectorFailed(connectorId, "probe-startup-failed");
            logger.LogWarning(ex, "[ContainerConnector] Container startup failed for connector '{ConnectorId}'", connectorId);
            string failureMessage = await DescribeFailureAsync(
                ex,
                ResolveFailureContainerId(connectorId, WorkspaceMcpExecutionContext.Isolated, probeContext, ephemeralWorkload),
                CancellationToken.None).ConfigureAwait(false);
            return (false, [], WorkspaceMcpConnectorUnavailabilityReason.InitializeFailed, failureMessage, null);
        }
        catch (Exception ex)
        {
            MarkConnectorFailed(connectorId, "probe-failed");
            logger.LogError(ex, "[ContainerConnector] Unexpected error probing connector '{ConnectorId}'", connectorId);
            string failureMessage = await DescribeFailureAsync(
                ex,
                ResolveFailureContainerId(connectorId, WorkspaceMcpExecutionContext.Isolated, probeContext, ephemeralWorkload),
                CancellationToken.None).ConfigureAwait(false);
            return (false, [], WorkspaceMcpConnectorUnavailabilityReason.HealthCheckFailed, failureMessage, null);
        }
        finally
        {
            try
            {
                try
                {
                    CompleteProbeAuthorization(connectorId, probeContext, ephemeralWorkload?.VerifiedContainerId ?? transportLaunch?.ContainerId ?? ephemeralWorkload?.ContainerName);
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Could not clear probe authorization for connector '{ConnectorId}'.", connectorId);
                }

                if (containerPrepared)
                {
                    if (!await CompleteEphemeralWorkloadAsync(ephemeralWorkload).ConfigureAwait(false))
                    {
                        await ResetConnectorCooldownAsync(
                            connector,
                            BuildRuntimeKey(connector.ConnectorId, WorkspaceMcpExecutionContext.Isolated, probeContext),
                            BuildContainerName(connector.ConnectorId, WorkspaceMcpExecutionContext.Isolated, probeContext)).ConfigureAwait(false);
                    }
                }
            }
            finally
            {
                await ReleaseLaunchLeaseAsync(transportLaunch).ConfigureAwait(false);
            }
        }
    }

    internal void CompleteProbeAuthorization(string connectorId, ContainerContext? probeContext, string? probeContainerId = null)
    {
        if (clearProbeAuthorization is null)
        {
            return;
        }

        string containerId = string.IsNullOrWhiteSpace(probeContainerId)
            ? BuildContainerName(connectorId, WorkspaceMcpExecutionContext.Isolated, probeContext)
            : probeContainerId.Trim();
        clearProbeAuthorization(containerId);
    }

    /// <summary>
    /// Lists tools from a containerized MCP connector.
    /// </summary>
    public async Task<(bool Ok, IReadOnlyList<McpClientTool> Tools, WorkspaceMcpConnectorUnavailabilityReason? UnavailabilityReason, string? FailureMessage)> ListToolsAsync(
        string connectorId,
        IReadOnlyDictionary<string, string>? environmentOverrides,
        int timeoutSeconds,
        bool allowInternalSystemConnector,
        ContainerContext? workspaceContext,
        WorkspaceMcpExecutionContext executionContext,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectorId);

        WorkspaceDesiredStateSnapshot authoritativeSnapshot = desiredStateStore.Snapshot;
        WorkspaceMcpContainerConnectorDefinition? connector = null;
        bool containerPrepared = false;
        EphemeralWorkload? ephemeralWorkload = null;
        TransportLaunch? transportLaunch = null;

        try
        {
            connector = ResolveEffectiveConnectorDefinition(
                connectorId,
                environmentOverrides,
                timeoutSeconds,
                allowInternalSystemConnector);

            if (logger.IsEnabled(LogLevel.Information))
            {
                logger.LogInformation("[ContainerConnector] Listing tools from connector '{ConnectorId}'", connector.ConnectorId);
            }

            _ = desiredStateStore.TryTransitionConnectorState(
                connector.ConnectorId,
                WorkspaceMcpContainerLifecycleState.Starting,
                "list-start");

            transportLaunch = await CreateTransportAsync(connector, requireEnabled: true, allowInternalSystemConnector, workspaceContext, executionContext, isProbe: false, cancellationToken: cancellationToken, authoritativeSnapshot: authoritativeSnapshot).ConfigureAwait(false);
            ephemeralWorkload = transportLaunch.EphemeralWorkload;
            containerPrepared = NormalizeExecutionContext(executionContext) != WorkspaceMcpExecutionContext.AgentSandbox;
            var tools = await ListMcpToolsAsync(transportLaunch, ClientOptionsFor(connector.McpProtocolVersion), cancellationToken).ConfigureAwait(false);

            _ = desiredStateStore.TryTransitionConnectorState(
                connector.ConnectorId,
                WorkspaceMcpContainerLifecycleState.Ready,
                "list-ready");

            return (true, tools, null, null);
        }
        catch (OperationCanceledException)
        {
            logger.LogWarning("[ContainerConnector] Tool listing timed out for connector '{ConnectorId}'", connectorId);
            return (false, [], WorkspaceMcpConnectorUnavailabilityReason.StartupTimeout, "Container connector tool listing timed out.");
        }
        catch (Exception ex)
        {
            MarkConnectorFailed(connectorId, "list-failed");
            logger.LogError(ex, "[ContainerConnector] Error listing tools from connector '{ConnectorId}'", connectorId);
            string failureMessage = await DescribeFailureAsync(
                ex,
                connector is null ? null : ResolveFailureContainerId(connectorId, executionContext, workspaceContext, ephemeralWorkload),
                CancellationToken.None).ConfigureAwait(false);
            return (false, [], WorkspaceMcpConnectorUnavailabilityReason.HealthCheckFailed, failureMessage);
        }
        finally
        {
            try
            {
                if (containerPrepared && connector is not null)
                {
                    if (!await CompleteEphemeralWorkloadAsync(ephemeralWorkload).ConfigureAwait(false))
                    {
                        await ResetConnectorCooldownAsync(
                            connector,
                            BuildRuntimeKey(connector.ConnectorId, executionContext, workspaceContext),
                            BuildContainerName(connector.ConnectorId, executionContext, workspaceContext)).ConfigureAwait(false);
                    }
                }
            }
            finally
            {
                await ReleaseLaunchLeaseAsync(transportLaunch).ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// Calls a tool on a containerized MCP connector.
    /// </summary>
    public async Task<(bool Ok, string? Result, WorkspaceMcpConnectorUnavailabilityReason? UnavailabilityReason, string? FailureMessage)> CallToolAsync(
        string connectorId,
        IReadOnlyDictionary<string, string>? environmentOverrides,
        string toolName,
        string toolInputJson,
        int timeoutSeconds,
        bool allowInternalSystemConnector,
        ContainerContext? workspaceContext,
        WorkspaceMcpExecutionContext executionContext,
        CancellationToken cancellationToken,
        string? hostArtifactDirectory = null,
        string? expectedImageDigest = null,
        string? expectedSourceDigest = null,
        string? expectedCommand = null,
        IReadOnlyList<string>? expectedArguments = null,
        string? expectedMcpProtocolVersion = null,
        string? expectedServerType = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectorId);
        ArgumentException.ThrowIfNullOrWhiteSpace(toolName);

        WorkspaceDesiredStateSnapshot authoritativeSnapshot = desiredStateStore.Snapshot;
        WorkspaceMcpContainerConnectorDefinition? connector = null;
        bool containerPrepared = false;
        EphemeralWorkload? ephemeralWorkload = null;
        TransportLaunch? transportLaunch = null;

        try
        {
            connector = ResolveEffectiveConnectorDefinition(
                connectorId,
                environmentOverrides,
                timeoutSeconds,
                allowInternalSystemConnector);

            if (expectedCommand is not null
                && (!string.Equals(connector.Command ?? string.Empty, expectedCommand, StringComparison.Ordinal)
                    || !connector.Args.SequenceEqual(expectedArguments ?? [], StringComparer.Ordinal)
                    || !string.Equals(connector.McpProtocolVersion ?? string.Empty, expectedMcpProtocolVersion ?? string.Empty, StringComparison.Ordinal)
                    || !string.Equals(connector.ServerType ?? string.Empty, expectedServerType ?? string.Empty, StringComparison.Ordinal)))
                throw new InvalidOperationException("The connector execution configuration changed. Test the connection and start a new run.");

            if (!string.IsNullOrWhiteSpace(expectedSourceDigest)
                && (string.IsNullOrWhiteSpace(connector.BundlePath) || !File.Exists(connector.BundlePath)
                    || !string.Equals(McpbImageMaterializer.ComputeSha256Hex(connector.BundlePath), expectedSourceDigest, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException("The connector package changed. Test the connection and start a new run.");

            if (logger.IsEnabled(LogLevel.Information))
            {
                logger.LogInformation("[ContainerConnector] Calling tool '{ToolName}' on connector '{ConnectorId}'", toolName, connector.ConnectorId);
            }

            _ = desiredStateStore.TryTransitionConnectorState(
                connector.ConnectorId,
                WorkspaceMcpContainerLifecycleState.Starting,
                "call-start");

            transportLaunch = await CreateTransportAsync(connector, requireEnabled: true, allowInternalSystemConnector, workspaceContext, executionContext, isProbe: false, cancellationToken: cancellationToken, authoritativeSnapshot: authoritativeSnapshot, expectedImageDigest: expectedImageDigest).ConfigureAwait(false);
            ephemeralWorkload = transportLaunch.EphemeralWorkload;
            containerPrepared = NormalizeExecutionContext(executionContext) != WorkspaceMcpExecutionContext.AgentSandbox;
            using CancellationTokenSource launchCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, transportLaunch.CancellationToken);
            McpClient client = await McpClient.CreateAsync(transportLaunch.Transport, ClientOptionsFor(connector.McpProtocolVersion), cancellationToken: launchCancellation.Token).ConfigureAwait(false);
            await using (client.ConfigureAwait(false))
            {
                await ReleaseLaunchLeaseAsync(transportLaunch).ConfigureAwait(false);
                var toolInput = McpToolContractCodec.ParseInput(toolInputJson);
                CallToolResult result = await client.CallToolAsync(toolName, toolInput, cancellationToken: cancellationToken).ConfigureAwait(false);
                string resultJson = ViewFileMaterializer.Materialize(
                    McpToolContractCodec.SerializeResult(result),
                    hostArtifactDirectory);

                _ = desiredStateStore.TryTransitionConnectorState(
                    connector.ConnectorId,
                    WorkspaceMcpContainerLifecycleState.Ready,
                    "call-ready");

                return (true, resultJson, null, null);
            }
        }
        catch (OperationCanceledException)
        {
            logger.LogWarning("[ContainerConnector] Tool call timed out for tool '{ToolName}' on connector '{ConnectorId}'", toolName, connectorId);
            return (false, null, WorkspaceMcpConnectorUnavailabilityReason.StartupTimeout, "Container connector tool call timed out.");
        }
        catch (Exception ex)
        {
            MarkConnectorFailed(connectorId, "call-failed");
            logger.LogError(ex, "[ContainerConnector] Error calling tool '{ToolName}' on connector '{ConnectorId}'", toolName, connectorId);
            string failureMessage = await DescribeFailureAsync(
                ex,
                connector is null ? null : ResolveFailureContainerId(connectorId, executionContext, workspaceContext, ephemeralWorkload),
                CancellationToken.None).ConfigureAwait(false);
            return (false, null, WorkspaceMcpConnectorUnavailabilityReason.HealthCheckFailed, failureMessage);
        }
        finally
        {
            try
            {
                if (containerPrepared && connector is not null)
                {
                    if (!await CompleteEphemeralWorkloadAsync(ephemeralWorkload).ConfigureAwait(false))
                    {
                        await ResetConnectorCooldownAsync(
                            connector,
                            BuildRuntimeKey(connector.ConnectorId, executionContext, workspaceContext),
                            BuildContainerName(connector.ConnectorId, executionContext, workspaceContext)).ConfigureAwait(false);
                    }
                }
            }
            finally
            {
                await ReleaseLaunchLeaseAsync(transportLaunch).ConfigureAwait(false);
            }
        }
    }

    private static string DescribeFailure(Exception exception)
    {
        var parts = new List<string>();

        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            string message = current.Message?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(message))
            {
                continue;
            }

            if (parts.Contains(message, StringComparer.Ordinal))
            {
                continue;
            }

            parts.Add(message);
        }

        if (parts.Count == 0)
        {
            return exception.GetType().Name;
        }

        const string genericShutdownMessage = "The server shut down unexpectedly.";
        if (parts.Count > 1 && string.Equals(parts[0], genericShutdownMessage, StringComparison.Ordinal))
        {
            parts.RemoveAt(0);
        }

        return string.Join(" | ", parts);
    }

    private async Task<string> DescribeFailureAsync(Exception exception, string? containerId, CancellationToken cancellationToken)
    {
        string failure = DescribeFailure(exception);
        if (string.IsNullOrWhiteSpace(containerId)
            || (!failure.Contains("server shut down unexpectedly", StringComparison.OrdinalIgnoreCase)
                && !failure.Contains("server process exited unexpectedly", StringComparison.OrdinalIgnoreCase)
                && !failure.Contains("transport was closed", StringComparison.OrdinalIgnoreCase)))
        {
            return failure;
        }

        try
        {
            PodmanCommandResult logs = await podmanCommandRunner
                .RunAsync(["logs", "--tail", "100", containerId], 10, cancellationToken)
                .ConfigureAwait(false);
            string details = string.Join("\n", new[] { logs.StdOut, logs.StdErr }
                .Where(static text => !string.IsNullOrWhiteSpace(text)))
                .Trim();
            if (logs.ExitCode == 0 && !string.IsNullOrWhiteSpace(details))
            {
                const int maxLogCharacters = 8192;
                if (details.Length > maxLogCharacters)
                {
                    details = details[^maxLogCharacters..];
                }

                return failure + " | Container logs: " + details;
            }
        }
        catch (Exception logException)
        {
            logger.LogDebug(logException, "Could not read Podman logs for failed connector container '{ContainerId}'", containerId);
        }

        return failure;
    }

    private static string? ResolveFailureContainerId(
        string connectorId,
        WorkspaceMcpExecutionContext executionContext,
        ContainerContext? workspaceContext,
        EphemeralWorkload? ephemeralWorkload)
    {
        if (ephemeralWorkload is not null)
        {
            return ephemeralWorkload.ContainerName;
        }

        return NormalizeExecutionContext(executionContext) == WorkspaceMcpExecutionContext.AgentSandbox
            ? null
            : BuildContainerName(connectorId, executionContext, workspaceContext);
    }

    private Task<TransportLaunch> CreateTransportAsync(
        WorkspaceMcpContainerConnectorDefinition connector,
        bool requireEnabled,
        bool allowInternalSystemConnector = false,
        ContainerContext? workspaceContext = null,
        WorkspaceMcpExecutionContext executionContext = WorkspaceMcpExecutionContext.Unspecified,
        bool isProbe = false,
        WorkspaceDesiredStateSnapshot? authoritativeSnapshot = null,
        string? expectedImageDigest = null,
        CancellationToken cancellationToken = default)
        => CreateTransportCoreAsync(connector, requireEnabled, allowInternalSystemConnector, workspaceContext,
            executionContext, isProbe, false, authoritativeSnapshot, expectedImageDigest, cancellationToken);

    private async Task<TransportLaunch> CreateTransportCoreAsync(
        WorkspaceMcpContainerConnectorDefinition connector,
        bool requireEnabled,
        bool allowInternalSystemConnector = false,
        ContainerContext? workspaceContext = null,
        WorkspaceMcpExecutionContext executionContext = WorkspaceMcpExecutionContext.Unspecified,
        bool isProbe = false,
        bool saveDiscovery = false,
        WorkspaceDesiredStateSnapshot? authoritativeSnapshot = null,
        string? expectedImageDigest = null,
        CancellationToken cancellationToken = default)
    {
        authoritativeSnapshot ??= desiredStateStore.Snapshot;
        bool canExecute = connector.Enabled || (allowInternalSystemConnector && connector.IsInternalSystemConnector);

        if (requireEnabled && !canExecute)
        {
            throw new InvalidOperationException($"Container connector '{connector.ConnectorId}' is disabled.");
        }

        if (allowInternalSystemConnector && connector.IsInternalSystemConnector && !connector.Enabled && logger.IsEnabled(LogLevel.Debug))
        {
            logger.LogDebug("[ContainerConnector] Allowing internal-system connector '{ConnectorId}' execution while disabled.", connector.ConnectorId);
        }

        if (string.IsNullOrWhiteSpace(connector.Image))
        {
            throw new InvalidOperationException($"Container connector '{connector.ConnectorId}' has no image configured.");
        }

        ConnectorContainerSpecBuilder.ConnectorIdentity launchIdentity = ConnectorContainerSpecBuilder.ResolveIdentity(
            connector,
            workspaceContext,
            isProbe ? (saveDiscovery ? "save-discovery" : "probe") : null);
        string? launchUserId = string.IsNullOrWhiteSpace(launchIdentity.UserId) ? null : launchIdentity.UserId;
        workloadLaunchGate?.EnsureLaunchAllowed(launchUserId);
        NetworkAccess.IWorkloadLaunchLease launchLease = workloadLaunchGate is null
            ? NoopLaunchLease.Instance
            : await workloadLaunchGate.AcquireLaunchLeaseAsync(launchUserId, cancellationToken).ConfigureAwait(false);
        using var launchCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, launchLease.CancellationToken);
        cancellationToken = launchCancellation.Token;
        bool leaseTransferred = false;
        EphemeralWorkload? ephemeralWorkload = null;
        try
        {
            bool isBraked = workloadLaunchGate?.IsBraked(launchUserId) ?? true;

            WorkspaceMcpExecutionContext effectiveExecutionContext = NormalizeExecutionContext(executionContext);

            if (effectiveExecutionContext == WorkspaceMcpExecutionContext.AgentSandbox)
            {
                if (workspaceContext is null)
                {
                    throw new InvalidOperationException($"Container connector '{connector.ConnectorId}' requires a workspace context for agent sandbox execution.");
                }

                if (!string.IsNullOrWhiteSpace(expectedImageDigest)
                    && (!connector.Image.Contains("@sha256:", StringComparison.OrdinalIgnoreCase)
                        && !connector.Image.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase)))
                    throw new InvalidOperationException("The connector base image must be pinned to a digest. Test the connection and start a new run.");

                IReadOnlyList<string> desiredConnectorIds = ResolveDesiredSandboxConnectorIds(workspaceContext);
                OciSandboxSession sandboxSession = await EnsureAgentSandboxProvisionedUnderLeaseAsync(workspaceContext, desiredConnectorIds, cancellationToken).ConfigureAwait(false);
                AgentSandboxInstalledContract installedContract = await GetInstalledAgentSandboxContractAsync(workspaceContext, connector.ConnectorId, cancellationToken).ConfigureAwait(false);
                WorkspaceMcpContainerConnectorDefinition sandboxConnector = ApplyInstalledAgentSandboxContract(connector, installedContract);
                List<string> sandboxArguments = ConnectorContainerSpecBuilder.BuildExec(
                    sandboxConnector,
                    sandboxSession.ContainerName,
                    installedContract.WorkingDirectory ?? installedContract.InstallRoot);

                var sandboxLaunch = new TransportLaunch(new StdioClientTransport(new StdioClientTransportOptions
                {
                    Command = stdioCommand,
                    Arguments = [.. (stdioArgumentsPrefix ?? []), .. sandboxArguments],
                    EnvironmentVariables = ConnectorContainerSpecBuilder.BuildTransportEnvironment(sandboxConnector),
                    Name = $"sandbox-connector-{connector.ConnectorId}"
                }), null, launchLease);
                leaseTransferred = true;
                return sandboxLaunch;
            }

            if (isProbe)
            {
                connector = await CompleteProbeMaterializationAsync(
                    token => PrepareRuntimeConnectorAsync(connector, isBraked, token),
                    cancellationToken,
                    launchLease.CancellationToken,
                    hostLifetime?.ApplicationStopping ?? CancellationToken.None).ConfigureAwait(false);
                workloadLaunchGate?.EnsureLaunchAllowed(launchUserId);
            }
            else
            {
                connector = await PrepareRuntimeConnectorAsync(connector, isBraked, cancellationToken).ConfigureAwait(false);
            }

            string? imageDigest = null;
            if (isProbe || !string.IsNullOrWhiteSpace(expectedImageDigest))
            {
                imageDigest = NormalizePodmanImageId(await TryReadImageIdAsync(connector.Image, cancellationToken).ConfigureAwait(false));
                if (string.IsNullOrWhiteSpace(imageDigest))
                    throw new InvalidOperationException("The connector image could not be pinned to an immutable digest.");
                if (!string.IsNullOrWhiteSpace(expectedImageDigest) && !string.Equals(imageDigest, expectedImageDigest, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("The connector image changed. Test the connection and start a new run.");
                connector = connector with { Image = imageDigest };
            }
            string runtimeKey = BuildRuntimeKey(connector.ConnectorId, effectiveExecutionContext, workspaceContext);
            string containerName = BuildContainerName(connector.ConnectorId, effectiveExecutionContext, workspaceContext);
            string? persistentContainerId = null;
            List<string> arguments;
            if (UsesImageDefaultCommand(connector))
            {
                containerName = BuildEphemeralContainerName(containerName);
                ConnectorContainerSpecBuilder.ConnectorIdentity identity = ConnectorContainerSpecBuilder.ResolveIdentity(
                    connector,
                    workspaceContext,
                    isProbe ? (saveDiscovery ? "save-discovery" : "probe") : null);
                string allocatedIp = (addressAllocator ?? throw new InvalidOperationException("The Manager workload address allocator is unavailable."))
                    .AllocateNextIp(workloadRegistry);
                ImageCompatibilityResolution imageResolution = await ResolveImageCompatibilityAsync(connector.Image, cancellationToken).ConfigureAwait(false);
                connector = connector with { Image = imageResolution.Image };
                NetworkAccess.ImageCompatProfile? compatProfile = imageResolution.Profile;
                string attribution = identity.Egress == "deny-all"
                    ? "none"
                    : (isProbe || string.IsNullOrWhiteSpace(identity.AgentId) ? "user" : "user-agent");

                var desiredIdentity = new Kautuka.NetworkAccessController.RegistryEntry(
                        allocatedIp,
                        string.IsNullOrWhiteSpace(identity.UserId) ? null : identity.UserId,
                        string.IsNullOrWhiteSpace(identity.AgentId) ? null : identity.AgentId,
                        containerName,
                        identity.Kind,
                        connector.ConnectorId,
                        ExecutionContextLabel(effectiveExecutionContext),
                        false,
                        identity.Egress,
                        attribution,
                        compatProfile is not null ? string.Join(',', compatProfile.Relaxations) : null,
                        DateTimeOffset.UtcNow,
                        isProbe ? (saveDiscovery ? "save-discovery" : "probe") : null);
                if (workloadRegistry is not null
                    && !workloadRegistry.TryRegisterQuarantined(desiredIdentity with { Egress = desiredIdentity.Egress == "deny-all" ? "deny-all" : "proxy-only", Attribution = "none" }))
                {
                    throw new InvalidOperationException($"Connector '{connector.ConnectorId}' workload address is already registered.");
                }

                ephemeralWorkload = new EphemeralWorkload(containerName, allocatedIp, compatProfile, desiredIdentity);
                trackedConnectorContainers["ephemeral:" + containerName] = containerName;
                arguments = BuildConnectorWorkloadRunArguments(
                    connector,
                    containerName,
                    effectiveExecutionContext,
                    WithConfiguredWorkspaceRoot(workspaceContext),
                    persistent: false,
                    allocatedIp: allocatedIp,
                    isBraked: isBraked,
                    purpose: isProbe ? (saveDiscovery ? "save-discovery" : "probe") : null,
                    compatProfile: compatProfile);
            }
            else
            {
                persistentContainerId = await EnsureConnectorContainerRunningCoreAsync(
                    connector,
                    runtimeKey,
                    containerName,
                    effectiveExecutionContext,
                    ResolveMountedWorkspace(effectiveExecutionContext, workspaceContext),
                    cancellationToken,
                    isProbe: isProbe,
                    saveDiscovery: saveDiscovery,
                    identityContext: workspaceContext,
                    authoritativeSnapshot: authoritativeSnapshot).ConfigureAwait(false);
                arguments = ConnectorContainerSpecBuilder.BuildExec(connector, containerName, ResolveExecWorkingDirectory(connector));
            }

            var stdioTransport = new StdioClientTransport(new StdioClientTransportOptions
            {
                Command = stdioCommand,
                Arguments = [.. (stdioArgumentsPrefix ?? []), .. arguments],
                EnvironmentVariables = ConnectorContainerSpecBuilder.BuildTransportEnvironment(connector),
                Name = $"container-connector-{connector.ConnectorId}"
            });
            IClientTransport transport = ephemeralWorkload is null
                ? stdioTransport
                : new VerifiedConnectorTransport(stdioTransport, token => VerifyEphemeralWorkloadAsync(ephemeralWorkload, connector.ReadinessTimeoutSeconds, token));
            var transportLaunch = new TransportLaunch(transport, ephemeralWorkload, launchLease, imageDigest, persistentContainerId);
            leaseTransferred = true;
            return transportLaunch;
        }
        finally
        {
            if (!leaseTransferred)
            {
                if (ephemeralWorkload is not null)
                {
                    RemoveNeverStartedEphemeralIdentity(ephemeralWorkload);
                    trackedConnectorContainers.TryRemove("ephemeral:" + ephemeralWorkload.ContainerName, out _);
                }

                await launchLease.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    private async Task<AgentSandboxInstalledManifest> BuildDesiredAgentSandboxManifestAsync(
        IReadOnlyList<string> desiredConnectorIds,
        bool isBraked,
        CancellationToken cancellationToken)
    {
        List<AgentSandboxInstalledContract> contracts = [];

        foreach (string connectorId in desiredConnectorIds.Distinct(StringComparer.Ordinal).OrderBy(static connectorId => connectorId, StringComparer.Ordinal))
        {
            WorkspaceMcpContainerConnectorDefinition sourceConnector = ResolveConnectorDefinition(connectorId);
            if (string.IsNullOrWhiteSpace(sourceConnector.BundlePath))
            {
                throw new InvalidOperationException($"Container connector '{connectorId}' must be bundle-backed to run inside an agent sandbox.");
            }

            string installRoot = AgentSandboxInstallContract.BuildInstallRoot(sourceConnector);
            string artifactImage = await mcpbImageMaterializer
                .EnsureSandboxArtifactImageAsync(sourceConnector, installRoot, isBraked, cancellationToken)
                .ConfigureAwait(false);

            (string executablePath, IReadOnlyList<string> argsTemplate) = AgentSandboxInstallContract.RewriteLaunch(
                sourceConnector.ServerType,
                McpbImageMaterializer.RewriteBundlePlaceholder(sourceConnector.Command, installRoot),
                [.. sourceConnector.Args.Select(arg => McpbImageMaterializer.RewriteBundlePlaceholder(arg, installRoot))],
                installRoot);

            contracts.Add(new AgentSandboxInstalledContract(
                sourceConnector.ConnectorId,
                artifactImage[(artifactImage.LastIndexOf(':') + 1)..],
                artifactImage,
                sourceConnector.ServerType ?? string.Empty,
                installRoot,
                executablePath,
                argsTemplate,
                [.. sourceConnector.Environment.Select(pair => new KeyValuePair<string, string>(
                    pair.Key,
                    McpbImageMaterializer.RewriteBundlePlaceholder(pair.Value, installRoot)))],
                installRoot,
                agentSandboxNormalizationVersion));
        }

        return new AgentSandboxInstalledManifest(
            agentSandboxNormalizationVersion,
            contracts);
    }

    private string[] ResolveDesiredSandboxConnectorIds(ContainerContext workspaceContext)
        => desiredStateStore.Snapshot.DesiredSandboxConnectorBindings
            .Where(binding => string.Equals(binding.ScopeType, workspaceContext.ScopeType, StringComparison.OrdinalIgnoreCase)
                && string.Equals(binding.ScopeId, workspaceContext.ScopeId, StringComparison.OrdinalIgnoreCase))
            .SelectMany(static binding => binding.ConnectorIds)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(static connectorId => connectorId, StringComparer.Ordinal)
            .ToArray();

    private async Task<AgentSandboxInstalledContract> GetInstalledAgentSandboxContractAsync(
        ContainerContext workspaceContext,
        string connectorId,
        CancellationToken cancellationToken)
    {
        AgentSandboxInstalledManifest manifest = await TryReadAgentSandboxManifestAsync(workspaceContext, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Agent sandbox '{workspaceContext.ScopeId}' has no installed connector manifest.");

        return manifest.Contracts.FirstOrDefault(contract => string.Equals(contract.ConnectorId, connectorId, StringComparison.Ordinal))
            ?? throw new InvalidOperationException($"Agent sandbox '{workspaceContext.ScopeId}' is missing installed contract for connector '{connectorId}'.");
    }

    private static WorkspaceMcpContainerConnectorDefinition ApplyInstalledAgentSandboxContract(
        WorkspaceMcpContainerConnectorDefinition connector,
        AgentSandboxInstalledContract installedContract)
    {
        Dictionary<string, string> mergedEnvironment = installedContract.EnvTemplate
            .ToDictionary(static pair => pair.Key, static pair => pair.Value, StringComparer.OrdinalIgnoreCase);

        foreach (KeyValuePair<string, string> pair in connector.Environment)
        {
            if (string.IsNullOrWhiteSpace(pair.Key))
            {
                continue;
            }

            mergedEnvironment[pair.Key] = AgentSandboxInstallContract.RewriteRuntimePath(pair.Value, installedContract.InstallRoot);
        }

        return connector with
        {
            Command = installedContract.ExecutablePath,
            Args = installedContract.ArgsTemplate,
            Environment = [.. mergedEnvironment.Select(static pair => new KeyValuePair<string, string>(pair.Key, pair.Value))]
        };
    }

    private async Task<AgentSandboxInstalledManifest?> TryReadAgentSandboxManifestAsync(
        ContainerContext workspaceContext,
        CancellationToken cancellationToken)
    {
        ContainerExecutionResult result = await containerTool
            .RunShellAsync(
                workspaceContext,
                $"if [ -f {AgentSandboxInstallContract.QuoteShell(agentSandboxManifestPath)} ]; then cat {AgentSandboxInstallContract.QuoteShell(agentSandboxManifestPath)}; fi",
                timeoutSeconds: workspaceContext.DefaultTimeoutSeconds,
                cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        if (result.ExitCode != 0 || string.IsNullOrWhiteSpace(result.StdOut))
        {
            return null;
        }

        return JsonSerializer.Deserialize(result.StdOut, AgentSandboxContractJsonContext.Default.AgentSandboxInstalledManifest);
    }

    private async Task ResetAgentSandboxInstallRootAsync(
        ContainerContext workspaceContext,
        CancellationToken cancellationToken)
    {
        await RunSandboxShellAsync(
            workspaceContext,
            $"rm -rf {AgentSandboxInstallContract.QuoteShell(agentSandboxInstallRoot)} && mkdir -p {AgentSandboxInstallContract.QuoteShell(agentSandboxInstallRoot)}",
            cancellationToken).ConfigureAwait(false);
    }

    private async Task InstallAgentSandboxConnectorAsync(
        ContainerContext workspaceContext,
        string sandboxContainerName,
        AgentSandboxInstalledContract contract,
        CancellationToken cancellationToken)
    {
        string stagingContainerName = $"kautuka-stage-{SanitizeSegment(contract.ConnectorId)}-{Guid.NewGuid():N}";

        try
        {
            PodmanCommandResult createResult = await podmanCommandRunner
                .RunAsync(["create", "--name", stagingContainerName, contract.RuntimeImage], 30, cancellationToken)
                .ConfigureAwait(false);

            if (createResult.ExitCode != 0)
            {
                throw new InvalidOperationException(PodmanFailureFormatter.FormatFailure(
                    $"Create staging container for connector '{contract.ConnectorId}'",
                    createResult));
            }

            await RunSandboxShellAsync(
                workspaceContext,
                $"rm -rf {AgentSandboxInstallContract.QuoteShell(contract.InstallRoot)} && mkdir -p {AgentSandboxInstallContract.QuoteShell(contract.InstallRoot)}",
                cancellationToken).ConfigureAwait(false);

            PodmanCommandResult copyResult = await podmanCommandRunner
                .RunAsync(["cp", $"{stagingContainerName}:{McpbImageMaterializer.SandboxArtifactRoot}/.", $"{sandboxContainerName}:{contract.InstallRoot}"], 180, cancellationToken)
                .ConfigureAwait(false);

            if (copyResult.ExitCode != 0)
            {
                throw new InvalidOperationException(PodmanFailureFormatter.FormatFailure(
                    $"Install sandbox connector '{contract.ConnectorId}'",
                    copyResult));
            }
        }
        finally
        {
            _ = await podmanCommandRunner.RunAsync(["rm", "-f", stagingContainerName], 15, CancellationToken.None).ConfigureAwait(false);
        }
    }

    private async Task WriteAgentSandboxManifestAsync(
        ContainerContext workspaceContext,
        AgentSandboxInstalledManifest manifest,
        CancellationToken cancellationToken)
    {
        string payload = Convert.ToBase64String(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(manifest, AgentSandboxContractJsonContext.Default.AgentSandboxInstalledManifest)));
        string command = $"mkdir -p {AgentSandboxInstallContract.QuoteShell(agentSandboxInstallRoot)} && printf %s {AgentSandboxInstallContract.QuoteShell(payload)} | base64 -d > {AgentSandboxInstallContract.QuoteShell(agentSandboxManifestPath)}";
        await RunSandboxShellAsync(workspaceContext, command, cancellationToken).ConfigureAwait(false);
    }

    private async Task RunSandboxShellAsync(
        ContainerContext workspaceContext,
        string command,
        CancellationToken cancellationToken)
    {
        ContainerExecutionResult result = await containerTool
            .RunShellAsync(workspaceContext, command, timeoutSeconds: workspaceContext.DefaultTimeoutSeconds, cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException($"Sandbox command failed for '{workspaceContext.ScopeId}': {result.StdErr}".Trim());
        }
    }

    private async Task<WorkspaceMcpContainerConnectorDefinition> PrepareRuntimeConnectorAsync(
        WorkspaceMcpContainerConnectorDefinition connector,
        bool isBraked,
        CancellationToken cancellationToken)
    {
        string? serverType = connector.ServerType;
        if (string.IsNullOrWhiteSpace(serverType))
        {
            serverType = McpbImageMaterializer.InferServerType(connector.Command);
        }

        if (string.IsNullOrWhiteSpace(connector.BundlePath) && string.IsNullOrWhiteSpace(serverType))
        {
            return connector;
        }

        string runtimeImage = await mcpbImageMaterializer
            .EnsureImageAsync(connector, isBraked, cancellationToken)
            .ConfigureAwait(false);

        if (string.IsNullOrWhiteSpace(connector.BundlePath))
        {
            return connector with { Image = runtimeImage };
        }

        return AgentSandboxInstallContract.ApplyRuntime(connector, runtimeImage);
    }

    internal static async Task<T> CompleteProbeMaterializationAsync<T>(
        Func<CancellationToken, Task<T>> materialize,
        CancellationToken requestCancellation,
        CancellationToken admissionCancellation,
        CancellationToken shutdownCancellation)
    {
        using var buildCancellation = CancellationTokenSource.CreateLinkedTokenSource(admissionCancellation, shutdownCancellation);
        T result = await materialize(buildCancellation.Token).ConfigureAwait(false);
        requestCancellation.ThrowIfCancellationRequested();
        return result;
    }

    private async Task EnsureConnectorContainerRunningAsync(
        WorkspaceMcpContainerConnectorDefinition connector,
        string runtimeKey,
        string containerName,
        WorkspaceMcpExecutionContext executionContext,
        ContainerContext? workspaceContext,
        CancellationToken cancellationToken,
        bool isProbe = false,
        ContainerContext? identityContext = null,
        WorkspaceDesiredStateSnapshot? authoritativeSnapshot = null)
    {
        _ = await EnsureConnectorContainerRunningCoreAsync(connector, runtimeKey, containerName, executionContext,
            workspaceContext, cancellationToken, isProbe, false, identityContext, authoritativeSnapshot).ConfigureAwait(false);
    }

    private async Task<string?> EnsureConnectorContainerRunningCoreAsync(
        WorkspaceMcpContainerConnectorDefinition connector,
        string runtimeKey,
        string containerName,
        WorkspaceMcpExecutionContext executionContext,
        ContainerContext? workspaceContext,
        CancellationToken cancellationToken,
        bool isProbe = false,
        bool saveDiscovery = false,
        ContainerContext? identityContext = null,
        WorkspaceDesiredStateSnapshot? authoritativeSnapshot = null)
    {
        await using var connectorLease = (await connectorLocks
            .AcquireAsync(runtimeKey, cancellationToken)
            .ConfigureAwait(false)).ConfigureAwait(false);
        ConnectorContainerSpecBuilder.ConnectorIdentity identity = ConnectorContainerSpecBuilder.ResolveIdentity(
            connector,
            identityContext ?? workspaceContext,
            isProbe ? (saveDiscovery ? "save-discovery" : "probe") : null);
        ImageCompatibilityResolution imageResolution = await ResolveImageCompatibilityAsync(connector.Image, cancellationToken).ConfigureAwait(false);
        connector = connector with { Image = imageResolution.Image };
        NetworkAccess.ImageCompatProfile? compatProfile = imageResolution.Profile;
        connector = connector with
        {
            SpecFingerprint = ConnectorContainerSpecBuilder.ComputeWorkloadFingerprint(
                connector,
                identity,
                executionContext,
                compatProfile),
        };
        ConnectorContainerInspection inspection = await InspectContainerStateAsync(containerName, connector, workspaceContext, cancellationToken).ConfigureAwait(false);
        await CancelConnectorCooldownAsync(runtimeKey).ConfigureAwait(false);
        if (inspection.IsRunning && inspection.Matches(connector))
        {
            if (workloadRegistry is not null && !isProbe)
            {
                string? reuseLaunchUserId = string.IsNullOrWhiteSpace(identity.UserId) ? null : identity.UserId;
                NetworkAccess.IWorkloadLaunchLease reuseLease = workloadLaunchGate is null
                    ? NoopLaunchLease.Instance
                    : await workloadLaunchGate.AcquireLaunchLeaseAsync(reuseLaunchUserId, cancellationToken).ConfigureAwait(false);
                await using var reuseLeaseScope = reuseLease.ConfigureAwait(false);
                using CancellationTokenSource reuseCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, reuseLease.CancellationToken);
                cancellationToken = reuseCancellation.Token;
                await EnsureExistingConnectorVerifiedAsync(
                    connector,
                    executionContext,
                    inspection,
                    identity,
                    compatProfile,
                    identityContext ?? workspaceContext,
                    authoritativeSnapshot ?? desiredStateStore.Snapshot,
                    cancellationToken).ConfigureAwait(false);
            }

            trackedConnectorContainers[runtimeKey] = containerName;
            return inspection.CanonicalContainerId;
        }

        string? launchUserId = string.IsNullOrWhiteSpace(identity.UserId) ? null : identity.UserId;
        NetworkAccess.IWorkloadLaunchLease launchLease = workloadLaunchGate is null
            ? NoopLaunchLease.Instance
            : await workloadLaunchGate.AcquireLaunchLeaseAsync(launchUserId, cancellationToken).ConfigureAwait(false);
        await using var launchLeaseScope = launchLease.ConfigureAwait(false);
        using CancellationTokenSource launchCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, launchLease.CancellationToken);
        cancellationToken = launchCancellation.Token;
        bool isBraked = workloadLaunchGate?.IsBraked(launchUserId) ?? true;

        if (workloadRegistry is not null && postStartVerifier is null)
        {
            throw new InvalidOperationException($"Connector '{connector.ConnectorId}' cannot be authorized because post-start verification is unavailable.");
        }

        if (inspection.Exists)
        {
            if (!await RemoveConnectorContainerAsync(containerName, inspection, cancellationToken).ConfigureAwait(false))
            {
                throw new ContainerInspectionUnavailableException($"Connector container '{containerName}' removal did not complete. Recreation is deferred.");
            }
        }

        string allocatedIp = (addressAllocator ?? throw new InvalidOperationException("The Manager workload address allocator is unavailable."))
            .AllocateNextIp(workloadRegistry);
        string attribution = identity.Egress == "deny-all"
            ? "none"
            : (isProbe || string.IsNullOrWhiteSpace(identity.AgentId) ? "user" : "user-agent");

        var desiredIdentity = new Kautuka.NetworkAccessController.RegistryEntry(
                allocatedIp,
                string.IsNullOrWhiteSpace(identity.UserId) ? null : identity.UserId,
                string.IsNullOrWhiteSpace(identity.AgentId) ? null : identity.AgentId,
                containerName,
                identity.Kind,
                connector.ConnectorId,
                ExecutionContextLabel(executionContext),
                false,
                identity.Egress,
                attribution,
                compatProfile is not null ? string.Join(',', compatProfile.Relaxations) : null,
                DateTimeOffset.UtcNow,
                isProbe ? (saveDiscovery ? "save-discovery" : "probe") : null);

        if (workloadRegistry is not null
            && !workloadRegistry.TryRegisterQuarantined(desiredIdentity with { Egress = "deny-all", Attribution = "none" }))
        {
            throw new InvalidOperationException($"Connector '{connector.ConnectorId}' workload address is already registered.");
        }

        var runArgs = BuildConnectorWorkloadRunArguments(
            connector,
            containerName,
            executionContext,
            WithConfiguredWorkspaceRoot(identityContext ?? workspaceContext),
            persistent: true,
            allocatedIp: allocatedIp,
            isBraked: isBraked,
            purpose: isProbe ? (saveDiscovery ? "save-discovery" : "probe") : null,
            compatProfile: compatProfile);

        PodmanCommandResult result = await podmanCommandRunner
            .RunAsync(
                runArgs,
                Math.Max(10, connector.ReadinessTimeoutSeconds),
                cancellationToken,
                BuildPodmanEnvironment(connector))
            .ConfigureAwait(false);

        if (result.ExitCode != 0)
        {
            if (workloadRegistry is not null)
            {
                try
                {
                    if (await PodmanContainerInspector.ReadAsync(podmanCommandRunner, containerName, cancellationToken).ConfigureAwait(false) is null)
                        workloadRegistry.Deregister(allocatedIp, containerName);
                }
                catch (ContainerInspectionUnavailableException ex)
                {
                    logger.LogWarning(ex, "Could not confirm whether failed connector start '{ContainerName}' created a container; retaining deny-all.", containerName);
                }
            }
            throw new InvalidOperationException(PodmanFailureFormatter.FormatFailure(
                $"Start container connector '{connector.ConnectorId}'",
                result));
        }

        bool hasCreatedId = PodmanContainerIdentity.TryReadCanonicalLaunchOutput(result.StdOut, out string createdId);
        if (workloadRegistry is not null
            && (!hasCreatedId || !workloadRegistry.UpdateContainerId(allocatedIp, containerName, createdId)))
        {
            throw new InvalidOperationException($"Connector '{connector.ConnectorId}' started without a confirmed container identity; it remains quarantined.");
        }

        if (postStartVerifier is not null)
        {
            // The verifier owns failure cleanup and retains a deny-all identity when force-removal
            // is unconfirmed. A second unchecked rm/deregister here could erase that quarantine.
            NetworkAccess.WorkloadVerificationResult verified = await postStartVerifier.VerifyWorkloadAsync(
                hasCreatedId ? createdId : containerName,
                allocatedIp,
                compatProfile,
                workloadRegistry,
                cancellationToken: cancellationToken).ConfigureAwait(false);
            if (workloadRegistry is not null)
            {
                Kautuka.NetworkAccessController.RegistryEntry? quarantined = workloadRegistry.FindByIp(allocatedIp);
                cancellationToken.ThrowIfCancellationRequested();
                workloadLaunchGate?.EnsureLaunchAllowed(identity.UserId);
                if (quarantined is null
                    || verified.CanonicalContainerId is not { Length: > 0 } verifiedId
                    || !string.Equals(quarantined.ContainerId, verifiedId, StringComparison.Ordinal)
                    || !workloadRegistry.TryPromoteVerified(quarantined, desiredIdentity with { ContainerId = verifiedId }))
                {
                    throw new InvalidOperationException($"Connector '{connector.ConnectorId}' registry identity changed before verification could be promoted.");
                }
            }
        }

        trackedConnectorContainers[runtimeKey] = containerName;
        return hasCreatedId ? createdId : null;
    }

    private async Task EnsureExistingConnectorVerifiedAsync(
        WorkspaceMcpContainerConnectorDefinition connector,
        WorkspaceMcpExecutionContext executionContext,
        ConnectorContainerInspection inspection,
        ConnectorContainerSpecBuilder.ConnectorIdentity identity,
        NetworkAccess.ImageCompatProfile? compatProfile,
        ContainerContext? identityContext,
        WorkspaceDesiredStateSnapshot authoritativeSnapshot,
        CancellationToken cancellationToken)
    {
        if (!PodmanContainerIdentity.IsCanonicalContainerId(inspection.CanonicalContainerId)
            || string.IsNullOrWhiteSpace(inspection.WorkloadIp))
        {
            throw new ContainerInspectionUnavailableException($"Connector '{connector.ConnectorId}' did not expose a canonical container ID and workload IP; it remains quarantined.");
        }

        string containerId = inspection.CanonicalContainerId!;
        string ip = inspection.WorkloadIp;
        string attribution = identity.Egress == "deny-all"
            ? "none"
            : (string.IsNullOrWhiteSpace(identity.AgentId) ? (string.IsNullOrWhiteSpace(identity.UserId) ? "none" : "user") : "user-agent");
        string? compat = compatProfile is not null ? string.Join(',', compatProfile.Relaxations) : null;
        var desired = new Kautuka.NetworkAccessController.RegistryEntry(
            ip,
            string.IsNullOrWhiteSpace(identity.UserId) ? null : identity.UserId,
            string.IsNullOrWhiteSpace(identity.AgentId) ? null : identity.AgentId,
            containerId,
            identity.Kind,
            connector.ConnectorId,
            ExecutionContextLabel(executionContext),
            false,
            identity.Egress,
            attribution,
            compat,
            DateTimeOffset.UtcNow);

        Kautuka.NetworkAccessController.RegistryEntry? current = workloadRegistry?.FindByIp(ip);
        if (current is null)
        {
            var candidate = desired with { Egress = "deny-all", Attribution = "none" };
            if (workloadRegistry?.TryRegisterQuarantined(candidate) != true)
            {
                throw new ContainerInspectionUnavailableException($"Connector '{connector.ConnectorId}' workload address is already owned by another registry identity.");
            }

            current = candidate;
        }

        if (current != (desired with
        {
            Egress = current.Egress,
            Attribution = current.Attribution,
            RegisteredUtc = current.RegisteredUtc,
        }))
        {
            throw new ContainerInspectionUnavailableException($"Connector '{connector.ConnectorId}' runtime identity does not match current desired state and trusted compatibility profile.");
        }

        if (workloadRegistry!.IsVerified(ip, containerId)
            && current.Egress == desired.Egress
            && current.Attribution == desired.Attribution)
        {
            return;
        }

        if (current.Egress != "deny-all" || current.Attribution != "none")
        {
            if (!workloadRegistry.TryQuarantine(ip, containerId))
            {
                throw new ContainerInspectionUnavailableException($"Connector '{connector.ConnectorId}' identity changed before quarantine.");
            }

            current = workloadRegistry.FindByIp(ip);
        }

        if (postStartVerifier is null)
        {
            throw new InvalidOperationException($"Connector '{connector.ConnectorId}' cannot be authorized because post-start verification is unavailable.");
        }

        await postStartVerifier.VerifyWorkloadAsync(containerId, ip, compatProfile, workloadRegistry, cancellationToken: cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        workloadLaunchGate?.EnsureLaunchAllowed(identity.UserId);

        WorkspaceDesiredStateSnapshot latestSnapshot = desiredStateStore.Snapshot;
        if (!SameDesiredStateIdentity(authoritativeSnapshot, latestSnapshot))
        {
            throw new InvalidOperationException($"Connector '{connector.ConnectorId}' desired state authority changed during re-verification; it remains quarantined.");
        }

        WorkspaceMcpContainerConnectorDefinition? latest = latestSnapshot.DesiredContainerConnectors
            .FirstOrDefault(candidate => string.Equals(candidate.ConnectorId, connector.ConnectorId, StringComparison.Ordinal));
        if (latest is null
            || latest.DesiredStateVersion != connector.DesiredStateVersion)
        {
            throw new InvalidOperationException($"Connector '{connector.ConnectorId}' desired state changed during re-verification; it remains quarantined.");
        }

        if (identityContext is not null)
        {
            ContainerContext? latestWorkspace = desiredStateStore.Snapshot.DesiredWorkspaces.FirstOrDefault(candidate =>
                string.Equals(candidate.ScopeType, identityContext.ScopeType, StringComparison.OrdinalIgnoreCase)
                && string.Equals(candidate.ScopeId, identityContext.ScopeId, StringComparison.Ordinal)
                && string.Equals(candidate.UserId, identityContext.UserId, StringComparison.Ordinal));
            if (latestWorkspace is null
                || latestWorkspace.DesiredStateVersion != identityContext.DesiredStateVersion
                || !string.Equals(latestWorkspace.SpecFingerprint, identityContext.SpecFingerprint, StringComparison.Ordinal))
            {
                throw new InvalidOperationException($"Connector '{connector.ConnectorId}' owner workspace changed during re-verification; it remains quarantined.");
            }
        }

        NetworkAccess.ImageCompatProfile? latestCompatProfile = imageCompatibilityStore?.GetProfileByDigest(connector.Image);
        if (!SameCompatRelaxations(latestCompatProfile, compatProfile))
        {
            throw new InvalidOperationException($"Connector '{connector.ConnectorId}' trusted compatibility profile changed during re-verification; it remains quarantined.");
        }

        current = workloadRegistry.FindByIp(ip);
        if (current is null || !string.Equals(current.ContainerId, containerId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Connector '{connector.ConnectorId}' registry identity changed before re-verification could be promoted.");
        }

        desired = desired with { RegisteredUtc = current.RegisteredUtc };
        cancellationToken.ThrowIfCancellationRequested();
        workloadLaunchGate?.EnsureLaunchAllowed(identity.UserId);
        if (!workloadRegistry.TryPromoteVerified(current, desired))
        {
            throw new InvalidOperationException($"Connector '{connector.ConnectorId}' registry identity or policy changed before re-verification could be promoted.");
        }
    }

    private static bool SameDesiredStateIdentity(
        WorkspaceDesiredStateSnapshot expected,
        WorkspaceDesiredStateSnapshot current)
        => expected.AcceptedVersion == current.AcceptedVersion
            && string.Equals(expected.PublisherEpoch, current.PublisherEpoch, StringComparison.Ordinal)
            && string.Equals(expected.ContentHash, current.ContentHash, StringComparison.Ordinal);

    private static bool SameCompatRelaxations(
        NetworkAccess.ImageCompatProfile? left,
        NetworkAccess.ImageCompatProfile? right)
        => left is null
            ? right is null
            : right is not null
                && string.Equals(left.ImageDigest, right.ImageDigest, StringComparison.Ordinal)
                && left.Relaxations.SequenceEqual(right.Relaxations, StringComparer.Ordinal);

    private async Task ResetConnectorCooldownAsync(
        WorkspaceMcpContainerConnectorDefinition connector,
        string runtimeKey,
        string containerName)
    {
        int cooldownSeconds = connector.JitCooldownSeconds <= 0 ? 600 : connector.JitCooldownSeconds;
        await connectorCooldowns.ScheduleAsync(
            runtimeKey,
            TimeSpan.FromSeconds(cooldownSeconds),
            cancellationToken => StopConnectorContainerAsync(runtimeKey, connector.ConnectorId, containerName, "connector-cooldown-expired", cancellationToken))
            .ConfigureAwait(false);
    }

    private Task CancelConnectorCooldownAsync(string runtimeKey)
        => connectorCooldowns.CancelAsync(runtimeKey);

    private async Task StopTrackedConnectorInstancesAsync(string connectorId, string eventName)
    {
        List<(string RuntimeKey, string ContainerName)> trackedInstances = [.. trackedConnectorContainers
            .Where(pair => IsRuntimeKeyForConnector(pair.Key, connectorId))
            .Select(static pair => (pair.Key, pair.Value))];

        foreach ((string runtimeKey, string containerName) in trackedInstances)
        {
            await CancelConnectorCooldownAsync(runtimeKey).ConfigureAwait(false);
            await StopConnectorContainerAsync(runtimeKey, connectorId, containerName, eventName).ConfigureAwait(false);
        }
    }

    private async Task StopConnectorContainerAsync(string runtimeKey, string connectorId, string containerName, string eventName, CancellationToken cancellationToken = default)
    {
        await using var connectorLease = (await connectorLocks
            .AcquireAsync(runtimeKey, cancellationToken)
            .ConfigureAwait(false)).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        ConnectorContainerInspection inspection = await InspectContainerStateAsync(containerName, null, null, cancellationToken).ConfigureAwait(false);
        if (!inspection.Exists)
        {
            trackedConnectorContainers.TryRemove(runtimeKey, out _);
            return;
        }

        if (!await RemoveConnectorContainerAsync(containerName, inspection, cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        trackedConnectorContainers.TryRemove(runtimeKey, out _);
        _ = desiredStateStore.TryTransitionConnectorState(
            connectorId,
            WorkspaceMcpContainerLifecycleState.IdleStopped,
            eventName);
    }

    private async Task<ConnectorContainerInspection> InspectContainerStateAsync(
        string containerName,
        WorkspaceMcpContainerConnectorDefinition? connector,
        ContainerContext? workspaceContext,
        CancellationToken cancellationToken)
    {
        string? inspectJson = await PodmanContainerInspector.ReadAsync(podmanCommandRunner, containerName, cancellationToken).ConfigureAwait(false);
        if (inspectJson is null)
        {
            return new ConnectorContainerInspection(false, null, 0, string.Empty, null);
        }

        string? canonicalContainerId = null;
        string? workloadIp = null;
        if (PodmanContainerIdentity.TryRead(inspectJson, out string inspectedId, out string inspectedIp))
        {
            canonicalContainerId = inspectedId;
            workloadIp = inspectedIp;
        }

        string? imageId = await TryReadImageIdAsync(connector?.Image, cancellationToken).ConfigureAwait(false);
        try
        {
            if (!LiveContainerSpec.TryReadLabels(inspectJson, out string? state, out long desiredVersion, out string fingerprint))
            {
                return new ConnectorContainerInspection(true, state, 0, string.Empty, "the container inspect could not be read", canonicalContainerId, workloadIp);
            }

            string? drift = LiveContainerSpec.DescribeDrift(
                inspectJson,
                LiveContainerSpec.ForConnector(workspaceContext, ConnectorEnvAssignments(connector), imageId));
            return new ConnectorContainerInspection(true, state, desiredVersion, fingerprint, drift, canonicalContainerId, workloadIp);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or KeyNotFoundException)
        {
            throw new ContainerInspectionUnavailableException($"Connector container '{containerName}' returned an invalid inspection. Container changes are deferred.", ex);
        }
    }

    private async Task<string?> TryReadImageIdAsync(string? image, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(image))
        {
            return null;
        }

        PodmanCommandResult result = await podmanCommandRunner
            .RunAsync(["image", "inspect", "--format", "{{.Id}}", image], 10, cancellationToken)
            .ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        if (result.ExitCode != 0 || string.IsNullOrWhiteSpace(result.StdOut))
        {
            throw new ContainerInspectionUnavailableException("Connector image could not be inspected. Container changes are deferred.");
        }

        string inspectedImageId = result.StdOut.Trim();
        return NormalizePodmanImageId(inspectedImageId) is { Length: > 0 } normalizedImageId
            ? normalizedImageId
            : inspectedImageId;
    }

    private async Task<ImageCompatibilityResolution> ResolveImageCompatibilityAsync(
        string? image,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(image))
        {
            return new ImageCompatibilityResolution(image ?? string.Empty, null);
        }

        if (imageCompatibilityStore is null)
        {
            return new ImageCompatibilityResolution(image, null);
        }

        // Do not let a successful local inspection turn an externally mutable registry
        // tag into an apparently trusted image ID. Only immutable external references
        // and appliance-local image names may enter this resolution path.
        ContainerImageProvenancePolicy.EnsureTrusted(image, "Connector runtime");

        // MCPB materialization returns a local tag (localhost/kautuka-mcpb-...). Resolve
        // every reference through Podman before consulting the digest-keyed compatibility
        // store. Prefer the immutable repository manifest digest for pinned images, then
        // fall back to Podman's immutable local image ID for locally built images. Never
        // derive compatibility from connector or container labels.
        PodmanCommandResult result = await podmanCommandRunner
            .RunAsync(["image", "inspect", image], 10, cancellationToken)
            .ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        if (result.ExitCode != 0 || string.IsNullOrWhiteSpace(result.StdOut))
        {
            // Distinguish an absent local image from Podman/timeout errors. `image exists`
            // returns 1 only when absent; any other failure means resolution is unavailable.
            if (result.TerminationReason == ProcessTerminationReason.NonZeroExit)
            {
                PodmanCommandResult exists = await podmanCommandRunner
                    .RunAsync(["image", "exists", image], 10, cancellationToken)
                    .ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                if (exists.ExitCode == 1)
                {
                    if (ContainerImageProvenancePolicy.IsLocalImage(image))
                    {
                        // Local MCPB images are materialized earlier in PrepareRuntimeConnectorAsync.
                        // Do not try to pull a localhost tag or infer a profile from a missing image.
                        return new ImageCompatibilityResolution(image, null);
                    }

                    // Resolve pinned registry images through the manager-owned acquisition path
                    // before selecting a stored compatibility profile. This guarantees the
                    // later run references the inspected immutable image ID.
                    ContainerImageProvenancePolicy.EnsureTrusted(image, "Connector runtime");
                    PodmanCommandResult pull = await podmanCommandRunner
                        .RunAsync(["pull", image], 300, cancellationToken)
                        .ConfigureAwait(false);
                    cancellationToken.ThrowIfCancellationRequested();
                    if (pull.ExitCode != 0)
                    {
                        throw new InvalidOperationException(PodmanFailureFormatter.FormatFailure($"Acquire connector image '{image}'", pull));
                    }

                    result = await podmanCommandRunner
                        .RunAsync(["image", "inspect", image], 10, cancellationToken)
                        .ConfigureAwait(false);
                    cancellationToken.ThrowIfCancellationRequested();
                    if (result.ExitCode != 0 || string.IsNullOrWhiteSpace(result.StdOut))
                    {
                        throw new ContainerInspectionUnavailableException("Acquired connector image could not be inspected. Container changes are deferred.");
                    }
                }
                else
                {
                    throw new ContainerInspectionUnavailableException("Connector image could not be inspected. Container changes are deferred.");
                }
            }
        }

        JsonElement inspectedImage;
        try
        {
            using JsonDocument document = JsonDocument.Parse(result.StdOut);
            if (document.RootElement.ValueKind == JsonValueKind.Array)
            {
                if (document.RootElement.GetArrayLength() == 0)
                {
                    throw new JsonException("Podman image inspection returned an empty array.");
                }

                inspectedImage = document.RootElement[0].Clone();
            }
            else if (document.RootElement.ValueKind == JsonValueKind.Object)
            {
                inspectedImage = document.RootElement.Clone();
            }
            else
            {
                throw new JsonException("Podman image inspection returned an invalid JSON shape.");
            }
        }
        catch (JsonException ex)
        {
            throw new ContainerInspectionUnavailableException("Connector image inspection returned invalid metadata. Container changes are deferred.", ex);
        }

        if (!inspectedImage.TryGetProperty("Id", out JsonElement immutableIdElement)
            || immutableIdElement.ValueKind != JsonValueKind.String)
        {
            throw new ContainerInspectionUnavailableException("Connector image inspection did not return a canonical immutable SHA-256 image ID. Container changes are deferred.");
        }

        string resolvedImage = NormalizePodmanImageId(immutableIdElement.GetString());
        if (string.IsNullOrWhiteSpace(resolvedImage))
        {
            throw new ContainerInspectionUnavailableException("Connector image inspection did not return a canonical immutable SHA-256 image ID. Container changes are deferred.");
        }
        NetworkAccess.ImageCompatProfile? resolvedProfile = null;
        if (inspectedImage.TryGetProperty("RepoDigests", out JsonElement repoDigests)
            && repoDigests.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement repoDigestElement in repoDigests.EnumerateArray())
            {
                if (repoDigestElement.ValueKind != JsonValueKind.String) continue;
                string repoDigest = repoDigestElement.GetString() ?? string.Empty;
                int separator = repoDigest.LastIndexOf('@');
                if (separator < 0 || separator + 1 >= repoDigest.Length) continue;
                string digest = repoDigest[(separator + 1)..];
                if (IsSha256Digest(digest))
                {
                    resolvedProfile = imageCompatibilityStore.GetProfileByDigest(digest);
                    if (resolvedProfile is not null) return new ImageCompatibilityResolution(resolvedImage, resolvedProfile);
                }
            }
        }

        if (inspectedImage.TryGetProperty("Digest", out JsonElement digestElement)
            && digestElement.ValueKind == JsonValueKind.String)
        {
            string digest = digestElement.GetString() ?? string.Empty;
            if (IsSha256Digest(digest))
            {
                resolvedProfile = imageCompatibilityStore.GetProfileByDigest(digest);
                if (resolvedProfile is not null) return new ImageCompatibilityResolution(resolvedImage, resolvedProfile);
            }
        }

        // Locally built images may have neither a repository manifest digest nor a usable
        // distribution digest. Their canonical Podman image ID remains a trusted fallback.
        resolvedProfile = imageCompatibilityStore.GetProfileByDigest(resolvedImage);

        return new ImageCompatibilityResolution(resolvedImage, resolvedProfile);
    }

    private static bool IsSha256Digest(string? value)
    {
        if (value is not { Length: 71 } || !value.StartsWith("sha256:", StringComparison.Ordinal))
        {
            return false;
        }

        for (int index = 7; index < value.Length; index++)
        {
            char digit = value[index];
            if ((digit < '0' || digit > '9')
                && (digit < 'a' || digit > 'f')
                && (digit < 'A' || digit > 'F'))
            {
                return false;
            }
        }

        return true;
    }

    private static string NormalizePodmanImageId(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        string trimmed = value.Trim();
        string hex = trimmed.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase)
            ? trimmed["sha256:".Length..]
            : trimmed;
        if (hex.Length != 64 || !hex.All(Uri.IsHexDigit))
        {
            return string.Empty;
        }

        return $"sha256:{hex.ToLowerInvariant()}";
    }

    private sealed record ImageCompatibilityResolution(string Image, NetworkAccess.ImageCompatProfile? Profile);

    private static List<string> ConnectorEnvAssignments(WorkspaceMcpContainerConnectorDefinition? connector)
    {
        var assignments = new List<string>
        {
            "HOME=/tmp",
            "NPM_CONFIG_CACHE=/tmp/.npm",
            "PIP_CACHE_DIR=/tmp/.pip",
            "UV_CACHE_DIR=/tmp/.uv",
        };
        if (connector is null)
        {
            return assignments;
        }

        foreach (KeyValuePair<string, string> pair in connector.Environment)
        {
            if (ConnectorContainerSpecBuilder.ForwardsEnvironmentKey(pair.Key) && !string.IsNullOrWhiteSpace(pair.Value))
            {
                assignments.Add(pair.Key + "=" + pair.Value);
            }
        }

        return assignments;
    }

    private async Task<bool> RemoveConnectorContainerAsync(
        string containerName,
        ConnectorContainerInspection? observedInspection,
        CancellationToken cancellationToken)
    {
        ConnectorContainerInspection inspection = observedInspection
            ?? await InspectContainerStateAsync(containerName, null, null, cancellationToken).ConfigureAwait(false);
        if (!inspection.Exists)
        {
            logger.LogWarning("Connector container '{ContainerName}' was not present in the supplied inspection; destructive cleanup is deferred.", containerName);
            return false;
        }

        string? canonicalContainerId = inspection.CanonicalContainerId;
        if (!PodmanContainerIdentity.IsCanonicalContainerId(canonicalContainerId))
        {
            logger.LogWarning("Connector container '{ContainerName}' did not expose a canonical ID in its original inspection; destructive cleanup is deferred.", containerName);
            return false;
        }

        PodmanCommandResult result = await podmanCommandRunner
            .RunAsync(["rm", "-f", canonicalContainerId!], 15, cancellationToken)
            .ConfigureAwait(false);

        if (result.ExitCode == 0)
        {
            if (workloadRegistry is not null)
            {
                foreach (Kautuka.NetworkAccessController.RegistryEntry entry in workloadRegistry.GetAll()
                    .Where(entry => string.Equals(entry.ContainerId, canonicalContainerId, StringComparison.Ordinal)))
                {
                    workloadRegistry.Deregister(entry.Ip, canonicalContainerId);
                }
            }
            return true;
        }

        logger.LogWarning(
            "[ContainerConnector] Failed to stop connector container '{ContainerName}': {Reason}",
            containerName,
            PodmanFailureFormatter.FormatDetails(result));
        return false;
    }

    private WorkspaceMcpContainerConnectorDefinition ResolveConnectorDefinition(string connectorId)
    {
        WorkspaceMcpContainerConnectorDefinition? connector = desiredStateStore.Snapshot.DesiredContainerConnectors
            .FirstOrDefault(c => string.Equals(c.ConnectorId, connectorId, StringComparison.OrdinalIgnoreCase));

        if (connector is null)
        {
            throw new InvalidOperationException($"Container connector '{connectorId}' not found in desired state.");
        }

        return connector;
    }

    private WorkspaceMcpContainerConnectorDefinition ResolveEffectiveConnectorDefinition(
        string connectorId,
        IReadOnlyDictionary<string, string>? environmentOverrides,
        int timeoutSeconds,
        bool allowInternalSystemConnector)
    {
        WorkspaceMcpContainerConnectorDefinition connector = ResolveConnectorDefinition(connectorId);
        return BuildEffectiveConnectorDefinition(
            connector,
            environmentOverrides,
            timeoutSeconds,
            enabledOverride: allowInternalSystemConnector && connector.IsInternalSystemConnector
                ? true
                : null);
    }

    private static WorkspaceMcpContainerConnectorDefinition BuildEffectiveConnectorDefinition(
        WorkspaceMcpContainerConnectorDefinition connector,
        IReadOnlyDictionary<string, string>? environmentOverrides,
        int timeoutSeconds,
        bool? enabledOverride)
    {
        ArgumentNullException.ThrowIfNull(connector);

        IReadOnlyList<KeyValuePair<string, string>> environment = MergeEnvironment(
            connector.Environment,
            environmentOverrides);

        int effectiveTimeoutSeconds = timeoutSeconds > 0
            ? timeoutSeconds
            : connector.ReadinessTimeoutSeconds;

        return connector with
        {
            Environment = environment,
            ReadinessTimeoutSeconds = effectiveTimeoutSeconds,
            Enabled = enabledOverride ?? connector.Enabled,
        };
    }

    private static IReadOnlyList<KeyValuePair<string, string>> MergeEnvironment(
        IReadOnlyList<KeyValuePair<string, string>> baseline,
        IReadOnlyDictionary<string, string>? overrides)
    {
        var merged = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach ((string key, string value) in baseline)
        {
            if (string.IsNullOrWhiteSpace(key))
            {
                continue;
            }

            merged[key] = value;
        }

        if (overrides is not null)
        {
            foreach ((string key, string value) in overrides)
            {
                if (!ConnectorContainerSpecBuilder.AllowsRuntimeEnvironmentOverride(key))
                {
                    continue;
                }

                merged[key] = value;
            }
        }

        return [.. merged];
    }

    private static string? ResolveExecWorkingDirectory(WorkspaceMcpContainerConnectorDefinition connector)
    {
        if ((!string.IsNullOrWhiteSpace(connector.Command)
                && connector.Command.Contains(McpbImageMaterializer.BundleWorkdir, StringComparison.Ordinal))
            || connector.Args.Any(static arg => arg.Contains(McpbImageMaterializer.BundleWorkdir, StringComparison.Ordinal)))
        {
            return McpbImageMaterializer.BundleWorkdir;
        }

        return null;
    }

    private static Dictionary<string, string> BuildPodmanEnvironment(WorkspaceMcpContainerConnectorDefinition connector)
        => connector.Environment
            .Where(static pair => ConnectorContainerSpecBuilder.ForwardsEnvironmentKey(pair.Key))
            .ToDictionary(static pair => pair.Key, static pair => pair.Value, StringComparer.Ordinal);

    private static bool UsesImageDefaultCommand(WorkspaceMcpContainerConnectorDefinition connector)
    {
        return string.IsNullOrWhiteSpace(connector.Command);
    }

    private static WorkspaceMcpExecutionContext NormalizeExecutionContext(WorkspaceMcpExecutionContext executionContext)
        => executionContext == WorkspaceMcpExecutionContext.Unspecified
            ? WorkspaceMcpExecutionContext.Isolated
            : executionContext;

    private static string BuildSandboxProvisionKey(ContainerContext workspaceContext)
        => string.Concat(workspaceContext.ScopeType, ":", workspaceContext.ScopeId);

    private static string SanitizeSegment(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (char ch in value)
        {
            builder.Append(char.IsLetterOrDigit(ch) ? char.ToLowerInvariant(ch) : '-');
        }

        return builder.ToString().Trim('-');
    }

    private static string ExecutionContextLabel(WorkspaceMcpExecutionContext executionContext)
        => NormalizeExecutionContext(executionContext) switch
        {
            WorkspaceMcpExecutionContext.Isolated => "isolated",
            WorkspaceMcpExecutionContext.IsolatedWithAgentFiles => "isolated-with-agent-files",
            WorkspaceMcpExecutionContext.AgentSandbox => "agent-sandbox",
            _ => "isolated",
        };

    private static Action<string>? CreateProbeClearAction(Kautuka.NetworkAccessController.NetworkAccessPolicyStore? store)
        => store is null ? null : store.ClearProbeContainer;

    private static bool RequiresContainerRecycle(
        WorkspaceMcpContainerConnectorDefinition previousConnector,
        WorkspaceMcpContainerConnectorDefinition nextConnector)
    {
        ArgumentNullException.ThrowIfNull(previousConnector);
        ArgumentNullException.ThrowIfNull(nextConnector);

        return !string.Equals(previousConnector.Image, nextConnector.Image, StringComparison.Ordinal)
            || !string.Equals(previousConnector.Command, nextConnector.Command, StringComparison.Ordinal)
            || !string.Equals(previousConnector.BundlePath, nextConnector.BundlePath, StringComparison.Ordinal)
            || !string.Equals(previousConnector.ServerType, nextConnector.ServerType, StringComparison.OrdinalIgnoreCase)
            || previousConnector.Enabled != nextConnector.Enabled
            || previousConnector.IsInternalSystemConnector != nextConnector.IsInternalSystemConnector
            || !string.Equals(previousConnector.CatalogScope, nextConnector.CatalogScope, StringComparison.OrdinalIgnoreCase)
            || previousConnector.ReadinessTimeoutSeconds != nextConnector.ReadinessTimeoutSeconds
            || previousConnector.JitCooldownSeconds != nextConnector.JitCooldownSeconds
            || !previousConnector.Args.SequenceEqual(nextConnector.Args, StringComparer.Ordinal)
            || !EnvironmentEquals(previousConnector.Environment, nextConnector.Environment);
    }

    private static bool EnvironmentEquals(
        IReadOnlyList<KeyValuePair<string, string>> left,
        IReadOnlyList<KeyValuePair<string, string>> right)
    {
        if (left.Count != right.Count)
        {
            return false;
        }

        Dictionary<string, string> leftMap = left.ToDictionary(static pair => pair.Key, static pair => pair.Value, StringComparer.Ordinal);
        foreach ((string key, string value) in right)
        {
            if (!leftMap.TryGetValue(key, out string? leftValue))
            {
                return false;
            }

            if (!string.Equals(leftValue, value, StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    private static ContainerContext? ResolveMountedWorkspace(
        WorkspaceMcpExecutionContext executionContext,
        ContainerContext? workspaceContext)
    {
        if (workspaceContext is null)
        {
            return null;
        }

        if (executionContext == WorkspaceMcpExecutionContext.IsolatedWithAgentFiles)
        {
            return workspaceContext;
        }

        return null;
    }

    internal static List<string> BuildConnectorWorkloadRunArguments(
        WorkspaceMcpContainerConnectorDefinition connector,
        string containerName,
        WorkspaceMcpExecutionContext executionContext,
        ContainerContext? identityContext,
        bool persistent,
        string allocatedIp,
        bool isBraked,
        string? purpose = null,
        NetworkAccess.ImageCompatProfile? compatProfile = null)
        => ConnectorContainerSpecBuilder.BuildRun(
            connector,
            containerName,
            executionContext,
            ResolveMountedWorkspace(executionContext, identityContext),
            persistent,
            allocatedIp,
            isBraked,
            purpose,
            compatProfile,
            identityContext);

    private ContainerContext? WithConfiguredWorkspaceRoot(ContainerContext? context)
    {
        if (context is null)
        {
            return null;
        }

        string? configuredRoot = desiredStateStore.Snapshot.RuntimeSettings?.Workspace.BaseRoot;
        return context with { WorkspaceBaseRoot = configuredRoot };
    }

    private static string BuildContainerName(
        string connectorId,
        WorkspaceMcpExecutionContext executionContext,
        ContainerContext? workspaceContext)
    {
        const string separator = "::";
        const int segmentLimit = 18;

        string userSegment = "system";
        string connectorSegment = connectorId;

        int separatorIndex = connectorId.IndexOf(separator, StringComparison.Ordinal);
        if (!string.IsNullOrWhiteSpace(workspaceContext?.UserId))
        {
            userSegment = workspaceContext.UserId;
            if (separatorIndex > 0 && separatorIndex + separator.Length < connectorId.Length)
            {
                connectorSegment = connectorId[(separatorIndex + separator.Length)..];
            }
        }
        else if (separatorIndex > 0 && separatorIndex + separator.Length < connectorId.Length)
        {
            userSegment = connectorId[..separatorIndex];
            connectorSegment = connectorId[(separatorIndex + separator.Length)..];
        }
        else if (!string.IsNullOrWhiteSpace(workspaceContext?.UserId))
        {
            userSegment = workspaceContext.UserId;
        }

        string normalizedUser = NormalizeSegment(userSegment, segmentLimit, "user");
        string normalizedConnector = NormalizeSegment(connectorSegment, segmentLimit, "connector");
        WorkspaceMcpExecutionContext effectiveExecutionContext = NormalizeExecutionContext(executionContext);
        string hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(BuildRuntimeKey(connectorId, executionContext, workspaceContext))))[..8];

        string? agentId = string.Equals(workspaceContext?.ScopeType, "agent", StringComparison.OrdinalIgnoreCase)
            ? workspaceContext?.ScopeId
            : null;
        string normalizedAgent = !string.IsNullOrWhiteSpace(agentId)
            ? NormalizeSegment(agentId, segmentLimit, "agent")
            : string.Empty;

        return effectiveExecutionContext switch
        {
            WorkspaceMcpExecutionContext.Isolated when !string.IsNullOrEmpty(normalizedAgent) =>
                $"kautuka-mcp-{normalizedUser}-{normalizedAgent}-{normalizedConnector}-isolated-{hash}",
            WorkspaceMcpExecutionContext.Isolated =>
                $"kautuka-mcp-{normalizedUser}-{normalizedConnector}-isolated-{hash}",
            WorkspaceMcpExecutionContext.IsolatedWithAgentFiles =>
                $"kautuka-mcp-{normalizedUser}-{NormalizeSegment(workspaceContext?.ScopeId ?? "agent", segmentLimit, "agent")}-{normalizedConnector}-files-{hash}",
            _ =>
                $"kautuka-mcp-{normalizedUser}-{normalizedConnector}-{NormalizeSegment(effectiveExecutionContext.ToString(), 10, "mode")}-{hash}"
        };
    }

    private static string BuildRuntimeKey(
        string connectorId,
        WorkspaceMcpExecutionContext executionContext,
        ContainerContext? workspaceContext)
    {
        string separator = "::";
        string? userId = workspaceContext?.UserId;
        if (string.IsNullOrWhiteSpace(userId))
        {
            int separatorIndex = connectorId.IndexOf(separator, StringComparison.Ordinal);
            userId = separatorIndex > 0 ? connectorId[..separatorIndex] : "system";
        }

        return string.Join(
            '|',
            connectorId,
            NormalizeExecutionContext(executionContext),
            workspaceContext?.ScopeType ?? "shared",
            workspaceContext?.ScopeId ?? "shared",
            userId);
    }

    private static bool IsRuntimeKeyForConnector(string runtimeKey, string connectorId)
        => runtimeKey.StartsWith(connectorId + "|", StringComparison.OrdinalIgnoreCase);

    private static string NormalizeSegment(string value, int maxLength, string fallback)
    {
        var builder = new StringBuilder(value.Length);
        bool previousDash = false;

        foreach (char ch in value)
        {
            char normalized = char.IsLetterOrDigit(ch)
                ? char.ToLowerInvariant(ch)
                : '-';

            if (normalized == '-')
            {
                if (previousDash)
                {
                    continue;
                }

                previousDash = true;
            }
            else
            {
                previousDash = false;
            }

            builder.Append(normalized);
        }

        string trimmed = builder.ToString().Trim('-');
        if (string.IsNullOrWhiteSpace(trimmed))
        {
            trimmed = fallback;
        }

        return trimmed.Length <= maxLength
            ? trimmed
            : trimmed[..maxLength].Trim('-');
    }

    internal sealed record AgentSandboxInstalledManifest(
        int NormalizationVersion,
        IReadOnlyList<AgentSandboxInstalledContract> Contracts);

    internal sealed record AgentSandboxInstalledContract(
        string ConnectorId,
        string ArtifactDigest,
        string RuntimeImage,
        string ServerType,
        string InstallRoot,
        string ExecutablePath,
        IReadOnlyList<string> ArgsTemplate,
        IReadOnlyList<KeyValuePair<string, string>> EnvTemplate,
        string? WorkingDirectory,
        int NormalizationVersion);

    public async ValueTask DisposeAsync()
    {
        await connectorCooldowns.DisposeAsync().ConfigureAwait(false);

        foreach (string containerName in trackedConnectorContainers.Values.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                await RemoveConnectorContainerAsync(containerName, null, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "[ContainerConnector] Failed to remove tracked connector container '{ContainerName}' during disposal", containerName);
            }
        }

        connectorLocks.Dispose();
        sandboxProvisionLocks.Dispose();

    }

    private void MarkConnectorFailed(string connectorId, string eventName)
    {
        _ = desiredStateStore.TryTransitionConnectorState(
            connectorId,
            WorkspaceMcpContainerLifecycleState.Failed,
            eventName);
    }

    private sealed record ConnectorContainerInspection(
        bool Exists,
        string? State,
        long DesiredStateVersion,
        string SpecFingerprint,
        string? Drift,
        string? CanonicalContainerId = null,
        string? WorkloadIp = null)
    {
        public bool IsRunning => string.Equals(State, "running", StringComparison.OrdinalIgnoreCase);

        public bool Matches(WorkspaceMcpContainerConnectorDefinition connector)
            => string.IsNullOrEmpty(Drift)
                && DesiredStateVersion == connector.DesiredStateVersion
                && !string.IsNullOrWhiteSpace(connector.SpecFingerprint)
                && string.Equals(SpecFingerprint, connector.SpecFingerprint, StringComparison.Ordinal);
    }

    internal static McpClientOptions? ClientOptionsFor(string? protocolVersion)
        => string.IsNullOrWhiteSpace(protocolVersion)
            ? null
            : new McpClientOptions { ProtocolVersion = protocolVersion.Trim() };

    private async Task<IReadOnlyList<McpClientTool>> ListMcpToolsAsync(
        TransportLaunch launch,
        McpClientOptions? clientOptions,
        CancellationToken cancellationToken)
    {
        using CancellationTokenSource launchCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, launch.CancellationToken);
        McpClient client = await McpClient.CreateAsync(launch.Transport, clientOptions, cancellationToken: launchCancellation.Token).ConfigureAwait(false);
        await using (client.ConfigureAwait(false))
        {
            await ReleaseLaunchLeaseAsync(launch).ConfigureAwait(false);
            IList<McpClientTool> tools = await client.ListToolsAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
            return [.. tools];
        }
    }

    private static async ValueTask ReleaseLaunchLeaseAsync(TransportLaunch? launch)
    {
        if (launch is not null)
        {
            await launch.ReleaseLaunchLeaseAsync().ConfigureAwait(false);
        }
    }

    private async Task VerifyEphemeralWorkloadAsync(EphemeralWorkload? workload, int readinessTimeoutSeconds, CancellationToken cancellationToken)
    {
        if (workload is null)
        {
            return;
        }

        NetworkAccess.WorkloadPostStartVerifier verifier = postStartVerifier
            ?? throw new InvalidOperationException("The workload post-start verifier is unavailable; refusing to use an ephemeral connector.");
        using var readiness = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        readiness.CancelAfter(TimeSpan.FromSeconds(Math.Max(1, readinessTimeoutSeconds)));
        while (true)
        {
            PodmanCommandResult state = await podmanCommandRunner.RunAsync(
                ["inspect", "--format", "{{.State.Running}}", workload.ContainerName], 5, readiness.Token).ConfigureAwait(false);
            if (state.ExitCode == 0 && string.Equals(state.StdOut.Trim(), "true", StringComparison.OrdinalIgnoreCase)) break;
            await Task.Delay(100, readiness.Token).ConfigureAwait(false);
        }
        NetworkAccess.WorkloadVerificationResult result = await verifier.VerifyWorkloadAsync(workload.ContainerName, workload.AllocatedIp, workload.CompatProfile, workloadRegistry, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        workload.VerifiedContainerId = result.CanonicalContainerId;
        if (workloadRegistry is not null)
        {
            cancellationToken.ThrowIfCancellationRequested();
            workloadLaunchGate?.EnsureLaunchAllowed(workload.DesiredIdentity.UserId);
            var current = workloadRegistry.FindByIp(workload.AllocatedIp);
            if (current is null || string.IsNullOrWhiteSpace(result.CanonicalContainerId)
                || !string.Equals(current.ContainerId, result.CanonicalContainerId, StringComparison.Ordinal)
                || !workloadRegistry.TryPromoteVerified(current, workload.DesiredIdentity with { ContainerId = result.CanonicalContainerId }))
                throw new InvalidOperationException("Ephemeral connector identity changed before verification could be promoted.");
        }
    }

    private void RemoveNeverStartedEphemeralIdentity(EphemeralWorkload workload)
    {
        try
        {
            if (workloadRegistry?.Deregister(workload.AllocatedIp, workload.ContainerName) != false)
            {
                return;
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Could not deregister never-started ephemeral connector '{ContainerName}' at {Ip}.", workload.ContainerName, workload.AllocatedIp);
        }

        try
        {
            Kautuka.NetworkAccessController.RegistryEntry? current = workloadRegistry?.FindByIp(workload.AllocatedIp);
            if (current is not null && string.Equals(current.ContainerId, workload.ContainerName, StringComparison.Ordinal))
            {
                _ = workloadRegistry!.TryQuarantine(workload.AllocatedIp, workload.ContainerName, workload.ContainerName);
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Could not quarantine never-started ephemeral connector '{ContainerName}' at {Ip}.", workload.ContainerName, workload.AllocatedIp);
        }
    }

    private async Task<bool> CompleteEphemeralWorkloadAsync(EphemeralWorkload? workload)
    {
        if (workload is null)
        {
            return false;
        }

        string expectedContainerId = workload.VerifiedContainerId ?? workload.ContainerName;
        try
        {
            if (workloadRegistry?.TryQuarantine(workload.AllocatedIp, expectedContainerId, workload.ContainerName) == false)
            {
                Kautuka.NetworkAccessController.RegistryEntry? current = workloadRegistry.FindByIp(workload.AllocatedIp);
                if (current is not null
                    && (!string.Equals(current.Egress, "deny-all", StringComparison.OrdinalIgnoreCase)
                        || !string.Equals(current.Attribution, "none", StringComparison.OrdinalIgnoreCase)))
                {
                    logger.LogError(
                        "Could not quarantine ephemeral workload '{ContainerName}' at {Ip}; registry now identifies '{CurrentContainerId}' with an active egress policy.",
                        workload.ContainerName,
                        workload.AllocatedIp,
                        current.ContainerId);
                }
            }
        }
        catch (Exception ex)
        {
            // This finally path must not replace the original MCP operation/verification error.
            logger.LogError(ex, "Could not quarantine ephemeral workload '{ContainerName}' at {Ip}.", workload.ContainerName, workload.AllocatedIp);
        }

        string? cleanupTarget = IsCanonicalContainerId(workload.VerifiedContainerId)
            ? workload.VerifiedContainerId
            : null;
        bool removed = false;
        if (cleanupTarget is not null)
        {
            try
            {
                PodmanCommandResult removeResult = await podmanCommandRunner
                    .RunAsync(["rm", "-f", cleanupTarget], 15, CancellationToken.None)
                    .ConfigureAwait(false);
                removed = removeResult.ExitCode == 0;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Could not force-remove ephemeral workload '{ContainerId}' at {Ip}.", cleanupTarget, workload.AllocatedIp);
            }
        }

        if (!removed && cleanupTarget is not null)
        {
            try
            {
                // Without an inspected canonical ID, querying/removing by the reusable name could
                // act on a replacement container or mistake a renamed original for absence.
                removed = await PodmanContainerInspector.ReadAsync(podmanCommandRunner, cleanupTarget, CancellationToken.None)
                    .ConfigureAwait(false) is null;
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "Ephemeral workload '{ContainerId}' at {Ip} remains unconfirmed after cleanup.", cleanupTarget, workload.AllocatedIp);
            }
        }

        if (removed)
        {
            try
            {
                Kautuka.NetworkAccessController.RegistryEntry? current = workloadRegistry?.FindByIp(workload.AllocatedIp);
                if (current is not null
                    && (string.Equals(current.ContainerId, expectedContainerId, StringComparison.Ordinal)
                        || string.Equals(current.ContainerId, cleanupTarget, StringComparison.Ordinal)))
                {
                    workloadRegistry!.Deregister(workload.AllocatedIp, current.ContainerId);
                }
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Could not deregister removed ephemeral workload '{ContainerName}' at {Ip}.", workload.ContainerName, workload.AllocatedIp);
            }
        }

        trackedConnectorContainers.TryRemove("ephemeral:" + workload.ContainerName, out _);
        return true;
    }

    private static string BuildEphemeralContainerName(string baseName)
    {
        string suffix = "-tmp-" + Guid.NewGuid().ToString("N")[..10];
        int baseLength = Math.Min(baseName.Length, 63 - suffix.Length);
        return baseName[..baseLength] + suffix;
    }

    private static bool IsCanonicalContainerId(string? value) =>
        value is { Length: 64 } && value.All(static character => char.IsAsciiHexDigit(character));

    private sealed class TransportLaunch(IClientTransport transport, EphemeralWorkload? ephemeralWorkload, NetworkAccess.IWorkloadLaunchLease? launchLease, string? imageDigest = null, string? containerId = null)
    {
        private NetworkAccess.IWorkloadLaunchLease? launchLease = launchLease;

        public IClientTransport Transport { get; } = transport;

        public EphemeralWorkload? EphemeralWorkload { get; } = ephemeralWorkload;

        public string? ImageDigest { get; } = imageDigest;

        public string? ContainerId { get; } = containerId;

        public CancellationToken CancellationToken => launchLease?.CancellationToken ?? System.Threading.CancellationToken.None;

        public async ValueTask ReleaseLaunchLeaseAsync()
        {
            NetworkAccess.IWorkloadLaunchLease? lease = Interlocked.Exchange(ref launchLease, null);
            if (lease is not null)
            {
                await lease.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    private sealed class NoopLaunchLease : NetworkAccess.IWorkloadLaunchLease
    {
        public static NoopLaunchLease Instance { get; } = new();

        public CancellationToken CancellationToken => System.Threading.CancellationToken.None;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class VerifiedConnectorTransport(IClientTransport inner, Func<CancellationToken, Task> verify) : IClientTransport
    {
        public string Name => inner.Name;

        public async Task<ModelContextProtocol.Protocol.ITransport> ConnectAsync(CancellationToken cancellationToken = default)
        {
            ModelContextProtocol.Protocol.ITransport session = await inner.ConnectAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await verify(cancellationToken).ConfigureAwait(false);
                return session;
            }
            catch
            {
                await session.DisposeAsync().ConfigureAwait(false);
                throw;
            }
        }
    }

    private sealed record EphemeralWorkload(string ContainerName, string AllocatedIp, NetworkAccess.ImageCompatProfile? CompatProfile, Kautuka.NetworkAccessController.RegistryEntry DesiredIdentity)
    {
        public string? VerifiedContainerId { get; set; }
    }

}

[JsonSerializable(typeof(WorkspaceContainerConnectorExecutionService.AgentSandboxInstalledManifest))]
internal sealed partial class AgentSandboxContractJsonContext : JsonSerializerContext;
