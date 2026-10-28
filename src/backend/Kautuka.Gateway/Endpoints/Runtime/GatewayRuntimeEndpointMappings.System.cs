using Grpc.Core;

using Kautuka.Gateway.Domain.Execution;
using Kautuka.Gateway.Domain.Stores;
using Kautuka.Gateway.Infrastructure.Appliance;
using Kautuka.Gateway.Infrastructure.Channels;
using Kautuka.Gateway.Infrastructure.Updates;
using Kautuka.Manager.Protos;
using Kautuka.Protos;
using Kautuka.UpdateService.Protos;

using Microsoft.AspNetCore.Mvc;

using UpdateServiceReleaseReferenceMessage = Kautuka.UpdateService.Protos.UpdateReleaseReferenceMessage;
using UpdateServiceStatusResponse = Kautuka.UpdateService.Protos.UpdateStatusResponse;

namespace Kautuka.Gateway.Endpoints.Runtime;

internal static partial class GatewayRuntimeEndpointMappings
{
    private const string workspaceNotReadyStatus = "core_not_ready";
    private const string agentsPendingStatus = "agents_pending";
    private const string agentsReadyStatus = "agents_ready";

    internal static void MapGatewayRuntimeSystemEndpoints(WebApplication app)
    {
        app.MapGet("/api/runtime/execution-readiness", async (
            IAgentExecutionReadinessService readinessService,
            IGatewayAuthGuard auth,
            HttpContext context,
            CancellationToken cancellationToken) =>
        {
            IResult? unauthorized = auth.RequireAuthenticated(context, out _);
            if (unauthorized is not null)
            {
                return unauthorized;
            }

            AgentExecutionReadiness readiness = await readinessService.CheckAsync(cancellationToken).ConfigureAwait(false);
            return Results.Json(readiness, KautukaJsonContext.Default.AgentExecutionReadiness);
        });

        app.MapGet("/api/runtime/execution-quarantine", (
            IExecutionQuarantineStore quarantineStore) =>
        {
            ExecutionQuarantineItem[] items = [.. quarantineStore.ListActive()];
            return Results.Json(items, KautukaJsonContext.Default.ExecutionQuarantineItemArray);
        }).RequireAuthorization("AdminDiagnostics");

        app.MapPost("/api/runtime/execution-quarantine/{quarantineId}/retry", (
            string quarantineId,
            IExecutionQuarantineStore quarantineStore) =>
        {
            return quarantineStore.RequestRetry(quarantineId)
                ? Results.Accepted()
                : Results.NotFound();
        }).RequireAuthorization("AdminDiagnostics");

        app.MapPost("/api/runtime/execution-quarantine/{quarantineId}/terminate", (
            string quarantineId,
            IExecutionQuarantineStore quarantineStore,
            IAgentToolInvocationStore invocationStore,
            IRunWaitStateStore waitStateStore,
            IGatewayApprovalStore approvalStore) =>
        {
            ExecutionQuarantineItem? item = quarantineStore.Get(quarantineId);
            if (item is null || item.Status is ExecutionQuarantineStatuses.Terminated or ExecutionQuarantineStatuses.Resolved)
            {
                return Results.NotFound();
            }

            switch (item.EntityType)
            {
                case "aaat_invocation":
                    invocationStore.MarkFailed(item.EntityId, "operator_terminated");
                    break;
                case "run_wait":
                    waitStateStore.MarkFailed(item.EntityId, "operator_terminated");
                    break;
                case "approval":
                    approvalStore.Expire(item.EntityId, DateTimeOffset.UtcNow, "operator_terminated");
                    break;
            }

            return quarantineStore.Terminate(quarantineId)
                ? Results.NoContent()
                : Results.Conflict();
        }).RequireAuthorization("AdminDiagnostics");

        app.MapGet("/api/runtime/update-status", async (
            [FromServices] IUpdateServiceGrpcService updateServiceGrpcService,
            IGatewayAuthGuard auth,
            HttpContext context,
            CancellationToken cancellationToken) =>
        {
            IResult? unauthorized = auth.RequireAuthenticated(context, out _);
            if (unauthorized is not null)
            {
                return unauthorized;
            }

            GatewayRuntimeUpdateStatusResponse response = await GetUpdateServiceStatusAsync(updateServiceGrpcService, cancellationToken).ConfigureAwait(false);
            return Results.Json(response, KautukaJsonContext.Default.GatewayRuntimeUpdateStatusResponse);
        });

        app.MapPost("/api/runtime/update-status/refresh", async (
            [FromServices] IUpdateServiceGrpcService updateServiceGrpcService,
            IGatewayAuthGuard auth,
            HttpContext context,
            CancellationToken cancellationToken) =>
        {
            IResult? unauthorized = auth.RequireAdmin(context, out _);
            if (unauthorized is not null)
            {
                return unauthorized;
            }

            GatewayRuntimeUpdateStatusResponse response = await RefreshUpdateServiceDiscoveryAsync(updateServiceGrpcService, cancellationToken).ConfigureAwait(false);
            return Results.Json(response, KautukaJsonContext.Default.GatewayRuntimeUpdateStatusResponse);
        });

        app.MapPost("/api/runtime/update-status/apply", async (
            [FromServices] IUpdateServiceGrpcService updateServiceGrpcService,
            IGatewayAuthGuard auth,
            HttpContext context,
            CancellationToken cancellationToken) =>
        {
            IResult? unauthorized = auth.RequireAdmin(context, out _);
            if (unauthorized is not null)
            {
                return unauthorized;
            }

            GatewayRuntimeUpdateStatusResponse response = await ApplyUpdateServiceUpdateAsync(updateServiceGrpcService, cancellationToken).ConfigureAwait(false);
            return Results.Json(response, KautukaJsonContext.Default.GatewayRuntimeUpdateStatusResponse);
        });

        app.MapPost("/api/runtime/update-status/revert", async (
            [FromServices] IUpdateServiceGrpcService updateServiceGrpcService,
            IGatewayAuthGuard auth,
            HttpContext context,
            CancellationToken cancellationToken) =>
        {
            IResult? unauthorized = auth.RequireAdmin(context, out _);
            if (unauthorized is not null)
            {
                return unauthorized;
            }

            GatewayRuntimeUpdateStatusResponse response = await RevertUpdateServiceUpdateAsync(updateServiceGrpcService, cancellationToken).ConfigureAwait(false);
            return Results.Json(response, KautukaJsonContext.Default.GatewayRuntimeUpdateStatusResponse);
        });

        app.MapGet("/api/runtime/update-history", async (
            [FromServices] UpdateOperationHistoryReader historyReader,
            IGatewayAuthGuard auth,
            HttpContext context,
            CancellationToken cancellationToken) =>
        {
            IResult? unauthorized = auth.RequireAuthenticated(context, out _);
            if (unauthorized is not null)
            {
                return unauthorized;
            }

            GatewayRuntimeUpdateOperationPayload[] history = await historyReader.GetHistoryAsync(cancellationToken).ConfigureAwait(false);
            return Results.Json(history, KautukaJsonContext.Default.GatewayRuntimeUpdateOperationPayloadArray);
        });

        app.MapGet("/api/runtime/appliance-health", async (
            [FromServices] UpdateOperationHistoryReader historyReader,
            [FromServices] SidecarStagingStatusReader sidecarReader,
            [FromServices] IUpdateServiceGrpcService updateServiceGrpcService,
            IGatewayAuthGuard auth,
            HttpContext context,
            CancellationToken cancellationToken) =>
        {
            IResult? unauthorized = auth.RequireAuthenticated(context, out _);
            if (unauthorized is not null)
            {
                return unauthorized;
            }

            GatewayRuntimeUpdateOperationPayload[] history = await historyReader.GetHistoryAsync(cancellationToken).ConfigureAwait(false);
            GatewayRuntimeUpdateOperationPayload? lastApply = Array.FindLast(history, static op => op.OperationType == "apply" && op.Succeeded);

            GatewayRuntimeUpdateStatusResponse status = await GetUpdateServiceStatusAsync(updateServiceGrpcService, cancellationToken).ConfigureAwait(false);
            (DateTimeOffset? sidecarAt, int? sidecarCount) = await sidecarReader.ReadAsync(cancellationToken).ConfigureAwait(false);

            GatewayApplianceHealthPayload payload = new(
                InstalledVersion: NullIfEmpty(status.ManagerVersion),
                LastUpdateAppliedAtUtc: lastApply?.CompletedAtUtc,
                SidecarsStagedAtUtc: sidecarAt?.ToString("O"),
                SidecarCount: sidecarCount);

            return Results.Json(payload, KautukaJsonContext.Default.GatewayApplianceHealthPayload);
        });

        app.MapGet("/api/runtime/system-health", async (
            HttpContext context,
            IManagerGrpcService workspaceGrpcService,
            IChannelHostGrpcService channelHostGrpcService,
            CancellationToken cancellationToken) =>
        {
            ServiceHealthResponse runtime = new(
                Status: "healthy",
                Service: "Gateway");

            ServiceHealthResponse channels = await GetChannelHostStatusAsync(channelHostGrpcService, cancellationToken).ConfigureAwait(false);

            ManagerStatusResponse managerStatus = await GetManagerStatusAsync(workspaceGrpcService, cancellationToken).ConfigureAwait(false);

            string lifecycleStatus = ResolveLifecycleStatus(managerStatus);
            bool agentsReady = string.Equals(lifecycleStatus, agentsReadyStatus, StringComparison.OrdinalIgnoreCase);
            bool agentsPending = string.Equals(lifecycleStatus, agentsPendingStatus, StringComparison.OrdinalIgnoreCase);

            ServiceHealthResponse manager = new(
                Status: managerStatus.CoreReady ? "healthy" : "unhealthy",
                Service: "Manager");

            // CoreReady reflects Kautuka.Manager readiness.
            SystemHealthLifecycleResponse lifecycle = new(
                CoreReady: managerStatus.CoreReady,
                AgentsPending: agentsPending,
                AgentsReady: agentsReady,
                Status: lifecycleStatus);

            string[] actionHints = ResolveActionHints(managerStatus, lifecycleStatus);
            // The route stays open for status words. Drift notes name containers, so only an
            // administrator receives them.
            IReadOnlyList<string>? driftActions = context.User.IsInRole("admin")
                ? managerStatus.DriftActions
                : null;

            return Results.Json(
                new SystemHealthResponse(runtime, channels, manager, lifecycle, actionHints, driftActions),
                KautukaJsonContext.Default.SystemHealthResponse);
        }).AllowAnonymous();

        app.MapGet("/api/runtime/diagnostics/manager/containers", async (
            IManagerGrpcService workspaceGrpcService,
            CancellationToken cancellationToken) =>
        {
            GatewayRuntimeManagerContainersResponse response = await GetManagerRuntimeContainersAsync(workspaceGrpcService, cancellationToken).ConfigureAwait(false);
            return Results.Json(response, KautukaJsonContext.Default.GatewayRuntimeManagerContainersResponse);
        }).RequireAuthorization("AdminDiagnostics");

        app.MapGet("/api/runtime/diagnostics/pkgs-proxy/stats", async (
            IManagerGrpcService workspaceGrpcService,
            CancellationToken cancellationToken) =>
        {
            GatewayPkgsProxyCacheStatsResponse response = await GetPkgsProxyCacheStatsAsync(workspaceGrpcService, cancellationToken).ConfigureAwait(false);
            return Results.Json(response, KautukaJsonContext.Default.GatewayPkgsProxyCacheStatsResponse);
        }).RequireAuthorization("AdminDiagnostics");
    }

