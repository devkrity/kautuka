using System.Reflection;
using System.Text.Json.Nodes;

using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

using Kautuka.Manager.Hosting;
using Kautuka.Manager.Protos;
using Kautuka.Manager.Grpc;
using Kautuka.Manager.RuntimeContainer.Configuration;
using Kautuka.Manager.RuntimeContainer.Hosting;
using Kautuka.Manager.RuntimeContainer.Hosting.NetworkAccess;
using Kautuka.Manager.RuntimeContainer.Sandbox;
using Kautuka.Manager.RuntimeContainer.Sandbox.Models;
using Kautuka.NetworkAccessController;

using ManagerSandboxRuntimeDefinition = Kautuka.Manager.RuntimeContainer.Configuration.SandboxRuntimeDefinition;
using ManagerWorkspaceDefinition = Kautuka.Manager.RuntimeContainer.Configuration.WorkspaceDefinition;

namespace Kautuka.IntegrationTests.NetworkAccess;

public sealed class WorkloadRegistryTrustLifecycleTests
{
    [Theory]
    [InlineData("0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef", true)]
    [InlineData("0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcde", false)]
    [InlineData("warning\n0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef", false)]
    [InlineData("", false)]
    public void LaunchIdentityRequiresOneUnambiguousCanonicalPodmanId(string output, bool expected)
    {
        Assert.Equal(expected, PodmanContainerIdentity.TryReadCanonicalLaunchOutput(output, out _));
    }

    [Fact]
    public void InspectedCanonicalIdSurvivesMissingStoppedNetworkIp()
    {
        const string cid = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
        const string inspect = "[{\"Id\":\"" + cid + "\",\"NetworkSettings\":{\"Networks\":{}}}]";

        Assert.True(PodmanContainerIdentity.TryRead(inspect, out string inspectedId, out string workloadIp));
        Assert.Equal(cid, inspectedId);
        Assert.Empty(workloadIp);
    }

