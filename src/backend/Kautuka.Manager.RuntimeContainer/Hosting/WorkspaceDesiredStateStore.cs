using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Channels;

using Kautuka.Manager.Protos;
using Kautuka.Manager.RuntimeContainer.Configuration;
using Kautuka.Manager.RuntimeContainer.Sandbox;
using Kautuka.Manager.RuntimeContainer.Sandbox.Models;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Kautuka.Manager.RuntimeContainer.Hosting;

public sealed class WorkspaceDesiredStateStore
{
    private readonly object sync = new();
    private readonly Channel<WorkspaceReconcileTriggerKind> updateChannel = Channel.CreateBounded<WorkspaceReconcileTriggerKind>(new BoundedChannelOptions(1)
    {
        SingleReader = true,
        SingleWriter = false,
        FullMode = BoundedChannelFullMode.DropWrite,
    });

    private ManagerRuntimeSettings? runtimeSettings;
    private IReadOnlyList<ContainerContext> desiredWorkspaces = [];
    private IReadOnlyList<WorkspaceMcpContainerConnectorDefinition> desiredContainerConnectors = [];
    private IReadOnlyList<WorkspaceSandboxConnectorBindingsDefinition> desiredSandboxConnectorBindings = [];
    private readonly Dictionary<string, WorkspaceMcpContainerRuntimeStatus> connectorStatuses = new(StringComparer.OrdinalIgnoreCase);
    private readonly ILogger<WorkspaceDesiredStateStore> logger;
    private readonly string? statePath;
    private string acceptedPublisherEpoch = string.Empty;
    private string acceptedContentHash = string.Empty;
    private long acceptedDesiredStateVersion;
    private WorkspaceDesiredStateSyncReason acceptedDesiredStateSyncReason;
    private long reconciledDesiredStateVersion;
    private string[] driftActions = [];
    private DateTimeOffset? reconciledAtUtc;
    private WorkspaceReconcileTriggerKind? lastReconcileTriggerKind;
    private WorkspaceDesiredStateLifecyclePhase lifecyclePhase;
    private int reconcileAttemptCount;
    private string? lastFailureClass;
    private IReadOnlyList<WorkspaceMcpContainerConnectorDefinition> pendingPreviousConnectors = [];

    public WorkspaceDesiredStateStore()
        : this(NullLogger<WorkspaceDesiredStateStore>.Instance)
    {
    }

    public WorkspaceDesiredStateStore(WorkspaceRuntimePaths runtimePaths, ILogger<WorkspaceDesiredStateStore> logger)
        : this(logger)
    {
        ArgumentNullException.ThrowIfNull(runtimePaths);
        statePath = Path.Combine(runtimePaths.RuntimeRoot, "manager-desired-state.json");
        LoadPersistedState();
    }

    internal WorkspaceDesiredStateStore(ILogger<WorkspaceDesiredStateStore> logger)
    {
        this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    internal WorkspaceDesiredStateStore(string durableStatePath, ILogger<WorkspaceDesiredStateStore> logger)
        : this(logger)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(durableStatePath);
        statePath = Path.GetFullPath(durableStatePath);
        LoadPersistedState();
    }

    public ChannelReader<WorkspaceReconcileTriggerKind> Updates => updateChannel.Reader;

    public WorkspaceDesiredStateApplyResult ApplyDesiredState(
        long nextDesiredStateVersion,
        WorkspaceDesiredStateSyncReason nextSyncReason,
        ManagerRuntimeSettings nextRuntimeSettings,
        IReadOnlyList<ContainerContext> nextDesiredWorkspaces,
        IReadOnlyList<WorkspaceMcpContainerConnectorDefinition> nextContainerConnectors,
        IReadOnlyList<WorkspaceSandboxConnectorBindingsDefinition> nextSandboxConnectorBindings,
        string publisherEpoch = "gateway")
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(nextDesiredStateVersion);
        ArgumentNullException.ThrowIfNull(nextRuntimeSettings);
        ArgumentNullException.ThrowIfNull(nextDesiredWorkspaces);
        ArgumentNullException.ThrowIfNull(nextContainerConnectors);
        ArgumentNullException.ThrowIfNull(nextSandboxConnectorBindings);
        ArgumentException.ThrowIfNullOrWhiteSpace(publisherEpoch);

