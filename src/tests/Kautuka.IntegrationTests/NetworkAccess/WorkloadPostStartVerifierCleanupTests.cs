using System.Reflection;

using Microsoft.Extensions.Logging.Abstractions;

using Kautuka.Manager.RuntimeContainer.Hosting;
using Kautuka.Manager.RuntimeContainer.Hosting.NetworkAccess;
using Kautuka.Manager.RuntimeContainer.Sandbox;
using Kautuka.Manager.RuntimeContainer.Sandbox.Models;
using Kautuka.Manager.Protos;
using Kautuka.NetworkAccessController;

namespace Kautuka.IntegrationTests.NetworkAccess;

public sealed class WorkloadPostStartVerifierCleanupTests
{
    private const string ContainerId = "surviving-workload";
    private const string AllocatedIp = "10.247.1.81";

    [Fact]
    public void QuarantineMatchesIdentityAtomicallyAndDoesNotReplaceDifferentContainer()
    {
        var registry = new WorkloadRegistry();
        RegistryEntry replacement = new(
            AllocatedIp, "user-1", "agent-1", "new-container", "agent-sandbox", null,
            "agent-sandbox", false, "normal", "user-agent", null, DateTimeOffset.UtcNow);
        registry.Register(replacement);

        Assert.False(registry.TryQuarantine(AllocatedIp, "old-container", "old-name"));
        Assert.Equal("normal", registry.FindByIp(AllocatedIp)?.Egress);

        Assert.True(registry.TryQuarantine(AllocatedIp, "old-name", "new-container"));
        Assert.Equal("deny-all", registry.FindByIp(AllocatedIp)?.Egress);
        Assert.Equal("none", registry.FindByIp(AllocatedIp)?.Attribution);
    }

