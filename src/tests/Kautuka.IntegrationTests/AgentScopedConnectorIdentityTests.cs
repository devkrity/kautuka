using System.Reflection;
using System.Text.Json.Nodes;

using Kautuka.Manager.Protos;
using Kautuka.Manager.RuntimeContainer.Configuration;
using Kautuka.Manager.RuntimeContainer.Hosting;
using Kautuka.Manager.RuntimeContainer.Sandbox;
using Kautuka.Manager.RuntimeContainer.Sandbox.Models;

using Microsoft.Extensions.Logging.Abstractions;

using WorkspaceDefinition = Kautuka.Manager.RuntimeContainer.Configuration.WorkspaceDefinition;
using SandboxRuntimeDefinition = Kautuka.Manager.RuntimeContainer.Configuration.SandboxRuntimeDefinition;

namespace Kautuka.IntegrationTests;

public sealed class AgentScopedConnectorIdentityTests
{
    [Fact]
    public async Task DesiredInstancesSeparateAgentsAndOwnersWithoutGrantingFileAccess()
    {
        var runner = new PodmanCommandRunner((_, _) => throw new InvalidOperationException("Naming must not call Podman."));
        await using var service = Service(runner);
        ContainerContext alpha = Workspace("alpha", "owner");
        ContainerContext beta = Workspace("beta", "owner");
        ContainerContext foreign = Workspace("foreign", "another-owner");
        WorkspaceMcpContainerConnectorDefinition connector = Connector();
        IReadOnlyCollection<string> names = service.DesiredIsolatedContainerNames([connector], [alpha, beta, foreign]);

        Assert.Equal(5, names.Count); // Management plus two modes per accepted agent.
        Assert.Contains(Name(connector, WorkspaceMcpExecutionContext.Isolated, alpha), names);
        Assert.Contains(Name(connector, WorkspaceMcpExecutionContext.Isolated, beta), names);
        Assert.NotEqual(Name(connector, WorkspaceMcpExecutionContext.Isolated, alpha), Name(connector, WorkspaceMcpExecutionContext.Isolated, beta));
        Assert.DoesNotContain(Name(connector, WorkspaceMcpExecutionContext.Isolated, foreign), names);
        Assert.Equal(names, service.DesiredIsolatedContainerNames([connector], [alpha, beta, foreign]));
        Assert.Empty(service.DesiredIsolatedContainerNames([connector with { Enabled = false }], [alpha]));
        Assert.DoesNotContain(Name(connector, WorkspaceMcpExecutionContext.Isolated, alpha), service.DesiredIsolatedContainerNames([connector], [beta]));

        List<string> args = ConnectorContainerSpecBuilder.BuildRun(connector, "test", WorkspaceMcpExecutionContext.Isolated, alpha, persistent: true);
        Assert.DoesNotContain("--volume", args);
        Assert.DoesNotContain(args, arg => arg.Contains("AGENT_PRIVATE", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ReconcileFindsExistingScopedInstancesWithoutStartingAbsentOnes()
    {
        WorkspaceMcpContainerConnectorDefinition connector = Connector();
        ContainerContext alpha = Workspace("alpha", "owner");
        ContainerContext beta = Workspace("beta", "owner");
        string name = Name(connector, WorkspaceMcpExecutionContext.Isolated, alpha);
        var store = new WorkspaceDesiredStateStore();
        Apply(store, 3, connector, [alpha, beta]);
        connector = Assert.Single(store.Snapshot.DesiredContainerConnectors);
        var commands = new List<string[]>();
        JsonNode inspect = JsonNode.Parse(LiveContainerSpecTests.Inspect(pidsLimit: 128))!;
        JsonNode container = inspect[0]!;
        container["HostConfig"]!["Binds"] = new JsonArray();
        container["HostConfig"]!["Tmpfs"]!.AsObject().Remove("/home/node/.kautuka");
        container["Config"]!["Labels"]!["kautuka.spec-fingerprint"] = connector.SpecFingerprint;
        container["Config"]!["Env"] = new JsonArray("PATH=/usr/bin", "HOME=/tmp", "NPM_CONFIG_CACHE=/tmp/.npm", "PIP_CACHE_DIR=/tmp/.pip", "UV_CACHE_DIR=/tmp/.uv");
        var runner = new PodmanCommandRunner((args, _) =>
        {
            commands.Add([.. args]);
            string output = args[0] switch
            {
                "ps" => new string('a', 64) + "|" + name + "\n",
                "inspect" when args[1] == name => inspect.ToJsonString(),
                "image" => "sha256:current",
                "rm" => string.Empty,
                "container" => string.Empty,
                _ => throw new InvalidOperationException("Unexpected Podman command: " + string.Join(' ', args)),
            };
            return Task.FromResult(new PodmanCommandResult(args[0] == "container" ? 1 : 0, output, string.Empty, args[0] == "container" ? ProcessTerminationReason.NonZeroExit : ProcessTerminationReason.Completed));
        });
        await using var service = Service(runner, store);

        await service.ReconcileDesiredConnectorsAsync(TestContext.Current.CancellationToken);
        Assert.Equal(name, Assert.Single(service.TrackedContainerNames));
        await service.ReconcileDesiredConnectorsAsync(TestContext.Current.CancellationToken);
        Assert.Equal(name, Assert.Single(service.TrackedContainerNames));
        Assert.Equal(2, commands.Count(args => args[0] == "inspect"));
        Assert.DoesNotContain(commands, args => args[0] == "run");

        Apply(store, 4, connector, [beta]);
        await service.RemoveOrphanedConnectorContainersAsync(TestContext.Current.CancellationToken);
        Assert.Empty(service.TrackedContainerNames);
        Assert.DoesNotContain(name, service.DesiredIsolatedContainerNames([connector], [beta]));
    }

    [Fact]
    public void AcceptedOwnerWorkspaceDoesNotResolveForAnotherUser()
    {
        ContainerContext workspace = Workspace("alpha", "owner");
        var settings = new ManagerRuntimeSettings(new WorkspaceDefinition { BaseRoot = Path.GetTempPath() }, new SandboxRuntimeDefinition(), new WorkspaceTelemetryDefinition());
        var store = new WorkspaceDesiredStateStore();
        store.ApplyDesiredState(1, WorkspaceDesiredStateSyncReason.Bootstrap, settings, [workspace], [], []);
        Assert.True(store.TryResolveWorkspace("agent", "alpha", "owner", out _));
        Assert.False(store.TryResolveWorkspace("agent", "alpha", "another-owner", out _));
        Assert.False(store.TryResolveWorkspace("agent", "alpha", null, out _));
    }

    [Fact]
    public async Task NewScopeCreatedDuringInventoryIsNotRemovedByOldReconciliation()
    {
        var store = new WorkspaceDesiredStateStore();
        ContainerContext alpha = Workspace("alpha", "owner");
        ContainerContext beta = Workspace("beta", "owner");
        WorkspaceMcpContainerConnectorDefinition connector = Connector();
        Apply(store, 3, connector, [alpha]);
        string name = Name(connector, WorkspaceMcpExecutionContext.Isolated, beta);
        var inventoryEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseInventory = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        bool created = false;
        int removals = 0;
        var runner = new PodmanCommandRunner(async (args, ct) =>
        {
            if (args[0] == "ps")
            {
                inventoryEntered.TrySetResult();
                await releaseInventory.Task.WaitAsync(ct);
                return Result(0, new string('a', 64) + "|" + name);
            }
            if (args[0] == "inspect") return created ? Result(0, MatchingInspection(Assert.Single(store.Snapshot.DesiredContainerConnectors))) : Result(1);
            if (args[0] == "container") return Result(created ? 0 : 1);
            if (args[0] == "image") return Result(0, "sha256:current");
            if (args[0] == "run") { created = true; return Result(0); }
            if (args[0] == "rm") { removals++; created = false; return Result(0); }
            throw new InvalidOperationException("Unexpected command.");
        });
        await using var service = Service(runner, store);
        Task<ManagedContainerReconcileResult> removing = service.RemoveOrphanedConnectorContainersAsync(TestContext.Current.CancellationToken);
        await inventoryEntered.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Apply(store, 4, connector, [alpha, beta]);
        await PrepareAsync(service, Assert.Single(store.Snapshot.DesiredContainerConnectors), beta);
        releaseInventory.SetResult();
        ManagedContainerReconcileResult result = await removing.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Assert.True(result.Succeeded);
        Assert.Equal(0, result.RemovedCount);
        Assert.Equal(0, removals);
        Assert.Contains(name, service.TrackedContainerNames);
    }

    [Fact]
    public async Task ExecutionCancelsRecoveredCooldownBeforeUsingTheContainer()
    {
        var store = new WorkspaceDesiredStateStore();
        ContainerContext alpha = Workspace("alpha", "owner");
        Apply(store, 3, Connector() with { JitCooldownSeconds = 1 }, [alpha]);
        WorkspaceMcpContainerConnectorDefinition connector = Assert.Single(store.Snapshot.DesiredContainerConnectors);
        string name = Name(connector, WorkspaceMcpExecutionContext.Isolated, alpha);
        var inspectionEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseInspection = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int inspections = 0;
        int removals = 0;
        var runner = new PodmanCommandRunner(async (args, ct) =>
        {
            if (args[0] == "ps") return Result(0, new string('a', 64) + "|" + name);
            if (args[0] == "inspect")
            {
                if (Interlocked.Increment(ref inspections) == 1)
                {
                    inspectionEntered.SetResult();
                    await releaseInspection.Task.WaitAsync(ct);
                }
                return Result(0, MatchingInspection(connector));
            }
            if (args[0] == "image") return Result(0, "sha256:current");
            if (args[0] == "rm") { Interlocked.Increment(ref removals); return Result(0); }
            throw new InvalidOperationException("Unexpected command.");
        });
        await using var service = Service(runner, store);
        Task recovery = service.ReconcileDesiredConnectorsAsync(TestContext.Current.CancellationToken);
        await inspectionEntered.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Task preparing = PrepareAsync(service, connector, alpha);
        Assert.False(preparing.IsCompleted);
        releaseInspection.SetResult();
        await Task.WhenAll(recovery, preparing).WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        await Task.Delay(TimeSpan.FromMilliseconds(1500), TestContext.Current.CancellationToken);
        Assert.Equal(0, Volatile.Read(ref removals));
    }

    [Fact]
    public async Task OrphanRemovalTargetsObservedIdWhenOneShotReusesItsName()
    {
        var store = new WorkspaceDesiredStateStore();
        WorkspaceMcpContainerConnectorDefinition connector = Connector() with { Command = string.Empty };
        ContainerContext alpha = Workspace("alpha", "owner");
        ContainerContext beta = Workspace("beta", "owner");
        Apply(store, 3, connector, [alpha]);
        string name = Name(connector, WorkspaceMcpExecutionContext.Isolated, beta);
        string oldId = new('a', 64);
        string replacementId = new('b', 64);
        string? remainingId = oldId;
        var runner = new PodmanCommandRunner((args, _) =>
        {
            if (args[0] == "ps") return Task.FromResult(Result(0, oldId + "|" + name));
            if (args[0] == "rm")
            {
                // Publication and an unleased one-shot replacement occur after
                // the orphan decision but before Podman resolves the rm target.
                Apply(store, 4, connector, [alpha, beta]);
                remainingId = replacementId;
                if (args[2] == name || args[2] == replacementId) remainingId = null;
                Assert.Equal(oldId, args[2]);
                return Task.FromResult(Result(1)); // Old object already disappeared.
            }
            if (args[0] == "container")
            {
                Assert.Equal(oldId, args[2]);
                return Task.FromResult(Result(1));
            }
            throw new InvalidOperationException("Unexpected command.");
        });
        await using var service = Service(runner, store);
        ManagedContainerReconcileResult result = await service.RemoveOrphanedConnectorContainersAsync(TestContext.Current.CancellationToken);
        Assert.True(result.Succeeded);
        Assert.Equal(replacementId, remainingId);
    }

    private static PodmanCommandResult Result(int code, string output = "")
        => new(code, output, string.Empty, code == 0 ? ProcessTerminationReason.Completed : ProcessTerminationReason.NonZeroExit);

    private static string MatchingInspection(WorkspaceMcpContainerConnectorDefinition connector)
    {
        JsonNode inspect = JsonNode.Parse(LiveContainerSpecTests.Inspect(pidsLimit: 128))!;
        JsonNode container = inspect[0]!;
        container["HostConfig"]!["Binds"] = new JsonArray();
        container["HostConfig"]!["Tmpfs"]!.AsObject().Remove("/home/node/.kautuka");
        container["Config"]!["Labels"]!["kautuka.desired-version"] = connector.DesiredStateVersion.ToString(System.Globalization.CultureInfo.InvariantCulture);
        container["Config"]!["Labels"]!["kautuka.spec-fingerprint"] = connector.SpecFingerprint;
        container["Config"]!["Env"] = new JsonArray("PATH=/usr/bin", "HOME=/tmp", "NPM_CONFIG_CACHE=/tmp/.npm", "PIP_CACHE_DIR=/tmp/.pip", "UV_CACHE_DIR=/tmp/.uv");
        return inspect.ToJsonString();
    }

    private static Task PrepareAsync(WorkspaceContainerConnectorExecutionService service, WorkspaceMcpContainerConnectorDefinition connector, ContainerContext workspace)
    {
        string name = Name(connector, WorkspaceMcpExecutionContext.Isolated, workspace);
        string key = (string)typeof(WorkspaceContainerConnectorExecutionService).GetMethod("BuildRuntimeKey", BindingFlags.Static | BindingFlags.NonPublic)!
            .Invoke(null, [connector.ConnectorId, WorkspaceMcpExecutionContext.Isolated, workspace])!;
        return (Task)typeof(WorkspaceContainerConnectorExecutionService).GetMethod("EnsureConnectorContainerRunningAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(service, [connector, key, name, WorkspaceMcpExecutionContext.Isolated, null, TestContext.Current.CancellationToken])!;
    }

    private static WorkspaceContainerConnectorExecutionService Service(PodmanCommandRunner runner, WorkspaceDesiredStateStore? store = null)
        => new(store ?? new WorkspaceDesiredStateStore(), runner, new McpbImageMaterializer(runner, NullLogger<McpbImageMaterializer>.Instance), new PodmanContainerTool(runner), NullLogger<WorkspaceContainerConnectorExecutionService>.Instance);

    private static void Apply(WorkspaceDesiredStateStore store, long version, WorkspaceMcpContainerConnectorDefinition connector, IReadOnlyList<ContainerContext> workspaces)
    {
        var settings = new ManagerRuntimeSettings(new WorkspaceDefinition { BaseRoot = Path.GetTempPath() }, new SandboxRuntimeDefinition(), new WorkspaceTelemetryDefinition());
        store.ApplyDesiredState(version, WorkspaceDesiredStateSyncReason.Bootstrap, settings, workspaces, [connector], []);
    }

    private static WorkspaceMcpContainerConnectorDefinition Connector()
        => new("owner::mail", "ghcr.io/example/mail@sha256:" + new string('a', 64), "mail", [], true, false, null, "binary", [], 30, 600)
        { DesiredStateVersion = 3, SpecFingerprint = "abc" };

    private static ContainerContext Workspace(string agent, string user)
        => new("agent", agent, user, Path.Combine(Path.GetTempPath(), "kautuka-scope-tests", user, agent), "/workspace", "image", 1, "512m", 30, [], new Dictionary<string, string> { ["AGENT_PRIVATE"] = "not-for-connector" });

    private static string Name(WorkspaceMcpContainerConnectorDefinition connector, WorkspaceMcpExecutionContext mode, ContainerContext workspace)
        => (string)typeof(WorkspaceContainerConnectorExecutionService).GetMethod("BuildContainerName", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, [connector.ConnectorId, mode, workspace])!;
}
