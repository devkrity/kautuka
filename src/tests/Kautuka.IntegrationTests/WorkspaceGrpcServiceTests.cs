using Grpc.Core;

using Kautuka.Manager.Grpc;
using Kautuka.Manager.Hosting;
using Kautuka.Manager.Protos;
using Kautuka.Manager.RuntimeContainer.Configuration;
using Kautuka.Manager.RuntimeContainer.Hosting;
using Kautuka.Manager.RuntimeContainer.Sandbox;
using Kautuka.Manager.RuntimeContainer.Sandbox.Models;

using Microsoft.Extensions.Logging.Abstractions;

using ManagerSandboxRuntimeDefinition = Kautuka.Manager.RuntimeContainer.Configuration.SandboxRuntimeDefinition;
using ManagerWorkspaceDefinition = Kautuka.Manager.RuntimeContainer.Configuration.WorkspaceDefinition;

namespace Kautuka.IntegrationTests;

public sealed class WorkspaceGrpcServiceTests
{
    [Theory]
    [InlineData("user-missing")]
    [InlineData("")]
    [InlineData(" ")]
    public async Task SharedWorkspaceMissingUserRejectsSpawnBeforeContainerTool(string userId)
    {
        WorkspaceDesiredStateStore store = CreateReconciledStore();
        WorkspaceDesiredStateSnapshot snapshot = store.Snapshot;
        ContainerContext template = Assert.Single(snapshot.DesiredWorkspaces);
        _ = store.ApplyDesiredState(2, WorkspaceDesiredStateSyncReason.Change, snapshot.RuntimeSettings!,
            [template with { UserId = "user-a", HostWorkspacePath = Path.Combine(template.HostWorkspacePath, "user-a") },
             template with { UserId = "user-b", HostWorkspacePath = Path.Combine(template.HostWorkspacePath, "user-b") }], [], []);
        var gate = new WorkspaceReadinessGate();
        gate.SignalReady();
        var tool = new CapturingContainerTool(template);
        var service = new WorkspaceGrpcService(tool, store, gate, new WorkspaceExecTracker(),
            new StubRuntimeHealthProbe(true), null!, null!, null!, NullLogger<WorkspaceGrpcService>.Instance);
        WorkspaceSandboxResponse response = await service.SpawnSandbox(new WorkspaceResolvedRequest
        {
            Context = new WorkspaceContextMessage { ScopeType = template.ScopeType, ScopeId = template.ScopeId, UserId = userId },
        }, TestServerCallContext.Instance);
        Assert.False(response.Ok);
        Assert.Equal(WorkspaceOperationOutcome.Failed, response.Outcome);
        Assert.Equal(0, tool.SpawnCalls);
    }

    [Fact]
    public async Task GetHostStatusReportsRuntimeUnavailableEvenWhenReconcileSnapshotIsReady()
    {
        WorkspaceDesiredStateStore store = CreateReconciledStore();
        var readinessGate = new WorkspaceReadinessGate();
        readinessGate.SignalReady();
        var service = new WorkspaceGrpcService(
            null!,
            store,
            readinessGate,
            new WorkspaceExecTracker(),
            new StubRuntimeHealthProbe(false),
            null!,
            null!,
            null!,
            NullLogger<WorkspaceGrpcService>.Instance);

        ManagerStatusResponse response = await service.GetHostStatus(
            new WorkspacePingRequest(),
            TestServerCallContext.Instance);

        Assert.True(response.CoreReady);
        Assert.False(response.AgentsReady);
        Assert.True(response.AgentsPending);
        Assert.Equal("runtime_unavailable", response.LastFailureClass);
        Assert.False(readinessGate.IsReady);
        Assert.True(store.Updates.TryRead(out WorkspaceReconcileTriggerKind trigger));
        Assert.Equal(WorkspaceReconcileTriggerKind.RecoveryRetry, trigger);
    }

    [Fact]
    public async Task GetHostStatusReportsReadyWhenRuntimeAndReconcileSnapshotAreReady()
    {
        WorkspaceDesiredStateStore store = CreateReconciledStore();
        var readinessGate = new WorkspaceReadinessGate();
        readinessGate.SignalReady();
        var service = new WorkspaceGrpcService(
            null!,
            store,
            readinessGate,
            new WorkspaceExecTracker(),
            new StubRuntimeHealthProbe(true),
            null!,
            null!,
            null!,
            NullLogger<WorkspaceGrpcService>.Instance);

        ManagerStatusResponse response = await service.GetHostStatus(
            new WorkspacePingRequest(),
            TestServerCallContext.Instance);

        Assert.True(response.CoreReady);
        Assert.True(response.AgentsReady);
        Assert.False(response.AgentsPending);
        Assert.Empty(response.LastFailureClass);
    }