        WorkspaceAuthorityPolicy.ValidateDesiredState(nextRuntimeSettings, nextDesiredWorkspaces);
        string contentHash = ComputeContentHash(
            nextRuntimeSettings,
            nextDesiredWorkspaces,
            nextContainerConnectors,
            nextSandboxConnectorBindings);

        WorkspaceDesiredStateApplyResult result;

        lock (sync)
        {
            bool samePublisherEpoch = string.Equals(publisherEpoch, acceptedPublisherEpoch, StringComparison.Ordinal);
            if (samePublisherEpoch && nextDesiredStateVersion < acceptedDesiredStateVersion)
            {
                return WorkspaceDesiredStateApplyResult.IgnoredStale(acceptedDesiredStateVersion);
            }

            if (samePublisherEpoch && nextDesiredStateVersion == acceptedDesiredStateVersion)
            {
                if (!CryptographicOperations.FixedTimeEquals(
                    Convert.FromHexString(contentHash),
                    Convert.FromHexString(acceptedContentHash)))
                {
                    throw new InvalidOperationException("A desired-state version cannot be replayed with different content.");
                }

                bool requiresReconciliation = reconciledDesiredStateVersion != acceptedDesiredStateVersion;
                WorkspaceDesiredStateApplyResult replay = WorkspaceDesiredStateApplyResult.IgnoredReplay(
                    acceptedDesiredStateVersion,
                    pendingPreviousConnectors,
                    requiresReconciliation);
                if (requiresReconciliation)
                {
                    replay = replay with { TriggerEnqueued = updateChannel.Writer.TryWrite(WorkspaceReconcileTriggerKind.DesiredStateAccepted) };
                }

                return replay;
            }

            IReadOnlyList<WorkspaceMcpContainerConnectorDefinition> previousConnectors = [.. desiredContainerConnectors];
            long previousAcceptedVersion = acceptedDesiredStateVersion;
            runtimeSettings = nextRuntimeSettings;
            desiredWorkspaces = EnrichDesiredWorkspaces(nextDesiredStateVersion, nextDesiredWorkspaces, nextSandboxConnectorBindings);
            desiredContainerConnectors = [.. nextContainerConnectors.Select(connector => connector with
            {
                DesiredStateVersion = nextDesiredStateVersion,
                SpecFingerprint = ConnectorContainerSpecBuilder.ComputeFingerprint(connector),
            })];
            desiredSandboxConnectorBindings = [.. nextSandboxConnectorBindings];
            acceptedPublisherEpoch = publisherEpoch;
            acceptedContentHash = contentHash;
            acceptedDesiredStateSyncReason = nextSyncReason;
            UpdateConnectorStatusesForDesiredState(nextContainerConnectors);
            bool hasVersionGap = acceptedDesiredStateVersion > 0 && nextDesiredStateVersion > acceptedDesiredStateVersion + 1;
            acceptedDesiredStateVersion = nextDesiredStateVersion;
            lifecyclePhase = WorkspaceDesiredStateLifecyclePhase.Accepted;
            reconcileAttemptCount = 0;
            lastFailureClass = null;
            pendingPreviousConnectors = previousConnectors;
            PersistState();
            result = WorkspaceDesiredStateApplyResult.AcceptedNewVersion(acceptedDesiredStateVersion, previousAcceptedVersion, hasVersionGap, previousConnectors);
        }

        if (result.Accepted)
        {
            result = result with { TriggerEnqueued = updateChannel.Writer.TryWrite(WorkspaceReconcileTriggerKind.DesiredStateAccepted) };
        }