    [Fact]
    public async Task ThrowingQuarantineSubscriberDoesNotSkipContainerCleanup()
    {
        var registry = new WorkloadRegistry();
        registry.Register(new RegistryEntry(
            AllocatedIp, "user-1", "agent-1", ContainerId, "connector", "connector-1",
            "isolated", false, "normal", "user-agent", null, DateTimeOffset.UtcNow, "probe"));
        registry.ContainerDeregistered += (_, _) => throw new IOException("simulated probe grant-store failure");

        var calls = new List<string[]>();
        var runner = new PodmanCommandRunner((args, _) =>
        {
            calls.Add([.. args]);
            if (args[0] == "inspect")
            {
                return Task.FromResult(new PodmanCommandResult(0, "[{\"HostConfig\":{\"SecurityOpt\":[]}}]", string.Empty, ProcessTerminationReason.Completed));
            }

            return Task.FromResult(new PodmanCommandResult(0, string.Empty, string.Empty, ProcessTerminationReason.Completed));
        });

        InvalidOperationException failure = await Assert.ThrowsAsync<InvalidOperationException>(() => new WorkloadPostStartVerifier(runner)
            .VerifyWorkloadAsync(ContainerId, AllocatedIp, registry: registry, cancellationToken: CancellationToken.None));

        Assert.Contains("quarantine failed", failure.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("registry finalization failed", failure.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(calls, static args => args[0] == "kill");
        Assert.Contains(calls, static args => args[0] == "rm");
        Assert.False(registry.TryGetByIp(AllocatedIp, out _));
    }

    [Fact]
    public async Task EphemeralFinallyKeepsUnverifiedRegistryIdentityQuarantined()
    {
        string stateDirectory = Path.Combine(Path.GetTempPath(), "nac-ephemeral-quarantine-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(stateDirectory);
        try
        {
            var registry = new WorkloadRegistry();
            registry.Register(new RegistryEntry(
                AllocatedIp, "user-1", "agent-1", ContainerId, "connector", "connector-1",
                "isolated", false, "normal", "user-agent", null, DateTimeOffset.UtcNow, "probe"));
            registry.ContainerDeregistered += (_, _) => throw new IOException("simulated probe grant-store failure");
            var runner = new PodmanCommandRunner((_, _) => throw new InvalidOperationException("No Podman command is expected."));
            await using var service = new WorkspaceContainerConnectorExecutionService(
                new WorkspaceDesiredStateStore(),
                runner,
                new McpbImageMaterializer(runner, NullLogger<McpbImageMaterializer>.Instance),
                new PodmanContainerTool(runner),
                NullLogger<WorkspaceContainerConnectorExecutionService>.Instance,
                new WorkloadAddressAllocator(Path.Combine(stateDirectory, "ip-cursor.json")),
                registry);

            Type workloadType = typeof(WorkspaceContainerConnectorExecutionService)
                .GetNestedType("EphemeralWorkload", BindingFlags.NonPublic)
                ?? throw new InvalidOperationException("EphemeralWorkload was not found.");
            object workload = Activator.CreateInstance(
                workloadType,
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                binder: null,
                args: [ContainerId, AllocatedIp, null],
                culture: null)!;
            MethodInfo complete = typeof(WorkspaceContainerConnectorExecutionService)
                .GetMethod("CompleteEphemeralWorkload", BindingFlags.Instance | BindingFlags.NonPublic)
                ?? throw new InvalidOperationException("CompleteEphemeralWorkload was not found.");

            Assert.True((bool)complete.Invoke(service, [workload])!);
            RegistryEntry quarantined = Assert.Single(registry.GetAll());
            Assert.Equal(ContainerId, quarantined.ContainerId);
            Assert.Equal("deny-all", quarantined.Egress);
            Assert.Equal("none", quarantined.Attribution);
        }
        finally
        {
            Directory.Delete(stateDirectory, recursive: true);
        }
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, true, false)]
    [InlineData(false, false, true)]
    [InlineData(true, false, true)]
    public async Task FailedVerificationRetainsDenyAllIdentityUnlessForceRemoveIsConfirmed(
        bool throwOnKill,
        bool throwOnRemove,
        bool removeSucceeds)
    {
        var registry = new WorkloadRegistry();
        registry.Register(new RegistryEntry(
            AllocatedIp, "user-1", "agent-1", ContainerId, "agent-sandbox", null,
            "agent-sandbox", false, "normal", "user-agent", null, DateTimeOffset.UtcNow));

        var calls = new List<string[]>();
        bool quarantinedAtKill = false;
        bool quarantinedAtRemove = false;
        var runner = new PodmanCommandRunner((args, _) =>
        {
            calls.Add([.. args]);
            if (args[0] == "kill")
            {
                quarantinedAtKill = registry.FindByIp(AllocatedIp)?.Egress == "deny-all"
                    && registry.FindByIp(AllocatedIp)?.Attribution == "none";
            }
            else if (args[0] == "rm")
            {
                quarantinedAtRemove = registry.FindByIp(AllocatedIp)?.Egress == "deny-all"
                    && registry.FindByIp(AllocatedIp)?.Attribution == "none";
            }

            if (args[0] == "inspect")
            {
                // Fail before namespace checks; cleanup behavior is the subject of this test.
                return Task.FromResult(new PodmanCommandResult(
                    0,
                    "[{\"HostConfig\":{\"SecurityOpt\":[]}}]",
                    string.Empty,
                    ProcessTerminationReason.Completed));
            }

            if (args[0] == "kill" && throwOnKill)
            {
                throw new IOException("simulated podman kill transport failure");
            }

            if (args[0] == "rm" && throwOnRemove)
            {
                throw new IOException("simulated podman rm transport failure");
            }

            if (args[0] == "rm")
            {
                return Task.FromResult(new PodmanCommandResult(
                    removeSucceeds ? 0 : 125,
                    string.Empty,
                    removeSucceeds ? string.Empty : "simulated rm failure",
                    ProcessTerminationReason.Completed));
            }

            if (args[0] == "kill")
            {
                return Task.FromResult(new PodmanCommandResult(125, string.Empty, "simulated kill failure", ProcessTerminationReason.Completed));
            }

            return Task.FromResult(new PodmanCommandResult(0, string.Empty, string.Empty, ProcessTerminationReason.Completed));
        });

        await Assert.ThrowsAsync<InvalidOperationException>(() => new WorkloadPostStartVerifier(runner)
            .VerifyWorkloadAsync(ContainerId, AllocatedIp, registry: registry, cancellationToken: CancellationToken.None));

        Assert.Contains(calls, static args => args[0] == "kill");
        Assert.Contains(calls, static args => args[0] == "rm");
        Assert.True(quarantinedAtKill, "The normal grant must be removed before the kill request is awaited.");
        Assert.True(quarantinedAtRemove, "The normal grant must remain removed while force removal is awaited.");
        if (removeSucceeds)
        {
            Assert.False(registry.TryGetByIp(AllocatedIp, out _));
        }
        else
        {
            Assert.True(registry.TryGetByIp(AllocatedIp, out RegistryEntry? quarantined));
            Assert.NotNull(quarantined);
            Assert.Equal(ContainerId, quarantined.ContainerId);
            Assert.Equal("deny-all", quarantined.Egress);
            Assert.Equal("none", quarantined.Attribution);
        }
    }

    [Fact]
    public async Task PersistentConnectorDoesNotEraseVerifierQuarantineWhenItsRemovalFails()
    {
        string stateDirectory = Path.Combine(Path.GetTempPath(), "nac-connector-verifier-cleanup-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(stateDirectory);
        try
        {
            var registry = new WorkloadRegistry();
            var commands = new List<string[]>();
            string? failureDetail = null;
            bool started = false;
            var runner = new PodmanCommandRunner((args, _) =>
            {
                commands.Add([.. args]);
                return Task.FromResult(args[0] switch
                {
                    "inspect" when !started => new PodmanCommandResult(125, string.Empty, "not found", ProcessTerminationReason.NonZeroExit),
                    "inspect" => new PodmanCommandResult(0, "[{\"Id\":\"runtime-connector\",\"HostConfig\":{\"SecurityOpt\":[]}}]", string.Empty, ProcessTerminationReason.Completed),
                    "container" => new PodmanCommandResult(1, string.Empty, "not found", ProcessTerminationReason.NonZeroExit),
                    "image" => new PodmanCommandResult(0, "sha256:" + new string('a', 64), string.Empty, ProcessTerminationReason.Completed),
                    "run" => StartContainer(),
                    "kill" or "rm" => new PodmanCommandResult(125, string.Empty, "simulated cleanup failure", ProcessTerminationReason.NonZeroExit),
                    _ => new PodmanCommandResult(0, string.Empty, string.Empty, ProcessTerminationReason.Completed),
                });
            });
            PodmanCommandResult StartContainer()
            {
                started = true;
                return new PodmanCommandResult(0, string.Empty, string.Empty, ProcessTerminationReason.Completed);
            }

            var service = new WorkspaceContainerConnectorExecutionService(
                new WorkspaceDesiredStateStore(),
                runner,
                new McpbImageMaterializer(runner, NullLogger<McpbImageMaterializer>.Instance),
                new PodmanContainerTool(runner),
                NullLogger<WorkspaceContainerConnectorExecutionService>.Instance,
                new WorkloadAddressAllocator(Path.Combine(stateDirectory, "ip-cursor.json")),
                registry,
                postStartVerifier: new WorkloadPostStartVerifier(runner),
                workloadLaunchGate: new WorkloadLaunchGate(
                    new OpenStartupState(),
                    new NetworkAccessPolicyStore(Path.Combine(stateDirectory, "policy.json"))));
            await using (service.ConfigureAwait(false))
            {
                var connector = new WorkspaceMcpContainerConnectorDefinition(
                    "connector-cleanup-test", "localhost/test:latest", "node", [], true, false, null, null, [], 30, 600);
                var workspaceContext = new ContainerContext(
                    "agent", "agent-1", "user-1", Path.GetTempPath(), "/workspace", "image", 30, "512m", 1, [], new Dictionary<string, string>());
                MethodInfo ensure = typeof(WorkspaceContainerConnectorExecutionService)
                    .GetMethod("EnsureConnectorContainerRunningAsync", BindingFlags.Instance | BindingFlags.NonPublic)
                    ?? throw new InvalidOperationException("EnsureConnectorContainerRunningAsync was not found.");

                InvalidOperationException failure = await Assert.ThrowsAsync<InvalidOperationException>(() => (Task)ensure.Invoke(service, [
                    connector,
                    "connector-cleanup-runtime",
                    ContainerId,
                    WorkspaceMcpExecutionContext.Isolated,
                    null,
                    TestContext.Current.CancellationToken,
                    false,
                    workspaceContext,
                ])!);
                failureDetail = failure.ToString();
            }

            Assert.True(commands.Any(static args => args[0] == "rm"), "No rm call; exception: " + failureDetail + Environment.NewLine + string.Join(Environment.NewLine, commands.Select(static args => string.Join(' ', args))));
            Assert.Single(commands, static args => args[0] == "rm");
            RegistryEntry quarantined = Assert.Single(registry.GetAll());
            Assert.Equal("runtime-connector", quarantined.ContainerId);
            Assert.Equal("deny-all", quarantined.Egress);
            Assert.Equal("none", quarantined.Attribution);
        }
        finally
        {
            Directory.Delete(stateDirectory, recursive: true);
        }
    }

    private sealed class OpenStartupState : IWorkloadStartupState
    {
        public bool IsStartupGateOpen => true;
    }

}