    [Fact]
    public async Task GetHostStatusKeepsCoreReadyWhileAgentStateIsReconciling()
    {
        WorkspaceDesiredStateStore store = CreateReconciledStore();
        WorkspaceDesiredStateSnapshot reconciled = store.Snapshot;
        _ = store.ApplyDesiredState(
            2,
            WorkspaceDesiredStateSyncReason.Change,
            reconciled.RuntimeSettings!,
            reconciled.DesiredWorkspaces,
            reconciled.DesiredContainerConnectors,
            reconciled.DesiredSandboxConnectorBindings);

        var readinessGate = new WorkspaceReadinessGate();
        readinessGate.SignalReady();
        readinessGate.Reset();
        var service = new WorkspaceGrpcService(
            null!,
            store,
            readinessGate,
            new WorkspaceExecTracker(),
            new StubRuntimeHealthProbe(true),
            null!,
            null!,
            null!,
            NullLogger<WorkspaceGrpcService>.Instance);

        ManagerStatusResponse response = await service.GetHostStatus(
            new WorkspacePingRequest(),
            TestServerCallContext.Instance);

        Assert.True(response.CoreReady);
        Assert.False(response.AgentsReady);
        Assert.True(response.AgentsPending);
        Assert.Equal(2, response.AcceptedVersion);
        Assert.Equal(1, response.ReconciledVersion);
    }

    [Fact]
    public async Task GetHostStatusReportsManagerReadyBeforeDesiredStateArrives()
    {
        var readinessGate = new WorkspaceReadinessGate();
        readinessGate.SignalReady();
        var service = new WorkspaceGrpcService(
            null!,
            new WorkspaceDesiredStateStore(),
            readinessGate,
            new WorkspaceExecTracker(),
            new StubRuntimeHealthProbe(true),
            null!,
            null!,
            null!,
            NullLogger<WorkspaceGrpcService>.Instance);

        ManagerStatusResponse response = await service.GetHostStatus(
            new WorkspacePingRequest(),
            TestServerCallContext.Instance);

        Assert.True(response.CoreReady);
        Assert.False(response.AgentsReady);
        Assert.False(response.AgentsPending);
        Assert.Equal(0, response.AcceptedVersion);
        Assert.Equal(0, response.ReconciledVersion);
    }

    [Fact]
    public async Task GetHostStatusDoesNotConfuseReconcileFailureWithManagerAvailability()
    {
        WorkspaceDesiredStateStore store = CreateReconciledStore();
        WorkspaceDesiredStateSnapshot reconciled = store.Snapshot;
        _ = store.ApplyDesiredState(
            2,
            WorkspaceDesiredStateSyncReason.Change,
            reconciled.RuntimeSettings!,
            reconciled.DesiredWorkspaces,
            reconciled.DesiredContainerConnectors,
            reconciled.DesiredSandboxConnectorBindings);
        Assert.True(store.TryMarkReconcileFailed(2, "sandbox_provision_failed"));

        var readinessGate = new WorkspaceReadinessGate();
        readinessGate.SignalReady();
        readinessGate.Reset();
        var service = new WorkspaceGrpcService(
            null!,
            store,
            readinessGate,
            new WorkspaceExecTracker(),
            new StubRuntimeHealthProbe(true),
            null!,
            null!,
            null!,
            NullLogger<WorkspaceGrpcService>.Instance);

        ManagerStatusResponse response = await service.GetHostStatus(
            new WorkspacePingRequest(),
            TestServerCallContext.Instance);

        Assert.True(response.CoreReady);
        Assert.False(response.AgentsReady);
        Assert.True(response.AgentsPending);
        Assert.Equal("failed", response.Lifecycle);
        Assert.Equal("sandbox_provision_failed", response.LastFailureClass);
    }

    [Fact]
    public async Task DesiredStateExpectedVersionConflictReturnsTypedOutcome()
    {
        var service = new WorkspaceGrpcService(
            null!,
            new WorkspaceDesiredStateStore(),
            new WorkspaceReadinessGate(),
            new WorkspaceExecTracker(),
            new StubRuntimeHealthProbe(true),
            null!,
            null!,
            null!,
            NullLogger<WorkspaceGrpcService>.Instance);
        string requestId = Guid.NewGuid().ToString("D");

        DesiredStateUpdateResponse response = await service.UpdateDesiredState(
            new WorkspaceDesiredStateUpdate
            {
                DesiredStateVersion = 1,
                SyncReason = WorkspaceDesiredStateSyncReason.Bootstrap,
                RequestId = requestId,
                ExpectedAcceptedVersion = 99,
            },
            TestServerCallContext.Instance);

        Assert.Equal(DesiredStateUpdateOutcome.Conflict, response.Outcome);
        Assert.Equal(requestId, response.RequestId);
        Assert.Equal(0, response.AcceptedVersion);
    }