    private static async Task<ServiceHealthResponse> GetChannelHostStatusAsync(
        IChannelHostGrpcService channelHostGrpcService,
        CancellationToken cancellationToken)
    {
        try
        {
            ChannelHostPingResponse ping = await channelHostGrpcService.PingAsync(
                new ChannelHostPingRequest(),
                cancellationToken).ConfigureAwait(false);

            ChannelProviderHealth? primary = ping.Providers.FirstOrDefault();

            return new ServiceHealthResponse(
                Status: primary?.Status ?? "unknown",
                Service: primary?.DisplayName ?? "Channels");
        }
        catch (RpcException)
        {
            return new ServiceHealthResponse(Status: "unknown", Service: "Channels");
        }
        catch (InvalidOperationException)
        {
            return new ServiceHealthResponse(Status: "unknown", Service: "Channels");
        }
    }

    private static string ResolveLifecycleStatus(ManagerStatusResponse managerStatus)
    {
        if (!managerStatus.CoreReady)
        {
            return workspaceNotReadyStatus;
        }

        if (managerStatus.AgentsReady)
        {
            return agentsReadyStatus;
        }

        return agentsPendingStatus;
    }

    private static string[] ResolveActionHints(
        ManagerStatusResponse managerStatus,
        string lifecycleStatus)
    {
        if (!managerStatus.CoreReady)
        {
            return ["initializeAgentRuntime", "restartAgentSupervisor"];
        }

        if (string.Equals(lifecycleStatus, agentsPendingStatus, StringComparison.OrdinalIgnoreCase))
        {
            return ["applyAgentDsc", "openAgentSetup"];
        }

        return [];
    }

