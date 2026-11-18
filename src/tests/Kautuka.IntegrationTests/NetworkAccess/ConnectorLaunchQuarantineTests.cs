using System.Net;
using System.Net.Sockets;

using Kautuka.Manager.Protos;
using Kautuka.Manager.RuntimeContainer.Configuration;
using Kautuka.Manager.RuntimeContainer.Hosting;
using Kautuka.Manager.RuntimeContainer.Hosting.NetworkAccess;
using Kautuka.Manager.RuntimeContainer.Sandbox;
using Kautuka.Manager.RuntimeContainer.Sandbox.Models;
using Kautuka.NetworkAccessController;

using Microsoft.Extensions.Logging.Abstractions;

using ManagerSandboxRuntimeDefinition = Kautuka.Manager.RuntimeContainer.Configuration.SandboxRuntimeDefinition;
using ManagerWorkspaceDefinition = Kautuka.Manager.RuntimeContainer.Configuration.WorkspaceDefinition;

namespace Kautuka.IntegrationTests.NetworkAccess;

public sealed class ConnectorLaunchQuarantineTests
{
    [Fact]
    public async Task ExplicitCommandPersistentLaunch_IsQuarantinedUntilVerified()
    {
        if (!OperatingSystem.IsLinux())
        {
            Assert.Skip("The persistent MCP launch uses a Linux Python process.");
        }

        string root = Path.Combine(Path.GetTempPath(), $"nac-persistent-command-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        const string canonicalId = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        string server = "import json,sys,socket\ns=socket.create_connection(('127.0.0.1'," + port + "))\ns.sendall(b'started\\n')\ns.close()\n" +
            "for line in sys.stdin:\n r=json.loads(line)\n m=r.get('method')\n i=r.get('id')\n" +
            " if m=='initialize': print(json.dumps({'jsonrpc':'2.0','id':i,'result':{'protocolVersion':'2025-03-26','capabilities':{'tools':{}},'serverInfo':{'name':'test','version':'1'}}}),flush=True)\n" +
            " elif m=='tools/list': print(json.dumps({'jsonrpc':'2.0','id':i,'result':{'tools':[]}}),flush=True)\n";

        var registry = new WorkloadRegistry();
        string allocatedIp = string.Empty;
        int inspectCount = 0;
        List<string[]> commands = [];
        (string Egress, string Attribution)? observedAtRun = null;
        var runner = new PodmanCommandRunner((args, _) =>
        {
            commands.Add([.. args]);
            if (args[0] == "run")
            {
                RegistryEntry entry = Assert.Single(registry.GetAll());
                allocatedIp = entry.Ip;
                observedAtRun = (entry.Egress, entry.Attribution);
                return Task.FromResult(new PodmanCommandResult(0, canonicalId + Environment.NewLine, string.Empty, ProcessTerminationReason.Completed));
            }

            if (args[0] == "inspect")
            {
                if (Interlocked.Increment(ref inspectCount) == 1)
                {
                    return Task.FromResult(new PodmanCommandResult(125, string.Empty, "no such container", ProcessTerminationReason.NonZeroExit));
                }

                RegistryEntry entry = Assert.Single(registry.GetAll());
                string inspect = "[{\"Id\":\"" + canonicalId + "\",\"EffectiveCaps\":[],\"BoundingCaps\":[],\"HostConfig\":{\"SecurityOpt\":[\"no-new-privileges\"],\"Sysctls\":{\"net.ipv6.conf.all.disable_ipv6\":\"1\"},\"Privileged\":false,\"CapAdd\":[],\"CapDrop\":[\"ALL\"],\"ReadonlyRootfs\":true,\"NetworkMode\":\"bridge\",\"Devices\":[]},\"Config\":{\"User\":\"65532:65532\"},\"State\":{\"Pid\":4242,\"Status\":\"running\"},\"NetworkSettings\":{\"Networks\":{\"kautuka-workload\":{\"IPAddress\":\"" + entry.Ip + "\"}}},\"Mounts\":[]}]";
                return Task.FromResult(new PodmanCommandResult(0, inspect, string.Empty, ProcessTerminationReason.Completed));
            }

            if (args[0] == "container")
            {
                return Task.FromResult(new PodmanCommandResult(1, string.Empty, "no such container", ProcessTerminationReason.NonZeroExit));
            }

            if (args[0] == "unshare")
            {
                if (args.Contains("cat", StringComparer.Ordinal))
                {
                    return Task.FromResult(new PodmanCommandResult(0, "1", string.Empty, ProcessTerminationReason.Completed));
                }

                string nft = BoundaryRulesetRenderer.ResolveTemplate("kautuka_workload.nft.json.tmpl")
                    .Replace("{{IP}}", allocatedIp, StringComparison.Ordinal);
                return Task.FromResult(new PodmanCommandResult(0, nft, string.Empty, ProcessTerminationReason.Completed));
            }

            return Task.FromResult(new PodmanCommandResult(0, string.Empty, string.Empty, ProcessTerminationReason.Completed));
        });

        var desired = new WorkspaceDesiredStateStore();
        var connector = new WorkspaceMcpContainerConnectorDefinition(
            "explicit-persistent-test", "localhost/test:latest", "test-command", [], true, false,
            null, null, [], 30, 600);
        string ownerWorkspace = Path.Combine(root, "users", "user-a", "agents", "agent-a");
        Directory.CreateDirectory(ownerWorkspace);
        var workspace = new ContainerContext("agent", "agent-a", "user-a", ownerWorkspace, "/workspace", "image", 30, "512m", 1, [], new Dictionary<string, string>());
        _ = desired.ApplyDesiredState(1, WorkspaceDesiredStateSyncReason.Bootstrap,
            new ManagerRuntimeSettings(new ManagerWorkspaceDefinition { BaseRoot = root }, new ManagerSandboxRuntimeDefinition { Image = "kautuka-base:latest", DefaultTimeout = 30 }, new WorkspaceTelemetryDefinition()),
            [workspace], [connector], []);
        using var policyStore = new NetworkAccessPolicyStore(Path.Combine(root, "policy.json"));
        var launchGate = new WorkloadLaunchGate(new OpenStartupGate(), policyStore);
        await using var service = new WorkspaceContainerConnectorExecutionService(
            desired,
            runner,
            new McpbImageMaterializer(runner, NullLogger<McpbImageMaterializer>.Instance),
            new PodmanContainerTool(runner),
            NullLogger<WorkspaceContainerConnectorExecutionService>.Instance,
            addressAllocator: new WorkloadAddressAllocator(Path.Combine(root, "ip-cursor.json")),
            workloadRegistry: registry,
            postStartVerifier: new WorkloadPostStartVerifier(runner, desired),
            workloadLaunchGate: launchGate,
            stdioCommand: "python3",
            stdioArgumentsPrefix: ["-u", "-c", server]);

        try
        {
            Task<(bool Ok, IReadOnlyList<ModelContextProtocol.Client.McpClientTool> Tools, WorkspaceMcpConnectorUnavailabilityReason? UnavailabilityReason, string? FailureMessage)> listing = service.ListToolsAsync(
                connector.ConnectorId,
                null,
                30,
                false,
                workspace,
                WorkspaceMcpExecutionContext.Isolated,
                TestContext.Current.CancellationToken);
            Task<TcpClient> acceptance = listener.AcceptTcpClientAsync(TestContext.Current.CancellationToken).AsTask();
            Assert.Same(acceptance, await Task.WhenAny(acceptance, listing));
            using TcpClient started = await acceptance;
            var result = await listing;

            Assert.True(result.Ok, result.FailureMessage);
            Assert.Equal(("deny-all", "none"), observedAtRun);
            string[][] runs = [.. commands.Where(static args => args[0] == "run")];
            Assert.Single(runs);
            Assert.Contains("--entrypoint", runs[0], StringComparer.Ordinal);
            Assert.Contains(commands, args => args.Length > 1 && args[0] == "inspect" && args[1] == canonicalId);
            RegistryEntry promoted = Assert.Single(registry.GetAll());
            Assert.Equal(canonicalId, promoted.ContainerId);
            Assert.Equal("normal", promoted.Egress);
            Assert.Equal("user-agent", promoted.Attribution);
            Assert.True(registry.IsVerified(promoted.Ip, canonicalId));
        }
        finally
        {
            listener.Stop();
            Directory.Delete(root, recursive: true);
        }
    }

    private sealed class OpenStartupGate : IWorkloadStartupState
    {
        public bool IsStartupGateOpen => true;
    }
}