    [Fact]
    public async Task DesiredStateMalformedRequestReturnsSanitizedTypedFailure()
    {
        var service = new WorkspaceGrpcService(
            null!,
            new WorkspaceDesiredStateStore(),
            new WorkspaceReadinessGate(),
            new WorkspaceExecTracker(),
            new StubRuntimeHealthProbe(true),
            null!,
            null!,
            null!,
            NullLogger<WorkspaceGrpcService>.Instance);

        DesiredStateUpdateResponse response = await service.UpdateDesiredState(
            new WorkspaceDesiredStateUpdate
            {
                DesiredStateVersion = 1,
                SyncReason = WorkspaceDesiredStateSyncReason.Bootstrap,
                RequestId = "not-a-uuid",
            },
            TestServerCallContext.Instance);

        Assert.Equal(DesiredStateUpdateOutcome.Failed, response.Outcome);
        Assert.Equal("Desired state was rejected. Consult the correlated Manager audit event.", response.Message);
    }

    [Fact]
    public async Task RebuildWorkspaceDelegatesToContainerTool()
    {
        ContainerContext rebuiltContext = new(
            ScopeType: "agent",
            ScopeId: "alpha",
            UserId: "user-1",
            HostWorkspacePath: "/tmp/rebuilt-alpha",
            ContainerWorkspacePath: "/workspace",
            Image: "localhost/kautuka-base:latest",
            DefaultCpuLimit: 2,
            DefaultMemoryLimit: "2g",
            DefaultTimeoutSeconds: 120,
            BaseCapabilities: [],
            ContainerEnvironment: new Dictionary<string, string>());

        var containerTool = new CapturingContainerTool(rebuiltContext);
        string workspaceRoot = Path.Combine(Path.GetTempPath(), "kautuka-manager-grpc");
        ContainerContext acceptedContext = rebuiltContext with
        {
            HostWorkspacePath = Path.Combine(workspaceRoot, "alpha"),
        };
        var desiredStateStore = new WorkspaceDesiredStateStore();
        _ = desiredStateStore.ApplyDesiredState(
            1,
            WorkspaceDesiredStateSyncReason.Bootstrap,
            new ManagerRuntimeSettings(
                new ManagerWorkspaceDefinition { BaseRoot = workspaceRoot },
                new ManagerSandboxRuntimeDefinition { Image = "localhost/kautuka-base:latest" },
                new WorkspaceTelemetryDefinition()),
            [acceptedContext],
            [],
            []);
        var service = new WorkspaceGrpcService(
            containerTool,
            desiredStateStore,
            new WorkspaceReadinessGate(),
            new WorkspaceExecTracker(),
            new StubRuntimeHealthProbe(true),
            null!,
            null!,
            null!,
            NullLogger<WorkspaceGrpcService>.Instance);

        WorkspaceContextResponse response = await service.RebuildWorkspace(
            new WorkspaceResolvedRequest
            {
                Context = new WorkspaceContextMessage
                {
                    ScopeType = "agent",
                    ScopeId = "alpha",
                    UserId = "user-1",
                    HostWorkspacePath = "/tmp/original-alpha",
                    ContainerWorkspacePath = "/workspace",
                    Image = "localhost/kautuka-base:latest",
                    DefaultTimeoutSeconds = 120,
                }
            },
            TestServerCallContext.Instance);

        Assert.True(response.Ok);
        Assert.NotNull(containerTool.RebuildRequest);
        Assert.Equal(acceptedContext.HostWorkspacePath, containerTool.RebuildRequest!.HostWorkspacePath);
        Assert.Equal(acceptedContext.Image, containerTool.RebuildRequest.Image);
        Assert.Equal(rebuiltContext.HostWorkspacePath, response.Context.HostWorkspacePath);
    }