    private static async Task<ManagerStatusResponse> GetManagerStatusAsync(
        IManagerGrpcService workspaceGrpcService,
        CancellationToken cancellationToken)
    {
        try
        {
            return await workspaceGrpcService.GetHostStatusAsync(
                new WorkspacePingRequest { Message = "health" },
                cancellationToken).ConfigureAwait(false);
        }
        catch (RpcException)
        {
            return new ManagerStatusResponse
            {
                Ok = false,
                Lifecycle = workspaceNotReadyStatus,
                CoreReady = false,
                AgentsPending = false,
                AgentsReady = false,
                Message = "Manager unavailable"
            };
        }
        catch (InvalidOperationException)
        {
            return new ManagerStatusResponse
            {
                Ok = false,
                Lifecycle = workspaceNotReadyStatus,
                CoreReady = false,
                AgentsPending = false,
                AgentsReady = false,
                Message = "Manager unavailable"
            };
        }
    }

    private static async Task<GatewayRuntimeManagerContainersResponse> GetManagerRuntimeContainersAsync(
        IManagerGrpcService workspaceGrpcService,
        CancellationToken cancellationToken)
    {
        try
        {
            ManagerRuntimeContainersResponse response = await workspaceGrpcService.ListRuntimeContainersAsync(
                new WorkspacePingRequest { Message = "runtime-diagnostics" },
                cancellationToken).ConfigureAwait(false);

            return new GatewayRuntimeManagerContainersResponse(
                Ok: response.Ok,
                Message: response.Message,
                Containers:
                [
                    .. response.Containers.Select(static container => new GatewayRuntimeManagerContainerItem(
                        container.Name,
                        container.Image,
                        container.Status,
                        container.Role))
                ]);
        }
        catch (RpcException)
        {
            return new GatewayRuntimeManagerContainersResponse(false, "Manager runtime diagnostics unavailable.", []);
        }
        catch (InvalidOperationException)
        {
            return new GatewayRuntimeManagerContainersResponse(false, "Manager runtime diagnostics unavailable.", []);
        }
    }

