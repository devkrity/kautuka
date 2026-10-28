using System.Text.Json;

using Grpc.Core;

using Kautuka.Manager.Hosting;
using Kautuka.Manager.Protos;
using Kautuka.Manager.RuntimeContainer.Hosting;
using Kautuka.Manager.RuntimeContainer.Sandbox;
using Kautuka.Manager.RuntimeContainer.Sandbox.Models;
using Kautuka.Telemetry.Correlation;

using ModelContextProtocol.Client;

namespace Kautuka.Manager.Grpc;

public sealed class WorkspaceGrpcService(
    IContainerTool workspaceTool,
    WorkspaceDesiredStateStore desiredStateStore,
    WorkspaceReadinessGate readinessGate,
    WorkspaceExecTracker execTracker,
    IManagerRuntimeHealthProbe runtimeHealthProbe,
    WorkspaceContainerConnectorExecutionService connectorExecutionService,
    ManagerRuntimeContainerDiagnosticsService runtimeContainerDiagnosticsService,
    PkgsProxyCacheDiagnosticsService pkgsProxyCacheDiagnosticsService,
    ILogger<WorkspaceGrpcService> logger) : ManagerService.ManagerServiceBase
{
    private readonly IContainerTool workspaceTool = workspaceTool;
    private readonly WorkspaceDesiredStateStore desiredStateStore = desiredStateStore;
    private readonly WorkspaceReadinessGate readinessGate = readinessGate;
    private readonly WorkspaceExecTracker execTracker = execTracker;
    private readonly IManagerRuntimeHealthProbe runtimeHealthProbe = runtimeHealthProbe;
    private readonly WorkspaceContainerConnectorExecutionService connectorExecutionService = connectorExecutionService;
    private readonly ManagerRuntimeContainerDiagnosticsService runtimeContainerDiagnosticsService = runtimeContainerDiagnosticsService;
    private readonly PkgsProxyCacheDiagnosticsService pkgsProxyCacheDiagnosticsService = pkgsProxyCacheDiagnosticsService;
    private readonly ILogger<WorkspaceGrpcService> logger = logger;

    /// <summary>
    /// Awaits machine readiness before any container operation. If the machine is reinitialising,
    /// this blocks until the next successful reconcile cycle signals the gate, or the caller's
    /// gRPC deadline/cancellation fires.
    /// </summary>
    private async Task WaitForReadyAsync(CancellationToken cancellationToken)
    {
        try
        {
            await this.readinessGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw new RpcException(new Status(StatusCode.Unavailable, "Container runtime is not ready."));
        }
    }

    public override Task<WorkspaceBoolResponse> Ping(WorkspacePingRequest request, ServerCallContext context)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(context);
        using IDisposable scope = BeginGrpcScope(context, "Ping");
        TagCurrentActivity(("rpc.method", "Ping"));
        return Task.FromResult(new WorkspaceBoolResponse
        {
            Ok = true,
            Message = string.IsNullOrWhiteSpace(request.Message) ? "pong" : request.Message,
            Outcome = WorkspaceOperationOutcome.Succeeded,
        });
    }

    public override async Task<ManagerStatusResponse> GetHostStatus(WorkspacePingRequest request, ServerCallContext context)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(context);
        using IDisposable scope = BeginGrpcScope(context, "GetHostStatus");
        TagCurrentActivity(("rpc.method", "GetHostStatus"));

        bool runtimeAvailable = await this.runtimeHealthProbe
            .IsAvailableAsync(context.CancellationToken)
            .ConfigureAwait(false);
        if (!runtimeAvailable)
        {
            // A live runtime probe is authoritative over a previously reconciled snapshot.
            // Invalidate the gate and wake the supervisor so recovery cannot wait for the
            // normal drift-audit interval after Podman returns.
            this.readinessGate.Reset();
            _ = this.desiredStateStore.SignalReconcile(WorkspaceReconcileTriggerKind.RecoveryRetry);
        }

        WorkspaceDesiredStateSnapshot snapshot = this.desiredStateStore.Snapshot;
        bool converged = snapshot.AcceptedVersion > 0 && snapshot.ReconciledVersion == snapshot.AcceptedVersion;
        // Reaching this RPC is the Manager readiness signal. Podman availability
        // and agent reconciliation are reported independently below.
        const bool coreReady = true;
        bool agentsReady = runtimeAvailable
            && this.readinessGate.IsReady
            && converged
            && snapshot.DesiredWorkspaces.Count > 0;
        bool agentsPending = snapshot.AcceptedVersion > 0 && !agentsReady;
        string lifecycle = snapshot.LifecyclePhase switch
        {
            WorkspaceDesiredStateLifecyclePhase.Unavailable => "unavailable",
            WorkspaceDesiredStateLifecyclePhase.Accepted => "accepted",
            WorkspaceDesiredStateLifecyclePhase.Reconciling => "reconciling",
            WorkspaceDesiredStateLifecyclePhase.Reconciled => "reconciled",
            WorkspaceDesiredStateLifecyclePhase.Failed => "failed",
            _ => "unknown",
        };
        (int degradedCount, int failedCount) = desiredStateStore.RuntimeStatusCounts;

        return new ManagerStatusResponse
        {
            Ok = true,
            CoreReady = coreReady,
            AgentsPending = agentsPending,
            AgentsReady = agentsReady,
            Lifecycle = lifecycle,
            ActiveExecCount = this.execTracker.ActiveExecCount,
            Message = string.IsNullOrWhiteSpace(request.Message) ? "status" : request.Message,
            AcceptedVersion = snapshot.AcceptedVersion,
            ReconciledVersion = snapshot.ReconciledVersion,
            ReconcileAttemptCount = snapshot.ReconcileAttemptCount,
            LastFailureClass = runtimeAvailable ? snapshot.LastFailureClass ?? string.Empty : "runtime_unavailable",
            LastReconciledAtUtc = snapshot.ReconciledAtUtc?.ToString("O") ?? string.Empty,
            DegradedCount = degradedCount,
            FailedCount = failedCount,
            DriftActions = { desiredStateStore.DriftActions },
        };
    }

    public override async Task<ManagerRuntimeContainersResponse> ListRuntimeContainers(WorkspacePingRequest request, ServerCallContext context)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(context);
        using IDisposable scope = BeginGrpcScope(context, "ListRuntimeContainers");
        TagCurrentActivity(("rpc.method", "ListRuntimeContainers"));

        ManagerRuntimeContainerDiagnosticsResult result = await runtimeContainerDiagnosticsService
            .ListManagedContainersAsync(context.CancellationToken)
            .ConfigureAwait(false);

        var response = new ManagerRuntimeContainersResponse
        {
            Ok = result.Ok,
            Message = result.Message,
        };

        response.Containers.AddRange(result.Containers.Select(static container => new ManagerRuntimeContainerMessage
        {
            Name = container.Name,
            Image = container.Image,
            Status = container.Status,
            Role = container.Role,
        }));

        return response;
    }

    public override async Task<ManagerPkgsProxyCacheStatsResponse> GetPkgsProxyCacheStats(WorkspacePingRequest request, ServerCallContext context)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(context);
        using IDisposable scope = BeginGrpcScope(context, "GetPkgsProxyCacheStats");
        TagCurrentActivity(("rpc.method", "GetPkgsProxyCacheStats"));

        PkgsProxyCacheStatsResult result = await pkgsProxyCacheDiagnosticsService
            .GetCacheStatsAsync(context.CancellationToken)
            .ConfigureAwait(false);

        var response = new ManagerPkgsProxyCacheStatsResponse
        {
            Ok = result.Ok,
            Message = result.Message,
        };

        if (result.Stats is not PkgsProxyCacheStatsSnapshot stats)
        {
            return response;
        }

        response.Packages = stats.Packages;
        response.Versions = stats.Versions;
        response.Artifacts = stats.Artifacts;
        response.TotalSizeBytes = stats.TotalSizeBytes;
        response.TotalHits = stats.TotalHits;
        foreach ((string ecosystem, long count) in stats.Ecosystems)
        {
            response.Ecosystems[ecosystem] = count;
        }

        response.Popular.AddRange(stats.Popular.Select(static package => new ManagerPkgsProxyPopularPackageMessage
        {
            Ecosystem = package.Ecosystem,
            Name = package.Name,
            Hits = package.Hits,
            SizeBytes = package.SizeBytes,
        }));
        response.Recent.AddRange(stats.Recent.Select(static package => new ManagerPkgsProxyRecentPackageMessage
        {
            Ecosystem = package.Ecosystem,
            Name = package.Name,
            Version = package.Version,
            CachedAt = package.CachedAt,
            SizeBytes = package.SizeBytes,
        }));

        return response;
    }

    public override Task<WorkspaceContextResponse> DescribeWorkspace(WorkspaceResolvedRequest request, ServerCallContext context)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(context);
        using IDisposable scope = BeginGrpcScope(context, "DescribeWorkspace",
            ("ScopeType", request.Context?.ScopeType),
            ("ScopeId", request.Context?.ScopeId),
            ("UserId", request.Context?.UserId));
        TagCurrentActivity(("rpc.method", "DescribeWorkspace"), ("workspace.scope_type", request.Context?.ScopeType), ("workspace.scope_id", request.Context?.ScopeId), ("workspace.user_id", request.Context?.UserId));
        return Task.FromResult(Execute(() =>
        {
            ContainerContext workspace = WorkspaceGrpcContractMapper.ResolveWorkspace(desiredStateStore, request.Context);

            return new WorkspaceContextResponse
            {
                Ok = true,
                Context = WorkspaceGrpcContractMapper.ToContract(workspace),
                Outcome = WorkspaceOperationOutcome.Succeeded,
            };
        }));
    }

    public override async Task<WorkspaceContextResponse> RebuildWorkspace(WorkspaceResolvedRequest request, ServerCallContext context)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(context);
        using IDisposable scope = BeginGrpcScope(context, "RebuildWorkspace",
            ("ScopeType", request.Context?.ScopeType),
            ("ScopeId", request.Context?.ScopeId),
            ("UserId", request.Context?.UserId));
        TagCurrentActivity(("rpc.method", "RebuildWorkspace"), ("workspace.scope_type", request.Context?.ScopeType), ("workspace.scope_id", request.Context?.ScopeId), ("workspace.user_id", request.Context?.UserId));
        return await ExecuteAsync(async () =>
        {
            ContainerContext workspace = await this.workspaceTool.RebuildWorkspaceAsync(WorkspaceGrpcContractMapper.ResolveWorkspace(desiredStateStore, request.Context), context.CancellationToken).ConfigureAwait(false);

            return new WorkspaceContextResponse
            {
                Ok = true,
                Context = WorkspaceGrpcContractMapper.ToContract(workspace),
                Outcome = WorkspaceOperationOutcome.Succeeded,
            };
        }).ConfigureAwait(false);
    }

    public override async Task<WorkspaceDeleteResponse> DeleteWorkspace(WorkspaceResolvedRequest request, ServerCallContext context)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(context);
        using IDisposable scope = BeginGrpcScope(context, "DeleteWorkspace",
            ("ScopeType", request.Context?.ScopeType),
            ("ScopeId", request.Context?.ScopeId),
            ("UserId", request.Context?.UserId));
        TagCurrentActivity(("rpc.method", "DeleteWorkspace"), ("workspace.scope_type", request.Context?.ScopeType), ("workspace.scope_id", request.Context?.ScopeId), ("workspace.user_id", request.Context?.UserId));
        return await ExecuteAsync(async () =>
        {
            bool deleted = await this.workspaceTool.DeleteWorkspaceAsync(WorkspaceGrpcContractMapper.ResolveWorkspace(desiredStateStore, request.Context), context.CancellationToken).ConfigureAwait(false);

            return new WorkspaceDeleteResponse
            {
                Ok = true,
                Deleted = deleted,
                Outcome = deleted ? WorkspaceOperationOutcome.Succeeded : WorkspaceOperationOutcome.NotFound,
            };
        }).ConfigureAwait(false);
    }

    public override async Task<WorkspaceSandboxResponse> SpawnSandbox(WorkspaceResolvedRequest request, ServerCallContext context)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(context);
        using IDisposable scope = BeginGrpcScope(context, "SpawnSandbox",
            ("ScopeType", request.Context?.ScopeType),
            ("ScopeId", request.Context?.ScopeId),
            ("UserId", request.Context?.UserId));
        TagCurrentActivity(("rpc.method", "SpawnSandbox"), ("workspace.scope_type", request.Context?.ScopeType), ("workspace.scope_id", request.Context?.ScopeId), ("workspace.user_id", request.Context?.UserId));

        await this.WaitForReadyAsync(context.CancellationToken).ConfigureAwait(false);

        WorkspaceSandboxResponse response;
        try
        {
            ContainerContext workspace = WorkspaceGrpcContractMapper.ResolveWorkspace(desiredStateStore, request.Context);
            OciSandboxSession sandbox = await this.workspaceTool.SpawnSandboxAsync(workspace, context.CancellationToken).ConfigureAwait(false);

            response = new WorkspaceSandboxResponse
            {
                Ok = true,
                Session = WorkspaceGrpcContractMapper.ToContract(sandbox),
                Outcome = WorkspaceOperationOutcome.Succeeded,
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            this.logger.LogWarning(ex, "Workspace spawn failed.");
            response = new WorkspaceSandboxResponse
            {
                Ok = false,
                Message = "Workspace spawn failed. Consult the correlated Manager audit event.",
                Outcome = WorkspaceOperationOutcome.Failed,
            };
        }

        return response;
    }

    public override async Task<WorkspaceSandboxResponse> InspectSandbox(WorkspaceResolvedRequest request, ServerCallContext context)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(context);
        using IDisposable scope = BeginGrpcScope(context, "InspectSandbox",
            ("ScopeType", request.Context?.ScopeType),
            ("ScopeId", request.Context?.ScopeId),
            ("UserId", request.Context?.UserId));
        TagCurrentActivity(("rpc.method", "InspectSandbox"), ("workspace.scope_type", request.Context?.ScopeType), ("workspace.scope_id", request.Context?.ScopeId), ("workspace.user_id", request.Context?.UserId));

        await this.WaitForReadyAsync(context.CancellationToken).ConfigureAwait(false);

        WorkspaceSandboxResponse response;
        try
        {
            ContainerContext workspace = WorkspaceGrpcContractMapper.ResolveWorkspace(desiredStateStore, request.Context);
            OciSandboxSession sandbox = await this.workspaceTool.InspectSandboxAsync(workspace, context.CancellationToken).ConfigureAwait(false);

            response = new WorkspaceSandboxResponse
            {
                Ok = true,
                Session = WorkspaceGrpcContractMapper.ToContract(sandbox),
                Outcome = WorkspaceOperationOutcome.Succeeded,
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            this.logger.LogWarning(ex, "Workspace inspect failed.");
            response = new WorkspaceSandboxResponse
            {
                Ok = false,
                Message = "Workspace inspect failed. Consult the correlated Manager audit event.",
                Outcome = WorkspaceOperationOutcome.Failed,
            };
        }

        return response;
    }

    public override async Task<WorkspaceExecutionResponse> Exec(WorkspaceExecRequest request, ServerCallContext context)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(context);
        using IDisposable scope = BeginGrpcScope(context, "Exec",
            ("ScopeType", request.Context?.ScopeType),
            ("ScopeId", request.Context?.ScopeId),
            ("UserId", request.Context?.UserId));
        TagCurrentActivity(("rpc.method", "Exec"), ("workspace.scope_type", request.Context?.ScopeType), ("workspace.scope_id", request.Context?.ScopeId), ("workspace.user_id", request.Context?.UserId));

        await this.WaitForReadyAsync(context.CancellationToken).ConfigureAwait(false);

        this.execTracker.ExecStarted();
        WorkspaceExecutionResponse response;
        try
        {
            ContainerContext workspace = WorkspaceGrpcContractMapper.ResolveWorkspace(desiredStateStore, request.Context);
            int? timeoutSeconds = request.TimeoutSeconds > 0 ? request.TimeoutSeconds : null;
            ContainerExecutionResult execution = request.Environment.Count == 0
                ? await this.workspaceTool.RunShellAsync(workspace, request.Command, timeoutSeconds, context.CancellationToken).ConfigureAwait(false)
                : await this.workspaceTool.RunShellWithEnvironmentAsync(workspace, request.Command, request.Environment, timeoutSeconds, context.CancellationToken).ConfigureAwait(false);

            response = new WorkspaceExecutionResponse
            {
                Ok = true,
                Result = WorkspaceGrpcContractMapper.ToContract(execution),
                Outcome = WorkspaceOperationOutcome.Succeeded,
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            this.logger.LogWarning(ex, "Workspace exec failed.");
            response = new WorkspaceExecutionResponse
            {
                Ok = false,
                Message = "Workspace exec failed. Consult the correlated Manager audit event.",
                Outcome = WorkspaceOperationOutcome.Failed,
            };
        }
        finally
        {
            // Signal idle when the last concurrent exec completes so the worker can
            // run an immediate drift audit rather than waiting up to 30 minutes.
            this.execTracker.ExecEnded();
        }

        return response;
    }

    public override async Task<WorkspaceBoolResponse> StopSandbox(WorkspaceResolvedRequest request, ServerCallContext context)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(context);
        using IDisposable scope = BeginGrpcScope(context, "StopSandbox",
            ("ScopeType", request.Context?.ScopeType),
            ("ScopeId", request.Context?.ScopeId),
            ("UserId", request.Context?.UserId));
        TagCurrentActivity(("rpc.method", "StopSandbox"), ("workspace.scope_type", request.Context?.ScopeType), ("workspace.scope_id", request.Context?.ScopeId), ("workspace.user_id", request.Context?.UserId));

        await this.WaitForReadyAsync(context.CancellationToken).ConfigureAwait(false);

        WorkspaceBoolResponse response;
        try
        {
            await this.workspaceTool.CloseSandboxAsync(WorkspaceGrpcContractMapper.ResolveWorkspace(desiredStateStore, request.Context), context.CancellationToken).ConfigureAwait(false);

            response = new WorkspaceBoolResponse
            {
                Ok = true,
                Outcome = WorkspaceOperationOutcome.Succeeded,
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            this.logger.LogWarning(ex, "Workspace stop failed.");
            response = new WorkspaceBoolResponse
            {
                Ok = false,
                Message = "Workspace stop failed. Consult the correlated Manager audit event.",
                Outcome = WorkspaceOperationOutcome.Failed,
            };
        }

        return response;
    }

    public override async Task<WorkspaceBoolResponse> PrimeWorkspace(WorkspaceResolvedRequest request, ServerCallContext context)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(context);
        using IDisposable scope = BeginGrpcScope(context, "PrimeWorkspace",
            ("ScopeType", request.Context?.ScopeType),
            ("ScopeId", request.Context?.ScopeId),
            ("UserId", request.Context?.UserId));
        TagCurrentActivity(("rpc.method", "PrimeWorkspace"), ("workspace.scope_type", request.Context?.ScopeType), ("workspace.scope_id", request.Context?.ScopeId), ("workspace.user_id", request.Context?.UserId));

        await this.WaitForReadyAsync(context.CancellationToken).ConfigureAwait(false);

        WorkspaceBoolResponse response;
        try
        {
            await this.workspaceTool.PrimeWorkspaceAsync(WorkspaceGrpcContractMapper.ResolveWorkspace(desiredStateStore, request.Context), context.CancellationToken).ConfigureAwait(false);

            response = new WorkspaceBoolResponse
            {
                Ok = true,
                Outcome = WorkspaceOperationOutcome.Succeeded,
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            this.logger.LogWarning(ex, "Workspace prime failed.");
            response = new WorkspaceBoolResponse
            {
                Ok = false,
                Message = "Workspace prime failed. Consult the correlated Manager audit event.",
                Outcome = WorkspaceOperationOutcome.Failed,
            };
        }

        return response;
    }

    public override async Task<WorkspaceMcpProbeResponse> ProbeMcpContainer(WorkspaceMcpContainerProbeRequest request, ServerCallContext context)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(context);
        using IDisposable scope = BeginGrpcScope(context, "ProbeMcpContainer", ("ConnectorId", request.ConnectorId));
        TagCurrentActivity(("rpc.method", "ProbeMcpContainer"), ("mcp.transport", "container"), ("mcp.connector_id", request.ConnectorId));

        await this.WaitForReadyAsync(context.CancellationToken).ConfigureAwait(false);

        WorkspaceMcpContainerConnectorDefinition probeConnector = WorkspaceGrpcContractMapper.FromProbe(request);

        (bool ok, IReadOnlyList<McpClientTool> tools, WorkspaceMcpConnectorUnavailabilityReason? reason, string? failureMessage) = await this.connectorExecutionService
            .ProbeConnectorAsync(probeConnector, request.TimeoutSeconds <= 0 ? 30 : request.TimeoutSeconds, context.CancellationToken)
            .ConfigureAwait(false);

        var response = new WorkspaceMcpProbeResponse
        {
            Ok = ok,
            Message = ok ? $"Connector '{request.ConnectorId}' is available." : failureMessage ?? $"Connector '{request.ConnectorId}' is unavailable.",
            ToolCount = tools.Count,
            Outcome = ok ? WorkspaceOperationOutcome.Succeeded : WorkspaceOperationOutcome.Failed,
        };
        response.Tools.AddRange(tools.Select(static t => new WorkspaceMcpToolMessage
        {
            Name = t.Name,
            Description = t.Description ?? string.Empty
        }));
        return response;
    }

    public override async Task<WorkspaceMcpContainerListToolsResponse> ListMcpContainerTools(WorkspaceMcpContainerCallRequest request, ServerCallContext context)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(context);
        using IDisposable scope = BeginGrpcScope(context, "ListMcpContainerTools", ("ConnectorId", request.ConnectorId));
        TagCurrentActivity(("rpc.method", "ListMcpContainerTools"), ("mcp.transport", "container"), ("mcp.connector_id", request.ConnectorId));

        await this.WaitForReadyAsync(context.CancellationToken).ConfigureAwait(false);

        Dictionary<string, string> environment = WorkspaceGrpcContractMapper.ToEnvironment(request.Environment);
        ContainerContext? workspaceContext = request.WorkspaceContext is null ? null : WorkspaceGrpcContractMapper.ResolveWorkspace(desiredStateStore, request.WorkspaceContext);

        (bool ok, IReadOnlyList<McpClientTool> tools, WorkspaceMcpConnectorUnavailabilityReason? reason, string? failureMessage) = await this.connectorExecutionService
            .ListToolsAsync(
                request.ConnectorId,
                environment,
                request.TimeoutSeconds <= 0 ? 30 : request.TimeoutSeconds,
                request.AllowInternalSystemConnector,
                workspaceContext,
                request.ExecutionContext,
                context.CancellationToken)
            .ConfigureAwait(false);

        var response = new WorkspaceMcpContainerListToolsResponse
        {
            Ok = ok,
            Message = ok ? $"Loaded {tools.Count} tool(s)." : failureMessage ?? "Failed to list tools.",
            ToolCount = tools.Count,
            UnavailabilityReason = reason ?? WorkspaceMcpConnectorUnavailabilityReason.UnavailabilityReasonUnspecified,
            Outcome = ok ? WorkspaceOperationOutcome.Succeeded : WorkspaceOperationOutcome.Failed,
        };
        response.Tools.AddRange(tools.Select(static t => new WorkspaceMcpToolMessage
        {
            Name = t.Name,
            Description = t.Description ?? string.Empty,
            JsonSchema = t.JsonSchema.ValueKind == JsonValueKind.Undefined ? string.Empty : t.JsonSchema.GetRawText(),
        }));
        return response;
    }

    public override async Task<WorkspaceMcpContainerCallResponse> CallMcpContainerTool(WorkspaceMcpContainerCallRequest request, ServerCallContext context)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(context);
        using IDisposable scope = BeginGrpcScope(context, "CallMcpContainerTool", ("ConnectorId", request.ConnectorId), ("ToolName", request.ToolName));
        TagCurrentActivity(("rpc.method", "CallMcpContainerTool"), ("mcp.transport", "container"), ("mcp.connector_id", request.ConnectorId), ("mcp.tool_name", request.ToolName));

        await this.WaitForReadyAsync(context.CancellationToken).ConfigureAwait(false);

        Dictionary<string, string> environment = WorkspaceGrpcContractMapper.ToEnvironment(request.Environment);
        ContainerContext? workspaceContext = request.WorkspaceContext is null ? null : WorkspaceGrpcContractMapper.ResolveWorkspace(desiredStateStore, request.WorkspaceContext);

        (bool ok, string? result, WorkspaceMcpConnectorUnavailabilityReason? reason, string? failureMessage) = await this.connectorExecutionService
            .CallToolAsync(
                request.ConnectorId,
                environment,
                request.ToolName,
                string.IsNullOrWhiteSpace(request.ToolInputJson) ? "{}" : request.ToolInputJson,
                request.TimeoutSeconds <= 0 ? 30 : request.TimeoutSeconds,
                request.AllowInternalSystemConnector,
                workspaceContext,
                request.ExecutionContext,
                context.CancellationToken,
                string.IsNullOrWhiteSpace(request.HostArtifactDirectory) ? null : request.HostArtifactDirectory)
            .ConfigureAwait(false);

        return new WorkspaceMcpContainerCallResponse
        {
            Ok = ok,
            Message = ok ? "Tool call completed." : failureMessage ?? "Tool call failed.",
            ResultJson = result ?? string.Empty,
            UnavailabilityReason = reason ?? WorkspaceMcpConnectorUnavailabilityReason.UnavailabilityReasonUnspecified,
            Outcome = ok ? WorkspaceOperationOutcome.Succeeded : WorkspaceOperationOutcome.Failed,
        };
    }

    public override async Task<DesiredStateUpdateResponse> UpdateDesiredState(WorkspaceDesiredStateUpdate request, ServerCallContext context)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(context);
        using IDisposable scope = BeginGrpcScope(context, "UpdateDesiredState");
        TagCurrentActivity(("rpc.method", "UpdateDesiredState"));

        try
        {
            if (request.DesiredStateVersion <= 0)
            {
                throw new InvalidOperationException("desired_state_version must be greater than zero.");
            }

            if (request.SyncReason == WorkspaceDesiredStateSyncReason.Unspecified)
            {
                throw new InvalidOperationException("sync_reason must be specified.");
            }

            if (!Guid.TryParse(request.RequestId, out _))
            {
                throw new InvalidOperationException("request_id must be a UUID.");
            }

            long currentAcceptedVersion = desiredStateStore.Snapshot.AcceptedVersion;
            if (request.ExpectedAcceptedVersion > 0 && request.ExpectedAcceptedVersion != currentAcceptedVersion)
            {
                return new DesiredStateUpdateResponse
                {
                    Outcome = DesiredStateUpdateOutcome.Conflict,
                    AcceptedVersion = currentAcceptedVersion,
                    RequestId = request.RequestId,
                    Message = "Expected accepted version does not match Manager state.",
                };
            }

            ManagerRuntimeSettings runtimeSettings = WorkspaceGrpcContractMapper.FromRuntime(request.Runtime);
            IReadOnlyList<ContainerContext> desiredWorkspaces = [.. request.DesiredWorkspaces.Select(message => WorkspaceGrpcContractMapper.FromDesiredState(message, runtimeSettings))];
            IReadOnlyList<WorkspaceMcpContainerConnectorDefinition> desiredConnectors = [.. request.ContainerConnectors.Select(static connector =>
                new WorkspaceMcpContainerConnectorDefinition(
                    connector.ConnectorId,
                    connector.Image,
                    connector.Command,
                    [.. connector.Args],
                    connector.Enabled,
                    connector.IsInternalSystemConnector,
                    string.IsNullOrWhiteSpace(connector.BundlePath) ? null : connector.BundlePath,
                    string.IsNullOrWhiteSpace(connector.ServerType) ? null : connector.ServerType,
                    [.. connector.Environment.Select(static pair => new KeyValuePair<string, string>(pair.Key, pair.Value))],
                    connector.ReadinessTimeoutSeconds <= 0 ? 30 : connector.ReadinessTimeoutSeconds,
                    connector.JitCooldownSeconds <= 0 ? 600 : connector.JitCooldownSeconds,
                    string.IsNullOrWhiteSpace(connector.McpProtocolVersion) ? null : connector.McpProtocolVersion))];
            IReadOnlyList<WorkspaceSandboxConnectorBindingsDefinition> desiredSandboxConnectorBindings = [.. request.SandboxConnectorBindings.Select(static binding =>
                new WorkspaceSandboxConnectorBindingsDefinition(
                    binding.ScopeType,
                    binding.ScopeId,
                    [.. binding.ConnectorIds.Where(static connectorId => !string.IsNullOrWhiteSpace(connectorId)).Distinct(StringComparer.Ordinal)]))];

            if (this.logger.IsEnabled(LogLevel.Information))
            {
                this.logger.LogInformation(
                    "Received desired state update: version={DesiredStateVersion} reason={SyncReason} workspaces={WorkspaceCount} connectors={ConnectorCount}",
                    request.DesiredStateVersion,
                    request.SyncReason,
                    desiredWorkspaces.Count,
                    desiredConnectors.Count);
            }

            WorkspaceDesiredStateApplyResult applyResult = this.desiredStateStore.ApplyDesiredState(
                request.DesiredStateVersion,
                request.SyncReason,
                runtimeSettings,
                desiredWorkspaces,
                desiredConnectors,
                desiredSandboxConnectorBindings,
                string.IsNullOrWhiteSpace(request.PublisherEpoch) ? "gateway" : request.PublisherEpoch.Trim());

            if (applyResult.RequiresReconciliation)
            {
                await this.connectorExecutionService
                    .ApplyDesiredStateUpdateAsync(applyResult.PreviousConnectors, desiredConnectors, context.CancellationToken)
                    .ConfigureAwait(false);
            }

            if (applyResult.IsStale)
            {
                return new DesiredStateUpdateResponse
                {
                    Outcome = DesiredStateUpdateOutcome.Stale,
                    AcceptedVersion = applyResult.Version,
                    RequestId = request.RequestId,
                    Message = $"Ignored stale desired state version {request.DesiredStateVersion}; latest accepted version is {applyResult.Version}."
                };
            }

            if (applyResult.IsReplay)
            {
                return new DesiredStateUpdateResponse
                {
                    Outcome = DesiredStateUpdateOutcome.Replayed,
                    AcceptedVersion = applyResult.Version,
                    ReconciliationRequired = applyResult.RequiresReconciliation,
                    RequestId = request.RequestId,
                    Message = $"Ignored replay desired state version {request.DesiredStateVersion}."
                };
            }

            return new DesiredStateUpdateResponse
            {
                Outcome = DesiredStateUpdateOutcome.Applied,
                AcceptedVersion = applyResult.Version,
                ReconciliationRequired = true,
                RequestId = request.RequestId,
                Message = applyResult.HasVersionGap
                    ? $"Applied desired state version {applyResult.Version} with a detected version gap for {desiredWorkspaces.Count} workspace(s) and {desiredConnectors.Count} connector(s)."
                    : $"Applied desired state version {applyResult.Version} for {desiredWorkspaces.Count} workspace(s) and {desiredConnectors.Count} connector(s)."
            };
        }
        catch (InvalidOperationException ex)
        {
            this.logger.LogWarning(ex, "Workspace desired state update rejected.");
            return new DesiredStateUpdateResponse
            {
                Outcome = DesiredStateUpdateOutcome.Failed,
                AcceptedVersion = desiredStateStore.Snapshot.AcceptedVersion,
                RequestId = request.RequestId,
                Message = "Desired state was rejected. Consult the correlated Manager audit event."
            };
        }
    }

    private IDisposable BeginGrpcScope(ServerCallContext context, string methodName, params (string Key, object? Value)[] properties)
    {
        string? externalCorrelationId = KautukaGrpcCorrelationMetadata.GetCorrelationId(context.RequestHeaders);
        List<(string Key, object? Value)> values = [("GrpcMethod", methodName)];
        if (!string.IsNullOrWhiteSpace(externalCorrelationId))
        {
            values.Add(("CorrelationId", externalCorrelationId));
        }

        values.AddRange(properties);
        return this.logger.BeginKautukaCorrelationScope([.. values]);
    }

    private static void TagCurrentActivity(params (string Key, object? Value)[] tags)
    {
        _ = tags;
    }

    private T Execute<T>(Func<T> action)
        where T : class, new()
    {
        try
        {
            return action();
        }
        catch (Exception ex)
        {
            this.logger.LogWarning(ex, "Workspace RPC operation failed.");
            if (typeof(T) == typeof(WorkspaceContextResponse))
            {
                return new WorkspaceContextResponse { Ok = false, Message = "Workspace operation failed. Consult the correlated Manager audit event.", Outcome = WorkspaceOperationOutcome.Failed } as T ?? new T();
            }

            if (typeof(T) == typeof(WorkspaceDeleteResponse))
            {
                return new WorkspaceDeleteResponse { Ok = false, Message = "Workspace operation failed. Consult the correlated Manager audit event.", Outcome = WorkspaceOperationOutcome.Failed } as T ?? new T();
            }

            return new T();
        }
    }

    private async Task<T> ExecuteAsync<T>(Func<Task<T>> action)
        where T : class, new()
    {
        try
        {
            return await action().ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            this.logger.LogWarning(ex, "Workspace RPC operation failed.");
            if (typeof(T) == typeof(WorkspaceContextResponse))
            {
                return new WorkspaceContextResponse { Ok = false, Message = "Workspace operation failed. Consult the correlated Manager audit event.", Outcome = WorkspaceOperationOutcome.Failed } as T ?? new T();
            }

            if (typeof(T) == typeof(WorkspaceDeleteResponse))
            {
                return new WorkspaceDeleteResponse { Ok = false, Message = "Workspace operation failed. Consult the correlated Manager audit event.", Outcome = WorkspaceOperationOutcome.Failed } as T ?? new T();
            }

            return new T();
        }
    }

}