        return result;
    }

    private void UpdateConnectorStatusesForDesiredState(IReadOnlyList<WorkspaceMcpContainerConnectorDefinition> nextContainerConnectors)
    {
        var incomingIds = new HashSet<string>(nextContainerConnectors.Select(static connector => connector.ConnectorId), StringComparer.OrdinalIgnoreCase);
        foreach (WorkspaceMcpContainerConnectorDefinition connector in nextContainerConnectors)
        {
            if (!connectorStatuses.TryGetValue(connector.ConnectorId, out WorkspaceMcpContainerRuntimeStatus? status))
            {
                connectorStatuses[connector.ConnectorId] = new WorkspaceMcpContainerRuntimeStatus(
                    connector.ConnectorId,
                    WorkspaceMcpContainerLifecycleState.Imported,
                    DateTimeOffset.UtcNow,
                    "connector-observed");
                continue;
            }

            if (!connector.Enabled)
            {
                connectorStatuses[connector.ConnectorId] = status! with
                {
                    State = WorkspaceMcpContainerLifecycleState.Failed,
                    UpdatedAtUtc = DateTimeOffset.UtcNow,
                    LastEvent = "connector-disabled"
                };
            }
        }

        string[] removed = [.. connectorStatuses.Keys.Where(id => !incomingIds.Contains(id))];
        foreach (string connectorId in removed)
        {
            WorkspaceMcpContainerRuntimeStatus status = connectorStatuses[connectorId];
            connectorStatuses[connectorId] = status with
            {
                State = WorkspaceMcpContainerLifecycleState.Deleted,
                UpdatedAtUtc = DateTimeOffset.UtcNow,
                LastEvent = "connector-removed"
            };
        }
    }

    public bool SignalReconcile(WorkspaceReconcileTriggerKind triggerKind = WorkspaceReconcileTriggerKind.OperatorRequested)
        => updateChannel.Writer.TryWrite(triggerKind);

    public WorkspaceDesiredStateReconcileCursor ReconcileCursor
    {
        get
        {
            lock (sync)
            {
                return new WorkspaceDesiredStateReconcileCursor(
                    acceptedDesiredStateVersion,
                    acceptedDesiredStateSyncReason,
                    reconciledDesiredStateVersion,
                    reconciledAtUtc,
                    lastReconcileTriggerKind);
            }
        }
    }

    public bool TryMarkReconciled(long version, WorkspaceReconcileTriggerKind triggerKind, DateTimeOffset completedAtUtc)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(version);

        lock (sync)
        {
            if (version <= 0 || version != acceptedDesiredStateVersion)
            {
                return false;
            }

            if (reconciledDesiredStateVersion > version)
            {
                return false;
            }

            reconciledDesiredStateVersion = version;
            reconciledAtUtc = completedAtUtc;
            lastReconcileTriggerKind = triggerKind;
            lifecyclePhase = WorkspaceDesiredStateLifecyclePhase.Reconciled;
            lastFailureClass = null;
            pendingPreviousConnectors = [];
            PersistState();
            return true;
        }
    }

    public bool TryMarkReconcileStarted(long version)
    {
        lock (sync)
        {
            if (version <= 0 || version != acceptedDesiredStateVersion)
            {
                return false;
            }

            lifecyclePhase = WorkspaceDesiredStateLifecyclePhase.Reconciling;
            reconcileAttemptCount++;
            lastFailureClass = null;
            PersistState();
            return true;
        }
    }

    public bool TryMarkReconcileFailed(long version, string failureClass)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(failureClass);
        lock (sync)
        {
            if (version <= 0 || version != acceptedDesiredStateVersion)
            {
                return false;
            }

            lifecyclePhase = WorkspaceDesiredStateLifecyclePhase.Failed;
            lastFailureClass = failureClass;
            PersistState();
            return true;
        }
    }

    public bool TryResolveWorkspace(string scopeType, string scopeId, string? userId, out ContainerContext? context)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scopeType);
        ArgumentException.ThrowIfNullOrWhiteSpace(scopeId);
        lock (sync)
        {
            ContainerContext[] matches = [.. desiredWorkspaces.Where(workspace =>
                string.Equals(workspace.ScopeType, scopeType, StringComparison.OrdinalIgnoreCase)
                && string.Equals(workspace.ScopeId, scopeId, StringComparison.OrdinalIgnoreCase))];
            if (!string.IsNullOrWhiteSpace(userId))
            {
                context = matches.FirstOrDefault(workspace =>
                    string.Equals(workspace.UserId, userId, StringComparison.Ordinal));
                if (context is not null)
                {
                    return true;
                }
            }

            context = matches.FirstOrDefault(workspace => string.IsNullOrWhiteSpace(workspace.UserId));
            return context is not null;
        }
    }

    public IReadOnlyList<string> DriftActions
    {
        get
        {
            lock (sync)
            {
                return driftActions;
            }
        }
    }

    public void ReplaceDriftActions(IReadOnlyList<string> actions)
    {
        ArgumentNullException.ThrowIfNull(actions);
        lock (sync)
        {
            driftActions = [.. actions.Where(static action => !string.IsNullOrWhiteSpace(action)).Take(12)];
        }
    }

    public WorkspaceDesiredStateSnapshot Snapshot
    {
        get
        {
            lock (sync)
            {
                return new WorkspaceDesiredStateSnapshot(
                    runtimeSettings,
                    desiredWorkspaces,
                    desiredContainerConnectors,
                    desiredSandboxConnectorBindings,
                    acceptedDesiredStateVersion,
                    acceptedDesiredStateSyncReason,
                    reconciledDesiredStateVersion,
                    reconciledAtUtc,
                    lastReconcileTriggerKind,
                    acceptedPublisherEpoch,
                    acceptedContentHash,
                    lifecyclePhase,
                    reconcileAttemptCount,
                    lastFailureClass);
            }
        }
    }

    public bool TryTransitionConnectorState(string connectorId, WorkspaceMcpContainerLifecycleState nextState, string eventName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectorId);
        ArgumentException.ThrowIfNullOrWhiteSpace(eventName);

        lock (sync)
        {
            if (!connectorStatuses.TryGetValue(connectorId, out WorkspaceMcpContainerRuntimeStatus? currentStatus))
            {
                return false;
            }

            if (!CanTransition(currentStatus.State, nextState))
            {
                return false;
            }

            connectorStatuses[connectorId] = currentStatus with
            {
                State = nextState,
                UpdatedAtUtc = DateTimeOffset.UtcNow,
                LastEvent = eventName,
            };

            return true;
        }
    }

    public WorkspaceMcpContainerRuntimeStatus? GetConnectorStatus(string connectorId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectorId);

        lock (sync)
        {
            return connectorStatuses.TryGetValue(connectorId, out WorkspaceMcpContainerRuntimeStatus? status)
                ? status
                : null;
        }
    }

    public (int DegradedCount, int FailedCount) RuntimeStatusCounts
    {
        get
        {
            lock (sync)
            {
                int failed = connectorStatuses.Values.Count(static status => status.State == WorkspaceMcpContainerLifecycleState.Failed);
                if (lifecyclePhase == WorkspaceDesiredStateLifecyclePhase.Failed)
                {
                    failed++;
                }

                int degraded = connectorStatuses.Values.Count(static status => status.State is WorkspaceMcpContainerLifecycleState.Restarting or WorkspaceMcpContainerLifecycleState.IdleStopped);
                return (degraded, failed);
            }
        }
    }

    private static bool CanTransition(WorkspaceMcpContainerLifecycleState current, WorkspaceMcpContainerLifecycleState next)
        => current switch
        {
            WorkspaceMcpContainerLifecycleState.Imported => next is WorkspaceMcpContainerLifecycleState.Materialized
                or WorkspaceMcpContainerLifecycleState.Starting
                or WorkspaceMcpContainerLifecycleState.Failed
                or WorkspaceMcpContainerLifecycleState.Deleted,
            WorkspaceMcpContainerLifecycleState.Materialized => next is WorkspaceMcpContainerLifecycleState.Starting
                or WorkspaceMcpContainerLifecycleState.Ready
                or WorkspaceMcpContainerLifecycleState.Failed
                or WorkspaceMcpContainerLifecycleState.Deleted,
            WorkspaceMcpContainerLifecycleState.Starting => next is WorkspaceMcpContainerLifecycleState.Ready
                or WorkspaceMcpContainerLifecycleState.Failed
                or WorkspaceMcpContainerLifecycleState.Deleted,
            WorkspaceMcpContainerLifecycleState.Ready => next is WorkspaceMcpContainerLifecycleState.IdleStopped
                or WorkspaceMcpContainerLifecycleState.Restarting
                or WorkspaceMcpContainerLifecycleState.Failed
                or WorkspaceMcpContainerLifecycleState.Deleted,
            WorkspaceMcpContainerLifecycleState.IdleStopped => next is WorkspaceMcpContainerLifecycleState.Starting
                or WorkspaceMcpContainerLifecycleState.Restarting
                or WorkspaceMcpContainerLifecycleState.Deleted,
            WorkspaceMcpContainerLifecycleState.Restarting => next is WorkspaceMcpContainerLifecycleState.Starting
                or WorkspaceMcpContainerLifecycleState.Ready
                or WorkspaceMcpContainerLifecycleState.Failed
                or WorkspaceMcpContainerLifecycleState.Deleted,
            WorkspaceMcpContainerLifecycleState.Failed => next is WorkspaceMcpContainerLifecycleState.Restarting
                or WorkspaceMcpContainerLifecycleState.Starting
                or WorkspaceMcpContainerLifecycleState.Materialized,
            WorkspaceMcpContainerLifecycleState.Deleted => false,
            _ => false,
        };

    private void LoadPersistedState()
    {
        if (statePath is null || !File.Exists(statePath))
        {
            return;
        }

        PersistedWorkspaceDesiredState persisted = JsonSerializer.Deserialize(
            File.ReadAllBytes(statePath),
            WorkspaceDesiredStateJsonContext.Default.PersistedWorkspaceDesiredState)
            ?? throw new InvalidOperationException("Manager desired-state ledger is empty.");
        if (persisted.SchemaVersion != 1)
        {
            throw new InvalidOperationException("Manager desired-state ledger schema is unsupported.");
        }

        runtimeSettings = persisted.RuntimeSettings;
        desiredWorkspaces = persisted.DesiredWorkspaces;
        desiredContainerConnectors = persisted.DesiredContainerConnectors;
        desiredSandboxConnectorBindings = persisted.DesiredSandboxConnectorBindings;
        acceptedPublisherEpoch = persisted.PublisherEpoch;
        acceptedContentHash = persisted.ContentHash;
        acceptedDesiredStateVersion = persisted.AcceptedVersion;
        acceptedDesiredStateSyncReason = persisted.AcceptedSyncReason;
        reconciledDesiredStateVersion = persisted.ReconciledVersion;
        reconciledAtUtc = persisted.ReconciledAtUtc;
        lastReconcileTriggerKind = persisted.LastReconcileTriggerKind;
        lifecyclePhase = persisted.LifecyclePhase;
        reconcileAttemptCount = persisted.ReconcileAttemptCount;
        lastFailureClass = persisted.LastFailureClass;
        pendingPreviousConnectors = persisted.PendingPreviousConnectors;
        UpdateConnectorStatusesForDesiredState(desiredContainerConnectors);
        if (acceptedDesiredStateVersion > reconciledDesiredStateVersion)
        {
            _ = updateChannel.Writer.TryWrite(WorkspaceReconcileTriggerKind.RecoveryRetry);
        }
    }

    private void PersistState()
    {
        if (statePath is null)
        {
            return;
        }

        var persisted = new PersistedWorkspaceDesiredState(
            1,
            acceptedPublisherEpoch,
            acceptedContentHash,
            acceptedDesiredStateVersion,
            acceptedDesiredStateSyncReason,
            runtimeSettings,
            [.. desiredWorkspaces],
            [.. desiredContainerConnectors],
            [.. desiredSandboxConnectorBindings],
            reconciledDesiredStateVersion,
            reconciledAtUtc,
            lastReconcileTriggerKind,
            lifecyclePhase,
            reconcileAttemptCount,
            lastFailureClass,
            [.. pendingPreviousConnectors]);
        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(
            persisted,
            WorkspaceDesiredStateJsonContext.Default.PersistedWorkspaceDesiredState);
        string tempPath = statePath + ".tmp";
        Directory.CreateDirectory(Path.GetDirectoryName(statePath)!);
        using (var stream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
        {
            stream.Write(payload);
            stream.Flush(flushToDisk: true);
        }

        File.Move(tempPath, statePath, overwrite: true);
    }

    private static string ComputeContentHash(
        ManagerRuntimeSettings settings,
        IReadOnlyList<ContainerContext> workspaces,
        IReadOnlyList<WorkspaceMcpContainerConnectorDefinition> connectors,
        IReadOnlyList<WorkspaceSandboxConnectorBindingsDefinition> bindings)
    {
        var content = new WorkspaceDesiredStateContent(
            settings,
            [.. workspaces
                .OrderBy(static workspace => workspace.ScopeType, StringComparer.Ordinal)
                .ThenBy(static workspace => workspace.ScopeId, StringComparer.Ordinal)
                .ThenBy(static workspace => workspace.UserId ?? string.Empty, StringComparer.Ordinal)
                .Select(static workspace => workspace with
                {
                    BaseCapabilities = [.. workspace.BaseCapabilities.Order(StringComparer.Ordinal)],
                    ContainerEnvironment = workspace.ContainerEnvironment
                        .OrderBy(static pair => pair.Key, StringComparer.Ordinal)
                        .ToDictionary(static pair => pair.Key, static pair => pair.Value, StringComparer.Ordinal),
                })],
            [.. connectors
                .OrderBy(static connector => connector.ConnectorId, StringComparer.Ordinal)
                .Select(static connector => connector with
                {
                    Environment = [.. connector.Environment.OrderBy(static pair => pair.Key, StringComparer.Ordinal)],
                })],
            [.. bindings
                .OrderBy(static binding => binding.ScopeType, StringComparer.Ordinal)
                .ThenBy(static binding => binding.ScopeId, StringComparer.Ordinal)
                .Select(static binding => binding with
                {
                    ConnectorIds = [.. binding.ConnectorIds.Order(StringComparer.Ordinal)],
                })]);
        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(
            content,
            WorkspaceDesiredStateJsonContext.Default.WorkspaceDesiredStateContent);
        return Convert.ToHexString(SHA256.HashData(payload));
    }

    private static IReadOnlyList<ContainerContext> EnrichDesiredWorkspaces(
        long desiredStateVersion,
        IReadOnlyList<ContainerContext> workspaces,
        IReadOnlyList<WorkspaceSandboxConnectorBindingsDefinition> bindings)
    {
        Dictionary<string, IReadOnlyList<string>> connectorIdsByWorkspace = bindings.ToDictionary(
            static binding => string.Join('|', binding.ScopeType, binding.ScopeId),
            static binding => binding.ConnectorIds,
            StringComparer.OrdinalIgnoreCase);
        return [.. workspaces.Select(workspace =>
        {
            connectorIdsByWorkspace.TryGetValue(string.Join('|', workspace.ScopeType, workspace.ScopeId), out IReadOnlyList<string>? connectorIds);
            connectorIds ??= [];
            return workspace with
            {
                DesiredStateVersion = desiredStateVersion,
                SpecFingerprint = WorkspaceContainerSpecFingerprint.Compute(workspace, connectorIds),
            };
        })];
    }

}