    [Fact]
    public async Task RebuildWorkspaceResolvesAgentWorkspaceIndependentlyOfCallerIdentity()
    {
        string workspaceRoot = Path.Combine(Path.GetTempPath(), "kautuka-manager-grpc");
        var acceptedContext = new ContainerContext(
            ScopeType: "agent",
            ScopeId: "admin-roller",
            UserId: null,
            HostWorkspacePath: Path.Combine(workspaceRoot, "admin-roller"),
            ContainerWorkspacePath: "/workspace",
            Image: "localhost/kautuka-base:latest",
            DefaultCpuLimit: 2,
            DefaultMemoryLimit: "2g",
            DefaultTimeoutSeconds: 120,
            BaseCapabilities: [],
            ContainerEnvironment: new Dictionary<string, string>());
        var containerTool = new CapturingContainerTool(acceptedContext);
        var desiredStateStore = new WorkspaceDesiredStateStore();
        _ = desiredStateStore.ApplyDesiredState(
            1,
            WorkspaceDesiredStateSyncReason.Bootstrap,
            new ManagerRuntimeSettings(
                new ManagerWorkspaceDefinition { BaseRoot = workspaceRoot },
                new ManagerSandboxRuntimeDefinition { Image = "localhost/kautuka-base:latest" },
                new WorkspaceTelemetryDefinition()),
            [acceptedContext],
            [],
            []);
        var service = new WorkspaceGrpcService(
            containerTool,
            desiredStateStore,
            new WorkspaceReadinessGate(),
            new WorkspaceExecTracker(),
            new StubRuntimeHealthProbe(true),
            null!,
            null!,
            null!,
            NullLogger<WorkspaceGrpcService>.Instance);

        WorkspaceContextResponse response = await service.RebuildWorkspace(
            new WorkspaceResolvedRequest
            {
                Context = new WorkspaceContextMessage
                {
                    ScopeType = "agent",
                    ScopeId = "admin-roller",
                    UserId = "authenticated-caller",
                }
            },
            TestServerCallContext.Instance);

        Assert.True(response.Ok);
        Assert.Equal(acceptedContext.ScopeId, containerTool.RebuildRequest!.ScopeId);
        Assert.Equal(acceptedContext.HostWorkspacePath, containerTool.RebuildRequest.HostWorkspacePath);
        Assert.Equal(1, containerTool.RebuildRequest.DesiredStateVersion);
        Assert.NotEmpty(containerTool.RebuildRequest.SpecFingerprint);
    }

    [Fact]
    public async Task ExecResolvesAaatAgentWorkspaceIndependentlyOfAuthenticatedCaller()
    {
        string workspaceRoot = Path.Combine(Path.GetTempPath(), "kautuka-manager-grpc");
        var acceptedContext = new ContainerContext(
            ScopeType: "agent",
            ScopeId: "admin-roller",
            UserId: null,
            HostWorkspacePath: Path.Combine(workspaceRoot, "admin-roller"),
            ContainerWorkspacePath: "/workspace",
            Image: "localhost/kautuka-base:latest",
            DefaultCpuLimit: 2,
            DefaultMemoryLimit: "2g",
            DefaultTimeoutSeconds: 120,
            BaseCapabilities: [],
            ContainerEnvironment: new Dictionary<string, string>());
        var containerTool = new CapturingContainerTool(acceptedContext);
        var desiredStateStore = new WorkspaceDesiredStateStore();
        _ = desiredStateStore.ApplyDesiredState(
            1,
            WorkspaceDesiredStateSyncReason.Bootstrap,
            new ManagerRuntimeSettings(
                new ManagerWorkspaceDefinition { BaseRoot = workspaceRoot },
                new ManagerSandboxRuntimeDefinition { Image = "localhost/kautuka-base:latest" },
                new WorkspaceTelemetryDefinition()),
            [acceptedContext],
            [],
            []);
        var readinessGate = new WorkspaceReadinessGate();
        readinessGate.SignalReady();
        var service = new WorkspaceGrpcService(
            containerTool,
            desiredStateStore,
            readinessGate,
            new WorkspaceExecTracker(),
            new StubRuntimeHealthProbe(true),
            null!,
            null!,
            null!,
            NullLogger<WorkspaceGrpcService>.Instance);

        WorkspaceExecutionResponse response = await service.Exec(
            new WorkspaceExecRequest
            {
                Context = new WorkspaceContextMessage
                {
                    ScopeType = "agent",
                    ScopeId = "admin-roller",
                    UserId = "authenticated-caller",
                },
                Command = "roll a d72",
            },
            TestServerCallContext.Instance);

        Assert.True(response.Ok);
        Assert.Equal(acceptedContext.ScopeId, containerTool.RunShellRequest!.ScopeId);
        Assert.Equal(acceptedContext.HostWorkspacePath, containerTool.RunShellRequest.HostWorkspacePath);
        Assert.Equal(1, containerTool.RunShellRequest.DesiredStateVersion);
        Assert.NotEmpty(containerTool.RunShellRequest.SpecFingerprint);
        Assert.Equal("roll a d72", containerTool.RunShellCommand);
    }