    private static async Task<GatewayPkgsProxyCacheStatsResponse> GetPkgsProxyCacheStatsAsync(
        IManagerGrpcService workspaceGrpcService,
        CancellationToken cancellationToken)
    {
        try
        {
            ManagerPkgsProxyCacheStatsResponse response = await workspaceGrpcService.GetPkgsProxyCacheStatsAsync(
                new WorkspacePingRequest { Message = "pkgs-proxy-cache-stats" },
                cancellationToken).ConfigureAwait(false);

            return ToPayload(response);
        }
        catch (RpcException)
        {
            return UnavailablePkgsProxyCacheStats("Manager package proxy cache diagnostics unavailable.");
        }
        catch (InvalidOperationException)
        {
            return UnavailablePkgsProxyCacheStats("Manager package proxy cache diagnostics unavailable.");
        }
    }

    private static GatewayPkgsProxyCacheStatsResponse ToPayload(ManagerPkgsProxyCacheStatsResponse response)
    {
        if (!response.Ok)
        {
            return UnavailablePkgsProxyCacheStats(response.Message);
        }

        var ecosystems = new SortedDictionary<string, long>(StringComparer.Ordinal);
        foreach ((string ecosystem, long count) in response.Ecosystems)
        {
            ecosystems[ecosystem] = count;
        }

        return new GatewayPkgsProxyCacheStatsResponse(
            Ok: true,
            Message: response.Message,
            Packages: response.Packages,
            Versions: response.Versions,
            Artifacts: response.Artifacts,
            TotalSizeBytes: response.TotalSizeBytes,
            TotalHits: response.TotalHits,
            Ecosystems: ecosystems,
            Popular:
            [
                .. response.Popular.Select(static package => new GatewayPkgsProxyPopularPackage(
                    package.Ecosystem,
                    package.Name,
                    package.Hits,
                    package.SizeBytes))
            ],
            Recent:
            [
                .. response.Recent.Select(static package => new GatewayPkgsProxyRecentPackage(
                    package.Ecosystem,
                    package.Name,
                    package.Version,
                    package.CachedAt,
                    package.SizeBytes))
            ]);
    }