/// <summary>
/// Runtime settings composed from Gateway desired-state input.
/// </summary>
public sealed record ManagerRuntimeSettings(
    WorkspaceDefinition Workspace,
    SandboxRuntimeDefinition Sandbox,
    WorkspaceTelemetryDefinition Telemetry);

/// <summary>
/// Definition of a containerized MCP connector from the desired-state update.
/// </summary>
public sealed record WorkspaceMcpContainerConnectorDefinition(
    string ConnectorId,
    string Image,
    string Command,
    IReadOnlyList<string> Args,
    bool Enabled,
    bool IsInternalSystemConnector,
    string? BundlePath,
    string? ServerType,
    IReadOnlyList<KeyValuePair<string, string>> Environment,
    int ReadinessTimeoutSeconds,
    int JitCooldownSeconds,
    string? McpProtocolVersion = null)
{
    public long DesiredStateVersion { get; init; }

    public string SpecFingerprint { get; init; } = string.Empty;
}

public sealed record WorkspaceSandboxConnectorBindingsDefinition(
    string ScopeType,
    string ScopeId,
    IReadOnlyList<string> ConnectorIds);

public sealed record WorkspaceDesiredStateSnapshot(
    ManagerRuntimeSettings? RuntimeSettings,
    IReadOnlyList<ContainerContext> DesiredWorkspaces,
    IReadOnlyList<WorkspaceMcpContainerConnectorDefinition> DesiredContainerConnectors,
    IReadOnlyList<WorkspaceSandboxConnectorBindingsDefinition> DesiredSandboxConnectorBindings,
    long AcceptedVersion,
    WorkspaceDesiredStateSyncReason AcceptedSyncReason,
    long ReconciledVersion,
    DateTimeOffset? ReconciledAtUtc,
    WorkspaceReconcileTriggerKind? LastReconcileTriggerKind,
    string PublisherEpoch,
    string ContentHash,
    WorkspaceDesiredStateLifecyclePhase LifecyclePhase,
    int ReconcileAttemptCount,
    string? LastFailureClass)
{
    public long Version => AcceptedVersion;

    public WorkspaceDesiredStateSyncReason SyncReason => AcceptedSyncReason;
}

