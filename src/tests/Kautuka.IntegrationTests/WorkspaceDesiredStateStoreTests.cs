using Kautuka.Manager.RuntimeContainer.Configuration;
using Kautuka.Manager.RuntimeContainer.Hosting;
using Kautuka.Manager.RuntimeContainer.Sandbox.Models;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

using ManagerSandboxRuntimeDefinition = Kautuka.Manager.RuntimeContainer.Configuration.SandboxRuntimeDefinition;
using ManagerWorkspaceDefinition = Kautuka.Manager.RuntimeContainer.Configuration.WorkspaceDefinition;

namespace Kautuka.IntegrationTests;

public sealed class WorkspaceDesiredStateStoreTests
{
    [Fact]
    public void DesiredStateRejectsDuplicateAgentWorkspaceScopeAcrossCallerIdentities()
    {
        ContainerContext workspace = CreateWorkspace("scope-a");
        var store = new WorkspaceDesiredStateStore();

        InvalidOperationException error = Assert.Throws<InvalidOperationException>(() => store.ApplyDesiredState(
            1,
            Kautuka.Manager.Protos.WorkspaceDesiredStateSyncReason.Bootstrap,
            CreateRuntimeSettings(),
            [workspace, workspace with { UserId = "caller-a" }],
            [],
            []));

        Assert.Equal("Desired state contains two workspaces for the same host path.", error.Message);
    }