    private static GatewayPkgsProxyCacheStatsResponse UnavailablePkgsProxyCacheStats(string message)
        => new(
            Ok: false,
            Message: message,
            Packages: 0,
            Versions: 0,
            Artifacts: 0,
            TotalSizeBytes: 0,
            TotalHits: 0,
            Ecosystems: new SortedDictionary<string, long>(StringComparer.Ordinal),
            Popular: [],
            Recent: []);

    private static async Task<GatewayRuntimeUpdateStatusResponse> GetUpdateServiceStatusAsync(
        IUpdateServiceGrpcService updateServiceGrpcService,
        CancellationToken cancellationToken)
    {
        try
        {
            UpdateServiceStatusResponse status = await updateServiceGrpcService.GetUpdateStatusAsync(
                new UpdateServicePingRequest { Message = "update-status" },
                cancellationToken).ConfigureAwait(false);

            return new GatewayRuntimeUpdateStatusResponse(
                Ok: status.Ok,
                Message: status.Message,
                State: string.IsNullOrWhiteSpace(status.WorkflowState) ? "Current" : status.WorkflowState,
                EffectiveReleaseChannel: NullIfEmpty(status.EffectiveReleaseChannel),
                EligibilityState: NullIfEmpty(status.EligibilityState),
                EligibilityReason: NullIfEmpty(status.EligibilityReason),
                CurrentRelease: ToPayload(status.CurrentRelease),
                AvailableRelease: ToPayload(status.AvailableRelease),
                LastKnownGoodRelease: ToPayload(status.LastKnownGoodRelease),
                LastDiscoveryAttemptUtc: NullIfEmpty(status.LastDiscoveryAttemptUtc),
                LastDiscoverySuccessUtc: NullIfEmpty(status.LastDiscoverySuccessUtc),
                ManagerVersion: status.ManagerVersion,
                HostReady: status.HostReady,
                ActiveExecCount: status.ActiveExecCount,
                LastError: NullIfEmpty(status.LastError));
        }
        catch (RpcException ex)
        {
            return CreateUnavailableUpdateStatus(
                message: "update-status-unavailable",
                lastError: DescribeUnavailable(ex));
        }
        catch (InvalidOperationException ex)
        {
            return CreateUnavailableUpdateStatus(
                message: "update-status-unavailable",
                lastError: ex.Message);
        }
    }