public sealed record WorkspaceDesiredStateReconcileCursor(
    long AcceptedVersion,
    WorkspaceDesiredStateSyncReason AcceptedSyncReason,
    long ReconciledVersion,
    DateTimeOffset? ReconciledAtUtc,
    WorkspaceReconcileTriggerKind? LastReconcileTriggerKind);

public sealed record WorkspaceDesiredStateApplyResult(
    long Version,
    bool Accepted,
    bool IsReplay,
    bool IsStale,
    bool HasVersionGap,
    IReadOnlyList<WorkspaceMcpContainerConnectorDefinition> PreviousConnectors,
    long PreviousAcceptedVersion,
    bool TriggerEnqueued,
    bool RequiresReconciliation)
{
    public static WorkspaceDesiredStateApplyResult AcceptedNewVersion(long version, long previousAcceptedVersion, bool hasVersionGap, IReadOnlyList<WorkspaceMcpContainerConnectorDefinition> previousConnectors)
        => new(version, true, false, false, hasVersionGap, previousConnectors, previousAcceptedVersion, false, true);

    public static WorkspaceDesiredStateApplyResult IgnoredReplay(
        long version,
        IReadOnlyList<WorkspaceMcpContainerConnectorDefinition> previousConnectors,
        bool requiresReconciliation)
        => new(version, false, true, false, false, previousConnectors, version, false, requiresReconciliation);

    public static WorkspaceDesiredStateApplyResult IgnoredStale(long version)
        => new(version, false, false, true, false, [], version, false, false);
}

