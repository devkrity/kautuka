using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

using Kautuka.Manager.Protos;
using Kautuka.Manager.RuntimeContainer.Sandbox;
using Kautuka.Manager.RuntimeContainer.Sandbox.Models;

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
    ILogger<WorkspaceContainerConnectorExecutionService> logger) : IAsyncDisposable
{
    private const string agentSandboxInstallRoot = "/home/node/.kautuka/connectors";
    private const string agentSandboxManifestPath = "/home/node/.kautuka/connectors/.installed.json";
    private const int agentSandboxNormalizationVersion = 3;

    private readonly ConcurrentDictionary<string, string> trackedConnectorContainers = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConnectorCooldownScheduler connectorCooldowns = new();
    private readonly AsyncKeyedLockPool connectorLocks = new();
    private readonly AsyncKeyedLockPool sandboxProvisionLocks = new();
    private readonly IContainerTool containerTool = containerTool;

    public IReadOnlyCollection<string> TrackedContainerNames
        => [.. trackedConnectorContainers.Values.Distinct(StringComparer.OrdinalIgnoreCase)];

    public IReadOnlyCollection<string> DesiredIsolatedContainerNames(
        IReadOnlyList<WorkspaceMcpContainerConnectorDefinition> connectors)
    {
        ArgumentNullException.ThrowIfNull(connectors);
        return [.. connectors
            .Where(static connector => connector.Enabled)
            .Select(static connector => BuildContainerName(connector.ConnectorId, WorkspaceMcpExecutionContext.Isolated, null))
            .Distinct(StringComparer.OrdinalIgnoreCase)];
    }

    public async Task<IReadOnlyList<string>> ReconcileDesiredConnectorsAsync(
        IReadOnlyList<WorkspaceMcpContainerConnectorDefinition> connectors,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connectors);
        var notes = new List<string>();
        foreach (WorkspaceMcpContainerConnectorDefinition connector in connectors.Where(static item => item.Enabled))
        {
            string containerName = BuildContainerName(connector.ConnectorId, WorkspaceMcpExecutionContext.Isolated, null);
            string runtimeKey = BuildRuntimeKey(connector.ConnectorId, WorkspaceMcpExecutionContext.Isolated, null);
            ConnectorContainerInspection inspection;
            try
            {
                inspection = await InspectContainerStateAsync(containerName, connector, null, cancellationToken).ConfigureAwait(false);
            }
            catch (ContainerInspectionUnavailableException ex)
            {
                notes.Add("Connector " + connector.ConnectorId + " inspection is unavailable. Reconciliation is deferred.");
                logger.LogWarning(ex, "[ContainerConnector] Reconcile deferred for connector '{ConnectorId}'.", connector.ConnectorId);
                continue;
            }
            if (inspection.Exists && inspection.IsRunning && inspection.Matches(connector))
            {
                trackedConnectorContainers[runtimeKey] = containerName;
                continue;
            }

            if (!inspection.Exists || !inspection.IsRunning)
            {
                notes.Add(connector.ConnectorId + " is not running. It starts on the next tool call.");
                continue;
            }

            string reason = inspection.Exists
                ? inspection.Drift ?? "its spec label changed"
                : "it was not running after Manager started";
            try
            {
                await EnsureConnectorContainerRunningAsync(
                    connector,
                    runtimeKey,
                    containerName,
                    WorkspaceMcpExecutionContext.Isolated,
                    workspaceContext: null,
                    cancellationToken).ConfigureAwait(false);
                await ResetConnectorCooldownAsync(connector, runtimeKey, containerName).ConfigureAwait(false);
                notes.Add("Connector " + connector.ConnectorId + " was replaced because " + reason + ".");
            }
            catch (Exception ex) when (ex is InvalidOperationException or IOException)
            {
                notes.Add("Connector " + connector.ConnectorId + " could not be replaced.");
                logger.LogWarning(ex, "[ContainerConnector] Reconcile did not replace connector '{ConnectorId}'.", connector.ConnectorId);
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

        foreach (WorkspaceMcpContainerConnectorDefinition connector in EnumerateBundleMaterializationCandidates(nextConnectors))
        {
            await EnsureConnectorArtifactsAsync(connector, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task EnsureConnectorArtifactsAsync(
        WorkspaceMcpContainerConnectorDefinition connector,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connector);

        _ = await mcpbImageMaterializer.EnsureImageAsync(connector, cancellationToken).ConfigureAwait(false);

        if (string.IsNullOrWhiteSpace(connector.BundlePath))
        {
            return;
        }

        _ = await mcpbImageMaterializer
            .EnsureSandboxArtifactImageAsync(connector, AgentSandboxInstallContract.BuildInstallRoot(connector), cancellationToken)
            .ConfigureAwait(false);
    }

    private static IReadOnlyList<WorkspaceMcpContainerConnectorDefinition> EnumerateBundleMaterializationCandidates(
        IReadOnlyList<WorkspaceMcpContainerConnectorDefinition> connectors)
        => [.. connectors.Where(static connector =>
            (!string.IsNullOrWhiteSpace(connector.BundlePath)
             || !string.IsNullOrWhiteSpace(connector.ServerType)
             || !string.IsNullOrWhiteSpace(McpbImageMaterializer.InferServerType(connector.Command)))
            && !string.IsNullOrWhiteSpace(connector.Image))];

    public async Task<OciSandboxSession> EnsureAgentSandboxProvisionedAsync(
        ContainerContext workspaceContext,
        IReadOnlyList<string> desiredConnectorIds,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(workspaceContext);
        ArgumentNullException.ThrowIfNull(desiredConnectorIds);

        string sandboxProvisionKey = BuildSandboxProvisionKey(workspaceContext);
        await using var sandboxProvisionLease = (await sandboxProvisionLocks
            .AcquireAsync(sandboxProvisionKey, cancellationToken)
            .ConfigureAwait(false)).ConfigureAwait(false);
        AgentSandboxInstalledManifest desiredManifest = await BuildDesiredAgentSandboxManifestAsync(desiredConnectorIds, cancellationToken).ConfigureAwait(false);
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
    public async Task<(bool Ok, IReadOnlyList<McpClientTool> Tools, WorkspaceMcpConnectorUnavailabilityReason? UnavailabilityReason, string? FailureMessage)> ProbeConnectorAsync(
        string connectorId,
        int timeoutSeconds,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectorId);

        WorkspaceMcpContainerConnectorDefinition connector = ResolveConnectorDefinition(connectorId);
        return await ProbeConnectorAsync(connector, timeoutSeconds, cancellationToken).ConfigureAwait(false);
    }

    public async Task<(bool Ok, IReadOnlyList<McpClientTool> Tools, WorkspaceMcpConnectorUnavailabilityReason? UnavailabilityReason, string? FailureMessage)> ProbeConnectorAsync(
        WorkspaceMcpContainerConnectorDefinition connector,
        int timeoutSeconds,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connector);

        string connectorId = connector.ConnectorId;
        bool containerPrepared = false;

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

            var transport = await CreateTransportAsync(effectiveConnector, requireEnabled: false, cancellationToken: cancellationToken).ConfigureAwait(false);
            containerPrepared = true;
            var tools = await ListMcpToolsAsync(transport, ClientOptionsFor(effectiveConnector.McpProtocolVersion), cancellationToken).ConfigureAwait(false);

            _ = desiredStateStore.TryTransitionConnectorState(
                effectiveConnector.ConnectorId,
                WorkspaceMcpContainerLifecycleState.Ready,
                "probe-ready");

            return (true, tools, null, null);
        }
        catch (OperationCanceledException)
        {
            logger.LogWarning("[ContainerConnector] Probe timed out for connector '{ConnectorId}'", connectorId);
            return (false, [], WorkspaceMcpConnectorUnavailabilityReason.StartupTimeout, "Container connector probe timed out.");
        }
        catch (InvalidOperationException ex)
        {
            MarkConnectorFailed(connectorId, "probe-startup-failed");
            logger.LogWarning(ex, "[ContainerConnector] Container startup failed for connector '{ConnectorId}'", connectorId);
            return (false, [], WorkspaceMcpConnectorUnavailabilityReason.InitializeFailed, await DescribeFailureAsync(connector, timeoutSeconds, ex, cancellationToken).ConfigureAwait(false));
        }
        catch (Exception ex)
        {
            MarkConnectorFailed(connectorId, "probe-failed");
            logger.LogError(ex, "[ContainerConnector] Unexpected error probing connector '{ConnectorId}'", connectorId);
            return (false, [], WorkspaceMcpConnectorUnavailabilityReason.HealthCheckFailed, await DescribeFailureAsync(connector, timeoutSeconds, ex, cancellationToken).ConfigureAwait(false));
        }
        finally
        {
            if (containerPrepared)
            {
                await ResetConnectorCooldownAsync(
                    connector,
                    BuildRuntimeKey(connector.ConnectorId, WorkspaceMcpExecutionContext.Isolated, null),
                    BuildContainerName(connector.ConnectorId, WorkspaceMcpExecutionContext.Isolated, null)).ConfigureAwait(false);
            }
        }
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

        WorkspaceMcpContainerConnectorDefinition? connector = null;
        bool containerPrepared = false;

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

            var transport = await CreateTransportAsync(connector, requireEnabled: true, allowInternalSystemConnector, workspaceContext, executionContext, cancellationToken).ConfigureAwait(false);
            containerPrepared = NormalizeExecutionContext(executionContext) != WorkspaceMcpExecutionContext.AgentSandbox;
            var tools = await ListMcpToolsAsync(transport, ClientOptionsFor(connector.McpProtocolVersion), cancellationToken).ConfigureAwait(false);

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
            string failureMessage = connector is null
                ? DescribeFailure(ex)
                : await DescribeFailureAsync(connector, timeoutSeconds, ex, cancellationToken).ConfigureAwait(false);
            return (false, [], WorkspaceMcpConnectorUnavailabilityReason.HealthCheckFailed, failureMessage);
        }
        finally
        {
            if (containerPrepared && connector is not null)
            {
                await ResetConnectorCooldownAsync(
                    connector,
                    BuildRuntimeKey(connector.ConnectorId, executionContext, workspaceContext),
                    BuildContainerName(connector.ConnectorId, executionContext, workspaceContext)).ConfigureAwait(false);
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
        string? hostArtifactDirectory = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectorId);
        ArgumentException.ThrowIfNullOrWhiteSpace(toolName);

        WorkspaceMcpContainerConnectorDefinition? connector = null;
        bool containerPrepared = false;

        try
        {
            connector = ResolveEffectiveConnectorDefinition(
                connectorId,
                environmentOverrides,
                timeoutSeconds,
                allowInternalSystemConnector);

            if (logger.IsEnabled(LogLevel.Information))
            {
                logger.LogInformation("[ContainerConnector] Calling tool '{ToolName}' on connector '{ConnectorId}'", toolName, connector.ConnectorId);
            }

            _ = desiredStateStore.TryTransitionConnectorState(
                connector.ConnectorId,
                WorkspaceMcpContainerLifecycleState.Starting,
                "call-start");

            var transport = await CreateTransportAsync(connector, requireEnabled: true, allowInternalSystemConnector, workspaceContext, executionContext, cancellationToken).ConfigureAwait(false);
            containerPrepared = NormalizeExecutionContext(executionContext) != WorkspaceMcpExecutionContext.AgentSandbox;
            McpClient client = await McpClient.CreateAsync(transport, ClientOptionsFor(connector.McpProtocolVersion), cancellationToken: cancellationToken).ConfigureAwait(false);
            await using (client.ConfigureAwait(false))
            {
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
            string failureMessage = connector is null
                ? DescribeFailure(ex)
                : await DescribeFailureAsync(connector, timeoutSeconds, ex, cancellationToken).ConfigureAwait(false);
            return (false, null, WorkspaceMcpConnectorUnavailabilityReason.HealthCheckFailed, failureMessage);
        }
        finally
        {
            if (containerPrepared && connector is not null)
            {
                await ResetConnectorCooldownAsync(
                    connector,
                    BuildRuntimeKey(connector.ConnectorId, executionContext, workspaceContext),
                    BuildContainerName(connector.ConnectorId, executionContext, workspaceContext)).ConfigureAwait(false);
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

    private async Task<string> DescribeFailureAsync(
        WorkspaceMcpContainerConnectorDefinition connector,
        int timeoutSeconds,
        Exception exception,
        CancellationToken cancellationToken)
    {
        string fallback = DescribeFailure(exception);

        if (!ContainsGenericShutdownMessage(exception))
        {
            return fallback;
        }

        try
        {
            string? processFailure = await TryDescribeProcessFailureAsync(
                connector,
                timeoutSeconds,
                cancellationToken).ConfigureAwait(false);

            return string.IsNullOrWhiteSpace(processFailure)
                ? fallback
                : processFailure;
        }
        catch (Exception diagnosticException)
        {
            logger.LogDebug(
                diagnosticException,
                "[ContainerConnector] Failed to capture process diagnostics for connector '{ConnectorId}'",
                connector.ConnectorId);
            return fallback;
        }
    }

    private async Task<string?> TryDescribeProcessFailureAsync(
        WorkspaceMcpContainerConnectorDefinition connector,
        int timeoutSeconds,
        CancellationToken cancellationToken)
    {
        connector = await PrepareRuntimeConnectorAsync(connector, cancellationToken).ConfigureAwait(false);

        List<string> arguments = ConnectorContainerSpecBuilder.BuildRun(
            connector,
            BuildContainerName(connector.ConnectorId, WorkspaceMcpExecutionContext.Isolated, null),
            WorkspaceMcpExecutionContext.Isolated,
            null,
            persistent: false);

        PodmanCommandResult result = await podmanCommandRunner
            .RunAsync(arguments, Math.Clamp(timeoutSeconds, 1, 10), cancellationToken, BuildPodmanEnvironment(connector))
            .ConfigureAwait(false);

        if (result.ExitCode == 124
            && string.IsNullOrWhiteSpace(result.StdErr)
            && string.IsNullOrWhiteSpace(result.StdOut))
        {
            return null;
        }

        return PodmanFailureFormatter.FormatFailure(
            $"Start container connector '{connector.ConnectorId}'",
            result);
    }

    private static bool ContainsGenericShutdownMessage(Exception exception)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            if (string.Equals(current.Message?.Trim(), "The server shut down unexpectedly.", StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private async Task<StdioClientTransport> CreateTransportAsync(
        WorkspaceMcpContainerConnectorDefinition connector,
        bool requireEnabled,
        bool allowInternalSystemConnector = false,
        ContainerContext? workspaceContext = null,
        WorkspaceMcpExecutionContext executionContext = WorkspaceMcpExecutionContext.Unspecified,
        CancellationToken cancellationToken = default)
    {
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

        WorkspaceMcpExecutionContext effectiveExecutionContext = NormalizeExecutionContext(executionContext);

        if (effectiveExecutionContext == WorkspaceMcpExecutionContext.AgentSandbox)
        {
            if (workspaceContext is null)
            {
                throw new InvalidOperationException($"Container connector '{connector.ConnectorId}' requires a workspace context for agent sandbox execution.");
            }

            IReadOnlyList<string> desiredConnectorIds = ResolveDesiredSandboxConnectorIds(workspaceContext);
            OciSandboxSession sandboxSession = await EnsureAgentSandboxProvisionedAsync(workspaceContext, desiredConnectorIds, cancellationToken).ConfigureAwait(false);
            AgentSandboxInstalledContract installedContract = await GetInstalledAgentSandboxContractAsync(workspaceContext, connector.ConnectorId, cancellationToken).ConfigureAwait(false);
            WorkspaceMcpContainerConnectorDefinition sandboxConnector = ApplyInstalledAgentSandboxContract(connector, installedContract);
            List<string> sandboxArguments = ConnectorContainerSpecBuilder.BuildExec(
                sandboxConnector,
                sandboxSession.ContainerName,
                installedContract.WorkingDirectory ?? installedContract.InstallRoot);

            return new StdioClientTransport(new StdioClientTransportOptions
            {
                Command = PodmanCommandRunner.BinaryName,
                Arguments = [.. sandboxArguments],
                EnvironmentVariables = ConnectorContainerSpecBuilder.BuildTransportEnvironment(sandboxConnector),
                Name = $"sandbox-connector-{connector.ConnectorId}"
            });
        }

        connector = await PrepareRuntimeConnectorAsync(connector, cancellationToken).ConfigureAwait(false);

        string runtimeKey = BuildRuntimeKey(connector.ConnectorId, effectiveExecutionContext, workspaceContext);
        string containerName = BuildContainerName(connector.ConnectorId, effectiveExecutionContext, workspaceContext);

        List<string> arguments;
        if (UsesImageDefaultCommand(connector))
        {
            arguments = ConnectorContainerSpecBuilder.BuildRun(
                connector,
                containerName,
                effectiveExecutionContext,
                ResolveMountedWorkspace(effectiveExecutionContext, workspaceContext),
                persistent: false);
        }
        else
        {
            await EnsureConnectorContainerRunningAsync(
                connector,
                runtimeKey,
                containerName,
                effectiveExecutionContext,
                ResolveMountedWorkspace(effectiveExecutionContext, workspaceContext),
                cancellationToken).ConfigureAwait(false);
            arguments = ConnectorContainerSpecBuilder.BuildExec(connector, containerName, ResolveExecWorkingDirectory(connector));
        }

        return new StdioClientTransport(new StdioClientTransportOptions
        {
            Command = PodmanCommandRunner.BinaryName,
            Arguments = [.. arguments],
            EnvironmentVariables = ConnectorContainerSpecBuilder.BuildTransportEnvironment(connector),
            Name = $"container-connector-{connector.ConnectorId}"
        });
    }

    private async Task<AgentSandboxInstalledManifest> BuildDesiredAgentSandboxManifestAsync(
        IReadOnlyList<string> desiredConnectorIds,
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
                .EnsureSandboxArtifactImageAsync(sourceConnector, installRoot, cancellationToken)
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
            .EnsureImageAsync(connector, cancellationToken)
            .ConfigureAwait(false);

        if (string.IsNullOrWhiteSpace(connector.BundlePath))
        {
            return connector with { Image = runtimeImage };
        }

        return AgentSandboxInstallContract.ApplyRuntime(connector, runtimeImage);
    }

    private async Task EnsureConnectorContainerRunningAsync(
        WorkspaceMcpContainerConnectorDefinition connector,
        string runtimeKey,
        string containerName,
        WorkspaceMcpExecutionContext executionContext,
        ContainerContext? workspaceContext,
        CancellationToken cancellationToken)
    {
        await using var connectorLease = (await connectorLocks
            .AcquireAsync(runtimeKey, cancellationToken)
            .ConfigureAwait(false)).ConfigureAwait(false);
        ConnectorContainerInspection inspection = await InspectContainerStateAsync(containerName, connector, workspaceContext, cancellationToken).ConfigureAwait(false);
        await CancelConnectorCooldownAsync(runtimeKey).ConfigureAwait(false);
        if (inspection.IsRunning && inspection.Matches(connector))
        {
            trackedConnectorContainers[runtimeKey] = containerName;
            return;
        }

        if (inspection.Exists)
        {
            if (!await RemoveConnectorContainerAsync(containerName, cancellationToken).ConfigureAwait(false))
            {
                throw new ContainerInspectionUnavailableException($"Connector container '{containerName}' removal did not complete. Recreation is deferred.");
            }
        }

        PodmanCommandResult result = await podmanCommandRunner
            .RunAsync(
                ConnectorContainerSpecBuilder.BuildRun(connector, containerName, executionContext, workspaceContext, persistent: true),
                Math.Max(10, connector.ReadinessTimeoutSeconds),
                cancellationToken,
                BuildPodmanEnvironment(connector))
            .ConfigureAwait(false);

        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException(PodmanFailureFormatter.FormatFailure(
                $"Start container connector '{connector.ConnectorId}'",
                result));
        }

        trackedConnectorContainers[runtimeKey] = containerName;
    }

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

        if (!await RemoveConnectorContainerAsync(containerName, cancellationToken).ConfigureAwait(false))
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

        string? imageId = await TryReadImageIdAsync(connector?.Image, cancellationToken).ConfigureAwait(false);
        try
        {
            if (!LiveContainerSpec.TryReadLabels(inspectJson, out string? state, out long desiredVersion, out string fingerprint))
            {
                return new ConnectorContainerInspection(true, state, 0, string.Empty, "the container inspect could not be read");
            }

            string? drift = LiveContainerSpec.DescribeDrift(
                inspectJson,
                LiveContainerSpec.ForConnector(workspaceContext, ConnectorEnvAssignments(connector), imageId));
            return new ConnectorContainerInspection(true, state, desiredVersion, fingerprint, drift);
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

        return result.StdOut.Trim();
    }

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

    private async Task<bool> RemoveConnectorContainerAsync(string containerName, CancellationToken cancellationToken)
    {
        PodmanCommandResult result = await podmanCommandRunner
            .RunAsync(["rm", "-f", containerName], 15, cancellationToken)
            .ConfigureAwait(false);

        if (result.ExitCode == 0)
        {
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
        if (separatorIndex > 0 && separatorIndex + separator.Length < connectorId.Length)
        {
            userSegment = connectorId[..separatorIndex];
            connectorSegment = connectorId[(separatorIndex + separator.Length)..];
        }

        string normalizedUser = NormalizeSegment(userSegment, segmentLimit, "user");
        string normalizedConnector = NormalizeSegment(connectorSegment, segmentLimit, "connector");
        WorkspaceMcpExecutionContext effectiveExecutionContext = NormalizeExecutionContext(executionContext);
        string hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(BuildRuntimeKey(connectorId, executionContext, workspaceContext))))[..8];

        return effectiveExecutionContext switch
        {
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
                await RemoveConnectorContainerAsync(containerName, CancellationToken.None).ConfigureAwait(false);
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
        string? Drift)
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

    private static async Task<IReadOnlyList<McpClientTool>> ListMcpToolsAsync(
        StdioClientTransport transport,
        McpClientOptions? clientOptions,
        CancellationToken cancellationToken)
    {
        McpClient client = await McpClient.CreateAsync(transport, clientOptions, cancellationToken: cancellationToken).ConfigureAwait(false);
        await using (client.ConfigureAwait(false))
        {
            IList<McpClientTool> tools = await client.ListToolsAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
            return [.. tools];
        }
    }

}

[JsonSerializable(typeof(WorkspaceContainerConnectorExecutionService.AgentSandboxInstalledManifest))]
internal sealed partial class AgentSandboxContractJsonContext : JsonSerializerContext;