    private static async Task<GatewayRuntimeUpdateStatusResponse> RefreshUpdateServiceDiscoveryAsync(
        IUpdateServiceGrpcService updateServiceGrpcService,
        CancellationToken cancellationToken)
    {
        try
        {
            UpdateServiceStatusResponse status = await updateServiceGrpcService.RefreshUpdateDiscoveryAsync(
                new UpdateServicePingRequest { Message = "update-refresh" },
                cancellationToken).ConfigureAwait(false);

            return ToPayload(status);
        }
        catch (RpcException ex)
        {
            return CreateUnavailableUpdateStatus(
                message: "update-refresh-unavailable",
                lastError: DescribeUnavailable(ex));
        }
        catch (InvalidOperationException ex)
        {
            return CreateUnavailableUpdateStatus(
                message: "update-refresh-failed",
                lastError: ex.Message);
        }
    }

    private static async Task<GatewayRuntimeUpdateStatusResponse> ApplyUpdateServiceUpdateAsync(
        IUpdateServiceGrpcService updateServiceGrpcService,
        CancellationToken cancellationToken)
    {
        GatewayRuntimeUpdateStatusResponse baselineStatus = await GetUpdateServiceStatusAsync(updateServiceGrpcService, cancellationToken).ConfigureAwait(false);

        try
        {
            UpdateServiceStatusResponse status = await updateServiceGrpcService.ApplyUpdateAsync(
                new UpdateServicePingRequest { Message = "update-apply" },
                cancellationToken).ConfigureAwait(false);

            return ToPayload(status);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return CreateAcceptedUpdateStatus(
                baselineStatus,
                message: "update-apply-accepted");
        }
        catch (RpcException ex)
        {
            return CreateUnavailableUpdateStatus(
                message: "update-apply-unavailable",
                lastError: DescribeUnavailable(ex));
        }
        catch (InvalidOperationException ex)
        {
            return CreateFailedUpdateStatus(
                baselineStatus,
                message: "update-apply-failed",
                lastError: ex.Message);
        }
    }

    private static async Task<GatewayRuntimeUpdateStatusResponse> RevertUpdateServiceUpdateAsync(
        IUpdateServiceGrpcService updateServiceGrpcService,
        CancellationToken cancellationToken)
    {
        GatewayRuntimeUpdateStatusResponse baselineStatus = await GetUpdateServiceStatusAsync(updateServiceGrpcService, cancellationToken).ConfigureAwait(false);

        try
        {
            UpdateServiceStatusResponse status = await updateServiceGrpcService.RevertUpdateAsync(
                new UpdateServicePingRequest { Message = "update-revert" },
                cancellationToken).ConfigureAwait(false);

            return ToPayload(status);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return CreateAcceptedUpdateStatus(
                baselineStatus,
                message: "update-revert-accepted");
        }
        catch (RpcException ex)
        {
            return CreateUnavailableUpdateStatus(
                message: "update-revert-unavailable",
                lastError: DescribeUnavailable(ex));
        }
        catch (InvalidOperationException ex)
        {
            return CreateFailedUpdateStatus(
                baselineStatus,
                message: "update-revert-failed",
                lastError: ex.Message);
        }
    }

    private static GatewayRuntimeUpdateStatusResponse CreateUnavailableUpdateStatus(
        string message = "UpdateService unavailable",
        string lastError = "UpdateService unavailable")
        => new(
            Ok: false,
            Message: message,
            State: "Unavailable",
            EffectiveReleaseChannel: null,
            EligibilityState: null,
            EligibilityReason: null,
            CurrentRelease: null,
            AvailableRelease: null,
            LastKnownGoodRelease: null,
            LastDiscoveryAttemptUtc: null,
            LastDiscoverySuccessUtc: null,
            ManagerVersion: string.Empty,
            HostReady: false,
            ActiveExecCount: 0,
            LastError: lastError);