public enum WorkspaceDesiredStateLifecyclePhase
{
    Unavailable = 0,
    Accepted = 1,
    Reconciling = 2,
    Reconciled = 3,
    Failed = 4,
}

public enum WorkspaceReconcileTriggerKind
{
    DesiredStateAccepted = 0,
    ExecIdle = 1,
    DriftAuditTimer = 2,
    RecoveryRetry = 3,
    HostLifecycleReconcile = 4,
    OperatorRequested = 5,
}

public enum WorkspaceMcpContainerLifecycleState
{
    Imported = 0,
    Materialized = 1,
    Starting = 2,
    Ready = 3,
    IdleStopped = 4,
    Restarting = 5,
    Failed = 6,
    Deleted = 7,
}

public sealed record WorkspaceMcpContainerRuntimeStatus(
    string ConnectorId,
    WorkspaceMcpContainerLifecycleState State,
    DateTimeOffset UpdatedAtUtc,
    string LastEvent);

internal sealed record WorkspaceDesiredStateContent(
    ManagerRuntimeSettings RuntimeSettings,
    ContainerContext[] DesiredWorkspaces,
    WorkspaceMcpContainerConnectorDefinition[] DesiredContainerConnectors,
    WorkspaceSandboxConnectorBindingsDefinition[] DesiredSandboxConnectorBindings);

internal sealed record PersistedWorkspaceDesiredState(
    int SchemaVersion,
    string PublisherEpoch,
    string ContentHash,
    long AcceptedVersion,
    WorkspaceDesiredStateSyncReason AcceptedSyncReason,
    ManagerRuntimeSettings? RuntimeSettings,
    ContainerContext[] DesiredWorkspaces,
    WorkspaceMcpContainerConnectorDefinition[] DesiredContainerConnectors,
    WorkspaceSandboxConnectorBindingsDefinition[] DesiredSandboxConnectorBindings,
    long ReconciledVersion,
    DateTimeOffset? ReconciledAtUtc,
    WorkspaceReconcileTriggerKind? LastReconcileTriggerKind,
    WorkspaceDesiredStateLifecyclePhase LifecyclePhase,
    int ReconcileAttemptCount,
    string? LastFailureClass,
    WorkspaceMcpContainerConnectorDefinition[] PendingPreviousConnectors);

[JsonSerializable(typeof(WorkspaceDesiredStateContent))]
[JsonSerializable(typeof(PersistedWorkspaceDesiredState))]
internal sealed partial class WorkspaceDesiredStateJsonContext : JsonSerializerContext;