    private sealed class StubRuntimeHealthProbe(bool available) : IManagerRuntimeHealthProbe
    {
        public Task<bool> IsAvailableAsync(CancellationToken cancellationToken)
            => Task.FromResult(available);

        public Task<bool> RefreshAsync(CancellationToken cancellationToken)
            => Task.FromResult(available);
    }

    private static WorkspaceDesiredStateStore CreateReconciledStore()
    {
        var store = new WorkspaceDesiredStateStore();
        string workspaceRoot = Path.Combine(Path.GetTempPath(), "kautuka-manager-status");
        var workspace = new ContainerContext(
            "agent",
            "alpha",
            null,
            Path.Combine(workspaceRoot, "alpha"),
            "/workspace",
            "localhost/kautuka-base:latest",
            1,
            "512m",
            30,
            [],
            new Dictionary<string, string>());
        _ = store.ApplyDesiredState(
            1,
            WorkspaceDesiredStateSyncReason.Bootstrap,
            new ManagerRuntimeSettings(
                new ManagerWorkspaceDefinition { BaseRoot = workspaceRoot },
                new ManagerSandboxRuntimeDefinition(),
                new WorkspaceTelemetryDefinition()),
            [workspace],
            [],
            []);
        Assert.True(store.TryMarkReconcileStarted(1));
        Assert.True(store.TryMarkReconciled(
            1,
            WorkspaceReconcileTriggerKind.DesiredStateAccepted,
            DateTimeOffset.UtcNow));
        while (store.Updates.TryRead(out _))
        {
        }
        return store;
    }

    private sealed class CapturingContainerTool(ContainerContext rebuiltContext) : IContainerTool
    {
        public int SpawnCalls { get; private set; }
        public ContainerContext? RebuildRequest { get; private set; }

        public ContainerContext? RunShellRequest { get; private set; }

        public string? RunShellCommand { get; private set; }

        public Task PrimeWorkspaceAsync(ContainerContext context, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<ContainerContext> RebuildWorkspaceAsync(ContainerContext context, CancellationToken cancellationToken = default)
        {
            RebuildRequest = context;
            return Task.FromResult(rebuiltContext);
        }

        public Task<bool> DeleteWorkspaceAsync(ContainerContext context, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<OciSandboxSession> SpawnSandboxAsync(ContainerContext context, CancellationToken cancellationToken = default)
        {
            SpawnCalls++;
            throw new NotSupportedException();
        }

        public Task<ContainerExecutionResult> RunShellAsync(ContainerContext context, string command, int? timeoutSeconds = null, CancellationToken cancellationToken = default)
        {
            RunShellRequest = context;
            RunShellCommand = command;
            return Task.FromResult(new ContainerExecutionResult("completed", 0, "35", string.Empty));
        }

        public Task<OciSandboxSession> InspectSandboxAsync(ContainerContext context, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task CloseSandboxAsync(ContainerContext context, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }

    private sealed class TestServerCallContext : ServerCallContext
    {
        public static readonly TestServerCallContext Instance = new();

        private readonly Dictionary<object, object> userState = [];
        private Metadata responseTrailers = [];
        private Status status;
        private WriteOptions? writeOptions;

        protected override string MethodCore => "RebuildWorkspace";

        protected override string HostCore => "localhost";

        protected override string PeerCore => "127.0.0.1";

        protected override DateTime DeadlineCore => DateTime.MaxValue;

        protected override Metadata RequestHeadersCore => [];

        protected override CancellationToken CancellationTokenCore => CancellationToken.None;

        protected override Metadata ResponseTrailersCore => responseTrailers;

        protected override IDictionary<object, object> UserStateCore => userState;

        protected override Status StatusCore
        {
            get => status;
            set => status = value;
        }

        protected override WriteOptions? WriteOptionsCore
        {
            get => writeOptions;
            set => writeOptions = value;
        }

        protected override AuthContext AuthContextCore => new(string.Empty, []);

        protected override ContextPropagationToken CreatePropagationTokenCore(ContextPropagationOptions? options)
            => throw new NotSupportedException();

        protected override Task WriteResponseHeadersAsyncCore(Metadata responseHeaders)
            => Task.CompletedTask;
    }
}