    [Fact]
    public async Task RemovedWorkspaceCannotLaunchOrExecuteStaleSandboxContext()
    {
        string root = Path.Combine(Path.GetTempPath(), "nac-removed-workspace-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var store = new WorkspaceDesiredStateStore();
            var settings = new ManagerRuntimeSettings(
                new ManagerWorkspaceDefinition { BaseRoot = root },
                new ManagerSandboxRuntimeDefinition { Image = "localhost/kautuka-base:latest", DefaultTimeout = 30 },
                new WorkspaceTelemetryDefinition());
            var original = new ContainerContext(
                "agent", "agent-removed", "user-a", Path.Combine(root, "workspace"), "/workspace",
                "localhost/kautuka-base:latest", 30, "512m", 1, [], new Dictionary<string, string>());
            _ = store.ApplyDesiredState(1, WorkspaceDesiredStateSyncReason.Bootstrap, settings, [original], [], []);
            ContainerContext staleContext = Assert.Single(store.Snapshot.DesiredWorkspaces);
            _ = store.ApplyDesiredState(2, WorkspaceDesiredStateSyncReason.Change, settings, [], [], []);
            int podmanCalls = 0;
            var runner = new PodmanCommandRunner((_, _) =>
            {
                Interlocked.Increment(ref podmanCalls);
                return Task.FromResult(new PodmanCommandResult(0, string.Empty, string.Empty, ProcessTerminationReason.Completed));
            });
            var tool = new PodmanContainerTool(runner, desiredStateStore: store);

            InvalidOperationException launchFailure = await Assert.ThrowsAsync<InvalidOperationException>(
                () => tool.SpawnSandboxAsync(staleContext, TestContext.Current.CancellationToken));
            InvalidOperationException executionFailure = await Assert.ThrowsAsync<InvalidOperationException>(
                () => tool.RunShellAsync(staleContext, "true", cancellationToken: TestContext.Current.CancellationToken));

            Assert.Contains("no longer present", launchFailure.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("no longer present", executionFailure.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(0, Volatile.Read(ref podmanCalls));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task WorkspaceRemovedDuringInspectionIsQuarantinedBeforeVerifiedFastReturn()
    {
        string root = Path.Combine(Path.GetTempPath(), "nac-workspace-remove-race-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        const string ip = "10.247.1.21";
        const string containerId = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
        const string imageId = "sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        try
        {
            string workspaceRoot = Path.Combine(root, "workspace");
            Directory.CreateDirectory(workspaceRoot);
            var store = new WorkspaceDesiredStateStore();
            ManagerRuntimeSettings settings = new(
                new ManagerWorkspaceDefinition { BaseRoot = root },
                new ManagerSandboxRuntimeDefinition { Image = imageId, DefaultTimeout = 30 },
                new WorkspaceTelemetryDefinition());
            _ = store.ApplyDesiredState(
                1,
                WorkspaceDesiredStateSyncReason.Bootstrap,
                settings,
                [new ContainerContext("agent", "agent-a", "user-a", workspaceRoot, "/workspace", imageId, 30, "512m", 1, [], new Dictionary<string, string>())],
                [],
                []);
            ContainerContext context = Assert.Single(store.Snapshot.DesiredWorkspaces);
            string inspectJson = CreateExistingSandboxInspectJson(context, containerId, ip, imageId);
            bool removed = false;
            var runner = new PodmanCommandRunner((args, _) =>
            {
                if (args[0] == "inspect" && !removed)
                {
                    removed = true;
                    store.ApplyDesiredState(2, WorkspaceDesiredStateSyncReason.Change, settings, [], [], []);
                    return Task.FromResult(new PodmanCommandResult(0, inspectJson, string.Empty, ProcessTerminationReason.Completed));
                }

                if (args[0] == "image")
                {
                    return Task.FromResult(new PodmanCommandResult(0, imageId, string.Empty, ProcessTerminationReason.Completed));
                }

                return Task.FromResult(new PodmanCommandResult(0, string.Empty, string.Empty, ProcessTerminationReason.Completed));
            });
            var registry = new WorkloadRegistry();
            RegistryEntry desired = PodmanContainerTool.BuildSandboxRegistryEntry(context, ip, containerId);
            RegistryEntry quarantined = desired with { Egress = "deny-all", Attribution = "none" };
            registry.Register(quarantined);
            Assert.True(registry.TryPromoteVerified(quarantined, desired));
            var tool = new PodmanContainerTool(
                runner,
                workloadRegistry: registry,
                desiredStateStore: store);

            await Assert.ThrowsAsync<InvalidOperationException>(
                () => tool.SpawnSandboxAsync(context, TestContext.Current.CancellationToken));

            RegistryEntry retained = Assert.Single(registry.GetAll());
            Assert.Equal(containerId, retained.ContainerId);
            Assert.Equal("deny-all", retained.Egress);
            Assert.Equal("none", retained.Attribution);
            Assert.False(registry.IsVerified(ip, containerId));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void RegistryPromotesOnlyExactQuarantinedIdentityAndInvalidatesVerificationOnRebuild()
    {
        const string ip = "10.247.1.9";
        const string cid = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
        DateTimeOffset registered = DateTimeOffset.UtcNow;
        RegistryEntry desired = new(
            ip, "user-a", "agent-a", cid, "agent-sandbox", null, "agent-sandbox", false,
            "normal", "user-agent", null, registered);
        RegistryEntry quarantined = desired with { Egress = "deny-all", Attribution = "none" };
        var registry = new WorkloadRegistry();
        registry.Register(quarantined);

        Assert.False(registry.TryPromoteVerified(quarantined with { AgentId = "other-agent" }, desired));
        Assert.True(registry.TryPromoteVerified(quarantined, desired));
        Assert.True(registry.IsVerified(ip, cid));

        registry.Rebuild([quarantined]);

        Assert.False(registry.IsVerified(ip, cid));
        Assert.Equal("deny-all", Assert.Single(registry.GetAll()).Egress);
    }

    [Fact]
    public void ReusingAlreadyVerifiedSandboxDoesNotRevokeItsGrant()
    {
        const string ip = "10.247.1.8";
        const string cid = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
        var context = new ContainerContext(
            "agent", "agent-a", "user-a", Path.GetTempPath(), "/workspace", "localhost/test:latest", 30, "512m", 1, [], new Dictionary<string, string>())
        {
            DesiredStateVersion = 7,
            SpecFingerprint = "trusted-spec",
        };
        RegistryEntry desired = PodmanContainerTool.BuildSandboxRegistryEntry(context, ip, cid);
        var registry = new WorkloadRegistry();
        registry.Register(desired with { Egress = "deny-all", Attribution = "none" });
        Assert.True(registry.TryPromoteVerified(desired with { Egress = "deny-all", Attribution = "none" }, desired));

        var tool = new PodmanContainerTool(new PodmanCommandRunner(), workloadRegistry: registry);
        Type inspectionType = typeof(PodmanContainerTool).GetNestedType("ContainerInspection", BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("ContainerInspection was not found.");
        object inspection = Activator.CreateInstance(
            inspectionType,
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            binder: null,
            args:
            [
                new OciSandboxSession("sandbox", true, context.ScopeType, context.ScopeId, context.UserId),
                true,
                "running",
                context.DesiredStateVersion,
                context.SpecFingerprint,
                null,
                cid,
                ip,
            ],
            culture: null)!;
        MethodInfo quarantine = typeof(PodmanContainerTool).GetMethod("QuarantineInspectedSandboxBeforeMutation", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("QuarantineInspectedSandboxBeforeMutation was not found.");

        quarantine.Invoke(tool, [context, inspection, false]);

        RegistryEntry retained = Assert.Single(registry.GetAll());
        Assert.Equal("normal", retained.Egress);
        Assert.Equal("user-agent", retained.Attribution);
        Assert.True(registry.IsVerified(ip, cid));
    }

    [Fact]
    public void DriftedVerifiedSandboxIsQuarantinedBeforeReplacementAttempt()
    {
        const string ip = "10.247.1.7";
        const string cid = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
        var context = new ContainerContext(
            "agent", "agent-a", "user-a", Path.GetTempPath(), "/workspace", "localhost/test:latest", 30, "512m", 1, [], new Dictionary<string, string>())
        {
            DesiredStateVersion = 7,
            SpecFingerprint = "trusted-spec",
        };
        RegistryEntry desired = PodmanContainerTool.BuildSandboxRegistryEntry(context, ip, cid);
        var registry = new WorkloadRegistry();
        RegistryEntry quarantined = desired with { Egress = "deny-all", Attribution = "none" };
        registry.Register(quarantined);
        Assert.True(registry.TryPromoteVerified(quarantined, desired));

        var tool = new PodmanContainerTool(new PodmanCommandRunner(), workloadRegistry: registry);
        Type inspectionType = typeof(PodmanContainerTool).GetNestedType("ContainerInspection", BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("ContainerInspection was not found.");
        object inspection = Activator.CreateInstance(
            inspectionType,
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            binder: null,
            args:
            [
                new OciSandboxSession("sandbox", true, context.ScopeType, context.ScopeId, context.UserId),
                true,
                "running",
                context.DesiredStateVersion,
                "drifted-spec",
                "its mounts do not match the workspace",
                cid,
                ip,
            ],
            culture: null)!;
        MethodInfo quarantine = typeof(PodmanContainerTool).GetMethod("QuarantineInspectedSandboxBeforeMutation", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("QuarantineInspectedSandboxBeforeMutation was not found.");

        quarantine.Invoke(tool, [context, inspection, false]);

        RegistryEntry retained = Assert.Single(registry.GetAll());
        Assert.Equal("deny-all", retained.Egress);
        Assert.Equal("none", retained.Attribution);
        Assert.False(registry.IsVerified(ip, cid));
    }

    [Fact]
    public void StoppedVerifiedSandboxWithoutInspectedIpIsQuarantinedByCanonicalId()
    {
        const string ip = "10.247.1.6";
        const string cid = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
        var context = new ContainerContext(
            "agent", "agent-a", "user-a", Path.GetTempPath(), "/workspace", "localhost/test:latest", 30, "512m", 1, [], new Dictionary<string, string>())
        {
            DesiredStateVersion = 7,
            SpecFingerprint = "trusted-spec",
        };
        RegistryEntry desired = PodmanContainerTool.BuildSandboxRegistryEntry(context, ip, cid);
        var registry = new WorkloadRegistry();
        RegistryEntry quarantined = desired with { Egress = "deny-all", Attribution = "none" };
        registry.Register(quarantined);
        Assert.True(registry.TryPromoteVerified(quarantined, desired));

        var tool = new PodmanContainerTool(new PodmanCommandRunner(), workloadRegistry: registry);
        Type inspectionType = typeof(PodmanContainerTool).GetNestedType("ContainerInspection", BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("ContainerInspection was not found.");
        object inspection = Activator.CreateInstance(
            inspectionType,
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            binder: null,
            args:
            [
                new OciSandboxSession("sandbox", false, context.ScopeType, context.ScopeId, context.UserId),
                true,
                "exited",
                context.DesiredStateVersion,
                context.SpecFingerprint,
                null,
                cid,
                null,
            ],
            culture: null)!;
        MethodInfo quarantine = typeof(PodmanContainerTool).GetMethod("QuarantineInspectedSandboxBeforeMutation", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("QuarantineInspectedSandboxBeforeMutation was not found.");

        quarantine.Invoke(tool, [context, inspection, false]);

        RegistryEntry retained = Assert.Single(registry.GetAll());
        Assert.Equal(ip, retained.Ip);
        Assert.Equal("deny-all", retained.Egress);
        Assert.False(registry.IsVerified(ip, cid));
    }

    [Fact]
    public async Task ExistingSupervisorProvisioningPathRestoresMatchingSandboxAfterRegistryRebuild()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Skip("This test exercises Podman Machine command behavior without requiring a live Podman daemon.");
        }

        string root = Path.Combine(Path.GetTempPath(), "nac-supervisor-sandbox-recovery-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        const string ip = "10.247.1.15";
        const string containerId = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
        string imageDigest = "sha256:" + new string('f', 64);
        const string configuredImage = "quay.io/fedora/fedora-minimal:latest";
        try
        {
            string workspaceRoot = Path.Combine(root, "workspace");
            Directory.CreateDirectory(workspaceRoot);
            var desiredState = new WorkspaceDesiredStateStore();
            _ = desiredState.ApplyDesiredState(
                1,
                WorkspaceDesiredStateSyncReason.Bootstrap,
                new ManagerRuntimeSettings(
                    new ManagerWorkspaceDefinition { BaseRoot = root },
                    new ManagerSandboxRuntimeDefinition { Image = configuredImage, DefaultTimeout = 30 },
                    new WorkspaceTelemetryDefinition()),
                [new ContainerContext("agent", "agent-a", "user-a", workspaceRoot, "/workspace", configuredImage, 30, "512m", 1, [], new Dictionary<string, string>())],
                [],
                []);
            RuntimeImageAllowlist allowlist = new(
                1,
                [new RuntimeImageEntry("kautuka-base", "quay.io/devkrity/kautuka-base@" + imageDigest, null)]);
            ContainerContext workspace = Assert.Single(desiredState.Snapshot.DesiredWorkspaces) with
            {
                Image = allowlist.SelectReleasedSandboxImage(configuredImage),
                DesiredStateVersion = desiredState.Snapshot.AcceptedVersion,
            };
            workspace = workspace with { SpecFingerprint = WorkspaceContainerSpecFingerprint.Compute(workspace, []) };
            string inspectJson = CreateExistingSandboxInspectJson(workspace, containerId, ip, imageDigest);
            string ruleset = BoundaryRulesetRenderer.ResolveTemplate("kautuka_workload.nft.json.tmpl")
                .Replace("{{IP}}", ip, StringComparison.Ordinal);
            bool attemptedReplacement = false;
            var verifierEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var runner = new PodmanCommandRunner((args, _) =>
            {
                if (args[0] is "run" or "start" or "rm")
                {
                    attemptedReplacement = true;
                }

                if (args[0] == "image")
                {
                    return Task.FromResult(new PodmanCommandResult(0, imageDigest, string.Empty, ProcessTerminationReason.Completed));
                }

                if (args.Any(static argument => argument.Contains(".installed.json", StringComparison.Ordinal)))
                {
                    return Task.FromResult(new PodmanCommandResult(0, "{\"NormalizationVersion\":3,\"Contracts\":[]}", string.Empty, ProcessTerminationReason.Completed));
                }

                if (args.Contains("cat", StringComparer.Ordinal))
                {
                    return Task.FromResult(new PodmanCommandResult(0, "1", string.Empty, ProcessTerminationReason.Completed));
                }

                if (args[0] == "machine")
                {
                    verifierEntered.TrySetResult();
                    return Task.FromResult(new PodmanCommandResult(0, ruleset, string.Empty, ProcessTerminationReason.Completed));
                }

                return Task.FromResult(new PodmanCommandResult(0, inspectJson, string.Empty, ProcessTerminationReason.Completed));
            });
            var registry = new WorkloadRegistry();
            RegistryEntry desiredEntry = PodmanContainerTool.BuildSandboxRegistryEntry(workspace, ip, containerId);
            registry.Rebuild([desiredEntry with { Egress = "deny-all", Attribution = "none" }]);
            using var policyStore = new NetworkAccessPolicyStore(Path.Combine(root, "policy.json"));
            var launchGate = new WorkloadLaunchGate(new OpenStartupState(), policyStore);
            var sandboxTool = new PodmanContainerTool(
                runner,
                new WorkloadAddressAllocator(Path.Combine(root, "ip-cursor.json")),
                registry,
                new WorkloadPostStartVerifier(runner, desiredState),
                launchGate,
                desiredState,
                allowlist);
            var service = new WorkspaceContainerConnectorExecutionService(
                desiredState,
                runner,
                new McpbImageMaterializer(runner, NullLogger<McpbImageMaterializer>.Instance),
                sandboxTool,
                NullLogger<WorkspaceContainerConnectorExecutionService>.Instance,
                new WorkloadAddressAllocator(Path.Combine(root, "ip-cursor.json")),
                registry,
                new WorkloadPostStartVerifier(runner, desiredState),
                workloadLaunchGate: new WorkloadLaunchGate(new OpenStartupState(), policyStore));
            var hostEnvironment = new RecoveryTestHostEnvironment();
            IConfiguration configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?> { ["Kautuka:Mode"] = "dev", ["Kautuka:ContainerStateRoot"] = root })
                .Build();
            var runtimePaths = new WorkspaceRuntimePaths(configuration, hostEnvironment, NullLogger<WorkspaceRuntimePaths>.Instance);
            var sidecars = new WorkspaceSidecarSupervisor(
                WorkspaceSidecarCatalog.Empty,
                new RecoveryTestHttpClientFactory(),
                runner,
                Options.Create(new ManagerContainerStartupOptions()),
                NullLogger<WorkspaceSidecarSupervisor>.Instance,
                allowlist);
            var worker = new WorkspaceContainerSupervisorWorker(
                desiredState,
                new RecoveryTestRuntimeAdapter(allowlist),
                sidecars,
                service,
                runtimePaths,
                new WorkspaceReadinessGate(),
                new WorkspaceExecTracker(),
                Options.Create(new ManagerContainerStartupOptions()),
                NullLogger<WorkspaceContainerSupervisorWorker>.Instance,
                Path.Combine(root, "runtime-sockets"));

            try
            {
                await worker.StartAsync(TestContext.Current.CancellationToken);
                await verifierEntered.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
                DateTimeOffset deadline = DateTimeOffset.UtcNow.AddSeconds(5);
                while (!registry.IsVerified(ip, containerId) && DateTimeOffset.UtcNow < deadline)
                {
                    await Task.Delay(10, TestContext.Current.CancellationToken);
                }
                ContainerContext rawGrpcContext = WorkspaceGrpcContractMapper.ResolveWorkspace(
                    desiredState,
                    new WorkspaceContextMessage { ScopeType = "agent", ScopeId = "agent-a", UserId = "user-a" });
                await sandboxTool.SpawnSandboxAsync(rawGrpcContext, TestContext.Current.CancellationToken);
            }
            finally
            {
                await worker.StopAsync(CancellationToken.None);
                worker.Dispose();
                await service.DisposeAsync();
            }

            RegistryEntry recovered = Assert.Single(registry.GetAll());
            Assert.Equal(containerId, recovered.ContainerId);
            Assert.Equal("normal", recovered.Egress);
            Assert.True(registry.IsVerified(ip, containerId));
            Assert.False(attemptedReplacement);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ConnectorInspectionRetainsIdWhenLabelsAreMissing()
    {
        const string id = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
        string inspection = "[{\"Id\":\"" + id + "\",\"HostConfig\":{},\"Config\":{},\"State\":{\"Status\":\"exited\"}}]";
        var runner = new PodmanCommandRunner((_, _) => Task.FromResult(new PodmanCommandResult(0, inspection, string.Empty, ProcessTerminationReason.Completed)));
        await using var service = new WorkspaceContainerConnectorExecutionService(
            new WorkspaceDesiredStateStore(), runner,
            new McpbImageMaterializer(runner, NullLogger<McpbImageMaterializer>.Instance),
            new PodmanContainerTool(runner), NullLogger<WorkspaceContainerConnectorExecutionService>.Instance);
        MethodInfo inspect = typeof(WorkspaceContainerConnectorExecutionService)
            .GetMethod("InspectContainerStateAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
        Task task = (Task)inspect.Invoke(service, ["connector", null, null, TestContext.Current.CancellationToken])!;
        await task;
        object result = task.GetType().GetProperty("Result")!.GetValue(task)!;
        Assert.Equal(id, result.GetType().GetProperty("CanonicalContainerId")!.GetValue(result));
    }

    [Theory]
    [InlineData("")]
    [InlineData("invalid-id")]
    public async Task ConnectorRemovalDefersWhenInspectedIdIsInvalid(string id)
    {
        string inspection = "[{\"Id\":\"" + id + "\",\"HostConfig\":{},\"Config\":{\"Labels\":{}},\"State\":{\"Status\":\"exited\"}}]";
        bool removed = false;
        var runner = new PodmanCommandRunner((args, _) =>
        {
            if (args[0] == "rm") removed = true;
            return Task.FromResult(new PodmanCommandResult(0, inspection, string.Empty, ProcessTerminationReason.Completed));
        });
        await using var service = new WorkspaceContainerConnectorExecutionService(
            new WorkspaceDesiredStateStore(), runner,
            new McpbImageMaterializer(runner, NullLogger<McpbImageMaterializer>.Instance),
            new PodmanContainerTool(runner), NullLogger<WorkspaceContainerConnectorExecutionService>.Instance);
        MethodInfo remove = typeof(WorkspaceContainerConnectorExecutionService)
            .GetMethod("RemoveConnectorContainerAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
        bool result = await ((Task<bool>)remove.Invoke(service, ["connector", null, TestContext.Current.CancellationToken])!);
        Assert.False(result);
        Assert.False(removed);
    }

    [Fact]
    public async Task ConnectorRemovalUsesInspectedCanonicalIdAndKeepsReusedNameReplacement()
    {
        const string containerName = "kautuka-mcp-user-a-agent-a-shared-files";
        const string oldContainerId = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
        const string replacementContainerId = "1123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
        const string oldIp = "10.247.1.61";
        const string replacementIp = "10.247.1.62";
        string imageDigest = "sha256:" + new string('a', 64);
        var registry = new WorkloadRegistry();
        DateTimeOffset registered = DateTimeOffset.UtcNow;
        RegistryEntry oldEntry = new(
            oldIp, "user-a", "agent-a", oldContainerId, "connector", "user-a::shared", "isolated-with-agent-files", false,
            "normal", "user-agent", null, registered);
        RegistryEntry replacementEntry = oldEntry with { Ip = replacementIp, ContainerId = replacementContainerId };
        registry.Register(oldEntry);
        string initialInspectJson = CreateConnectorInspectJson(oldContainerId, string.Empty, imageDigest, "fingerprint", "user-a::shared", kind: "connector");
        string replacementInspectJson = CreateConnectorInspectJson(replacementContainerId, replacementIp, imageDigest, "fingerprint", "user-a::shared", kind: "connector");
        int inspectCount = 0;
        string? removalTarget = null;
        var runner = new PodmanCommandRunner((args, _) =>
        {
            if (args[0] == "inspect")
            {
                inspectCount++;
                string observed = inspectCount == 1 ? initialInspectJson : replacementInspectJson;
                return Task.FromResult(new PodmanCommandResult(0, observed, string.Empty, ProcessTerminationReason.Completed));
            }

            if (args[0] == "rm")
            {
                removalTarget = args[2];
                return Task.FromResult(new PodmanCommandResult(0, string.Empty, string.Empty, ProcessTerminationReason.Completed));
            }

            throw new InvalidOperationException("Unexpected Podman command: " + string.Join(' ', args));
        });
        await using var service = new WorkspaceContainerConnectorExecutionService(
            new WorkspaceDesiredStateStore(),
            runner,
            new McpbImageMaterializer(runner, NullLogger<McpbImageMaterializer>.Instance),
            new PodmanContainerTool(runner),
            NullLogger<WorkspaceContainerConnectorExecutionService>.Instance,
            workloadRegistry: registry);
        MethodInfo remove = typeof(WorkspaceContainerConnectorExecutionService)
            .GetMethod("RemoveConnectorContainerAsync", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("RemoveConnectorContainerAsync was not found.");
        MethodInfo inspect = typeof(WorkspaceContainerConnectorExecutionService)
            .GetMethod("InspectContainerStateAsync", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("InspectContainerStateAsync was not found.");

        Task inspectionTask = (Task)inspect.Invoke(service, [containerName, null, null, TestContext.Current.CancellationToken])!;
        await inspectionTask;
        object inspection = inspectionTask.GetType().GetProperty("Result")!.GetValue(inspectionTask)!;
        // The mutable name now points at B. Cleanup must use the identity captured from A.
        registry.Register(replacementEntry);
        bool removed = await ((Task<bool>)remove.Invoke(service, [containerName, inspection, TestContext.Current.CancellationToken])!);

        Assert.True(removed);
        Assert.Equal(1, inspectCount);
        Assert.Equal(oldContainerId, removalTarget);
        RegistryEntry retained = Assert.Single(registry.GetAll());
        Assert.Equal(replacementIp, retained.Ip);
        Assert.Equal(replacementContainerId, retained.ContainerId);
        Assert.Null(registry.FindByIp(oldIp));
    }

    [Fact]
    public async Task DesiredReconcileReverifiesAndRestoresRunningSystemwideConnectorWithTrustedCompatProfile()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Skip("This test exercises Podman Machine command behavior without requiring a live Podman daemon.");
        }

        string root = Path.Combine(Path.GetTempPath(), "nac-reused-connector-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        const string ip = "10.247.1.32";
        const string containerId = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
        string imageDigest = "sha256:" + new string('a', 64);
        try
        {
            string workspaceRoot = Path.Combine(root, "workspace");
            Directory.CreateDirectory(workspaceRoot);
            var workspace = new ContainerContext(
                "user", "user-a", "user-a", workspaceRoot, "/workspace", imageDigest, 30, "512m", 1, [], new Dictionary<string, string>());
            WorkspaceMcpContainerConnectorDefinition connector = new(
                "user-a::shared-service", imageDigest, string.Empty, [], true, false, null, null, [], 30, 600)
            {
                CatalogScope = "system",
            };
            var desiredState = new WorkspaceDesiredStateStore();
            _ = desiredState.ApplyDesiredState(
                1,
                WorkspaceDesiredStateSyncReason.Bootstrap,
                new ManagerRuntimeSettings(
                    new ManagerWorkspaceDefinition { BaseRoot = root },
                    new ManagerSandboxRuntimeDefinition { Image = imageDigest, DefaultTimeout = 30 },
                    new WorkspaceTelemetryDefinition()),
                [workspace],
                [connector],
                []);
            WorkspaceMcpContainerConnectorDefinition desiredConnector = Assert.Single(desiredState.Snapshot.DesiredContainerConnectors);

            var compatibilityStore = new ImageCompatibilityStore(Path.Combine(root, "image-compat.json"));
            ImageCompatProfile profile = compatibilityStore.SetProfile(
                new NetworkAccessActor("admin", IsAdmin: true),
                imageDigest,
                [ImageCompatibilityStore.WritableRootfs],
                desiredConnector.ConnectorId,
                reason: "restart recovery test");
            ConnectorContainerSpecBuilder.ConnectorIdentity identity = ConnectorContainerSpecBuilder.ResolveIdentity(desiredConnector, workspace, purpose: null);
            string fingerprint = ConnectorContainerSpecBuilder.ComputeWorkloadFingerprint(
                desiredConnector,
                identity,
                WorkspaceMcpExecutionContext.Isolated,
                profile);
            string ruleset = BoundaryRulesetRenderer.ResolveTemplate("kautuka_workload.nft.json.tmpl")
                .Replace("{{IP}}", ip, StringComparison.Ordinal);
            string inspectJson = CreateConnectorInspectJson(containerId, ip, imageDigest, fingerprint, desiredConnector.ConnectorId);
            var runner = new PodmanCommandRunner((args, _) => Task.FromResult(args[0] switch
            {
                "inspect" => new PodmanCommandResult(0, inspectJson, string.Empty, ProcessTerminationReason.Completed),
                "image" => new PodmanCommandResult(0, args.Contains("--format", StringComparer.Ordinal) ? imageDigest : "[{\"Id\":\"" + imageDigest + "\",\"RepoDigests\":[\"example/image@" + imageDigest + "\"]}]", string.Empty, ProcessTerminationReason.Completed),
                "machine" when args.Contains("cat", StringComparer.Ordinal) => new PodmanCommandResult(0, "1", string.Empty, ProcessTerminationReason.Completed),
                "machine" => new PodmanCommandResult(0, ruleset, string.Empty, ProcessTerminationReason.Completed),
                _ => new PodmanCommandResult(0, string.Empty, string.Empty, ProcessTerminationReason.Completed),
            }));
            var registry = new WorkloadRegistry();
            var quarantined = new RegistryEntry(
                ip, identity.UserId, null, containerId, "connector", desiredConnector.ConnectorId, "isolated", true,
                "deny-all", "none", string.Join(',', profile.Relaxations), DateTimeOffset.UtcNow);
            registry.Rebuild([quarantined]);
            using var policyStore = new NetworkAccessPolicyStore(Path.Combine(root, "policy.json"));
            await using var service = new WorkspaceContainerConnectorExecutionService(
                desiredState,
                runner,
                new McpbImageMaterializer(runner, NullLogger<McpbImageMaterializer>.Instance),
                new PodmanContainerTool(runner),
                NullLogger<WorkspaceContainerConnectorExecutionService>.Instance,
                new WorkloadAddressAllocator(Path.Combine(root, "ip-cursor.json")),
                registry,
                new WorkloadPostStartVerifier(runner, desiredState),
                compatibilityStore,
                new WorkloadLaunchGate(new OpenStartupState(), policyStore));

            _ = await service.ReconcileDesiredConnectorsAsync([desiredConnector], TestContext.Current.CancellationToken);

            RegistryEntry recovered = Assert.Single(registry.GetAll());
            Assert.Equal(containerId, recovered.ContainerId);
            Assert.Equal("normal", recovered.Egress);
            Assert.Equal("user", recovered.Attribution);
            Assert.True(recovered.Systemwide);
            Assert.Equal("writable-rootfs", recovered.CompatProfile);
            Assert.True(registry.IsVerified(ip, containerId));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task SandboxRemainsQuarantinedWhenDesiredStateChangesDuringVerification()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Skip("This test uses the fake Podman Machine inspection path.");
        }

        string root = Path.Combine(Path.GetTempPath(), "nac-stale-sandbox-promotion-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        const string containerId = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
        string imageDigest = "sha256:" + new string('b', 64);
        try
        {
            string workspaceRoot = Path.Combine(root, "workspace");
            Directory.CreateDirectory(workspaceRoot);
            var initialWorkspace = new ContainerContext(
                "agent", "agent-a", "user-a", workspaceRoot, "/workspace", imageDigest, 30, "512m", 1, [], new Dictionary<string, string>());
            var desiredState = new WorkspaceDesiredStateStore();
            ManagerRuntimeSettings settings = new(
                new ManagerWorkspaceDefinition { BaseRoot = root },
                new ManagerSandboxRuntimeDefinition { Image = imageDigest, DefaultTimeout = 30 },
                new WorkspaceTelemetryDefinition());
            _ = desiredState.ApplyDesiredState(1, WorkspaceDesiredStateSyncReason.Bootstrap, settings, [initialWorkspace], [], []);
            ContainerContext launchContext = Assert.Single(desiredState.Snapshot.DesiredWorkspaces);
            var registry = new WorkloadRegistry();
            string? allocatedIp = null;
            bool started = false;
            bool updatedDesiredState = false;
            string ruleset = string.Empty;
            string inspectJson = string.Empty;
            var runner = new PodmanCommandRunner((args, _) =>
            {
                if (args[0] == "inspect" && !started)
                {
                    return Task.FromResult(new PodmanCommandResult(125, string.Empty, "not found", ProcessTerminationReason.NonZeroExit));
                }

                if (args[0] == "image")
                {
                    return Task.FromResult(new PodmanCommandResult(0, imageDigest, string.Empty, ProcessTerminationReason.Completed));
                }

                if (args[0] == "container")
                {
                    return Task.FromResult(new PodmanCommandResult(1, string.Empty, "not found", ProcessTerminationReason.NonZeroExit));
                }

                if (args[0] == "run")
                {
                    started = true;
                    allocatedIp = Assert.Single(registry.GetAll()).Ip;
                    inspectJson = CreateSandboxVerifierInspectJson(containerId, allocatedIp);
                    ruleset = BoundaryRulesetRenderer.ResolveTemplate("kautuka_workload.nft.json.tmpl")
                        .Replace("{{IP}}", allocatedIp, StringComparison.Ordinal);
                    return Task.FromResult(new PodmanCommandResult(0, containerId, string.Empty, ProcessTerminationReason.Completed));
                }

                if (args[0] == "inspect")
                {
                    if (!updatedDesiredState)
                    {
                        updatedDesiredState = true;
                        ContainerContext changedWorkspace = initialWorkspace with { ContainerWorkspacePath = "/workspace-v2" };
                        desiredState.ApplyDesiredState(2, WorkspaceDesiredStateSyncReason.Bootstrap, settings, [changedWorkspace], [], []);
                    }

                    return Task.FromResult(new PodmanCommandResult(0, inspectJson, string.Empty, ProcessTerminationReason.Completed));
                }

                if (args[0] == "machine" && args.Contains("cat", StringComparer.Ordinal))
                {
                    return Task.FromResult(new PodmanCommandResult(0, "1", string.Empty, ProcessTerminationReason.Completed));
                }

                if (args[0] == "machine")
                {
                    return Task.FromResult(new PodmanCommandResult(0, ruleset, string.Empty, ProcessTerminationReason.Completed));
                }

                return Task.FromResult(new PodmanCommandResult(0, string.Empty, string.Empty, ProcessTerminationReason.Completed));
            });
            using var policyStore = new NetworkAccessPolicyStore(Path.Combine(root, "policy.json"));
            var tool = new PodmanContainerTool(
                runner,
                new WorkloadAddressAllocator(Path.Combine(root, "ip-cursor.json")),
                registry,
                new WorkloadPostStartVerifier(runner, desiredState),
                new WorkloadLaunchGate(new OpenStartupState(), policyStore),
                desiredState);

            InvalidOperationException failure = await Assert.ThrowsAsync<InvalidOperationException>(
                () => tool.SpawnSandboxAsync(launchContext, TestContext.Current.CancellationToken));

            Assert.Contains("desired state changed", failure.Message, StringComparison.OrdinalIgnoreCase);
            RegistryEntry retained = Assert.Single(registry.GetAll());
            Assert.Equal(containerId, retained.ContainerId);
            Assert.Equal("deny-all", retained.Egress);
            Assert.Equal("none", retained.Attribution);
            Assert.NotNull(allocatedIp);
            Assert.False(registry.IsVerified(allocatedIp, containerId));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task EffectiveEnvironmentOverrideAndDisabledInternalConnectorCanReuseAfterRestart()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Skip("This test exercises Podman Machine command behavior without requiring a live Podman daemon.");
        }

        await AssertExistingConnectorReuseAsync(internalDisabled: false, TestContext.Current.CancellationToken);
        await AssertExistingConnectorReuseAsync(internalDisabled: true, TestContext.Current.CancellationToken);
        await AssertExistingConnectorReuseAsync(internalDisabled: false, TestContext.Current.CancellationToken, replaceAuthorityDuringVerification: true);
    }

    private static async Task AssertExistingConnectorReuseAsync(
        bool internalDisabled,
        CancellationToken cancellationToken,
        bool replaceAuthorityDuringVerification = false)
    {
        string root = Path.Combine(Path.GetTempPath(), "nac-effective-connector-reuse-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        const string ip = "10.247.1.42";
        const string containerId = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
        string imageDigest = "sha256:" + new string(internalDisabled ? 'd' : 'c', 64);
        try
        {
            string workspaceRoot = Path.Combine(root, "workspace");
            Directory.CreateDirectory(workspaceRoot);
            var workspace = new ContainerContext(
                "agent", "agent-a", "user-a", workspaceRoot, "/workspace", imageDigest, 30, "512m", 1, [], new Dictionary<string, string>());
            string connectorId = internalDisabled ? "system::internal-reuse" : "user-a::effective-reuse";
            var baseEnvironment = internalDisabled
                ? (IReadOnlyList<KeyValuePair<string, string>>)Array.Empty<KeyValuePair<string, string>>()
                : [new KeyValuePair<string, string>("BASE_SETTING", "baseline")];
            WorkspaceMcpContainerConnectorDefinition connector = new(
                connectorId,
                imageDigest,
                "custom-mcp",
                [],
                Enabled: !internalDisabled,
                IsInternalSystemConnector: internalDisabled,
                BundlePath: null,
                ServerType: null,
                Environment: baseEnvironment,
                ReadinessTimeoutSeconds: 30,
                JitCooldownSeconds: 600)
            {
                CatalogScope = internalDisabled ? "system" : "user",
            };
            var desiredState = new WorkspaceDesiredStateStore();
            _ = desiredState.ApplyDesiredState(
                1,
                WorkspaceDesiredStateSyncReason.Bootstrap,
                new ManagerRuntimeSettings(
                    new ManagerWorkspaceDefinition { BaseRoot = root },
                    new ManagerSandboxRuntimeDefinition { Image = imageDigest, DefaultTimeout = 30 },
                    new WorkspaceTelemetryDefinition()),
                [workspace],
                [connector],
                []);
            WorkspaceMcpContainerConnectorDefinition storedConnector = Assert.Single(desiredState.Snapshot.DesiredContainerConnectors);
            ContainerContext storedWorkspace = Assert.Single(desiredState.Snapshot.DesiredWorkspaces);

            WorkspaceMcpContainerConnectorDefinition effectiveConnector;
            IReadOnlyList<KeyValuePair<string, string>> effectiveEnvironment;
            if (internalDisabled)
            {
                MethodInfo resolveEffective = typeof(WorkspaceContainerConnectorExecutionService)
                    .GetMethod("ResolveEffectiveConnectorDefinition", BindingFlags.Instance | BindingFlags.NonPublic)
                    ?? throw new InvalidOperationException("ResolveEffectiveConnectorDefinition was not found.");
                var runnerForResolution = new PodmanCommandRunner((_, _) => Task.FromResult(new PodmanCommandResult(0, string.Empty, string.Empty, ProcessTerminationReason.Completed)));
                await using var resolutionService = new WorkspaceContainerConnectorExecutionService(
                    desiredState,
                    runnerForResolution,
                    new McpbImageMaterializer(runnerForResolution, NullLogger<McpbImageMaterializer>.Instance),
                    new PodmanContainerTool(runnerForResolution),
                    NullLogger<WorkspaceContainerConnectorExecutionService>.Instance);
                effectiveConnector = (WorkspaceMcpContainerConnectorDefinition)resolveEffective.Invoke(
                    resolutionService,
                    [connectorId, null, 0, true])!;
                effectiveEnvironment = [];
            }
            else
            {
                MethodInfo buildEffective = typeof(WorkspaceContainerConnectorExecutionService)
                    .GetMethod("BuildEffectiveConnectorDefinition", BindingFlags.Static | BindingFlags.NonPublic)
                    ?? throw new InvalidOperationException("BuildEffectiveConnectorDefinition was not found.");
                effectiveConnector = (WorkspaceMcpContainerConnectorDefinition)buildEffective.Invoke(
                    null,
                    [storedConnector, new Dictionary<string, string>(StringComparer.Ordinal) { ["CUSTOM_SETTING"] = "override" }, 77, null])!;
                effectiveEnvironment = effectiveConnector.Environment;
            }

            ConnectorContainerSpecBuilder.ConnectorIdentity identity = ConnectorContainerSpecBuilder.ResolveIdentity(
                effectiveConnector,
                storedWorkspace,
                purpose: null);
            string fingerprint = ConnectorContainerSpecBuilder.ComputeWorkloadFingerprint(
                effectiveConnector,
                identity,
                WorkspaceMcpExecutionContext.Isolated);
            bool systemwide = identity.Systemwide;
            string? agentId = string.IsNullOrWhiteSpace(identity.AgentId) ? null : identity.AgentId;
            string inspectJson = CreateConnectorInspectJson(
                containerId,
                ip,
                imageDigest,
                fingerprint,
                connectorId,
                systemwide,
                agentId,
                identity.Egress,
                identity.Kind,
                effectiveEnvironment);
            string ruleset = BoundaryRulesetRenderer.ResolveTemplate("kautuka_workload.nft.json.tmpl")
                .Replace("{{IP}}", ip, StringComparison.Ordinal);
            bool authorityReplaced = false;
            var runner = new PodmanCommandRunner((args, _) => Task.FromResult(args[0] switch
            {
                "inspect" => InspectConnector(),
                "image" => new PodmanCommandResult(0, imageDigest, string.Empty, ProcessTerminationReason.Completed),
                "machine" when args.Contains("cat", StringComparer.Ordinal) => new PodmanCommandResult(0, "1", string.Empty, ProcessTerminationReason.Completed),
                "machine" => new PodmanCommandResult(0, ruleset, string.Empty, ProcessTerminationReason.Completed),
                _ => new PodmanCommandResult(0, string.Empty, string.Empty, ProcessTerminationReason.Completed),
            }));

            PodmanCommandResult InspectConnector()
            {
                if (replaceAuthorityDuringVerification && !authorityReplaced)
                {
                    authorityReplaced = true;
                    WorkspaceMcpContainerConnectorDefinition changed = connector with
                    {
                        Image = "sha256:" + new string('e', 64),
                    };
                    desiredState.ApplyDesiredState(
                        1,
                        WorkspaceDesiredStateSyncReason.Bootstrap,
                        new ManagerRuntimeSettings(
                            new ManagerWorkspaceDefinition { BaseRoot = root },
                            new ManagerSandboxRuntimeDefinition { Image = imageDigest, DefaultTimeout = 30 },
                            new WorkspaceTelemetryDefinition()),
                        [workspace],
                        [changed],
                        [],
                        publisherEpoch: "replacement-epoch");
                }

                return new PodmanCommandResult(0, inspectJson, string.Empty, ProcessTerminationReason.Completed);
            }
            var registry = new WorkloadRegistry();
            registry.Rebuild(
            [
                new RegistryEntry(
                    ip,
                    string.IsNullOrWhiteSpace(identity.UserId) ? null : identity.UserId,
                    agentId,
                    containerId,
                    identity.Kind,
                    connectorId,
                    "isolated",
                    identity.Systemwide,
                    "deny-all",
                    "none",
                    null,
                    DateTimeOffset.UtcNow),
            ]);
            using var policyStore = new NetworkAccessPolicyStore(Path.Combine(root, "policy.json"));
            await using var service = new WorkspaceContainerConnectorExecutionService(
                desiredState,
                runner,
                new McpbImageMaterializer(runner, NullLogger<McpbImageMaterializer>.Instance),
                new PodmanContainerTool(runner),
                NullLogger<WorkspaceContainerConnectorExecutionService>.Instance,
                workloadRegistry: registry,
                postStartVerifier: new WorkloadPostStartVerifier(runner, desiredState),
                workloadLaunchGate: new WorkloadLaunchGate(new OpenStartupState(), policyStore));

            MethodInfo createTransport = typeof(WorkspaceContainerConnectorExecutionService)
                .GetMethod("CreateTransportAsync", BindingFlags.Instance | BindingFlags.NonPublic)
                ?? throw new InvalidOperationException("CreateTransportAsync was not found.");
            Task pending = (Task)createTransport.Invoke(service,
            [
                effectiveConnector,
                true,
                internalDisabled,
                storedWorkspace,
                WorkspaceMcpExecutionContext.Isolated,
                false,
                null,
                cancellationToken,
            ])!;
            if (replaceAuthorityDuringVerification)
            {
                await Assert.ThrowsAsync<InvalidOperationException>(async () => await pending.ConfigureAwait(false));
            }
            else
            {
                await pending.ConfigureAwait(false);
                object launch = pending.GetType().GetProperty("Result")!.GetValue(pending)!;
                MethodInfo releaseLease = typeof(WorkspaceContainerConnectorExecutionService)
                    .GetMethod("ReleaseLaunchLeaseAsync", BindingFlags.Static | BindingFlags.NonPublic)
                    ?? throw new InvalidOperationException("ReleaseLaunchLeaseAsync was not found.");
                await ((ValueTask)releaseLease.Invoke(null, [launch])!).ConfigureAwait(false);
            }

            RegistryEntry recovered = Assert.Single(registry.GetAll());
            if (replaceAuthorityDuringVerification)
            {
                Assert.Equal("deny-all", recovered.Egress);
                Assert.Equal("none", recovered.Attribution);
                Assert.False(registry.IsVerified(ip, containerId));
            }
            else
            {
                Assert.Equal(identity.Egress, recovered.Egress);
                Assert.Equal(identity.Systemwide ? "none" : "user-agent", recovered.Attribution);
                Assert.True(registry.IsVerified(ip, containerId));
            }
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task SandboxLaunchDoesNotPublishNormalEgressBeforePostStartVerification()
    {
        string root = Path.Combine(Path.GetTempPath(), "nac-preverify-grant-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var registry = new WorkloadRegistry();
            const string canonicalContainerId = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
            string? egressObservedDuringRun = null;
            bool started = false;
            var runner = new PodmanCommandRunner((args, _) =>
            {
                return Task.FromResult(args[0] switch
                {
                    "inspect" when !started => new PodmanCommandResult(125, string.Empty, "not found", ProcessTerminationReason.NonZeroExit),
                    // A failed security check drives the verifier cleanup after the run callback.
                    "inspect" => new PodmanCommandResult(0, "[{\"Id\":\"" + canonicalContainerId + "\",\"HostConfig\":{\"SecurityOpt\":[]}}]", string.Empty, ProcessTerminationReason.Completed),
                    "container" => new PodmanCommandResult(1, string.Empty, "not found", ProcessTerminationReason.NonZeroExit),
                    "kill" or "rm" => new PodmanCommandResult(125, string.Empty, "simulated cleanup failure", ProcessTerminationReason.NonZeroExit),
                    "image" => new PodmanCommandResult(0, "sha256:" + new string('a', 64), string.Empty, ProcessTerminationReason.Completed),
                    "run" => StartAndCaptureGrant(),
                    _ => new PodmanCommandResult(0, string.Empty, string.Empty, ProcessTerminationReason.Completed),
                });
            });

            PodmanCommandResult StartAndCaptureGrant()
            {
                started = true;
                egressObservedDuringRun = Assert.Single(registry.GetAll()).Egress;
                return new PodmanCommandResult(0, canonicalContainerId, string.Empty, ProcessTerminationReason.Completed);
            }

            using var policyStore = new NetworkAccessPolicyStore(Path.Combine(root, "policy.json"));
            var launchGate = new WorkloadLaunchGate(new OpenStartupState(), policyStore);
            var tool = new PodmanContainerTool(
                runner,
                new WorkloadAddressAllocator(Path.Combine(root, "ip-cursor.json")),
                registry,
                new WorkloadPostStartVerifier(runner),
                launchGate);
            var context = new ContainerContext(
                "agent", "agent-a", "user-a", Path.Combine(root, "workspace"), "/workspace", "localhost/test:latest", 30, "512m", 1, [], new Dictionary<string, string>())
            {
                WorkspaceBaseRoot = root,
            };

            await Assert.ThrowsAsync<InvalidOperationException>(() => tool.SpawnSandboxAsync(context, TestContext.Current.CancellationToken));

            Assert.Equal("deny-all", egressObservedDuringRun);
            RegistryEntry retained = Assert.Single(registry.GetAll());
            Assert.Equal("deny-all", retained.Egress);
            Assert.Equal("none", retained.Attribution);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task RegistryRebuildDoesNotTreatRuntimeLabelsAsPostStartVerification()
    {
        string root = Path.Combine(Path.GetTempPath(), "nac-rebuild-unverified-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        const string containerId = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
        var runner = new PodmanCommandRunner((args, _) => Task.FromResult(args[0] switch
        {
            "ps" => new PodmanCommandResult(0, "[{\"Id\":\"" + containerId + "\"}]", string.Empty, ProcessTerminationReason.Completed),
            "inspect" => new PodmanCommandResult(0,
                "[{\"Config\":{\"Labels\":{\"io.kautuka.user\":\"user-a\",\"io.kautuka.agent\":\"agent-a\",\"io.kautuka.exec-context\":\"agent-sandbox\",\"io.kautuka.kind\":\"agent-sandbox\",\"io.kautuka.egress\":\"normal\"}},\"NetworkSettings\":{\"Networks\":{\"kautuka-workload\":{\"IPAddress\":\"10.247.1.10\"}}}}]",
                string.Empty,
                ProcessTerminationReason.Completed),
            _ => new PodmanCommandResult(0, string.Empty, string.Empty, ProcessTerminationReason.Completed),
        }));

        try
        {
            var registry = new WorkloadRegistry();
            var manager = new EgressTopologyManager(
                runner,
                new WorkloadAddressAllocator(Path.Combine(root, "ip-cursor.json")),
                new WorkloadPostStartVerifier(runner),
                registry,
                root,
                NullLogger<EgressTopologyManager>.Instance);
            await using (manager.ConfigureAwait(false))
            {
                InvalidOperationException failure = await Assert.ThrowsAnyAsync<InvalidOperationException>(
                    () => manager.RebuildRegistryFromLiveContainersAsync(registry, TestContext.Current.CancellationToken));
                Assert.Contains("Startup must remain closed", failure.Message, StringComparison.Ordinal);
            }

            Assert.Empty(registry.GetAll());
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData("removed", true)]
    [InlineData("absent", true)]
    [InlineData("running", false)]
    [InlineData("unknown", false)]
    public async Task EphemeralCleanupDeregistersOnlyAfterRemovalOrAbsenceIsConfirmed(string outcome, bool shouldDeregister)
    {
        string root = Path.Combine(Path.GetTempPath(), "nac-ephemeral-cleanup-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        const string ip = "10.247.1.20";
        const string containerName = "temporary-connector";
        const string containerId = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
        string? cleanupTarget = null;
        try
        {
            var registry = new WorkloadRegistry();
            registry.Register(new RegistryEntry(
                ip, "user-a", "agent-a", containerId, "connector", "connector-a", "isolated", false,
                "normal", "user-agent", null, DateTimeOffset.UtcNow));
            var runner = new PodmanCommandRunner((args, _) =>
            {
                if (args[0] == "rm")
                {
                    cleanupTarget = args[2];
                    if (outcome == "removed")
                    {
                        return Task.FromResult(new PodmanCommandResult(0, string.Empty, string.Empty, ProcessTerminationReason.Completed));
                    }

                    if (outcome == "unknown")
                    {
                        throw new IOException("simulated rm transport failure");
                    }

                    return Task.FromResult(new PodmanCommandResult(125, string.Empty, "simulated rm failure", ProcessTerminationReason.NonZeroExit));
                }

                if (args[0] == "inspect")
                {
                    Assert.Equal(containerId, args[1]);
                    if (outcome == "running")
                    {
                        return Task.FromResult(new PodmanCommandResult(
                            0,
                            "[{\"Id\":\"" + containerId + "\",\"HostConfig\":{},\"Config\":{},\"State\":{\"Status\":\"running\"}}]",
                            string.Empty,
                            ProcessTerminationReason.Completed));
                    }

                    return Task.FromResult(new PodmanCommandResult(125, string.Empty, "not found", ProcessTerminationReason.NonZeroExit));
                }

                if (args[0] == "container")
                {
                    return Task.FromResult(outcome == "absent"
                        ? new PodmanCommandResult(1, string.Empty, "not found", ProcessTerminationReason.NonZeroExit)
                        : new PodmanCommandResult(0, string.Empty, string.Empty, ProcessTerminationReason.Completed));
                }

                throw new InvalidOperationException("Unexpected Podman command: " + string.Join(' ', args));
            });
            await using var service = new WorkspaceContainerConnectorExecutionService(
                new WorkspaceDesiredStateStore(),
                runner,
                new McpbImageMaterializer(runner, NullLogger<McpbImageMaterializer>.Instance),
                new PodmanContainerTool(runner),
                NullLogger<WorkspaceContainerConnectorExecutionService>.Instance,
                new WorkloadAddressAllocator(Path.Combine(root, "ip-cursor.json")),
                registry);

            Type workloadType = typeof(WorkspaceContainerConnectorExecutionService)
                .GetNestedType("EphemeralWorkload", BindingFlags.NonPublic)
                ?? throw new InvalidOperationException("EphemeralWorkload was not found.");
            object workload = Activator.CreateInstance(
                workloadType,
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                binder: null,
                args: [containerName, ip, null],
                culture: null)!;
            workloadType.GetProperty("VerifiedContainerId")!.SetValue(workload, containerId);
            MethodInfo complete = typeof(WorkspaceContainerConnectorExecutionService)
                .GetMethod("CompleteEphemeralWorkloadAsync", BindingFlags.Instance | BindingFlags.NonPublic)
                ?? throw new InvalidOperationException("CompleteEphemeralWorkloadAsync was not found.");

            Assert.True(await ((Task<bool>)complete.Invoke(service, [workload])!).ConfigureAwait(false));
            Assert.Equal(containerId, cleanupTarget);

            if (shouldDeregister)
            {
                Assert.Empty(registry.GetAll());
            }
            else
            {
                RegistryEntry quarantined = Assert.Single(registry.GetAll());
                Assert.Equal(containerId, quarantined.ContainerId);
                Assert.Equal("deny-all", quarantined.Egress);
                Assert.Equal("none", quarantined.Attribution);
            }
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static string CreateConnectorInspectJson(
        string containerId,
        string ip,
        string imageDigest,
        string fingerprint,
        string connectorId,
        bool systemwide = true,
        string? agentId = null,
        string egress = "normal",
        string kind = "connector",
        IReadOnlyList<KeyValuePair<string, string>>? connectorEnvironment = null)
        => new JsonObject
        {
            ["Id"] = containerId,
            ["Image"] = imageDigest,
            ["State"] = new JsonObject { ["Status"] = "running", ["Pid"] = Environment.ProcessId },
            ["Config"] = new JsonObject
            {
                ["Image"] = imageDigest,
                ["User"] = "65532:65532",
                ["Env"] = CreateConnectorEnvironment(connectorEnvironment),
                ["Labels"] = new JsonObject
                {
                    ["kautuka.desired-version"] = "1",
                    ["kautuka.spec-fingerprint"] = fingerprint,
                    ["io.kautuka.user"] = "user-a",
                    ["io.kautuka.agent"] = agentId ?? string.Empty,
                    ["io.kautuka.connector"] = connectorId,
                    ["io.kautuka.exec-context"] = "isolated",
                    ["io.kautuka.kind"] = kind,
                    ["io.kautuka.systemwide"] = systemwide ? "true" : "false",
                    ["io.kautuka.egress"] = egress,
                    ["io.kautuka.compat"] = ImageCompatibilityStore.WritableRootfs,
                },
            },
            ["HostConfig"] = new JsonObject
            {
                ["NetworkMode"] = "bridge",
                ["Binds"] = new JsonArray(),
                ["CapAdd"] = new JsonArray(),
                ["CapDrop"] = new JsonArray("ALL"),
                ["Privileged"] = false,
                ["Devices"] = new JsonArray(),
                ["ReadonlyRootfs"] = true,
                ["PidsLimit"] = 128,
                ["SecurityOpt"] = new JsonArray("no-new-privileges"),
                ["Tmpfs"] = new JsonObject { ["/tmp"] = "rw,noexec,nosuid" },
            },
            ["NetworkSettings"] = new JsonObject
            {
                ["Networks"] = new JsonObject
                {
                    ["kautuka-workload"] = new JsonObject { ["IPAddress"] = ip },
                },
            },
            ["Mounts"] = new JsonArray(),
            ["EffectiveCaps"] = new JsonArray(),
            ["BoundingCaps"] = new JsonArray(),
        }.ToJsonString();

    private static JsonArray CreateConnectorEnvironment(IReadOnlyList<KeyValuePair<string, string>>? connectorEnvironment)
    {
        var environment = new JsonArray("HOME=/tmp", "NPM_CONFIG_CACHE=/tmp/.npm", "PIP_CACHE_DIR=/tmp/.pip", "UV_CACHE_DIR=/tmp/.uv");
        if (connectorEnvironment is not null)
        {
            foreach ((string key, string value) in connectorEnvironment)
            {
                if (ConnectorContainerSpecBuilder.ForwardsEnvironmentKey(key) && !string.IsNullOrWhiteSpace(value))
                {
                    environment.Add(key + "=" + value);
                }
            }
        }

        return environment;
    }

    private static string CreateSandboxVerifierInspectJson(string containerId, string ip)
        => new JsonObject
        {
            ["Id"] = containerId,
            ["HostConfig"] = new JsonObject
            {
                ["SecurityOpt"] = new JsonArray("no-new-privileges"),
                ["Sysctls"] = new JsonObject { ["net.ipv6.conf.all.disable_ipv6"] = "1" },
                ["Privileged"] = false,
                ["CapAdd"] = new JsonArray(),
                ["CapDrop"] = new JsonArray("ALL"),
                ["ReadonlyRootfs"] = true,
                ["NetworkMode"] = "kautuka-workload",
                ["Devices"] = new JsonArray(),
                ["PidMode"] = "private",
                ["IpcMode"] = "private",
                ["UtsMode"] = "private",
                ["CgroupMode"] = "private",
            },
            ["Config"] = new JsonObject
            {
                ["User"] = "65532:65532",
            },
            ["State"] = new JsonObject { ["Pid"] = Environment.ProcessId },
            ["NetworkSettings"] = new JsonObject
            {
                ["Networks"] = new JsonObject
                {
                    ["kautuka-workload"] = new JsonObject { ["IPAddress"] = ip, ["GlobalIPv6Address"] = string.Empty },
                },
            },
            ["Mounts"] = new JsonArray(),
            ["EffectiveCaps"] = new JsonArray(),
            ["BoundingCaps"] = new JsonArray(),
        }.ToJsonString();

    private static string CreateExistingSandboxInspectJson(ContainerContext context, string containerId, string ip, string imageDigest)
        => new JsonObject
        {
            ["Id"] = containerId,
            ["Image"] = imageDigest,
            ["State"] = new JsonObject { ["Status"] = "running", ["Pid"] = Environment.ProcessId },
            ["Config"] = new JsonObject
            {
                ["User"] = "65532:65532",
                ["Env"] = new JsonArray(),
                ["Labels"] = new JsonObject
                {
                    ["kautuka.desired-version"] = context.DesiredStateVersion.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    ["kautuka.spec-fingerprint"] = context.SpecFingerprint,
                },
            },
            ["HostConfig"] = new JsonObject
            {
                ["NetworkMode"] = "kautuka-workload",
                ["Binds"] = new JsonArray($"{context.HostWorkspacePath}:{context.ContainerWorkspacePath}:rw"),
                ["CapAdd"] = new JsonArray(),
                ["CapDrop"] = new JsonArray("ALL"),
                ["Privileged"] = false,
                ["Devices"] = new JsonArray(),
                ["ReadonlyRootfs"] = true,
                ["PidsLimit"] = 256,
                ["SecurityOpt"] = new JsonArray("no-new-privileges"),
                ["Tmpfs"] = new JsonObject
                {
                    ["/tmp"] = "rw,noexec,nosuid,size=67108864",
                    ["/home/node/.kautuka"] = "rw,noexec,nosuid,size=16777216",
                },
                ["Sysctls"] = new JsonObject { ["net.ipv6.conf.all.disable_ipv6"] = "1" },
                ["PidMode"] = "private",
                ["IpcMode"] = "private",
                ["UtsMode"] = "private",
                ["CgroupMode"] = "private",
            },
            ["NetworkSettings"] = new JsonObject
            {
                ["Networks"] = new JsonObject
                {
                    ["kautuka-workload"] = new JsonObject { ["IPAddress"] = ip, ["GlobalIPv6Address"] = string.Empty },
                },
            },
            ["Mounts"] = new JsonArray(new JsonObject
            {
                ["Type"] = "bind",
                ["Source"] = context.HostWorkspacePath,
                ["Destination"] = context.ContainerWorkspacePath,
                ["RW"] = true,
            }),
            ["EffectiveCaps"] = new JsonArray(),
            ["BoundingCaps"] = new JsonArray(),
        }.ToJsonString();

    private sealed class OpenStartupState : IWorkloadStartupState
    {
        public bool IsStartupGateOpen => true;
    }

    private sealed class RecoveryTestRuntimeAdapter(RuntimeImageAllowlist allowlist) : IManagerRuntimeAdapter
    {
        public void ApplyRuntimeSettings(ManagerRuntimeSettings runtimeSettings) { }

        public Task<bool> EnsureReadyAsync(ManagerRuntimeSettings runtimeSettings, CancellationToken cancellationToken)
            => Task.FromResult(true);

        public Task<string?> PrepareSandboxImageAsync(ManagerSandboxRuntimeDefinition sandbox, CancellationToken cancellationToken)
            => Task.FromResult<string?>(null);

        public string ResolveSandboxImage(string configuredImage)
            => allowlist.SelectReleasedSandboxImage(configuredImage);

        public Task<string[]> GetManagedContainerNamesAsync(IReadOnlyCollection<string> trackedContainers, CancellationToken cancellationToken)
            => Task.FromResult(Array.Empty<string>());

        public Task StopContainersAsync(IReadOnlyCollection<string> containerNames, CancellationToken cancellationToken)
            => Task.CompletedTask;

        public Task<ManagedContainerReconcileResult> RemoveOrphanedManagedContainersAsync(
            string role,
            IReadOnlyCollection<string> desiredContainerNames,
            CancellationToken cancellationToken)
            => Task.FromResult(new ManagedContainerReconcileResult(true, 0, null));

        public Task EnsureNetworkAsync(string networkName, CancellationToken cancellationToken)
            => Task.CompletedTask;
    }

    private sealed class RecoveryTestHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => throw new NotSupportedException(name);
    }

    private sealed class RecoveryTestHostEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Development;
        public string ApplicationName { get; set; } = "Kautuka.IntegrationTests";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