    private static GatewayRuntimeUpdateStatusResponse CreateFailedUpdateStatus(
        GatewayRuntimeUpdateStatusResponse baselineStatus,
        string message,
        string lastError)
        => new(
            Ok: false,
            Message: message,
            State: string.IsNullOrWhiteSpace(baselineStatus.State) ? "Current" : baselineStatus.State,
            EffectiveReleaseChannel: baselineStatus.EffectiveReleaseChannel,
            EligibilityState: baselineStatus.EligibilityState,
            EligibilityReason: baselineStatus.EligibilityReason,
            CurrentRelease: baselineStatus.CurrentRelease,
            AvailableRelease: baselineStatus.AvailableRelease,
            LastKnownGoodRelease: baselineStatus.LastKnownGoodRelease,
            LastDiscoveryAttemptUtc: baselineStatus.LastDiscoveryAttemptUtc,
            LastDiscoverySuccessUtc: baselineStatus.LastDiscoverySuccessUtc,
            ManagerVersion: baselineStatus.ManagerVersion,
            HostReady: baselineStatus.HostReady,
            ActiveExecCount: baselineStatus.ActiveExecCount,
            LastError: lastError);

    private static GatewayRuntimeUpdateStatusResponse CreateAcceptedUpdateStatus(
        GatewayRuntimeUpdateStatusResponse baselineStatus,
        string message)
        => new(
            Ok: true,
            Message: message,
            State: "Applying",
            EffectiveReleaseChannel: baselineStatus.EffectiveReleaseChannel,
            EligibilityState: baselineStatus.EligibilityState,
            EligibilityReason: baselineStatus.EligibilityReason,
            CurrentRelease: baselineStatus.CurrentRelease,
            AvailableRelease: baselineStatus.AvailableRelease,
            LastKnownGoodRelease: baselineStatus.LastKnownGoodRelease,
            LastDiscoveryAttemptUtc: baselineStatus.LastDiscoveryAttemptUtc,
            LastDiscoverySuccessUtc: baselineStatus.LastDiscoverySuccessUtc,
            ManagerVersion: baselineStatus.ManagerVersion,
            HostReady: baselineStatus.HostReady,
            ActiveExecCount: baselineStatus.ActiveExecCount,
            LastError: null);

    private static GatewayRuntimeUpdateStatusResponse ToPayload(UpdateServiceStatusResponse status)
        => new(
            Ok: status.Ok,
            Message: status.Message,
            State: string.IsNullOrWhiteSpace(status.WorkflowState) ? "Current" : status.WorkflowState,
            EffectiveReleaseChannel: NullIfEmpty(status.EffectiveReleaseChannel),
            EligibilityState: NullIfEmpty(status.EligibilityState),
            EligibilityReason: NullIfEmpty(status.EligibilityReason),
            CurrentRelease: ToPayload(status.CurrentRelease),
            AvailableRelease: ToPayload(status.AvailableRelease),
            LastKnownGoodRelease: ToPayload(status.LastKnownGoodRelease),
            LastDiscoveryAttemptUtc: NullIfEmpty(status.LastDiscoveryAttemptUtc),
            LastDiscoverySuccessUtc: NullIfEmpty(status.LastDiscoverySuccessUtc),
            ManagerVersion: status.ManagerVersion,
            HostReady: status.HostReady,
            ActiveExecCount: status.ActiveExecCount,
            LastError: NullIfEmpty(status.LastError));

    private static GatewayRuntimeUpdateReleasePayload? ToPayload(UpdateServiceReleaseReferenceMessage? release)
    {
        if (release is null)
        {
            return null;
        }

        return new GatewayRuntimeUpdateReleasePayload(
            ReleaseId: release.ReleaseId,
            Version: release.Version,
            Channel: release.Channel,
            ReleaseFingerprint: release.ReleaseFingerprint,
            SourceUrl: NullIfEmpty(release.SourceUrl),
            PublishedAtUtc: NullIfEmpty(release.PublishedAtUtc),
            Tag: NullIfEmpty(release.Tag));
    }

    private static string? NullIfEmpty(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value;

    private static string DescribeUnavailable(RpcException exception)
        => string.IsNullOrWhiteSpace(exception.Status.Detail)
            ? "UpdateService unavailable"
            : $"Unavailable: {exception.Status.Detail}";
}