    [Fact]
    public void DesiredStateAcceptsPerUserHostPathsForOneAgentScope()
    {
        string root = Path.Combine(Path.GetTempPath(), $"kautuka-manager-users-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            ContainerContext ash = CreateWorkspace("shared-brain", root) with
            {
                UserId = "user-ash",
                HostWorkspacePath = Path.Combine(root, "shared-brain", "user-ash"),
            };
            ContainerContext bee = CreateWorkspace("shared-brain", root) with
            {
                UserId = "user-bee",
                HostWorkspacePath = Path.Combine(root, "shared-brain", "user-bee"),
            };
            var store = new WorkspaceDesiredStateStore();

            WorkspaceDesiredStateApplyResult applied = store.ApplyDesiredState(
                1,
                Kautuka.Manager.Protos.WorkspaceDesiredStateSyncReason.Bootstrap,
                CreateRuntimeSettings(root),
                [ash, bee],
                [],
                []);

            Assert.True(applied.Accepted);
            Assert.True(store.TryResolveWorkspace("agent", "shared-brain", "user-ash", out ContainerContext? resolvedAsh));
            Assert.True(store.TryResolveWorkspace("agent", "shared-brain", "user-bee", out ContainerContext? resolvedBee));
            Assert.EndsWith(Path.Combine("shared-brain", "user-ash"), resolvedAsh!.HostWorkspacePath, StringComparison.OrdinalIgnoreCase);
            Assert.EndsWith(Path.Combine("shared-brain", "user-bee"), resolvedBee!.HostWorkspacePath, StringComparison.OrdinalIgnoreCase);
            foreach (string? missingUser in new string?[] { "user-missing", null, "", " ", "USER-ASH" })
            {
                Assert.False(store.TryResolveWorkspace("agent", "shared-brain", missingUser, out ContainerContext? missing));
                Assert.Null(missing);
            }
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void NonSharedWorkspaceRetainsNullUserFallback()
    {
        ContainerContext workspace = CreateWorkspace("private-brain") with { UserId = null };
        var store = new WorkspaceDesiredStateStore();
        _ = store.ApplyDesiredState(1, Kautuka.Manager.Protos.WorkspaceDesiredStateSyncReason.Bootstrap,
            CreateRuntimeSettings(), [workspace], [], []);
        foreach (string? userId in new string?[] { "user-a", null, "" })
        {
            Assert.True(store.TryResolveWorkspace("agent", "private-brain", userId, out ContainerContext? resolved));
            Assert.Equal(workspace.HostWorkspacePath, resolved!.HostWorkspacePath);
        }
    }

    [Fact]
    public void RuntimePathsAnchorManagerStateUnderConfiguredApplianceRoot()
    {
        string root = Path.Combine(Path.GetTempPath(), $"kautuka-manager-paths-{Guid.NewGuid():N}");
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Kautuka:ContainerStateRoot"] = root,
                ["Kautuka:Mode"] = "dev",
            })
            .Build();

        try
        {
            var paths = new WorkspaceRuntimePaths(
                configuration,
                new TestHostEnvironment(),
                NullLogger<WorkspaceRuntimePaths>.Instance);

            Assert.Equal(Path.Combine(Path.GetFullPath(root), "manager"), paths.RuntimeRoot);
            Assert.Equal(Path.GetFullPath(root), paths.SharedDataRoot);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public async Task ApplyDesiredStateAcceptsIncomingVersionAndReturnsPreviousConnectors()
    {
        var store = new WorkspaceDesiredStateStore();
        ManagerRuntimeSettings runtimeSettings = CreateRuntimeSettings();
        WorkspaceMcpContainerConnectorDefinition firstConnector = CreateConnector("connector-a");
        WorkspaceMcpContainerConnectorDefinition secondConnector = CreateConnector("connector-b");

        WorkspaceDesiredStateApplyResult firstApply = store.ApplyDesiredState(
            7,
            Kautuka.Manager.Protos.WorkspaceDesiredStateSyncReason.Bootstrap,
            runtimeSettings,
            [CreateWorkspace("scope-a")],
            [firstConnector],
            []);

        WorkspaceDesiredStateApplyResult secondApply = store.ApplyDesiredState(
            9,
            Kautuka.Manager.Protos.WorkspaceDesiredStateSyncReason.Change,
            runtimeSettings,
            [CreateWorkspace("scope-b")],
            [secondConnector],
            []);

        Assert.True(firstApply.Accepted);
        Assert.Equal(7, firstApply.Version);
        Assert.Empty(firstApply.PreviousConnectors);
        Assert.True(secondApply.Accepted);
        Assert.True(secondApply.HasVersionGap);
        Assert.Equal(9, secondApply.Version);
        Assert.Single(secondApply.PreviousConnectors, static connector => connector.ConnectorId == "connector-a");

        WorkspaceDesiredStateSnapshot snapshot = store.Snapshot;
        Assert.Equal(9, snapshot.Version);
        Assert.Equal(0, snapshot.ReconciledVersion);
        Assert.Single(snapshot.DesiredWorkspaces, static workspace => workspace.ScopeId == "scope-b");
        Assert.Single(snapshot.DesiredContainerConnectors, static connector => connector.ConnectorId == "connector-b");
        Assert.NotNull(snapshot.RuntimeSettings);

        Assert.True(await store.Updates.WaitToReadAsync(TestContext.Current.CancellationToken).ConfigureAwait(true));
        Assert.True(store.Updates.TryRead(out WorkspaceReconcileTriggerKind triggerKind));
        Assert.Equal(WorkspaceReconcileTriggerKind.DesiredStateAccepted, triggerKind);
        Assert.False(store.Updates.TryRead(out _));
    }

    [Fact]
    public void ApplyDesiredStateAcceptsIdenticalReplayAndRejectsVersionMutationAndStaleVersions()
    {
        var store = new WorkspaceDesiredStateStore();
        ManagerRuntimeSettings runtimeSettings = CreateRuntimeSettings();

        WorkspaceDesiredStateApplyResult accepted = store.ApplyDesiredState(
            5,
            Kautuka.Manager.Protos.WorkspaceDesiredStateSyncReason.Change,
            runtimeSettings,
            [CreateWorkspace("scope-a")],
            [CreateConnector("connector-a")],
            []);

        WorkspaceDesiredStateApplyResult replay = store.ApplyDesiredState(
            5,
            Kautuka.Manager.Protos.WorkspaceDesiredStateSyncReason.Bootstrap,
            runtimeSettings,
            [CreateWorkspace("scope-a")],
            [CreateConnector("connector-a")],
            []);

        Assert.Throws<InvalidOperationException>(() => store.ApplyDesiredState(
            5,
            Kautuka.Manager.Protos.WorkspaceDesiredStateSyncReason.Change,
            runtimeSettings,
            [CreateWorkspace("scope-b")],
            [CreateConnector("connector-b")],
            []));

        WorkspaceDesiredStateApplyResult stale = store.ApplyDesiredState(
            4,
            Kautuka.Manager.Protos.WorkspaceDesiredStateSyncReason.Change,
            runtimeSettings,
            [CreateWorkspace("scope-c")],
            [CreateConnector("connector-c")],
            []);

        Assert.True(accepted.Accepted);
        Assert.True(replay.IsReplay);
        Assert.False(replay.Accepted);
        Assert.True(stale.IsStale);
        Assert.False(stale.Accepted);

        WorkspaceDesiredStateSnapshot snapshot = store.Snapshot;
        Assert.Equal(5, snapshot.Version);
        Assert.Equal(0, snapshot.ReconciledVersion);
        Assert.Single(snapshot.DesiredWorkspaces, static workspace => workspace.ScopeId == "scope-a");
    }

    [Fact]
    public void SignalReconcileCoalescesPendingWakeups()
    {
        var store = new WorkspaceDesiredStateStore();

        _ = store.SignalReconcile(WorkspaceReconcileTriggerKind.HostLifecycleReconcile);
        _ = store.SignalReconcile(WorkspaceReconcileTriggerKind.OperatorRequested);
        _ = store.SignalReconcile(WorkspaceReconcileTriggerKind.ExecIdle);

        Assert.True(store.Updates.TryRead(out WorkspaceReconcileTriggerKind triggerKind));
        Assert.Equal(WorkspaceReconcileTriggerKind.HostLifecycleReconcile, triggerKind);
        Assert.False(store.Updates.TryRead(out _));
    }

    [Fact]
    public void MarkReconciledTracksSeparateCursorFromAcceptedVersion()
    {
        var store = new WorkspaceDesiredStateStore();
        ManagerRuntimeSettings runtimeSettings = CreateRuntimeSettings();

        WorkspaceDesiredStateApplyResult accepted = store.ApplyDesiredState(
            5,
            Kautuka.Manager.Protos.WorkspaceDesiredStateSyncReason.Change,
            runtimeSettings,
            [CreateWorkspace("scope-a")],
            [CreateConnector("connector-a")],
            []);

        Assert.True(accepted.Accepted);
        WorkspaceDesiredStateReconcileCursor cursorBefore = store.ReconcileCursor;
        Assert.Equal(5, cursorBefore.AcceptedVersion);
        Assert.Equal(0, cursorBefore.ReconciledVersion);

        bool marked = store.TryMarkReconciled(5, WorkspaceReconcileTriggerKind.DesiredStateAccepted, DateTimeOffset.UtcNow);
        Assert.True(marked);

        WorkspaceDesiredStateReconcileCursor cursorAfter = store.ReconcileCursor;
        Assert.Equal(5, cursorAfter.AcceptedVersion);
        Assert.Equal(5, cursorAfter.ReconciledVersion);
        Assert.Equal(WorkspaceReconcileTriggerKind.DesiredStateAccepted, cursorAfter.LastReconcileTriggerKind);

        WorkspaceDesiredStateApplyResult nextAccepted = store.ApplyDesiredState(
            7,
            Kautuka.Manager.Protos.WorkspaceDesiredStateSyncReason.Change,
            runtimeSettings,
            [CreateWorkspace("scope-b")],
            [CreateConnector("connector-b")],
            []);

        Assert.True(nextAccepted.Accepted);

        WorkspaceDesiredStateReconcileCursor cursorAfterNewAccept = store.ReconcileCursor;
        Assert.Equal(7, cursorAfterNewAccept.AcceptedVersion);
        Assert.Equal(5, cursorAfterNewAccept.ReconciledVersion);
    }

    [Fact]
    public void LocalReconcileTriggersDoNotMutateAcceptedState()
    {
        var store = new WorkspaceDesiredStateStore();

        WorkspaceDesiredStateApplyResult accepted = store.ApplyDesiredState(
            3,
            Kautuka.Manager.Protos.WorkspaceDesiredStateSyncReason.Change,
            CreateRuntimeSettings(),
            [CreateWorkspace("scope-a")],
            [CreateConnector("connector-a")],
            []);

        Assert.True(accepted.Accepted);
        Assert.True(store.Updates.TryRead(out _));

        _ = store.SignalReconcile(WorkspaceReconcileTriggerKind.ExecIdle);
        Assert.True(store.Updates.TryRead(out WorkspaceReconcileTriggerKind triggerKind));
        Assert.Equal(WorkspaceReconcileTriggerKind.ExecIdle, triggerKind);

        WorkspaceDesiredStateReconcileCursor cursor = store.ReconcileCursor;
        Assert.Equal(3, cursor.AcceptedVersion);
        Assert.Equal(0, cursor.ReconciledVersion);
    }

    [Fact]
    public void DesiredStateAndIncompleteReconciliationSurviveRestart()
    {
        string root = Path.Combine(Path.GetTempPath(), $"kautuka-manager-ledger-{Guid.NewGuid():N}");
        string ledgerPath = Path.Combine(root, "desired-state.json");
        Directory.CreateDirectory(root);
        try
        {
            var store = new WorkspaceDesiredStateStore(ledgerPath, NullLogger<WorkspaceDesiredStateStore>.Instance);

            WorkspaceDesiredStateApplyResult apply = store.ApplyDesiredState(
                11,
                Kautuka.Manager.Protos.WorkspaceDesiredStateSyncReason.Change,
                CreateRuntimeSettings(root),
                [CreateWorkspace("scope-a", root)],
                [CreateConnector("connector-a")],
                [],
                "gateway-epoch-a");

            Assert.True(apply.Accepted);
            Assert.True(store.TryMarkReconcileStarted(11));

            var restarted = new WorkspaceDesiredStateStore(ledgerPath, NullLogger<WorkspaceDesiredStateStore>.Instance);
            WorkspaceDesiredStateSnapshot snapshot = restarted.Snapshot;

            Assert.Equal(11, snapshot.Version);
            Assert.Equal(0, snapshot.ReconciledVersion);
            Assert.Equal("gateway-epoch-a", snapshot.PublisherEpoch);
            Assert.Equal(WorkspaceDesiredStateLifecyclePhase.Reconciling, snapshot.LifecyclePhase);
            Assert.Equal(1, snapshot.ReconcileAttemptCount);
            Assert.Single(snapshot.DesiredWorkspaces);
            Assert.True(restarted.Updates.TryRead(out WorkspaceReconcileTriggerKind trigger));
            Assert.Equal(WorkspaceReconcileTriggerKind.RecoveryRetry, trigger);

            WorkspaceDesiredStateApplyResult replay = restarted.ApplyDesiredState(
                11,
                Kautuka.Manager.Protos.WorkspaceDesiredStateSyncReason.Change,
                CreateRuntimeSettings(root),
                [CreateWorkspace("scope-a", root)],
                [CreateConnector("connector-a")],
                [],
                "gateway-epoch-a");
            Assert.True(replay.IsReplay);
            Assert.True(replay.RequiresReconciliation);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void PublisherEpochFencesVersionOrderingAndSameVersionContent()
    {
        string root = Path.Combine(Path.GetTempPath(), $"kautuka-manager-epoch-{Guid.NewGuid():N}");
        var store = new WorkspaceDesiredStateStore();
        _ = store.ApplyDesiredState(10, Kautuka.Manager.Protos.WorkspaceDesiredStateSyncReason.Change,
            CreateRuntimeSettings(root), [CreateWorkspace("scope-a", root)], [], [], "epoch-a");

        WorkspaceDesiredStateApplyResult resetPublisher = store.ApplyDesiredState(1,
            Kautuka.Manager.Protos.WorkspaceDesiredStateSyncReason.Bootstrap,
            CreateRuntimeSettings(root), [CreateWorkspace("scope-b", root)], [], [], "epoch-b");

        Assert.True(resetPublisher.Accepted);
        Assert.Equal(1, store.Snapshot.AcceptedVersion);
        Assert.Throws<InvalidOperationException>(() => store.ApplyDesiredState(1,
            Kautuka.Manager.Protos.WorkspaceDesiredStateSyncReason.Bootstrap,
            CreateRuntimeSettings(root), [CreateWorkspace("scope-c", root)], [], [], "epoch-b"));
    }

    [Fact]
    public void DesiredWorkspaceMustBeStrictDescendantOfConfiguredRoot()
    {
        string root = Path.Combine(Path.GetTempPath(), $"kautuka-manager-root-{Guid.NewGuid():N}");
        string sibling = root + "-sibling";
        var store = new WorkspaceDesiredStateStore();

        Assert.Throws<InvalidOperationException>(() => store.ApplyDesiredState(
            1,
            Kautuka.Manager.Protos.WorkspaceDesiredStateSyncReason.Bootstrap,
            CreateRuntimeSettings(root),
            [CreateWorkspace("scope-a", sibling)],
            [],
            []));
    }

    [Fact]
    public void ContentHashIsOrderIndependentAndIncludesTracesEndpoint()
    {
        string root = Path.Combine(Path.GetTempPath(), $"kautuka-manager-hash-{Guid.NewGuid():N}");
        ManagerRuntimeSettings settings = CreateRuntimeSettings(root) with
        {
            Telemetry = new WorkspaceTelemetryDefinition { TracesEndpoint = "http://otel-a/v1/traces" },
        };
        var first = new WorkspaceDesiredStateStore();
        var reordered = new WorkspaceDesiredStateStore();
        var changedTrace = new WorkspaceDesiredStateStore();

        _ = first.ApplyDesiredState(1, Kautuka.Manager.Protos.WorkspaceDesiredStateSyncReason.Bootstrap,
            settings, [CreateWorkspace("a", root), CreateWorkspace("b", root)], [], []);
        _ = reordered.ApplyDesiredState(1, Kautuka.Manager.Protos.WorkspaceDesiredStateSyncReason.Bootstrap,
            settings, [CreateWorkspace("b", root), CreateWorkspace("a", root)], [], []);
        _ = changedTrace.ApplyDesiredState(1, Kautuka.Manager.Protos.WorkspaceDesiredStateSyncReason.Bootstrap,
            settings with { Telemetry = new WorkspaceTelemetryDefinition { TracesEndpoint = "http://otel-b/v1/traces" } },
            [CreateWorkspace("a", root), CreateWorkspace("b", root)], [], []);

        Assert.Equal(first.Snapshot.ContentHash, reordered.Snapshot.ContentHash);
        Assert.NotEqual(first.Snapshot.ContentHash, changedTrace.Snapshot.ContentHash);
    }

    [Fact]
    public void ANewDesiredStateVersionStoresTheConnectorProtocol()
    {
        string root = Path.Combine(Path.GetTempPath(), $"kautuka-manager-protocol-{Guid.NewGuid():N}");
        var store = new WorkspaceDesiredStateStore();
        WorkspaceMcpContainerConnectorDefinition connector = CreateConnector("system::markitdown");

        _ = store.ApplyDesiredState(1, Kautuka.Manager.Protos.WorkspaceDesiredStateSyncReason.Bootstrap,
            CreateRuntimeSettings(root), [CreateWorkspace("scope-a", root)], [connector], []);
        Assert.Null(store.Snapshot.DesiredContainerConnectors[0].McpProtocolVersion);

        WorkspaceDesiredStateApplyResult updated = store.ApplyDesiredState(2,
            Kautuka.Manager.Protos.WorkspaceDesiredStateSyncReason.Change,
            CreateRuntimeSettings(root),
            [CreateWorkspace("scope-a", root)],
            [connector with { McpProtocolVersion = "2024-11-05" }],
            []);

        Assert.True(updated.Accepted);
        Assert.Equal("2024-11-05", store.Snapshot.DesiredContainerConnectors[0].McpProtocolVersion);
    }

    private static ManagerRuntimeSettings CreateRuntimeSettings(string? root = null)
        => new(
            new ManagerWorkspaceDefinition
            {
                BaseRoot = root ?? Path.Combine(Path.GetTempPath(), "kautuka-workspaces"),
            },
            new ManagerSandboxRuntimeDefinition
            {
                Image = "kautuka-base:latest",
                DefaultTimeout = 120,
            },
            new WorkspaceTelemetryDefinition());

    private static ContainerContext CreateWorkspace(string scopeId, string? root = null)
        => new(
            "agent",
            scopeId,
            "user-1",
            Path.Combine(root ?? Path.Combine(Path.GetTempPath(), "kautuka-workspaces"), scopeId),
            "/workspace",
            "kautuka-base:latest",
            2,
            "2g",
            120,
            [],
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));

    private static WorkspaceMcpContainerConnectorDefinition CreateConnector(string connectorId)
        => new(
            connectorId,
            "quay.io/fedora/fedora-minimal:44",
            "npx",
            ["-y", "@modelcontextprotocol/server-filesystem"],
            true,
            false,
            null,
            null,
            [],
            30,
            600);

    private sealed class TestHostEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Development;

        public string ApplicationName { get; set; } = "Kautuka.IntegrationTests";

        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;

        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }

}
