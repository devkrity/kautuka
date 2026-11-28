using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text.Json;

using Kautuka.Manager.Protos;
using Kautuka.Manager.RuntimeContainer.Hosting;
using Kautuka.Manager.RuntimeContainer.Hosting.NetworkAccess;
using Kautuka.Manager.RuntimeContainer.Sandbox;
using Kautuka.Manager.RuntimeContainer.Sandbox.Models;
using Kautuka.Manager.RuntimeContainer.Configuration;
using Kautuka.NetworkAccessController;

using Microsoft.Extensions.Logging.Abstractions;

using ModelContextProtocol.Client;

using ManagerSandboxRuntimeDefinition = Kautuka.Manager.RuntimeContainer.Configuration.SandboxRuntimeDefinition;
using ManagerWorkspaceDefinition = Kautuka.Manager.RuntimeContainer.Configuration.WorkspaceDefinition;

namespace Kautuka.IntegrationTests;

public sealed class WorkspaceContainerConnectorExecutionServiceTests
{
    [Theory]
    [InlineData("matching")]
    [InlineData("modified")]
    [InlineData("malformed")]
    public async Task VersionThreeSandboxManifestOnlyControlsReinstallation(string installedState)
    {
        string root = Directory.CreateTempSubdirectory().FullName;
        try
        {
            string bundle = Path.Combine(root, "connector.mcpb");
            var token = TestContext.Current.CancellationToken;
            await File.WriteAllTextAsync(bundle, "bundle", token);
            string image = "quay.io/devkrity/kautuka-base@sha256:" + new string('a', 64);
            string workspacePath = Path.Combine(root, "users", "user", "agents", "agent");
            Directory.CreateDirectory(workspacePath);
            var workspace = new ContainerContext("agent", "agent", "user", workspacePath, "/workspace", image, 30, "512m", 1, [], new Dictionary<string, string>());
            var connector = new WorkspaceMcpContainerConnectorDefinition("user::web", image, "manager-command", ["manager-arg"], true, false, bundle, "binary", [], 30, 600);
            var desired = new WorkspaceDesiredStateStore();
            _ = desired.ApplyDesiredState(1, WorkspaceDesiredStateSyncReason.Bootstrap,
                new ManagerRuntimeSettings(new ManagerWorkspaceDefinition { BaseRoot = root }, new ManagerSandboxRuntimeDefinition { Image = image, DefaultTimeout = 30 }, new WorkspaceTelemetryDefinition()),
                [workspace], [connector], [new("agent", "agent", [connector.ConnectorId])]);

            // Frozen v0.1.27 values for this bundle and base image.
            const string installRoot = "/home/node/.kautuka/connectors/by-digest/d9f3820066cd225f4407a643b1961bb39d4e356c8ef6c6a0e2c389dd20ea9c3b";
            const string artifactDigest = "cda184e0acf69f4c84153a6eacbbc9dc5d0b4c220de5d4f19503b4f0d58c9552";
            string legacyManifest = $$$"""
                {"NormalizationVersion":3,"Contracts":[{"ConnectorId":"user::web","ArtifactDigest":"{{{artifactDigest}}}","RuntimeImage":"localhost/kautuka-mcpb-sandbox-binary:{{{artifactDigest}}}","ServerType":"binary","InstallRoot":"{{{installRoot}}}","ExecutablePath":"manager-command","ArgsTemplate":["manager-arg"],"EnvTemplate":[],"WorkingDirectory":"{{{installRoot}}}","NormalizationVersion":3}]}
                """;
            var modified = System.Text.Json.Nodes.JsonNode.Parse(legacyManifest)!;
            var contract = modified["Contracts"]![0]!;
            contract["ExecutablePath"] = "sandbox-command";
            contract["ArgsTemplate"] = new System.Text.Json.Nodes.JsonArray("sandbox-arg");
            contract["EnvTemplate"] = JsonSerializer.SerializeToNode(new[]
            {
                new KeyValuePair<string, string>("LD_PRELOAD", "sandbox-value"),
                new KeyValuePair<string, string>("CONTAINERS_CONF", "sandbox-value"),
                new KeyValuePair<string, string>("PATH", "sandbox-value")
            });
            string poison = modified.ToJsonString();
            bool drifted = installedState != "matching";
            string firstRead = installedState switch
            {
                "matching" => legacyManifest,
                "modified" => poison,
                _ => "{"
            };
            var sandbox = new ManifestSandbox(firstRead, poison);
            List<string> commands = [];
            var runner = new PodmanCommandRunner((args, _) =>
            {
                commands.Add(args[0]);
                return Task.FromResult(new PodmanCommandResult(0, "", "", ProcessTerminationReason.Completed));
            });
            var materializer = new McpbImageMaterializer(runner, NullLogger<McpbImageMaterializer>.Instance,
                new RuntimeImageAllowlist(1, [new("kautuka-base", image, null)]));
            string capture = Path.Combine(root, "launch.json");
            const string recorder = "import json, os, sys; json.dump([sys.argv[2:], [os.getenv(k) for k in ('LD_PRELOAD','CONTAINERS_CONF','PATH')]], open(sys.argv[1],'w'))";
            await using var service = new WorkspaceContainerConnectorExecutionService(desired, runner, materializer, sandbox,
                NullLogger<WorkspaceContainerConnectorExecutionService>.Instance, stdioCommand: "python3", stdioArgumentsPrefix: ["-c", recorder, capture]);
            await service.CallToolAsync(connector.ConnectorId, null, "test", "{}", 30, false, workspace, WorkspaceMcpExecutionContext.AgentSandbox, token);
            Assert.Equal(1, sandbox.ManifestReads);
            Assert.Equal(drifted ? 1 : 0, sandbox.Replacements);
            Assert.Equal(drifted ? new[] { "image", "create", "cp", "rm" } : ["image"], commands);
            using var recorded = JsonDocument.Parse(await File.ReadAllTextAsync(capture, token));
            Assert.Equal(new[] { "exec", "-i", "--workdir", installRoot, "sandbox", "manager-command", "manager-arg" },
                recorded.RootElement[0].EnumerateArray().Select(value => value.GetString()));
            Assert.DoesNotContain(recorded.RootElement[1].EnumerateArray(), value => value.GetString() == "sandbox-value");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private sealed class ManifestSandbox(string firstManifest, string laterManifest) : IContainerTool
    {
        public int ManifestReads { get; private set; }
        public int Replacements { get; private set; }
        public Task<OciSandboxSession> SpawnSandboxAsync(ContainerContext context, CancellationToken cancellationToken)
            => Task.FromResult(new OciSandboxSession("sandbox", Replacements > 0, context.ScopeType, context.ScopeId, context.UserId));
        public Task CloseSandboxAsync(ContainerContext context, CancellationToken cancellationToken)
        {
            Replacements++;
            return Task.CompletedTask;
        }
        public Task<ContainerExecutionResult> RunShellAsync(ContainerContext context, string command, int? timeoutSeconds, CancellationToken cancellationToken)
        {
            string output = command.StartsWith("if [ -f ", StringComparison.Ordinal)
                ? (++ManifestReads == 1 ? firstManifest : laterManifest) : "";
            return Task.FromResult(new ContainerExecutionResult("ok", 0, output, ""));
        }
        public Task PrimeWorkspaceAsync(ContainerContext context, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ContainerContext> RebuildWorkspaceAsync(ContainerContext context, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<bool> DeleteWorkspaceAsync(ContainerContext context, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<OciSandboxSession> InspectSandboxAsync(ContainerContext context, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    [Fact]
    public async Task CallToolMaterializesViewFilesIntoDesiredWorkspaceOnly()
    {
        string root = Directory.CreateDirectory(Path.Combine(OperatingSystem.IsMacOS() ? "/private/tmp" : Path.GetTempPath(), "native-call-" + Guid.NewGuid().ToString("N"))).FullName;
        string outside = Directory.CreateTempSubdirectory().FullName;
        try
        {
            var token = TestContext.Current.CancellationToken;
            string bundle = Path.Combine(root, "connector.mcpb");
            await File.WriteAllTextAsync(bundle, "bundle", token);
            string script = Path.Combine(root, "mcp_view_server.py");
            await File.WriteAllTextAsync(script, """
                import json, sys, pathlib
                for line in sys.stdin:
                    if not line.strip():
                        continue
                    req = json.loads(line)
                    method = req.get("method")
                    req_id = req.get("id")
                    if method == "initialize":
                        print(json.dumps({"jsonrpc": "2.0", "id": req_id, "result": {"protocolVersion": "2025-03-26", "capabilities": {"tools": {}}, "serverInfo": {"name": "view", "version": "1"}}}), flush=True)
                    elif method == "tools/call":
                        if "--env-file" in sys.argv:
                            env_file = pathlib.Path(sys.argv[sys.argv.index("--env-file") + 1])
                            assert not env_file.exists(), "Connector settings file remained after initialization"
                        structured = {"view": {"blocks": [{"type": "file", "fileId": "f1"}]}, "files": [{"fileId": "f1", "name": "report.txt", "base64": "aGVsbG8="}]}
                        print(json.dumps({"jsonrpc": "2.0", "id": req_id, "result": {"content": [{"type": "text", "text": "ok"}], "structuredContent": structured, "isError": False}}), flush=True)
                """, token);

            string image = "quay.io/devkrity/kautuka-base@sha256:" + new string('a', 64);
            string desiredWorkspacePath = Path.Combine(root, "users", "user", "agents", "agent");
            Directory.CreateDirectory(desiredWorkspacePath);
            var desiredWorkspace = new ContainerContext("agent", "agent", "user", desiredWorkspacePath, "/workspace", image, 30, "512m", 1, [], new Dictionary<string, string>());
            var spoofedCallerWorkspace = desiredWorkspace with { HostWorkspacePath = outside };
            var connector = new WorkspaceMcpContainerConnectorDefinition("user::web", image, "manager-command", ["manager-arg"], true, false, bundle, "binary", [new("TOKEN", "runtime-secret")], 30, 600);
            var desired = new WorkspaceDesiredStateStore();
            _ = desired.ApplyDesiredState(1, WorkspaceDesiredStateSyncReason.Bootstrap,
                new ManagerRuntimeSettings(new ManagerWorkspaceDefinition { BaseRoot = root }, new ManagerSandboxRuntimeDefinition { Image = image, DefaultTimeout = 30 }, new WorkspaceTelemetryDefinition()),
                [desiredWorkspace], [connector], [new("agent", "agent", [connector.ConnectorId])]);

            var runner = new PodmanCommandRunner((_, _) => Task.FromResult(new PodmanCommandResult(0, "", "", ProcessTerminationReason.Completed)));
            var materializer = new McpbImageMaterializer(runner, NullLogger<McpbImageMaterializer>.Instance,
                new RuntimeImageAllowlist(1, [new("kautuka-base", image, null)]));
            var sandbox = new ManifestSandbox("{", "{");
            await using var service = new WorkspaceContainerConnectorExecutionService(desired, runner, materializer, sandbox,
                NullLogger<WorkspaceContainerConnectorExecutionService>.Instance, stdioCommand: "python3", stdioArgumentsPrefix: ["-u", script]);

            var call = await service.CallToolAsync(connector.ConnectorId, null, "render", "{}", 30, false, spoofedCallerWorkspace, WorkspaceMcpExecutionContext.AgentSandbox, token);
            Assert.True(call.Ok, call.FailureMessage);
            Assert.Contains("view-files/", call.Result, StringComparison.Ordinal);
            string written = Assert.Single(Directory.GetFiles(desiredWorkspacePath, "report.txt", SearchOption.AllDirectories));
            Assert.Equal("hello", await File.ReadAllTextAsync(written, token));
            Assert.Empty(Directory.EnumerateFileSystemEntries(outside));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
            Directory.Delete(outside, recursive: true);
        }
    }

    [Fact]
    public async Task PersistentConnectorStartFiltersHostProcessEnvironment()
    {
        string root = Directory.CreateTempSubdirectory().FullName;
        try
        {
            var token = TestContext.Current.CancellationToken;
            string image = "quay.io/devkrity/kautuka-base@sha256:" + new string('a', 64);
            string workspacePath = Path.Combine(root, "users", "user", "agents", "agent");
            Directory.CreateDirectory(workspacePath);
            var workspace = new ContainerContext("agent", "agent", "user", workspacePath, "/workspace", image, 30, "512m", 1, [], new Dictionary<string, string>());
            var connector = new WorkspaceMcpContainerConnectorDefinition(
                "user::persistent",
                image,
                "node",
                ["server.js"],
                true,
                false,
                null,
                "node",
                [
                    new("TOKEN", "allowed-value"),
                    new("PYTHONPATH", "/app/custom-python"),
                    new("LD_PRELOAD", "/tmp/poison.so"),
                    new("CONTAINERS_CONF", "/tmp/containers.conf"),
                    new("DOCKER_HOST", "tcp://127.0.0.1:2375"),
                    new("PODMAN_CUSTOM", "poison"),
                    new("PATH", "/tmp/bin"),
                ],
                30,
                600);
            var desired = new WorkspaceDesiredStateStore();
            _ = desired.ApplyDesiredState(1, WorkspaceDesiredStateSyncReason.Bootstrap,
                new ManagerRuntimeSettings(new ManagerWorkspaceDefinition { BaseRoot = root }, new ManagerSandboxRuntimeDefinition { Image = image, DefaultTimeout = 30 }, new WorkspaceTelemetryDefinition()),
                [workspace], [connector], []);

            IReadOnlyDictionary<string, string>? observedRunEnvironment = null;
            List<string>? observedRunArgs = null;
            string? observedEnvFilePath = null;
            string? observedEnvFileContent = null;
            var runner = new PodmanCommandRunner(async (args, env, ct) =>
            {
                if (args[0] == "run")
                {
                    observedRunArgs = [.. args];
                    observedRunEnvironment = new Dictionary<string, string>(env, StringComparer.Ordinal);
                    int envFileIndex = observedRunArgs.IndexOf("--env-file");
                    if (envFileIndex >= 0 && envFileIndex + 1 < observedRunArgs.Count)
                    {
                        observedEnvFilePath = observedRunArgs[envFileIndex + 1];
                        observedEnvFileContent = await File.ReadAllTextAsync(observedEnvFilePath, ct);
                    }

                    return new PodmanCommandResult(125, string.Empty, "synthetic stop after capture", ProcessTerminationReason.NonZeroExit);
                }

                if (args[0] == "image" && args.Count > 1 && args[1] == "inspect")
                {
                    return new PodmanCommandResult(0, "sha256:" + new string('a', 64), string.Empty, ProcessTerminationReason.Completed);
                }

                if (args[0] == "inspect")
                {
                    return new PodmanCommandResult(125, string.Empty, "no such container", ProcessTerminationReason.NonZeroExit);
                }

                if (args[0] == "container" && args.Count > 1 && args[1] == "exists")
                {
                    return new PodmanCommandResult(1, string.Empty, "no such container", ProcessTerminationReason.NonZeroExit);
                }

                return new PodmanCommandResult(0, string.Empty, string.Empty, ProcessTerminationReason.Completed);
            });

            using var policyStore = new NetworkAccessPolicyStore(Path.Combine(root, "policy.json"));
            var launchGate = new WorkloadLaunchGate(new TestWorkloadStartupState(true), policyStore);
            await using var service = new WorkspaceContainerConnectorExecutionService(
                desired,
                runner,
                new McpbImageMaterializer(runner, NullLogger<McpbImageMaterializer>.Instance, new RuntimeImageAllowlist(1, [new("kautuka-base", image, null)])),
                new PodmanContainerTool(runner),
                NullLogger<WorkspaceContainerConnectorExecutionService>.Instance,
                addressAllocator: new WorkloadAddressAllocator(Path.Combine(root, "ip-cursor.json")),
                workloadLaunchGate: launchGate);

            var result = await service.CallToolAsync(connector.ConnectorId, null, "ping", "{}", 30, false, workspace, WorkspaceMcpExecutionContext.Isolated, token);
            Assert.False(result.Ok);
            Assert.NotNull(observedRunEnvironment);
            Assert.Empty(observedRunEnvironment);
            Assert.NotNull(observedRunArgs);
            foreach (string hidden in new[] { "TOKEN", "allowed-value", "PYTHONPATH", "/app/custom-python", "DOCKER_HOST", "PODMAN_CUSTOM", "LD_PRELOAD", "CONTAINERS_CONF" })
            {
                Assert.DoesNotContain(observedRunArgs, arg => arg.Contains(hidden, StringComparison.OrdinalIgnoreCase));
            }

            Assert.NotNull(observedEnvFilePath);
            Assert.False(File.Exists(observedEnvFilePath));
            Assert.NotNull(observedEnvFileContent);
            Assert.Contains("PYTHONPATH=/app/custom-python\n", observedEnvFileContent, StringComparison.Ordinal);
            Assert.Contains("DOCKER_HOST=tcp://127.0.0.1:2375\n", observedEnvFileContent, StringComparison.Ordinal);
            Assert.Contains("TOKEN=allowed-value\n", observedEnvFileContent, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task V0127ConnectorWithPythonPathAndDockerSettingsReusesMatchingContainerOrRecreatesOnceWithoutHostOrCliLeak()
    {
        string root = Directory.CreateTempSubdirectory().FullName;
        try
        {
            var token = TestContext.Current.CancellationToken;
            string image = "quay.io/devkrity/kautuka-base@sha256:" + new string('a', 64);
            string imageId = "sha256:" + new string('a', 64);
            string containerId = new string('b', 64);
            string workspacePath = Path.Combine(root, "users", "user-alpha", "agents", "agent-alpha");
            Directory.CreateDirectory(workspacePath);
            var workspace = new ContainerContext("agent", "agent-alpha", "user-alpha", workspacePath, "/workspace", image, 30, "512m", 1, [], new Dictionary<string, string>());
            var connector = new WorkspaceMcpContainerConnectorDefinition(
                "user-alpha::python-docker",
                image,
                "python3",
                ["-m", "connector_server"],
                true,
                false,
                null,
                "python",
                [
                    new("PYTHONPATH", "/app/connector/src"),
                    new("DOCKER_CUSTOM_ENDPOINT", "https://docker.example.internal"),
                ],
                30,
                600);
            var desired = new WorkspaceDesiredStateStore();
            _ = desired.ApplyDesiredState(1, WorkspaceDesiredStateSyncReason.Bootstrap,
                new ManagerRuntimeSettings(new ManagerWorkspaceDefinition { BaseRoot = root }, new ManagerSandboxRuntimeDefinition { Image = image, DefaultTimeout = 30 }, new WorkspaceTelemetryDefinition()),
                [workspace], [connector], []);
            WorkspaceMcpContainerConnectorDefinition storedConnector = Assert.Single(desired.Snapshot.DesiredContainerConnectors);
            var identity = ConnectorContainerSpecBuilder.ResolveIdentity(storedConnector, workspace, purpose: null);
            string currentFingerprint = ConnectorContainerSpecBuilder.ComputeWorkloadFingerprint(
                storedConnector,
                identity,
                WorkspaceMcpExecutionContext.Isolated);
            string currentDesiredVersion = storedConnector.DesiredStateVersion.ToString(System.Globalization.CultureInfo.InvariantCulture);

            string[] liveEnv =
            [
                "PATH=/usr/bin",
                "HOME=/tmp",
                "NPM_CONFIG_CACHE=/tmp/.npm",
                "PIP_CACHE_DIR=/tmp/.pip",
                "UV_CACHE_DIR=/tmp/.uv",
                "DOCKER_CUSTOM_ENDPOINT=https://docker.example.internal",
            ];
            int runCount = 0;
            int rmCount = 0;
            List<string[]> observedCommands = [];
            List<Dictionary<string, string>> observedHostEnvs = [];

            string BuildV0127Inspect() => JsonSerializer.Serialize(new[]
            {
                new
                {
                    Id = containerId,
                    Image = imageId,
                    EffectiveCaps = Array.Empty<string>(),
                    BoundingCaps = Array.Empty<string>(),
                    NetworkSettings = new { Networks = new Dictionary<string, object> { ["kautuka-workload"] = new { IPAddress = "10.247.1.25" } } },
                    State = new { Status = "running", Pid = 4242 },
                    Config = new
                    {
                        User = "65532:65532",
                        Env = liveEnv,
                        Labels = new Dictionary<string, string>
                        {
                            ["kautuka.desired-version"] = currentDesiredVersion,
                            ["kautuka.spec-fingerprint"] = currentFingerprint,
                        },
                    },
                    Mounts = Array.Empty<object>(),
                    HostConfig = new
                    {
                        NetworkMode = "bridge",
                        Binds = Array.Empty<string>(),
                        CapDrop = new[] { "ALL" },
                        CapAdd = Array.Empty<string>(),
                        Privileged = false,
                        Devices = Array.Empty<object>(),
                        PublishAllPorts = false,
                        PortBindings = new Dictionary<string, object[]>(),
                        ReadonlyRootfs = true,
                        PidsLimit = 128,
                        SecurityOpt = new[] { "no-new-privileges", "label=disable" },
                        Sysctls = new Dictionary<string, string> { ["net.ipv6.conf.all.disable_ipv6"] = "1" },
                        Tmpfs = new Dictionary<string, string> { ["/tmp"] = "rw,noexec,nosuid,nodev" },
                    },
                },
            });

            var runner = new PodmanCommandRunner(async (args, env, ct) =>
            {
                observedCommands.Add([.. args]);
                observedHostEnvs.Add(new Dictionary<string, string>(env, StringComparer.Ordinal));
                if (args[0] == "image" && args.Count > 1 && args[1] == "inspect")
                {
                    return new PodmanCommandResult(0, imageId, string.Empty, ProcessTerminationReason.Completed);
                }

                if (args[0] == "inspect")
                {
                    return new PodmanCommandResult(0, BuildV0127Inspect(), string.Empty, ProcessTerminationReason.Completed);
                }

                if (args[0] == "rm")
                {
                    rmCount++;
                    return new PodmanCommandResult(0, containerId, string.Empty, ProcessTerminationReason.Completed);
                }

                if (args[0] == "run")
                {
                    runCount++;
                    string? fingerprintLabel = args.FirstOrDefault(static item => item.StartsWith("kautuka.spec-fingerprint=", StringComparison.Ordinal));
                    if (fingerprintLabel is not null)
                    {
                        currentFingerprint = fingerprintLabel["kautuka.spec-fingerprint=".Length..];
                    }

                    int envIdx = args.ToList().IndexOf("--env-file");
                    string[] fileLines = await File.ReadAllLinesAsync(args[envIdx + 1], ct);
                    liveEnv =
                    [
                        "PATH=/usr/bin",
                        "HOME=/tmp",
                        "NPM_CONFIG_CACHE=/tmp/.npm",
                        "PIP_CACHE_DIR=/tmp/.pip",
                        "UV_CACHE_DIR=/tmp/.uv",
                        .. fileLines,
                    ];
                    return new PodmanCommandResult(0, containerId, string.Empty, ProcessTerminationReason.Completed);
                }

                return new PodmanCommandResult(0, string.Empty, string.Empty, ProcessTerminationReason.Completed);
            });

            string capture = Path.Combine(root, "exec-capture.json");
            string recorderScript = Path.Combine(root, "recorder.py");
            const string recorder = """
                import json, os, pathlib, sys
                argv = sys.argv[2:]
                idx = argv.index("--env-file")
                env_path = argv[idx + 1]
                env_text = pathlib.Path(env_path).read_text(encoding="utf-8")
                pathlib.Path(sys.argv[1]).write_text(json.dumps({
                    "argv": argv,
                    "envPath": env_path,
                    "envText": env_text,
                    "hostPythonPath": os.getenv("PYTHONPATH"),
                    "hostDocker": os.getenv("DOCKER_CUSTOM_ENDPOINT"),
                }), encoding="utf-8")
                """;
            await File.WriteAllTextAsync(recorderScript, recorder, token);
            using var policyStore = new NetworkAccessPolicyStore(Path.Combine(root, "policy.json"));
            var launchGate = new WorkloadLaunchGate(new TestWorkloadStartupState(true), policyStore);
            await using var service = new WorkspaceContainerConnectorExecutionService(
                desired,
                runner,
                new McpbImageMaterializer(runner, NullLogger<McpbImageMaterializer>.Instance, new RuntimeImageAllowlist(1, [new("kautuka-base", image, null)])),
                new PodmanContainerTool(runner),
                NullLogger<WorkspaceContainerConnectorExecutionService>.Instance,
                addressAllocator: new WorkloadAddressAllocator(Path.Combine(root, "ip-cursor.json")),
                workloadLaunchGate: launchGate,
                stdioCommand: "python3",
                stdioArgumentsPrefix: ["-u", recorderScript, capture]);

            // First call after v0.1.27 upgrade: missing PYTHONPATH in v0.1.27 container triggers one recreate.
            _ = await service.CallToolAsync(connector.ConnectorId, new Dictionary<string, string> { ["DOCKER_RUNTIME_TOKEN"] = "runtime-secret" }, "ping", "{}", 30, false, workspace, WorkspaceMcpExecutionContext.Isolated, token);
            Assert.True(rmCount == 1, $"Expected rmCount=1 after first call, got {rmCount}. Commands: {string.Join(" | ", observedCommands.Select(c => string.Join(' ', c)))}");
            Assert.Equal(1, runCount);

            // Second call: container now has PYTHONPATH + DOCKER_CUSTOM_ENDPOINT and is NOT treated as changed.
            _ = await service.CallToolAsync(connector.ConnectorId, new Dictionary<string, string> { ["DOCKER_RUNTIME_TOKEN"] = "runtime-secret" }, "ping", "{}", 30, false, workspace, WorkspaceMcpExecutionContext.Isolated, token);
            Assert.True(rmCount == 1, $"Expected rmCount=1 after second call, got {rmCount}. Commands: {string.Join(" | ", observedCommands.Select(c => string.Join(' ', c)))}");
            Assert.Equal(1, runCount);

            Assert.All(observedHostEnvs, hostEnv => Assert.Empty(hostEnv));
            foreach (string[] cmd in observedCommands)
            {
                Assert.DoesNotContain(cmd, arg =>
                    arg.Contains("PYTHONPATH", StringComparison.OrdinalIgnoreCase)
                    || arg.Contains("DOCKER_", StringComparison.OrdinalIgnoreCase)
                    || arg.Contains("runtime-secret", StringComparison.Ordinal));
            }

            using JsonDocument doc = JsonDocument.Parse(await File.ReadAllTextAsync(capture, token));
            string[] execArgv = [.. doc.RootElement.GetProperty("argv").EnumerateArray().Select(static x => x.GetString()!)];
            Assert.DoesNotContain(execArgv, arg => arg.Contains("PYTHONPATH", StringComparison.OrdinalIgnoreCase) || arg.Contains("DOCKER_", StringComparison.OrdinalIgnoreCase));
            string execEnvPath = doc.RootElement.GetProperty("envPath").GetString()!;
            Assert.False(File.Exists(execEnvPath));
            string execEnvText = doc.RootElement.GetProperty("envText").GetString()!;
            Assert.Contains("PYTHONPATH=/app/connector/src\n", execEnvText, StringComparison.Ordinal);
            Assert.Contains("DOCKER_CUSTOM_ENDPOINT=https://docker.example.internal\n", execEnvText, StringComparison.Ordinal);
            Assert.Contains("DOCKER_RUNTIME_TOKEN=runtime-secret\n", execEnvText, StringComparison.Ordinal);
            Assert.Equal(JsonValueKind.Null, doc.RootElement.GetProperty("hostPythonPath").ValueKind);
            Assert.Equal(JsonValueKind.Null, doc.RootElement.GetProperty("hostDocker").ValueKind);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData("old", "one")]
    [InlineData("server", "old")]
    public async Task ChangedCommandOrArgumentsAreRejectedBeforeStartingConnector(string expectedCommand, string expectedArgument)
    {
        string root = Path.Combine(Path.GetTempPath(), "connector-command-pin-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var commands = new List<string[]>();
        var runner = new PodmanCommandRunner((args, _) => { commands.Add([.. args]); return Task.FromResult(new PodmanCommandResult(1, "", "not found", ProcessTerminationReason.NonZeroExit)); });
        var desired = new WorkspaceDesiredStateStore();
        string workspacePath = Path.Combine(root, "users", "user", "agents", "agent");
        Directory.CreateDirectory(workspacePath);
        var workspace = new ContainerContext("agent", "agent", "user", workspacePath, "/workspace", "image", 30, "512m", 1, [], new Dictionary<string, string>());
        var connector = new WorkspaceMcpContainerConnectorDefinition("user::web", "localhost/test:latest", "server", ["one"], true, false, null, null, [], 30, 600);
        _ = desired.ApplyDesiredState(1, WorkspaceDesiredStateSyncReason.Bootstrap,
            new ManagerRuntimeSettings(new ManagerWorkspaceDefinition { BaseRoot = root }, new ManagerSandboxRuntimeDefinition { Image = "kautuka-base:latest", DefaultTimeout = 30 }, new WorkspaceTelemetryDefinition()), [workspace], [connector], []);
        await using var service = new WorkspaceContainerConnectorExecutionService(desired, runner,
            new McpbImageMaterializer(runner, NullLogger<McpbImageMaterializer>.Instance), new PodmanContainerTool(runner), NullLogger<WorkspaceContainerConnectorExecutionService>.Instance);
        try
        {
            var result = await service.CallToolAsync("user::web", null, "fetch", "{}", 30, false, workspace, WorkspaceMcpExecutionContext.Isolated,
                TestContext.Current.CancellationToken, expectedCommand: expectedCommand, expectedArguments: [expectedArgument], expectedMcpProtocolVersion: "");
            Assert.False(result.Ok);
            Assert.Contains("execution configuration changed", result.FailureMessage, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(commands, command => command[0] is "run" or "exec");
        }
        finally { Directory.Delete(root, recursive: true); }
    }
    [Fact]
    public async Task ChangedConnectorBundleIsRejectedBeforeToolExecution()
    {
        string root = Path.Combine(Path.GetTempPath(), "connector-pin-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string bundle = Path.Combine(root, "connector.mcpb");
        await File.WriteAllTextAsync(bundle, "changed", TestContext.Current.CancellationToken);
        var commands = new List<string[]>();
        var runner = new PodmanCommandRunner((args, _) =>
        {
            commands.Add([.. args]);
            return Task.FromResult(new PodmanCommandResult(1, "", "not found", ProcessTerminationReason.NonZeroExit));
        });
        var desired = new WorkspaceDesiredStateStore();
        string workspacePath = Path.Combine(root, "users", "user", "agents", "agent");
        Directory.CreateDirectory(workspacePath);
        var workspace = new ContainerContext("agent", "agent", "user", workspacePath, "/workspace", "image", 30, "512m", 1, [], new Dictionary<string, string>());
        var connector = new WorkspaceMcpContainerConnectorDefinition("user::web", "localhost/test:latest", "server", [], true, false, bundle, "uv", [], 30, 600);
        _ = desired.ApplyDesiredState(1, WorkspaceDesiredStateSyncReason.Bootstrap,
            new ManagerRuntimeSettings(new ManagerWorkspaceDefinition { BaseRoot = root }, new ManagerSandboxRuntimeDefinition { Image = "kautuka-base:latest", DefaultTimeout = 30 }, new WorkspaceTelemetryDefinition()),
            [workspace], [connector], []);
        await using var service = new WorkspaceContainerConnectorExecutionService(desired, runner,
            new McpbImageMaterializer(runner, NullLogger<McpbImageMaterializer>.Instance), new PodmanContainerTool(runner),
            NullLogger<WorkspaceContainerConnectorExecutionService>.Instance);
        try
        {
            var result = await service.CallToolAsync("user::web", null, "fetch", "{}", 30, false, workspace,
                WorkspaceMcpExecutionContext.Isolated, TestContext.Current.CancellationToken, expectedSourceDigest: new string('a', 64));
            Assert.False(result.Ok);
            Assert.Contains("package changed", result.FailureMessage, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(commands, command => command[0] is "run" or "exec");
        }
        finally { Directory.Delete(root, recursive: true); }
    }
    private static readonly WorkloadLaunchGate TestOpenWorkloadLaunchGate = new(
        new TestWorkloadStartupState(true),
        new NetworkAccessPolicyStore(Path.Combine(Path.GetTempPath(), $"nac-test-open-gate-{Guid.NewGuid():N}.json")));

    [Fact]
    public async Task PackageMaterializerCleanupUsesIndependentBoundedTokenAfterRequestCancellation()
    {
        using var requestCancellation = new CancellationTokenSource();
        await requestCancellation.CancelAsync();
        bool cleanupRan = false;

        await McpbImageMaterializer.RunBoundedCleanupAsync(
            cancellationToken =>
            {
                Assert.True(cancellationToken.CanBeCanceled);
                Assert.False(cancellationToken.IsCancellationRequested);
                cleanupRan = true;
                return Task.CompletedTask;
            },
            TimeSpan.FromSeconds(1));

        Assert.True(cleanupRan);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await McpbImageMaterializer.RunBoundedCleanupAsync(
                cancellationToken => Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken),
                TimeSpan.FromMilliseconds(50)));
    }

    [Fact]
    public async Task ProbeRequestCancellationDoesNotCancelImageBuild()
    {
        using var request = new CancellationTokenSource();
        var buildStarted = new TaskCompletionSource<CancellationToken>(TaskCreationOptions.RunContinuationsAsynchronously);
        var finishBuild = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<string> probe = WorkspaceContainerConnectorExecutionService.CompleteProbeMaterializationAsync(
            token => { buildStarted.SetResult(token); return finishBuild.Task; },
            request.Token, CancellationToken.None, CancellationToken.None);

        CancellationToken buildToken = await buildStarted.Task;
        await request.CancelAsync();
        Assert.False(buildToken.IsCancellationRequested);
        Assert.False(probe.IsCompleted);
        finishBuild.SetResult("completed-image");
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => probe);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ProbeImageBuildStillStopsOnAdmissionClosureOrShutdown(bool stopHost)
    {
        using var admission = new CancellationTokenSource();
        using var shutdown = new CancellationTokenSource();
        var buildStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<string> probe = WorkspaceContainerConnectorExecutionService.CompleteProbeMaterializationAsync(
            async token => { buildStarted.SetResult(); await Task.Delay(Timeout.InfiniteTimeSpan, token); return "image"; },
            CancellationToken.None, admission.Token, shutdown.Token);
        await buildStarted.Task;
        await (stopHost ? shutdown : admission).CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => probe);
    }

    [Theory]
    [InlineData(true, WorkspaceMcpConnectorUnavailabilityReason.InitializeFailed, "Network protection paused")]
    [InlineData(false, WorkspaceMcpConnectorUnavailabilityReason.StartupTimeout, "probe timed out")]
    public async Task ProbeDistinguishesAdmissionClosureFromItsRequestDeadline(
        bool closeAdmission, WorkspaceMcpConnectorUnavailabilityReason reason, string message)
    {
        using var request = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        using var admission = new CancellableProbeAdmission();
        string policyPath = Path.Combine(Path.GetTempPath(), $"probe-admission-{Guid.NewGuid():N}.json");
        using var policy = new NetworkAccessPolicyStore(policyPath);
        var runner = new PodmanCommandRunner(async (args, token) =>
        {
            Assert.Equal("image", args[0]);
            if (closeAdmission) admission.CloseLaunchAdmission();
            else await request.CancelAsync();
            token.ThrowIfCancellationRequested();
            throw new InvalidOperationException("Fixture expected image inspection to be cancelled.");
        });
        await using var service = new WorkspaceContainerConnectorExecutionService(new WorkspaceDesiredStateStore(), runner,
            new McpbImageMaterializer(runner, NullLogger<McpbImageMaterializer>.Instance), new PodmanContainerTool(runner),
            NullLogger<WorkspaceContainerConnectorExecutionService>.Instance, workloadLaunchGate: new WorkloadLaunchGate(admission, policy));
        var connector = new WorkspaceMcpContainerConnectorDefinition("test", "localhost/test:latest", "server", [], true, false, null, null, [], 30, 600);

        var result = await service.ProbeConnectorAsync(connector, 30, request.Token, userId: "fixture-user");

        Assert.False(result.Ok);
        Assert.Equal(reason, result.UnavailabilityReason);
        Assert.Contains(message, result.FailureMessage, StringComparison.Ordinal);
        Assert.True(admission.LeaseReleased);
    }

    [Fact]
    public async Task ImageCompatibilityLookupResolvesMaterializedTagToImmutableImageId_WithNegativeControl()
    {
        string stateDirectory = Path.Combine(Path.GetTempPath(), "nac-mcpb-image-compat-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(stateDirectory);
        try
        {
            const string imageTag = "localhost/kautuka-mcpb-connector:latest";
            const string imageId = "sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
            string rawPodmanImageId = imageId["sha256:".Length..];
            const string pinnedImage = "ghcr.io/example/connector@sha256:cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc";
            const string pinnedImageDigest = "sha256:cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc";
            var commands = new List<string[]>();
            var runner = new PodmanCommandRunner((args, _) =>
            {
                commands.Add([.. args]);
                if (args.SequenceEqual(["image", "inspect", imageTag], StringComparer.Ordinal))
                {
                    return Task.FromResult(new PodmanCommandResult(0, "[{\"Id\":\"" + rawPodmanImageId + "\",\"RepoDigests\":[]}]", string.Empty, ProcessTerminationReason.Completed));
                }

                if (args.SequenceEqual(["image", "inspect", pinnedImage], StringComparer.Ordinal))
                {
                    return Task.FromResult(new PodmanCommandResult(0, "[{\"Id\":\"" + rawPodmanImageId + "\",\"RepoDigests\":[\"ghcr.io/example/connector@" + pinnedImageDigest + "\"]}]", string.Empty, ProcessTerminationReason.Completed));
                }

                return Task.FromResult(new PodmanCommandResult(1, string.Empty, "unexpected Podman command", ProcessTerminationReason.NonZeroExit));
            });
            var store = new ImageCompatibilityStore(Path.Combine(stateDirectory, "image-compat.json"));
            var profile = store.SetProfile(
                new NetworkAccessActor("admin", IsAdmin: true),
                imageId,
                [ImageCompatibilityStore.RootUser]);
            var pinnedProfile = store.SetProfile(
                new NetworkAccessActor("admin", IsAdmin: true),
                pinnedImageDigest,
                [ImageCompatibilityStore.WritableRootfs]);
            await using var service = new WorkspaceContainerConnectorExecutionService(
                new WorkspaceDesiredStateStore(),
                runner,
                new McpbImageMaterializer(runner, NullLogger<McpbImageMaterializer>.Instance),
                new PodmanContainerTool(runner),
                NullLogger<WorkspaceContainerConnectorExecutionService>.Instance,
                imageCompatibilityStore: store);

            MethodInfo method = typeof(WorkspaceContainerConnectorExecutionService)
                .GetMethod("ResolveImageCompatibilityAsync", BindingFlags.Instance | BindingFlags.NonPublic)
                ?? throw new InvalidOperationException("ResolveImageCompatibilityAsync method was not found.");
            object resolved = await InvokeImageCompatibilityResolutionAsync(method, service, imageTag);

            Assert.Equal(profile, ReadProperty<ImageCompatProfile?>(resolved, "Profile"));
            Assert.Equal(imageId, ReadProperty<string>(resolved, "Image"));
            Assert.Contains(commands, args => args.SequenceEqual(["image", "inspect", imageTag], StringComparer.Ordinal));
            int commandCountBeforeMutableReference = commands.Count;
            const string mutableRegistryTag = "ghcr.io/example/connector:latest";
            await Assert.ThrowsAsync<InvalidOperationException>(
                async () => await InvokeImageCompatibilityResolutionAsync(method, service, mutableRegistryTag));
            Assert.Equal(commandCountBeforeMutableReference, commands.Count);
            WorkspaceMcpContainerConnectorDefinition launchConnector = new(
                "mcpb-connector", ReadProperty<string>(resolved, "Image")!, "node", ["server.js"],
                true, false, null, null, [], 30, 600);
            List<string> renderedLaunch = WorkspaceContainerConnectorExecutionService.BuildConnectorWorkloadRunArguments(
                launchConnector,
                "mcpb-test-launch",
                WorkspaceMcpExecutionContext.Isolated,
                CreateWorkspaceContext(),
                persistent: false,
                allocatedIp: "10.247.1.77",
                isBraked: false,
                compatProfile: profile);
            Assert.Contains(imageId, renderedLaunch, StringComparer.Ordinal);

            object pinnedResolved = await InvokeImageCompatibilityResolutionAsync(method, service, pinnedImage);
            Assert.Equal(pinnedProfile, ReadProperty<ImageCompatProfile?>(pinnedResolved, "Profile"));
            Assert.Equal(imageId, ReadProperty<string>(pinnedResolved, "Image"));

            var cleanStore = new ImageCompatibilityStore(Path.Combine(stateDirectory, "clean-image-compat.json"));
            await using var cleanService = new WorkspaceContainerConnectorExecutionService(
                new WorkspaceDesiredStateStore(), runner,
                new McpbImageMaterializer(runner, NullLogger<McpbImageMaterializer>.Instance),
                new PodmanContainerTool(runner),
                NullLogger<WorkspaceContainerConnectorExecutionService>.Instance,
                imageCompatibilityStore: cleanStore);
            object cleanResolution = await InvokeImageCompatibilityResolutionAsync(method, cleanService, imageTag);
            Assert.Null(ReadProperty<ImageCompatProfile?>(cleanResolution, "Profile"));
            Assert.Equal(imageId, ReadProperty<string>(cleanResolution, "Image"));

            var malformedIdRunner = new PodmanCommandRunner((_, _) =>
                Task.FromResult(new PodmanCommandResult(0,
                    "[{\"Id\":\"not-a-canonical-id\",\"RepoDigests\":[\"ghcr.io/example/connector@" + pinnedImageDigest + "\"]}]",
                    string.Empty,
                    ProcessTerminationReason.Completed)));
            await using var malformedIdService = new WorkspaceContainerConnectorExecutionService(
                new WorkspaceDesiredStateStore(), malformedIdRunner,
                new McpbImageMaterializer(malformedIdRunner, NullLogger<McpbImageMaterializer>.Instance),
                new PodmanContainerTool(malformedIdRunner),
                NullLogger<WorkspaceContainerConnectorExecutionService>.Instance,
                imageCompatibilityStore: store);
            await Assert.ThrowsAsync<ContainerInspectionUnavailableException>(
                async () => await InvokeImageCompatibilityResolutionAsync(method, malformedIdService, pinnedImage));

            const string unapprovedImageId = "sha256:bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
            var negativeRunner = new PodmanCommandRunner((args, _) =>
                Task.FromResult(new PodmanCommandResult(0, "[{\"Id\":\"" + unapprovedImageId + "\",\"RepoDigests\":[]}]", string.Empty, ProcessTerminationReason.Completed)));
            await using var negativeService = new WorkspaceContainerConnectorExecutionService(
                new WorkspaceDesiredStateStore(),
                negativeRunner,
                new McpbImageMaterializer(negativeRunner, NullLogger<McpbImageMaterializer>.Instance),
                new PodmanContainerTool(negativeRunner),
                NullLogger<WorkspaceContainerConnectorExecutionService>.Instance,
                imageCompatibilityStore: store);
            object negativeResolved = await InvokeImageCompatibilityResolutionAsync(method, negativeService, imageTag);
            Assert.Null(ReadProperty<ImageCompatProfile?>(negativeResolved, "Profile"));
            Assert.Equal(unapprovedImageId, ReadProperty<string>(negativeResolved, "Image"));

            const string absentImage = "ghcr.io/example/acquired@sha256:dddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddd";
            var acquiredProfile = store.SetProfile(
                new NetworkAccessActor("admin", IsAdmin: true),
                "sha256:dddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddd",
                [ImageCompatibilityStore.FileOwnershipCaps]);
            bool acquired = false;
            var absentRunner = new PodmanCommandRunner((args, _) =>
            {
                if (args.SequenceEqual(["image", "inspect", absentImage], StringComparer.Ordinal))
                {
                    if (!acquired) return Task.FromResult(new PodmanCommandResult(1, string.Empty, "image not known", ProcessTerminationReason.NonZeroExit));
                    return Task.FromResult(new PodmanCommandResult(0,
                        "[{\"Id\":\"" + imageId + "\",\"Digest\":\"sha256:dddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddd\",\"RepoDigests\":[]}]",
                        string.Empty,
                        ProcessTerminationReason.Completed));
                }

                if (args.SequenceEqual(["image", "exists", absentImage], StringComparer.Ordinal))
                {
                    return Task.FromResult(new PodmanCommandResult(1, string.Empty, "image not known", ProcessTerminationReason.NonZeroExit));
                }

                if (args.SequenceEqual(["pull", absentImage], StringComparer.Ordinal))
                {
                    acquired = true;
                    return Task.FromResult(new PodmanCommandResult(0, string.Empty, string.Empty, ProcessTerminationReason.Completed));
                }

                return Task.FromResult(new PodmanCommandResult(1, string.Empty, "unexpected command", ProcessTerminationReason.NonZeroExit));
            });
            await using var absentService = new WorkspaceContainerConnectorExecutionService(
                new WorkspaceDesiredStateStore(),
                absentRunner,
                new McpbImageMaterializer(absentRunner, NullLogger<McpbImageMaterializer>.Instance),
                new PodmanContainerTool(absentRunner),
                NullLogger<WorkspaceContainerConnectorExecutionService>.Instance,
                imageCompatibilityStore: store);
            object absentResolved = await InvokeImageCompatibilityResolutionAsync(method, absentService, absentImage);
            Assert.Equal(acquiredProfile, ReadProperty<ImageCompatProfile?>(absentResolved, "Profile"));
            Assert.Equal(imageId, ReadProperty<string>(absentResolved, "Image"));
            Assert.True(acquired);

            var unavailableRunner = new PodmanCommandRunner((_, _) =>
                Task.FromResult(new PodmanCommandResult(125, string.Empty, "Podman inspection failed", ProcessTerminationReason.NonZeroExit)));
            await using var unavailableService = new WorkspaceContainerConnectorExecutionService(
                new WorkspaceDesiredStateStore(),
                unavailableRunner,
                new McpbImageMaterializer(unavailableRunner, NullLogger<McpbImageMaterializer>.Instance),
                new PodmanContainerTool(unavailableRunner),
                NullLogger<WorkspaceContainerConnectorExecutionService>.Instance,
                imageCompatibilityStore: store);
            await Assert.ThrowsAsync<ContainerInspectionUnavailableException>(
                async () => await InvokeImageCompatibilityResolutionAsync(method, unavailableService, imageTag));

            var timeoutRunner = new PodmanCommandRunner((_, _) =>
                Task.FromResult(new PodmanCommandResult(124, string.Empty, "timed out", ProcessTerminationReason.TimedOut)));
            await using var timeoutService = new WorkspaceContainerConnectorExecutionService(
                new WorkspaceDesiredStateStore(),
                timeoutRunner,
                new McpbImageMaterializer(timeoutRunner, NullLogger<McpbImageMaterializer>.Instance),
                new PodmanContainerTool(timeoutRunner),
                NullLogger<WorkspaceContainerConnectorExecutionService>.Instance,
                imageCompatibilityStore: store);
            await Assert.ThrowsAsync<ContainerInspectionUnavailableException>(
                async () => await InvokeImageCompatibilityResolutionAsync(method, timeoutService, imageTag));
        }
        finally
        {
            Directory.Delete(stateDirectory, recursive: true);
        }
    }

    [Theory]
    [InlineData("none", false, true)]
    [InlineData("unrelated", true, true)]
    [InlineData("unrelated", false, false)]
    [InlineData("same-connector", true, false)]
    [InlineData("settings", true, false)]
    [InlineData("multiple", true, false)]
    [InlineData("different-publisher", true, false)]
    public async Task PublicProbeMaterializesInspectsRunsAndVerifiesUsingImmutableImageId(
        string concurrentPublication, bool saveDiscovery, bool expectSuccess)
    {
        if (!OperatingSystem.IsLinux())
        {
            Assert.Skip("The public probe test uses a Linux Python stdio process.");
        }

        string stateDirectory = Path.Combine(Path.GetTempPath(), "nac-mcpb-public-probe-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(stateDirectory);
        string bundlePath = CreateBundleArchive();
        try
        {
            const string imageId = "sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
            const string containerId = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
            using var policyStore = new NetworkAccessPolicyStore(Path.Combine(stateDirectory, "probe-policy.json"));
            string probeRequest = Assert.IsType<string>(policyStore.RecordOrUpdatePendingProbeRequest(
                containerId, "probe-user", null, "binary-mcpb-probe", "user", "host", "probe.example", 443, "connector", "probe"));
            _ = policyStore.DecideRequest(new NetworkAccessActor("admin", true), probeRequest, true, null, policyStore.Revision);
            Assert.True(policyStore.TryEvaluateTemporaryProbeDecision(containerId, "probe-user", "binary-mcpb-probe", "host", "probe.example", 443, out _, out _));
            var registry = new WorkloadRegistry();
            var calls = new List<string[]>();
            var pidIps = new Dictionary<string, string>(StringComparer.Ordinal);
            var desired = new WorkspaceDesiredStateStore();
            var connector = new WorkspaceMcpContainerConnectorDefinition(
                "binary-mcpb-probe", "localhost/fedora-minimal:44", "python3", ["-m", "bundle_server"],
                true, false, bundlePath, "binary", [], 30, 600);
            ManagerRuntimeSettings Settings(int timeout) => new(
                new ManagerWorkspaceDefinition { BaseRoot = stateDirectory },
                new ManagerSandboxRuntimeDefinition { DefaultTimeout = timeout },
                new WorkspaceTelemetryDefinition());
            _ = desired.ApplyDesiredState(1, WorkspaceDesiredStateSyncReason.Bootstrap, Settings(30), [], [], []);

            bool runStarted = false;
            int nextPid = 4240;
            var runner = new PodmanCommandRunner((args, _) =>
            {
                calls.Add([.. args]);
                if (args[0] == "tag" && concurrentPublication != "none")
                {
                    string publishedId = concurrentPublication == "same-connector" ? connector.ConnectorId : "other-connector";
                    desired.ApplyDesiredState(
                        concurrentPublication == "multiple" ? 3 : 2,
                        WorkspaceDesiredStateSyncReason.Bootstrap,
                        Settings(concurrentPublication == "settings" ? 60 : 30),
                        [],
                        [connector with { ConnectorId = publishedId }],
                        [],
                        concurrentPublication == "different-publisher" ? "replacement-gateway" : "gateway");
                }

                if (args[0] == "image" && args.Count > 1 && args[1] == "exists")
                {
                    return Task.FromResult(new PodmanCommandResult(1, string.Empty, "image not known", ProcessTerminationReason.NonZeroExit));
                }

                if (args[0] == "image" && args.Count > 1 && args[1] == "inspect")
                {
                    if (args.Contains("--format", StringComparer.Ordinal))
                    {
                        return Task.FromResult(new PodmanCommandResult(0, imageId + Environment.NewLine, string.Empty, ProcessTerminationReason.Completed));
                    }

                    return Task.FromResult(new PodmanCommandResult(0,
                        "[{\"Id\":\"" + imageId + "\",\"RepoDigests\":[],\"Digest\":\"\"}]",
                        string.Empty,
                        ProcessTerminationReason.Completed));
                }

                if (args[0] == "inspect")
                {
                    if (!runStarted)
                    {
                        return Task.FromResult(new PodmanCommandResult(1, string.Empty, "no such container", ProcessTerminationReason.NonZeroExit));
                    }

                    RegistryEntry entry = Assert.Single(registry.GetAll());
                    int pid = Interlocked.Increment(ref nextPid);
                    pidIps[pid.ToString(System.Globalization.CultureInfo.InvariantCulture)] = entry.Ip;
                    string inspect = "[{\"Id\":\"" + containerId + "\",\"Image\":\"" + imageId + "\",\"EffectiveCaps\":[],\"BoundingCaps\":[],\"HostConfig\":{\"SecurityOpt\":[\"no-new-privileges\"],\"Sysctls\":{\"net.ipv6.conf.all.disable_ipv6\":\"1\"},\"Privileged\":false,\"CapAdd\":[],\"CapDrop\":[\"ALL\"],\"ReadonlyRootfs\":true,\"NetworkMode\":\"bridge\",\"Devices\":[]},\"Config\":{\"User\":\"0:0\"},\"State\":{\"Pid\":" + pid + ",\"Status\":\"running\"},\"NetworkSettings\":{\"Networks\":{\"kautuka-workload\":{\"IPAddress\":\"" + entry.Ip + "\"}}},\"Mounts\":[]}]";
                    return Task.FromResult(new PodmanCommandResult(0, inspect, string.Empty, ProcessTerminationReason.Completed));
                }

                if (args[0] == "container" && args.Count > 1 && args[1] == "exists")
                {
                    return Task.FromResult(new PodmanCommandResult(1, string.Empty, "no such container", ProcessTerminationReason.NonZeroExit));
                }

                if (args[0] == "run")
                {
                    runStarted = true;
                    return Task.FromResult(new PodmanCommandResult(0, containerId, string.Empty, ProcessTerminationReason.Completed));
                }
                else if (args[0] == "unshare")
                {
                    if (args.Contains("cat", StringComparer.Ordinal))
                    {
                        return Task.FromResult(new PodmanCommandResult(0, "1", string.Empty, ProcessTerminationReason.Completed));
                    }

                    string ip = pidIps[args[3]];
                    string nft = BoundaryRulesetRenderer.ResolveTemplate("kautuka_workload.nft.json.tmpl")
                        .Replace("{{IP}}", ip, StringComparison.Ordinal);
                    return Task.FromResult(new PodmanCommandResult(0, nft, string.Empty, ProcessTerminationReason.Completed));
                }

                return Task.FromResult(new PodmanCommandResult(0, string.Empty, string.Empty, ProcessTerminationReason.Completed));
            });
            string server = "import json,sys\nfor line in sys.stdin:\n r=json.loads(line)\n m=r.get('method')\n i=r.get('id')\n if m=='initialize': print(json.dumps({'jsonrpc':'2.0','id':i,'result':{'protocolVersion':'2025-03-26','capabilities':{'tools':{}},'serverInfo':{'name':'materialized-test','version':'1'}}}),flush=True)\n elif m=='tools/list': print(json.dumps({'jsonrpc':'2.0','id':i,'result':{'tools':[]}}),flush=True)\n";
            var store = new ImageCompatibilityStore(Path.Combine(stateDirectory, "image-compat.json"));
            ImageCompatProfile profile = store.SetProfile(new NetworkAccessActor("admin", IsAdmin: true), imageId, [ImageCompatibilityStore.RootUser]);
            await using var service = new WorkspaceContainerConnectorExecutionService(
                desired,
                runner,
                new McpbImageMaterializer(runner, NullLogger<McpbImageMaterializer>.Instance),
                new PodmanContainerTool(runner),
                NullLogger<WorkspaceContainerConnectorExecutionService>.Instance,
                addressAllocator: new WorkloadAddressAllocator(Path.Combine(stateDirectory, "ip-cursor.json")),
                workloadRegistry: registry,
                postStartVerifier: new WorkloadPostStartVerifier(runner, desired),
                imageCompatibilityStore: store,
                probePolicyStore: policyStore,
                workloadLaunchGate: TestOpenWorkloadLaunchGate,
                stdioCommand: "python3",
                stdioArgumentsPrefix: ["-u", "-c", server]);

            (bool ok, IReadOnlyList<McpClientTool> tools, _, string? failureMessage, _) = await service.ProbeConnectorAsync(
                connector,
                30,
                TestContext.Current.CancellationToken,
                "probe-user",
                saveDiscovery);

            if (!expectSuccess)
            {
                Assert.False(ok);
                Assert.Contains("Connector desired state changed during preparation.", failureMessage, StringComparison.Ordinal);
                Assert.DoesNotContain(calls, args => args[0] == "run");
                Assert.Empty(registry.GetAll());
                return;
            }

            Assert.True(ok, failureMessage);
            Assert.False(policyStore.TryEvaluateTemporaryProbeDecision(containerId, "probe-user", "binary-mcpb-probe", "host", "probe.example", 443, out _, out _));
            Assert.Empty(tools);
            string[] build = Assert.Single(calls, args => args[0] == "build");
            Assert.Contains("--pull=never", build);
            Assert.Contains("--network=none", build);
            Assert.Contains(calls, args => args[0] == "tag");
            Assert.Contains(calls, args => args.Length > 2 && args[0] == "image" && args[1] == "inspect"
                && args[^1].StartsWith("localhost/kautuka-mcpb-binary:", StringComparison.Ordinal));
            string[] run = Assert.Single(calls, args => args[0] == "run");
            Assert.Contains(imageId, run, StringComparer.Ordinal);
            Assert.DoesNotContain(run, static arg => arg.StartsWith("localhost/kautuka-mcpb-", StringComparison.Ordinal));
            Assert.Contains(calls, args => args[0] == "inspect");
            Assert.Contains(calls, args => args[0] == "unshare");
            RegistryEntry entry = Assert.Single(registry.GetAll());
            Assert.Equal(profile.Relaxations, entry.CompatProfile!.Split(',', StringSplitOptions.RemoveEmptyEntries));
        }
        finally
        {
            File.Delete(bundlePath);
            Directory.Delete(stateDirectory, recursive: true);
        }
    }

    private static async Task<object> InvokeImageCompatibilityResolutionAsync(MethodInfo method, WorkspaceContainerConnectorExecutionService service, string image)
    {
        object? invocation = method.Invoke(service, [image, CancellationToken.None]);
        Task resolutionTask = Assert.IsAssignableFrom<Task>(invocation);
        await resolutionTask;
        return resolutionTask.GetType().GetProperty("Result")?.GetValue(resolutionTask)
            ?? throw new InvalidOperationException("Image compatibility resolution returned no result.");
    }

    private static T? ReadProperty<T>(object instance, string propertyName)
    {
        object? value = instance.GetType().GetProperty(propertyName)?.GetValue(instance);
        return value is null ? default : Assert.IsType<T>(value);
    }

    [Fact]
    public async Task V7_41_ConnectorStartupFailureIncludesContainerLogs_WithNegativeControl()
    {
        if (!OperatingSystem.IsLinux())
        {
            Assert.Skip("The stdio process failure reproduction uses a Linux Python process.");
        }

        string stateDirectory = Path.Combine(Path.GetTempPath(), "nac-v7-connector-diagnostics-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(stateDirectory);
        try
        {
            const string diagnostic = "synthetic connector startup diagnostic from Podman";
            const string server = "import json,sys\nfor line in sys.stdin:\n r=json.loads(line)\n m=r.get('method')\n i=r.get('id')\n if m=='initialize': print(json.dumps({'jsonrpc':'2.0','id':i,'result':{'protocolVersion':'2025-03-26','capabilities':{'tools':{}},'serverInfo':{'name':'test','version':'1'}}}),flush=True)\n elif m=='tools/list': sys.exit(7)\n";
            var registry = new WorkloadRegistry();
            var commands = new List<string[]>();
            var pidIps = new Dictionary<string, string>(StringComparer.Ordinal);
            var runner = new PodmanCommandRunner((args, _) =>
            {
                commands.Add([.. args]);
                if (args.Count > 1 && args[0] == "image" && args[1] == "inspect")
                {
                    string imageId = "sha256:" + new string('a', 64);
                    return Task.FromResult(new PodmanCommandResult(0, imageId, string.Empty, ProcessTerminationReason.Completed));
                }
                if (args[0] == "inspect" && args.Contains("--format", StringComparer.Ordinal))
                    return Task.FromResult(new PodmanCommandResult(0, "true", string.Empty, ProcessTerminationReason.Completed));
                if (args[0] == "run")
                {
                    return Task.FromResult(new PodmanCommandResult(0, string.Empty, string.Empty, ProcessTerminationReason.Completed));
                }

                if (args[0] == "inspect")
                {
                    RegistryEntry entry = Assert.Single(registry.GetAll());
                    const int pid = 4242;
                    pidIps[pid.ToString(System.Globalization.CultureInfo.InvariantCulture)] = entry.Ip;
                    const string canonicalId = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
                    string inspect = "[{\"Id\":\"" + canonicalId + "\",\"EffectiveCaps\":[],\"BoundingCaps\":[],\"HostConfig\":{\"SecurityOpt\":[\"no-new-privileges\"],\"Sysctls\":{\"net.ipv6.conf.all.disable_ipv6\":\"1\"},\"Privileged\":false,\"CapAdd\":[],\"CapDrop\":[\"ALL\"],\"ReadonlyRootfs\":true,\"NetworkMode\":\"bridge\",\"Devices\":[]},\"Config\":{\"User\":\"65532:65532\"},\"State\":{\"Pid\":" + pid + "},\"NetworkSettings\":{\"Networks\":{\"kautuka-workload\":{\"IPAddress\":\"" + entry.Ip + "\"}}},\"Mounts\":[]}]";
                    return Task.FromResult(new PodmanCommandResult(0, inspect, string.Empty, ProcessTerminationReason.Completed));
                }

                if (args[0] == "unshare")
                {
                    if (args.Contains("cat", StringComparer.Ordinal))
                    {
                        return Task.FromResult(new PodmanCommandResult(0, "1", string.Empty, ProcessTerminationReason.Completed));
                    }

                    string ip = pidIps[args[3]];
                    string nft = BoundaryRulesetRenderer.ResolveTemplate("kautuka_workload.nft.json.tmpl")
                        .Replace("{{IP}}", ip, StringComparison.Ordinal);
                    return Task.FromResult(new PodmanCommandResult(0, nft, string.Empty, ProcessTerminationReason.Completed));
                }

                if (args[0] == "logs")
                {
                    return Task.FromResult(new PodmanCommandResult(0, diagnostic, string.Empty, ProcessTerminationReason.Completed));
                }

                return Task.FromResult(new PodmanCommandResult(0, string.Empty, string.Empty, ProcessTerminationReason.Completed));
            });
            var desired = new WorkspaceDesiredStateStore();
            var connector = new WorkspaceMcpContainerConnectorDefinition(
                "diagnostic-probe",
                "localhost/test:latest",
                string.Empty,
                [],
                true,
                false,
                null,
                null,
                [],
                30,
                600);
            await using var service = new WorkspaceContainerConnectorExecutionService(
                desired,
                runner,
                new McpbImageMaterializer(runner, NullLogger<McpbImageMaterializer>.Instance),
                new PodmanContainerTool(runner),
                NullLogger<WorkspaceContainerConnectorExecutionService>.Instance,
                addressAllocator: new WorkloadAddressAllocator(Path.Combine(stateDirectory, "ip-cursor.json")),
                workloadRegistry: registry,
                postStartVerifier: new WorkloadPostStartVerifier(runner, desired),
                workloadLaunchGate: TestOpenWorkloadLaunchGate,
                stdioCommand: "python3",
                stdioArgumentsPrefix: ["-u", "-c", server]);

            var result = await service.ProbeConnectorAsync(connector, 15, TestContext.Current.CancellationToken, "user-a");

            Assert.False(result.Ok);
            Assert.Contains(diagnostic, result.FailureMessage, StringComparison.Ordinal);
            Assert.Contains(commands, static args => args[0] == "logs");
            Assert.Empty(registry.GetAll());
        }
        finally
        {
            Directory.Delete(stateDirectory, recursive: true);
        }
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, false)]
    [InlineData(true, true)]
    [InlineData(false, true)]
    public async Task V7_45_BrakedProbeIsRefusedBeforePodman_AndOtherUserReachesRun_WithNegativeControl(bool confirmAbsent, bool timedOut)
    {
        string stateDirectory = Path.Combine(Path.GetTempPath(), "nac-v7-launch-gate-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(stateDirectory);
        try
        {
            using var policyStore = new NetworkAccessPolicyStore(Path.Combine(stateDirectory, "policy.json"));
            policyStore.SetBrake(new NetworkAccessActor("admin", IsAdmin: true), "user", "blocked-user", on: true);
            var commands = new List<string[]>();
            bool runAttempted = false;
            var runner = new PodmanCommandRunner((args, _) =>
            {
                commands.Add([.. args]);
                if (args[0] == "image" && args.Count > 1 && args[1] == "inspect")
                {
                    string id = "sha256:" + new string('a', 64);
                    return Task.FromResult(new PodmanCommandResult(0, args.Contains("--format", StringComparer.Ordinal) ? id : "[{\"Id\":\"" + id + "\"}]", string.Empty, ProcessTerminationReason.Completed));
                }
                if (args[0] == "run")
                {
                    runAttempted = true;
                    return Task.FromResult(new PodmanCommandResult(timedOut ? 124 : 125, string.Empty, "synthetic start failure", timedOut ? ProcessTerminationReason.TimedOut : ProcessTerminationReason.NonZeroExit));
                }

                if (args[0] == "inspect")
                {
                    return Task.FromResult(new PodmanCommandResult(125, string.Empty, "no such container", ProcessTerminationReason.NonZeroExit));
                }

                if (args[0] == "container" && args.Count > 1 && args[1] == "exists")
                {
                    if (runAttempted && !confirmAbsent)
                        return Task.FromResult(new PodmanCommandResult(125, string.Empty, "inspection unavailable", ProcessTerminationReason.NonZeroExit));
                    return Task.FromResult(new PodmanCommandResult(1, string.Empty, "no such container", ProcessTerminationReason.NonZeroExit));
                }

                return Task.FromResult(new PodmanCommandResult(0, string.Empty, string.Empty, ProcessTerminationReason.Completed));
            });
            var registry = new WorkloadRegistry();
            var gate = new WorkloadLaunchGate(new TestWorkloadStartupState(true), policyStore);
            await using var service = new WorkspaceContainerConnectorExecutionService(
                new WorkspaceDesiredStateStore(),
                runner,
                new McpbImageMaterializer(runner, NullLogger<McpbImageMaterializer>.Instance),
                new PodmanContainerTool(runner),
                NullLogger<WorkspaceContainerConnectorExecutionService>.Instance,
                addressAllocator: new WorkloadAddressAllocator(Path.Combine(stateDirectory, "ip-cursor.json")),
                workloadRegistry: registry,
                postStartVerifier: new WorkloadPostStartVerifier(runner),
                workloadLaunchGate: gate);
            var connector = new WorkspaceMcpContainerConnectorDefinition(
                "launch-gate-test",
                "ghcr.io/devkrity/kautuka-base@sha256:" + new string('a', 64),
                "fake-server",
                [],
                true,
                false,
                null,
                null,
                [],
                30,
                600);

            var blocked = await service.ProbeConnectorAsync(connector, 15, TestContext.Current.CancellationToken, "blocked-user");
            Assert.False(blocked.Ok);
            Assert.Contains("network brake is active", blocked.FailureMessage, StringComparison.OrdinalIgnoreCase);
            Assert.Empty(commands);
            Assert.Empty(registry.GetAll());

            var allowedUser = await service.ProbeConnectorAsync(connector, 15, TestContext.Current.CancellationToken, "other-user");
            Assert.False(allowedUser.Ok);
            Assert.True(commands.Any(static args => args[0] == "run"), allowedUser.FailureMessage);
            string[] run = Assert.Single(commands, static args => args[0] == "run");
            Assert.Contains("io.kautuka.user=other-user", run, StringComparer.Ordinal);
            Assert.DoesNotContain(run, static argument => argument.StartsWith("io.kautuka.agent=", StringComparison.Ordinal));
            Assert.Contains("io.kautuka.purpose=probe", run, StringComparer.Ordinal);
            if (confirmAbsent) Assert.Empty(registry.GetAll());
            else Assert.Equal("deny-all", Assert.Single(registry.GetAll()).Egress);
        }
        finally
        {
            Directory.Delete(stateDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task V7_20_ReconcileLaunchChecksBrakeBeforeRecreate_WithNegativeControl()
    {
        string stateDirectory = Path.Combine(Path.GetTempPath(), "nac-v7-reconcile-brake-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(stateDirectory);
        try
        {
            using var policyStore = new NetworkAccessPolicyStore(Path.Combine(stateDirectory, "policy.json"));
            policyStore.SetBrake(new NetworkAccessActor("admin", IsAdmin: true), "user", "user-alpha", on: true);
            var commands = new List<string[]>();
            var runner = new PodmanCommandRunner((args, _) =>
            {
                commands.Add([.. args]);
                if (args[0] == "inspect")
                {
                    return Task.FromResult(new PodmanCommandResult(125, string.Empty, "missing", ProcessTerminationReason.NonZeroExit));
                }

                if (args[0] == "container")
                {
                    return Task.FromResult(new PodmanCommandResult(1, string.Empty, "missing", ProcessTerminationReason.NonZeroExit));
                }

                return Task.FromResult(Success(string.Empty));
            });
            var workspace = FingerprintContext("agent-alpha", Path.Combine(stateDirectory, "workspace"));
            var connector = new WorkspaceMcpContainerConnectorDefinition(
                "user-alpha::brake-reconcile",
                "ghcr.io/devkrity/kautuka-base@sha256:" + new string('a', 64),
                "server",
                [], true, false, null, null, [], 30, 600);
            await using var service = new WorkspaceContainerConnectorExecutionService(
                new WorkspaceDesiredStateStore(),
                runner,
                new McpbImageMaterializer(runner, NullLogger<McpbImageMaterializer>.Instance),
                new PodmanContainerTool(runner),
                NullLogger<WorkspaceContainerConnectorExecutionService>.Instance,
                addressAllocator: new WorkloadAddressAllocator(Path.Combine(stateDirectory, "ip-cursor.json")),
                workloadRegistry: new WorkloadRegistry(),
                workloadLaunchGate: new WorkloadLaunchGate(new TestWorkloadStartupState(true), policyStore));

            MethodInfo ensure = typeof(WorkspaceContainerConnectorExecutionService)
                .GetMethod("EnsureConnectorContainerRunningAsync", BindingFlags.Instance | BindingFlags.NonPublic)
                ?? throw new InvalidOperationException("EnsureConnectorContainerRunningAsync method was not found.");
            Task launch = Assert.IsAssignableFrom<Task>(ensure.Invoke(service, [
                connector,
                "runtime-key",
                "brake-reconcile-container",
                WorkspaceMcpExecutionContext.Isolated,
                workspace,
                CancellationToken.None,
                false,
                workspace,
                null,
            ]));

            InvalidOperationException exception = await Assert.ThrowsAsync<InvalidOperationException>(() => launch);
            Assert.Contains("user network brake is active", exception.Message, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(commands, static args => args[0] is "run" or "rm");
        }
        finally
        {
            Directory.Delete(stateDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task V7_48_OverlappingCallsVerifyAndDeregisterTheirOwnEphemeralWorkloads()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        const string connectorId = "overlap-ephemeral-test";
        string workspaceRoot = Path.Combine(Path.GetTempPath(), "nac-v7-overlap-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workspaceRoot);
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        string server = "import socket,sys,json\ns=socket.create_connection(('127.0.0.1'," + port + "))\ns.sendall(json.dumps(sys.argv[1:]).encode()+b'\\n')\ns.recv(1)\n" +
            "for line in sys.stdin:\n r=json.loads(line)\n m=r.get('method')\n i=r.get('id')\n if m=='initialize': print(json.dumps({'jsonrpc':'2.0','id':i,'result':{'protocolVersion':'2025-03-26','capabilities':{'tools':{}},'serverInfo':{'name':'test','version':'1'}}}),flush=True)\n elif m=='tools/call':\n  s.sendall(b'call\\n')\n  s.recv(1)\n  s.close()\n  print(json.dumps({'jsonrpc':'2.0','id':i,'result':{'content':[{'type':'text','text':'ok'}],'isError':False}}),flush=True)\n";
        var inspectedNames = new System.Collections.Concurrent.ConcurrentBag<string>();
        var egressDuringVerification = new System.Collections.Concurrent.ConcurrentBag<string>();
        var launchArguments = new System.Collections.Concurrent.ConcurrentBag<string[]>();
        var pidIps = new System.Collections.Concurrent.ConcurrentDictionary<string, string>();
        int nextPid = 4242;
        var registry = new WorkloadRegistry();
        var runner = new PodmanCommandRunner((args, _) =>
        {
            if (args[0] == "inspect" && args.Contains("--format", StringComparer.Ordinal))
                return Task.FromResult(new PodmanCommandResult(0, "true", string.Empty, ProcessTerminationReason.Completed));
            if (args[0] == "inspect")
            {
                inspectedNames.Add(args[1]);
                RegistryEntry entry = Assert.Single(registry.GetAll(), item => item.ContainerId == args[1]);
                egressDuringVerification.Add(entry.Egress);
                int pid = Interlocked.Increment(ref nextPid);
                pidIps[pid.ToString(System.Globalization.CultureInfo.InvariantCulture)] = entry.Ip;
                string canonicalId = new string('0', 56) + Convert.ToHexString(IPAddress.Parse(entry.Ip).GetAddressBytes());
                string inspect = "[{\"Id\":\"" + canonicalId + "\",\"EffectiveCaps\":[],\"BoundingCaps\":[],\"HostConfig\":{\"SecurityOpt\":[\"no-new-privileges\"],\"Sysctls\":{\"net.ipv6.conf.all.disable_ipv6\":\"1\"},\"Privileged\":false,\"CapAdd\":[],\"CapDrop\":[\"ALL\"],\"ReadonlyRootfs\":true,\"NetworkMode\":\"bridge\",\"Devices\":[]},\"Config\":{\"User\":\"65532:65532\"},\"State\":{\"Pid\":" + pid + "},\"NetworkSettings\":{\"Networks\":{\"kautuka-workload\":{\"IPAddress\":\"" + entry.Ip + "\"}}},\"Mounts\":[]}]";
                return Task.FromResult(new PodmanCommandResult(0, inspect, string.Empty, ProcessTerminationReason.Completed));
            }

            if (args[0] == "unshare")
            {
                if (args.Contains("cat", StringComparer.Ordinal))
                {
                    return Task.FromResult(new PodmanCommandResult(0, "1", string.Empty, ProcessTerminationReason.Completed));
                }

                string ip = pidIps[args[3]];
                string nft = BoundaryRulesetRenderer.ResolveTemplate("kautuka_workload.nft.json.tmpl").Replace("{{IP}}", ip, StringComparison.Ordinal);
                return Task.FromResult(new PodmanCommandResult(0, nft, string.Empty, ProcessTerminationReason.Completed));
            }

            return Task.FromResult(new PodmanCommandResult(0, string.Empty, string.Empty, ProcessTerminationReason.Completed));
        });
        var desired = new WorkspaceDesiredStateStore();
        string ownerWorkspace = Path.Combine(workspaceRoot, "users", "u", "agents", "a");
        Directory.CreateDirectory(ownerWorkspace);
        var workspace = new ContainerContext("agent", "a", "u", ownerWorkspace, "/workspace", "image", 30, "512m", 1, [], new Dictionary<string, string>());
        var connector = new WorkspaceMcpContainerConnectorDefinition(connectorId, "localhost/test:latest", "", [], true, false, null, null, [], 30, 600);
        await using var service = new WorkspaceContainerConnectorExecutionService(
            desired, runner,
            new McpbImageMaterializer(runner, NullLogger<McpbImageMaterializer>.Instance),
            new PodmanContainerTool(runner), NullLogger<WorkspaceContainerConnectorExecutionService>.Instance,
            addressAllocator: new WorkloadAddressAllocator(Path.Combine(workspaceRoot, "ip-cursor.json")),
            workloadRegistry: registry,
            postStartVerifier: new WorkloadPostStartVerifier(runner, desired),
            workloadLaunchGate: TestOpenWorkloadLaunchGate,
            stdioCommand: "python3",
            stdioArgumentsPrefix: ["-u", "-c", server]);

        try
        {
            _ = desired.ApplyDesiredState(1, WorkspaceDesiredStateSyncReason.Bootstrap,
                new ManagerRuntimeSettings(new ManagerWorkspaceDefinition { BaseRoot = workspaceRoot }, new ManagerSandboxRuntimeDefinition { Image = "kautuka-base:latest", DefaultTimeout = 30 }, new WorkspaceTelemetryDefinition()),
                [workspace], [connector], []);
            Task<(bool Ok, string? Result, WorkspaceMcpConnectorUnavailabilityReason? UnavailabilityReason, string? FailureMessage)> first = service.CallToolAsync(connectorId, null, "echo", "{}", 30, false, workspace, WorkspaceMcpExecutionContext.Isolated, CancellationToken.None);
            Task<(bool Ok, string? Result, WorkspaceMcpConnectorUnavailabilityReason? UnavailabilityReason, string? FailureMessage)> second = service.CallToolAsync(connectorId, null, "echo", "{}", 30, false, workspace, WorkspaceMcpExecutionContext.Isolated, CancellationToken.None);
            Task<(bool Ok, string? Result, WorkspaceMcpConnectorUnavailabilityReason? UnavailabilityReason, string? FailureMessage)[]> calls = Task.WhenAll(first, second);

            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            Task<TcpClient> firstConnection = listener.AcceptTcpClientAsync(timeout.Token).AsTask();
            if (await Task.WhenAny(calls, firstConnection) == calls)
            {
                var earlyResults = await calls;
                Assert.All(earlyResults, result => Assert.True(result.Ok, result.FailureMessage));
                Assert.Fail("The MCP subprocesses completed without reaching the overlap barrier.");
            }
            using TcpClient clientA = await firstConnection;
            using TcpClient clientB = await listener.AcceptTcpClientAsync(timeout.Token);
            RegistryEntry[] concurrentEntries = [.. registry.GetAll()];
            Assert.Equal(2, concurrentEntries.Length);
            Assert.Equal(2, concurrentEntries.Select(static entry => entry.ContainerId).Distinct(StringComparer.Ordinal).Count());
            Assert.All(concurrentEntries, entry =>
            {
                Assert.Equal("u", entry.UserId);
                Assert.Equal("a", entry.AgentId);
            });
            foreach (TcpClient client in new[] { clientA, clientB })
            {
                NetworkStream stream = client.GetStream();
                using var reader = new StreamReader(stream, System.Text.Encoding.UTF8, detectEncodingFromByteOrderMarks: false, bufferSize: 1024, leaveOpen: true);
                string argumentJson = await reader.ReadLineAsync(timeout.Token) ?? throw new EndOfStreamException("Connector launch arguments were not received.");
                string[] arguments = Assert.IsType<string[]>(JsonSerializer.Deserialize<string[]>(argumentJson));
                launchArguments.Add(arguments);
            }
            Assert.All(launchArguments, args => Assert.Contains(args[Array.IndexOf(args, "--name") + 1], service.TrackedContainerNames, StringComparer.OrdinalIgnoreCase));
            foreach (TcpClient client in new[] { clientA, clientB })
            {
                await client.GetStream().WriteAsync(new byte[] { 1 }, timeout.Token);
            }

            foreach (TcpClient client in new[] { clientA, clientB })
            {
                byte[] ready = new byte[5];
                await client.GetStream().ReadExactlyAsync(ready, timeout.Token);
                Assert.Equal("call\n", System.Text.Encoding.UTF8.GetString(ready));
            }
            Assert.All(registry.GetAll(), entry => Assert.Equal("normal", entry.Egress));
            foreach (TcpClient client in new[] { clientA, clientB })
                await client.GetStream().WriteAsync(new byte[] { 1 }, timeout.Token);

            var results = await calls;
            Assert.All(results, result => Assert.True(result.Ok, result.FailureMessage));
            Assert.Equal(2, inspectedNames.Count);
            Assert.Equal(2, inspectedNames.Distinct(StringComparer.Ordinal).Count());
            Assert.Equal(2, egressDuringVerification.Count);
            Assert.All(egressDuringVerification, egress => Assert.Equal("proxy-only", egress));
            Assert.Equal(2, launchArguments.Count);
            Assert.All(launchArguments, run =>
            {
                Assert.Contains("io.kautuka.user=u", run, StringComparer.Ordinal);
                Assert.Contains("io.kautuka.agent=a", run, StringComparer.Ordinal);
                Assert.DoesNotContain(run, static argument => argument.StartsWith("io.kautuka.purpose=", StringComparison.Ordinal));
            });
            Assert.Empty(registry.GetAll());
        }
        finally
        {
            listener.Stop();
            Directory.Delete(workspaceRoot, recursive: true);
        }
    }

    [Fact]
    public async Task V7_45_PersistentIsolatedAgentLaunchUsesIdentityContextLabels()
    {
        if (!OperatingSystem.IsLinux())
        {
            Assert.Skip("The MCP server launch reproduction uses a Linux Python process.");
        }

        string workspaceRoot = Path.Combine(Path.GetTempPath(), "nac-v7-persistent-identity-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workspaceRoot);
        try
        {
            var runArguments = new List<string[]>();
            var runner = new PodmanCommandRunner((args, _) =>
            {
                if (args[0] == "run")
                {
                    runArguments.Add([.. args]);
                    return Task.FromResult(new PodmanCommandResult(0, string.Empty, string.Empty, ProcessTerminationReason.Completed));
                }

                if (args[0] == "image" && args.Count > 1 && args[1] == "inspect")
                {
                    return Task.FromResult(new PodmanCommandResult(0, "sha256:synthetic", string.Empty, ProcessTerminationReason.Completed));
                }

                if (args[0] == "inspect")
                {
                    return Task.FromResult(new PodmanCommandResult(125, string.Empty, "no such container", ProcessTerminationReason.NonZeroExit));
                }

                if (args[0] == "container" && args.Count > 1 && args[1] == "exists")
                {
                    return Task.FromResult(new PodmanCommandResult(1, string.Empty, "no such container", ProcessTerminationReason.NonZeroExit));
                }

                return Task.FromResult(new PodmanCommandResult(0, string.Empty, string.Empty, ProcessTerminationReason.Completed));
            });
            string ownerWorkspace = Path.Combine(workspaceRoot, "users", "user-persistent", "agents", "agent-persistent");
            Directory.CreateDirectory(ownerWorkspace);
            var workspace = new ContainerContext("agent", "agent-persistent", "user-persistent", ownerWorkspace, "/workspace", "image", 30, "512m", 1, [], new Dictionary<string, string>());
            var connector = new WorkspaceMcpContainerConnectorDefinition(
                "persistent-identity-test", "localhost/test:latest", "test-command", [], true, false, null, null, [], 30, 600);
            var desired = new WorkspaceDesiredStateStore();
            _ = desired.ApplyDesiredState(1, WorkspaceDesiredStateSyncReason.Bootstrap,
                new ManagerRuntimeSettings(new ManagerWorkspaceDefinition { BaseRoot = workspaceRoot }, new ManagerSandboxRuntimeDefinition { Image = "kautuka-base:latest", DefaultTimeout = 30 }, new WorkspaceTelemetryDefinition()),
                [workspace], [connector], []);
            const string server = "import json,sys\nfor line in sys.stdin:\n r=json.loads(line)\n m=r.get('method')\n i=r.get('id')\n if m=='initialize': print(json.dumps({'jsonrpc':'2.0','id':i,'result':{'protocolVersion':'2025-03-26','capabilities':{'tools':{}},'serverInfo':{'name':'test','version':'1'}}}),flush=True)\n elif m=='tools/list': print(json.dumps({'jsonrpc':'2.0','id':i,'result':{'tools':[]}}),flush=True)\n";
            await using var service = new WorkspaceContainerConnectorExecutionService(
                desired, runner,
                new McpbImageMaterializer(runner, NullLogger<McpbImageMaterializer>.Instance),
                new PodmanContainerTool(runner), NullLogger<WorkspaceContainerConnectorExecutionService>.Instance,
                addressAllocator: new WorkloadAddressAllocator(Path.Combine(workspaceRoot, "ip-cursor.json")),
                workloadLaunchGate: TestOpenWorkloadLaunchGate,
                stdioCommand: "python3",
                stdioArgumentsPrefix: ["-u", "-c", server]);

            var result = await service.ListToolsAsync(
                connector.ConnectorId, null, 30, false, workspace, WorkspaceMcpExecutionContext.Isolated, CancellationToken.None);

            Assert.True(result.Ok, result.FailureMessage);
            string[] run = Assert.Single(runArguments);
            Assert.Contains("io.kautuka.user=user-persistent", run, StringComparer.Ordinal);
            Assert.Contains("io.kautuka.agent=agent-persistent", run, StringComparer.Ordinal);
            Assert.DoesNotContain(run, static argument => argument.StartsWith("io.kautuka.purpose=", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(workspaceRoot, recursive: true);
        }
    }

    [Fact]
    public async Task V7_18_EphemeralCallRefusesWhenPostStartVerifierIsUnavailable()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        const string connectorId = "unverified-ephemeral-test";
        string workspaceRoot = Path.Combine(Path.GetTempPath(), "nac-v7-unverified-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workspaceRoot);
        var runner = new PodmanCommandRunner((args, _) => Task.FromResult(args[0] switch
        {
            // This test uses a host Python process as the fake MCP transport, so no verified
            // container identity exists. Name lookup cannot prove that a container was not
            // renamed or replaced, so the production registry must retain its deny-all entry.
            "inspect" => new PodmanCommandResult(125, string.Empty, "not found", ProcessTerminationReason.NonZeroExit),
            "container" => new PodmanCommandResult(1, string.Empty, "not found", ProcessTerminationReason.NonZeroExit),
            _ => new PodmanCommandResult(0, string.Empty, string.Empty, ProcessTerminationReason.Completed),
        }));
        var registry = new WorkloadRegistry();
        var desired = new WorkspaceDesiredStateStore();
        string ownerWorkspace = Path.Combine(workspaceRoot, "users", "u", "agents", "a");
        Directory.CreateDirectory(ownerWorkspace);
        var workspace = new ContainerContext("agent", "a", "u", ownerWorkspace, "/workspace", "image", 30, "512m", 1, [], new Dictionary<string, string>());
        var connector = new WorkspaceMcpContainerConnectorDefinition(connectorId, "localhost/test:latest", "", [], true, false, null, null, [], 30, 600);
        _ = desired.ApplyDesiredState(1, WorkspaceDesiredStateSyncReason.Bootstrap,
            new ManagerRuntimeSettings(new ManagerWorkspaceDefinition { BaseRoot = workspaceRoot }, new ManagerSandboxRuntimeDefinition { Image = "kautuka-base:latest", DefaultTimeout = 30 }, new WorkspaceTelemetryDefinition()),
            [workspace], [connector], []);
        const string server = "import json,sys\nfor line in sys.stdin:\n r=json.loads(line)\n m=r.get('method')\n i=r.get('id')\n if m=='initialize': print(json.dumps({'jsonrpc':'2.0','id':i,'result':{'protocolVersion':'2025-03-26','capabilities':{'tools':{}},'serverInfo':{'name':'test','version':'1'}}}),flush=True)\n elif m=='tools/call': print(json.dumps({'jsonrpc':'2.0','id':i,'result':{'content':[{'type':'text','text':'called'}],'isError':False}}),flush=True)\n";
        await using var service = new WorkspaceContainerConnectorExecutionService(
            desired, runner, new McpbImageMaterializer(runner, NullLogger<McpbImageMaterializer>.Instance),
            new PodmanContainerTool(runner), NullLogger<WorkspaceContainerConnectorExecutionService>.Instance,
            addressAllocator: new WorkloadAddressAllocator(Path.Combine(workspaceRoot, "ip-cursor.json")),
            workloadRegistry: registry,
            postStartVerifier: null,
            workloadLaunchGate: TestOpenWorkloadLaunchGate,
            stdioCommand: "python3",
            stdioArgumentsPrefix: ["-u", "-c", server]);

        try
        {
            var result = await service.CallToolAsync(connectorId, null, "echo", "{}", 30, false, workspace, WorkspaceMcpExecutionContext.Isolated, CancellationToken.None);
            Assert.False(result.Ok);
            Assert.Contains("post-start verifier is unavailable", result.FailureMessage, StringComparison.OrdinalIgnoreCase);
            RegistryEntry retained = Assert.Single(registry.GetAll());
            Assert.Equal("deny-all", retained.Egress);
            Assert.Equal("none", retained.Attribution);
        }
        finally
        {
            Directory.Delete(workspaceRoot, recursive: true);
        }
    }

    [Fact]
    public async Task V7_18_ProbeStopsBeforeListingToolsWhenPostStartVerificationFails_WithNegativeControl()
    {
        if (!OperatingSystem.IsLinux())
        {
            Assert.Skip("The ephemeral stdio probe reproduction uses a Linux Python process.");
        }

        string workspaceRoot = Path.Combine(Path.GetTempPath(), "nac-v7-probe-verifier-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workspaceRoot);
        const string server = "import json,sys\nfor line in sys.stdin:\n r=json.loads(line)\n m=r.get('method')\n i=r.get('id')\n if m=='initialize': print(json.dumps({'jsonrpc':'2.0','id':i,'result':{'protocolVersion':'2025-03-26','capabilities':{'tools':{}},'serverInfo':{'name':'test','version':'1'}}}),flush=True)\n elif m=='tools/list': print(json.dumps({'jsonrpc':'2.0','id':i,'result':{'tools':[]}}),flush=True)\n";
        var registry = new WorkloadRegistry();
        var commands = new List<string[]>();
        string? observedEgress = null;
        string? observedAttribution = null;
        var runner = new PodmanCommandRunner((args, _) =>
        {
            commands.Add([.. args]);
            if (args.Count > 1 && args[0] == "image" && args[1] == "inspect")
            {
                string imageId = "sha256:" + new string('a', 64);
                return Task.FromResult(new PodmanCommandResult(0, imageId, string.Empty, ProcessTerminationReason.Completed));
            }
            if (args[0] == "run")
            {
                return Task.FromResult(new PodmanCommandResult(0, string.Empty, string.Empty, ProcessTerminationReason.Completed));
            }

            if (args[0] == "inspect" && args.Contains("--format", StringComparer.Ordinal))
            {
                return Task.FromResult(new PodmanCommandResult(0, "true", string.Empty, ProcessTerminationReason.Completed));
            }

            if (args[0] == "inspect")
            {
                RegistryEntry entry = Assert.Single(registry.GetAll());
                observedEgress = entry.Egress;
                observedAttribution = entry.Attribution;
                const string canonicalId = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
                string inspect = "[{\"Id\":\"" + canonicalId + "\",\"EffectiveCaps\":[],\"BoundingCaps\":[],\"HostConfig\":{\"SecurityOpt\":[],\"Sysctls\":{\"net.ipv6.conf.all.disable_ipv6\":\"1\"},\"Privileged\":false,\"CapAdd\":[],\"CapDrop\":[\"ALL\"],\"ReadonlyRootfs\":true,\"NetworkMode\":\"bridge\",\"Devices\":[]},\"Config\":{\"User\":\"65532:65532\"},\"NetworkSettings\":{\"Networks\":{\"kautuka-workload\":{\"IPAddress\":\"" + entry.Ip + "\"}}},\"Mounts\":[]}]";
                return Task.FromResult(new PodmanCommandResult(0, inspect, string.Empty, ProcessTerminationReason.Completed));
            }

            return Task.FromResult(new PodmanCommandResult(0, string.Empty, string.Empty, ProcessTerminationReason.Completed));
        });
        var desired = new WorkspaceDesiredStateStore();
        const string connectorId = "probe-verifier-test";
        var connector = new WorkspaceMcpContainerConnectorDefinition(connectorId, "localhost/test:latest", "", [], true, false, null, null, [], 30, 600);
        string ownerWorkspace = Path.Combine(workspaceRoot, "users", "user-a", "agents", "agent-a");
        Directory.CreateDirectory(ownerWorkspace);
        var workspace = new ContainerContext("agent", "agent-a", "user-a", ownerWorkspace, "/workspace", "image", 30, "512m", 1, [], new Dictionary<string, string>());
        _ = desired.ApplyDesiredState(1, WorkspaceDesiredStateSyncReason.Bootstrap,
            new ManagerRuntimeSettings(new ManagerWorkspaceDefinition { BaseRoot = workspaceRoot }, new ManagerSandboxRuntimeDefinition { Image = "kautuka-base:latest", DefaultTimeout = 30 }, new WorkspaceTelemetryDefinition()),
            [workspace], [connector], []);
        await using var service = new WorkspaceContainerConnectorExecutionService(
            desired,
            runner,
            new McpbImageMaterializer(runner, NullLogger<McpbImageMaterializer>.Instance),
            new PodmanContainerTool(runner),
            NullLogger<WorkspaceContainerConnectorExecutionService>.Instance,
            addressAllocator: new WorkloadAddressAllocator(Path.Combine(workspaceRoot, "ip-cursor.json")),
            workloadRegistry: registry,
            postStartVerifier: new WorkloadPostStartVerifier(runner, desired),
            workloadLaunchGate: TestOpenWorkloadLaunchGate,
            stdioCommand: "python3",
            stdioArgumentsPrefix: ["-u", "-c", server]);

        try
        {
            var result = await service.ProbeConnectorAsync(connector, 15, TestContext.Current.CancellationToken, "user-a");

            Assert.False(result.Ok);
            Assert.Equal("proxy-only", observedEgress);
            Assert.Equal("none", observedAttribution);
            Assert.Contains("WorkloadPostStartVerifier failed", result.FailureMessage, StringComparison.Ordinal);
            Assert.Contains(commands, static args => args[0] == "inspect");
            Assert.Contains(commands, static args => args[0] == "rm" && args[^1] == "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef");
            Assert.Empty(registry.GetAll());
        }
        finally
        {
            Directory.Delete(workspaceRoot, recursive: true);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task V7_18_EphemeralToolListingFailureCleansUpBeforeUseOrAfterCancellation_WithNegativeControl(bool cancelWhileListing)
    {
        if (!OperatingSystem.IsLinux())
        {
            Assert.Skip("The ephemeral stdio reproduction uses a Linux Python process.");
        }

        string workspaceRoot = Path.Combine(Path.GetTempPath(), "nac-v7-list-failure-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workspaceRoot);
        string requestLogPath = Path.Combine(workspaceRoot, "mcp-methods.log");
        string server = "import json,sys,time\nlog_path=" + JsonSerializer.Serialize(requestLogPath) + "\n" +
            "for line in sys.stdin:\n r=json.loads(line)\n m=r.get('method')\n i=r.get('id')\n open(log_path,'a').write(m+'\\n')\n" +
            " if m=='server/discover': print(json.dumps({'jsonrpc':'2.0','id':i,'error':{'code':-32601,'message':'Method not found'}}),flush=True)\n" +
            " elif m=='initialize': print(json.dumps({'jsonrpc':'2.0','id':i,'result':{'protocolVersion':'2025-03-26','capabilities':{'tools':{}},'serverInfo':{'name':'test','version':'1'}}}),flush=True)\n" +
            " elif m=='tools/list':\n" + (cancelWhileListing ? "  time.sleep(60)\n" : "  print(json.dumps({'jsonrpc':'2.0','id':i,'result':{'tools':[]}}),flush=True)\n");
        var registry = new WorkloadRegistry();
        var commands = new System.Collections.Concurrent.ConcurrentBag<string[]>();
        var pidIps = new System.Collections.Concurrent.ConcurrentDictionary<string, string>();
        int nextPid = 7000;
        var runner = new PodmanCommandRunner((args, _) =>
        {
            commands.Add([.. args]);
            if (args[0] == "inspect" && args.Contains("--format", StringComparer.Ordinal))
                return Task.FromResult(new PodmanCommandResult(0, "true", string.Empty, ProcessTerminationReason.Completed));
            if (args[0] == "run")
            {
                return Task.FromResult(new PodmanCommandResult(0, string.Empty, string.Empty, ProcessTerminationReason.Completed));
            }

            if (args[0] == "inspect")
            {
                if (!cancelWhileListing)
                {
                    return Task.FromResult(new PodmanCommandResult(125, string.Empty, "synthetic inspect failure", ProcessTerminationReason.NonZeroExit));
                }

                RegistryEntry entry = Assert.Single(registry.GetAll());
                int pid = Interlocked.Increment(ref nextPid);
                pidIps[pid.ToString(System.Globalization.CultureInfo.InvariantCulture)] = entry.Ip;
                string canonicalId = new string('0', 56) + Convert.ToHexString(IPAddress.Parse(entry.Ip).GetAddressBytes());
                string inspect = "[{\"Id\":\"" + canonicalId + "\",\"EffectiveCaps\":[],\"BoundingCaps\":[],\"HostConfig\":{\"SecurityOpt\":[\"no-new-privileges\"],\"Sysctls\":{\"net.ipv6.conf.all.disable_ipv6\":\"1\"},\"Privileged\":false,\"CapAdd\":[],\"CapDrop\":[\"ALL\"],\"ReadonlyRootfs\":true,\"NetworkMode\":\"bridge\",\"Devices\":[]},\"Config\":{\"User\":\"65532:65532\"},\"State\":{\"Pid\":" + pid + "},\"NetworkSettings\":{\"Networks\":{\"kautuka-workload\":{\"IPAddress\":\"" + entry.Ip + "\"}}},\"Mounts\":[]}]";
                return Task.FromResult(new PodmanCommandResult(0, inspect, string.Empty, ProcessTerminationReason.Completed));
            }

            if (args[0] == "unshare")
            {
                if (args.Contains("cat", StringComparer.Ordinal))
                {
                    return Task.FromResult(new PodmanCommandResult(0, "1", string.Empty, ProcessTerminationReason.Completed));
                }

                string ip = pidIps[args[3]];
                string nft = BoundaryRulesetRenderer.ResolveTemplate("kautuka_workload.nft.json.tmpl").Replace("{{IP}}", ip, StringComparison.Ordinal);
                return Task.FromResult(new PodmanCommandResult(0, nft, string.Empty, ProcessTerminationReason.Completed));
            }

            return Task.FromResult(new PodmanCommandResult(0, string.Empty, string.Empty, ProcessTerminationReason.Completed));
        });
        var desired = new WorkspaceDesiredStateStore();
        const string connectorId = "ephemeral-list-cleanup-test";
        var connector = new WorkspaceMcpContainerConnectorDefinition(connectorId, "localhost/test:latest", "", [], true, false, null, null, [], 30, 600);
        string ownerWorkspace = Path.Combine(workspaceRoot, "users", "user-a", "agents", "agent-a");
        Directory.CreateDirectory(ownerWorkspace);
        var workspace = new ContainerContext("agent", "agent-a", "user-a", ownerWorkspace, "/workspace", "image", 30, "512m", 1, [], new Dictionary<string, string>());
        _ = desired.ApplyDesiredState(1, WorkspaceDesiredStateSyncReason.Bootstrap,
            new ManagerRuntimeSettings(new ManagerWorkspaceDefinition { BaseRoot = workspaceRoot }, new ManagerSandboxRuntimeDefinition { Image = "kautuka-base:latest", DefaultTimeout = 30 }, new WorkspaceTelemetryDefinition()),
            [workspace], [connector], []);
        await using var service = new WorkspaceContainerConnectorExecutionService(
            desired,
            runner,
            new McpbImageMaterializer(runner, NullLogger<McpbImageMaterializer>.Instance),
            new PodmanContainerTool(runner),
            NullLogger<WorkspaceContainerConnectorExecutionService>.Instance,
            addressAllocator: new WorkloadAddressAllocator(Path.Combine(workspaceRoot, "ip-cursor.json")),
            workloadRegistry: registry,
            postStartVerifier: new WorkloadPostStartVerifier(runner, desired),
            workloadLaunchGate: TestOpenWorkloadLaunchGate,
            stdioCommand: "python3",
            stdioArgumentsPrefix: ["-u", "-c", server]);

        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            Task<(bool Ok, IReadOnlyList<McpClientTool> Tools, WorkspaceMcpConnectorUnavailabilityReason? UnavailabilityReason, string? FailureMessage)> listing = service.ListToolsAsync(
                connectorId, null, 30, false, workspace, WorkspaceMcpExecutionContext.Isolated, timeout.Token);
            if (cancelWhileListing)
            {
                bool reachedList = false;
                while (!timeout.IsCancellationRequested && !reachedList)
                {
                    if (File.Exists(requestLogPath))
                    {
                        string[] observedMethods = await File.ReadAllLinesAsync(requestLogPath, TestContext.Current.CancellationToken);
                        reachedList = observedMethods.Contains("tools/list", StringComparer.Ordinal);
                    }

                    if (!reachedList)
                    {
                        await Task.Delay(20, TestContext.Current.CancellationToken);
                    }
                }

                Assert.True(reachedList, "The connector did not receive tools/list before the safety timeout.");
                await timeout.CancelAsync();
            }

            var result = await listing;
            Assert.False(result.Ok);
            Assert.Contains(commands, static args => args[0] == "inspect");
            if (cancelWhileListing)
            {
                Assert.Empty(registry.GetAll());
            }
            else
            {
                RegistryEntry quarantined = Assert.Single(registry.GetAll());
                Assert.Equal("deny-all", quarantined.Egress);
                Assert.Equal("none", quarantined.Attribution);
            }
            string[] methods = File.Exists(requestLogPath)
                ? await File.ReadAllLinesAsync(requestLogPath, TestContext.Current.CancellationToken)
                : [];
            if (cancelWhileListing)
            {
                Assert.Contains("tools/list", methods, StringComparer.Ordinal);
            }
            else
            {
                Assert.Empty(methods);
            }
        }
        finally
        {
            Directory.Delete(workspaceRoot, recursive: true);
        }
    }

    [Theory]
    [InlineData("node", "abcdef0123456789", "localhost/kautuka-mcpb-node:")]
    [InlineData("UV", "0123deadbeef", "localhost/kautuka-mcpb-uv:")]
    public void BuildLocalImageReferenceUsesRecipeVersionedContentAddressedLocalTag(string serverType, string digest, string expectedPrefix)
    {
        string actual = McpbImageMaterializer.BuildLocalImageReference(serverType, digest);

        Assert.StartsWith(expectedPrefix, actual, StringComparison.Ordinal);
        Assert.Equal(64, actual[(actual.LastIndexOf(':') + 1)..].Length);
        Assert.Equal(actual, McpbImageMaterializer.BuildLocalImageReference(serverType, digest));
    }

    [Theory]
    [InlineData("node", "abcdef0123456789", "localhost/kautuka-mcpb-sandbox-node:")]
    [InlineData("UV", "0123deadbeef", "localhost/kautuka-mcpb-sandbox-uv:")]
    public void BuildSandboxArtifactImageReferenceUsesRecipeVersionedContentAddressedLocalTag(string serverType, string digest, string expectedPrefix)
    {
        string actual = McpbImageMaterializer.BuildSandboxArtifactImageReference(serverType, digest);

        Assert.StartsWith(expectedPrefix, actual, StringComparison.Ordinal);
        Assert.Equal(64, actual[(actual.LastIndexOf(':') + 1)..].Length);
        Assert.Equal(actual, McpbImageMaterializer.BuildSandboxArtifactImageReference(serverType, digest));
    }

    [Fact]
    public void UvRuntimeContainerfileUsesToolsFromTheSignedBase()
    {
        string containerfile = McpbImageMaterializer.BuildContainerfile(
            "quay.io/fedora/fedora-minimal:44",
            "uv",
            hasBundle: true);
        string installScript = McpbImageMaterializer.BuildLanguagePackageInstallScript("uv", forSandboxArtifact: false);

        Assert.Contains("USER root", containerfile, StringComparison.Ordinal);
        Assert.Contains("RUN chown -R node:node /opt/kautuka/mcpb", containerfile, StringComparison.Ordinal);
        Assert.Contains("USER node", containerfile, StringComparison.Ordinal);
        Assert.Contains("ENV UV_PYTHON_DOWNLOADS=never", containerfile, StringComparison.Ordinal);
        Assert.Contains("ENV UV_PROJECT_ENVIRONMENT=/opt/kautuka/mcpb/.venv", containerfile, StringComparison.Ordinal);
        Assert.DoesNotContain("microdnf", containerfile, StringComparison.Ordinal);
        Assert.DoesNotContain("uv sync", containerfile, StringComparison.Ordinal);
        Assert.DoesNotContain("pip install", containerfile, StringComparison.Ordinal);
        Assert.Contains("export UV_PYTHON_DOWNLOADS=never", installScript, StringComparison.Ordinal);
        Assert.DoesNotContain("uv==0.11.19", installScript, StringComparison.Ordinal);
        Assert.Contains("uv sync --no-dev --index-url \"$UV_INDEX_URL\"", installScript, StringComparison.Ordinal);
        Assert.DoesNotContain("UV_PYTHON_PREFERENCE", installScript, StringComparison.Ordinal);
    }

    [Fact]
    public void NodeRuntimeContainerfileUsesNodeFromTheSignedBase()
    {
        string containerfile = McpbImageMaterializer.BuildContainerfile(
            "quay.io/fedora/fedora-minimal:44",
            "node",
            hasBundle: true);

        Assert.Contains("RUN chown -R node:node /opt/kautuka/mcpb", containerfile, StringComparison.Ordinal);
        Assert.DoesNotContain("microdnf", containerfile, StringComparison.Ordinal);
        Assert.DoesNotContain("curl", containerfile, StringComparison.Ordinal);
        Assert.DoesNotContain("npm install", containerfile, StringComparison.Ordinal);
    }

    [Fact]
    public void UvSandboxArtifactContainerfileCopiesTheVirtualEnvironmentUsingBasePython()
    {
        const string installRoot = "/home/node/.kautuka/connectors/by-digest/test";
        string systemContainerfile = McpbImageMaterializer.BuildSandboxSystemContainerfile(
            "quay.io/fedora/fedora-minimal:44",
            "uv",
            installRoot);
        string scratchContainerfile = McpbImageMaterializer.BuildSandboxScratchContainerfile(
            "localhost/kautuka-mcpb-sandbox-uv:deps",
            installRoot);
        string installScript = McpbImageMaterializer.BuildLanguagePackageInstallScript("uv", forSandboxArtifact: true);

        Assert.Contains("ENV UV_PYTHON_DOWNLOADS=never", systemContainerfile, StringComparison.Ordinal);
        Assert.Contains($"ENV UV_PROJECT_ENVIRONMENT={installRoot}/.venv", systemContainerfile, StringComparison.Ordinal);
        Assert.DoesNotContain("UV_PYTHON_PREFERENCE", systemContainerfile, StringComparison.Ordinal);
        Assert.DoesNotContain("uv sync", systemContainerfile, StringComparison.Ordinal);
        Assert.Contains("uv sync --no-dev --no-editable --index-url \"$UV_INDEX_URL\"", installScript, StringComparison.Ordinal);
        Assert.Contains("uv venv .venv; uv pip install", installScript, StringComparison.Ordinal);
        Assert.Contains($"COPY --from=builder {installRoot}/ /artifact/", scratchContainerfile, StringComparison.Ordinal);
    }

    [Fact]
    public void LanguagePackageInstallRequiresSandboxIndexEnvironment()
    {
        InvalidOperationException missing = Assert.Throws<InvalidOperationException>(() =>
            McpbImageMaterializer.ResolveLanguagePackageIndexEnvironment([], "uv"));
        Assert.Contains("PIP_INDEX_URL", missing.Message, StringComparison.Ordinal);

        IReadOnlyList<KeyValuePair<string, string>> resolved = McpbImageMaterializer.ResolveLanguagePackageIndexEnvironment(
            [
                new("PIP_INDEX_URL", "http://kautuka-pkgs-proxy:20101/pypi/simple/"),
                new("UV_INDEX_URL", "http://kautuka-pkgs-proxy:20101/pypi/simple/"),
                new("PIP_TRUSTED_HOST", "kautuka-pkgs-proxy"),
                new("PIP_EXTRA_INDEX_URL", "https://pypi.org/simple"),
                new("UNRELATED", "ignore-me"),
            ],
            "uv");

        Assert.Equal(
            ["PIP_INDEX_URL", "PIP_TRUSTED_HOST", "UV_INDEX_URL"],
            [.. resolved.Select(static pair => pair.Key)]);
        Assert.DoesNotContain(resolved, static pair => pair.Key == "UNRELATED");
        Assert.DoesNotContain(resolved, static pair => pair.Key == "PIP_EXTRA_INDEX_URL");
    }

    [Fact]
    public void NodeLanguagePackageInstallPinsRegistryOnCli()
    {
        string installScript = McpbImageMaterializer.BuildLanguagePackageInstallScript("node", forSandboxArtifact: false);

        Assert.Contains("npm ci --omit=dev --registry=\"$NPM_CONFIG_REGISTRY\"", installScript, StringComparison.Ordinal);
        Assert.Contains("npm install --omit=dev --registry=\"$NPM_CONFIG_REGISTRY\"", installScript, StringComparison.Ordinal);
    }

    [Fact]
    public void PythonLanguagePackageInstallPinsIndexAndTrustedHost()
    {
        string installScript = McpbImageMaterializer.BuildLanguagePackageInstallScript("python", forSandboxArtifact: false);

        Assert.Contains("--index-url \"$PIP_INDEX_URL\"", installScript, StringComparison.Ordinal);
        Assert.Contains("$pip_trusted", installScript, StringComparison.Ordinal);
        Assert.Contains("--trusted-host $PIP_TRUSTED_HOST", installScript, StringComparison.Ordinal);
    }

    [Fact]
    public void BinaryBundleContainerfileMakesEntryPointExecutableWithoutPackageIndexes()
    {
        string containerfile = McpbImageMaterializer.BuildContainerfile(
            "localhost/kautuka-base:latest",
            "binary",
            hasBundle: true);

        Assert.Contains("os.chmod", containerfile, StringComparison.Ordinal);
        Assert.Contains("USER root", containerfile, StringComparison.Ordinal);
        Assert.DoesNotContain("microdnf", containerfile, StringComparison.Ordinal);
        Assert.DoesNotContain("npm ci", containerfile, StringComparison.Ordinal);
        Assert.DoesNotContain("npm install", containerfile, StringComparison.Ordinal);
        Assert.Equal("exit 0", McpbImageMaterializer.BuildLanguagePackageInstallScript("binary", forSandboxArtifact: false));
        Assert.Empty(McpbImageMaterializer.ResolveLanguagePackageIndexEnvironment([], "binary"));
    }

    [Fact]
    public void LanguagePackageRunDoesNotBakeShellEntrypoint()
    {
        List<string> arguments = McpbImageMaterializer.BuildLanguagePackageRunArguments(
            "kautuka-mcpb-pkg-test",
            "localhost/kautuka-mcpb-uv:sys",
            [new("UV_INDEX_URL", "http://kautuka-pkgs-proxy:20101/pypi/simple/")],
            "uv sync --no-dev",
            "10.247.1.10",
            isBraked: false);

        Assert.DoesNotContain("--entrypoint", arguments, StringComparer.Ordinal);
        // Podman must retain the foreground builder container until the subsequent `podman commit`.
        // `--rm` deletes it as soon as this run exits; `-d` would let commit race the installer.
        Assert.DoesNotContain("--rm", arguments, StringComparer.Ordinal);
        Assert.DoesNotContain("-d", arguments, StringComparer.Ordinal);
        Assert.Contains("--network", arguments, StringComparer.Ordinal);
        Assert.Contains(arguments, static argument => argument.StartsWith(ConnectorContainerSpecBuilder.WorkloadNetworkName, StringComparison.Ordinal));
        Assert.Equal("/bin/sh", arguments[^3]);
        Assert.Equal("-lc", arguments[^2]);
        Assert.Equal("uv sync --no-dev", arguments[^1]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PackageBuilderCleanupRemovesOnlyItsOwnRegistration(bool replaceRegistration)
    {
        DirectoryInfo temp = Directory.CreateTempSubdirectory("nac-package-build-");
        try
        {
            var registry = new WorkloadRegistry();
            RegistryEntry? replacement = null;
            string? deregisteredContainerId = null;
            registry.ContainerDeregistered += (_, args) => deregisteredContainerId = args.ContainerId;
            string promotedId = new('a', 64);
            var podman = new PodmanCommandRunner((args, _) =>
            {
                if (args[0] == "run")
                {
                    RegistryEntry builder = Assert.Single(registry.GetAll());
                    Assert.True(registry.UpdateContainerId(builder.Ip, builder.ContainerId, promotedId));
                    if (replaceRegistration)
                    {
                        replacement = builder with { ContainerId = new string('b', 64), RegisteredUtc = builder.RegisteredUtc.AddSeconds(1) };
                        registry.Register(replacement);
                        deregisteredContainerId = null;
                    }
                }

                return Task.FromResult(new PodmanCommandResult(0, "", ""));
            });
            var materializer = new McpbImageMaterializer(podman, NullLogger<McpbImageMaterializer>.Instance,
                addressAllocator: new WorkloadAddressAllocator(Path.Combine(temp.FullName, "ip.json")), workloadRegistry: registry);
            var connector = new WorkspaceMcpContainerConnectorDefinition("user::builder", "localhost/test:base", "server", [], true, false, null, "uv",
                [new("PIP_INDEX_URL", "http://kautuka-pkgs-proxy:20101/pypi/simple/"), new("UV_INDEX_URL", "http://kautuka-pkgs-proxy:20101/pypi/simple/")], 30, 600);
            MethodInfo commit = typeof(McpbImageMaterializer).GetMethod("CommitLanguagePackagesAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;

            await (Task)commit.Invoke(materializer,
                ["localhost/test:base", "localhost/test:installed", connector, "uv", false, false, TestContext.Current.CancellationToken])!;

            if (replaceRegistration)
            {
                Assert.Equal(replacement, Assert.Single(registry.GetAll()));
                Assert.Null(deregisteredContainerId);
            }
            else
            {
                Assert.Empty(registry.GetAll());
                Assert.Equal(promotedId, deregisteredContainerId);
            }
        }
        finally { temp.Delete(recursive: true); }
    }

    [Fact]
    public void V7_20_PackageBuilderPreservesBrakeState()
    {
        Assert.Throws<InvalidOperationException>(() => McpbImageMaterializer.BuildLanguagePackageRunArguments(
            "kautuka-mcpb-pkg-braked",
            "localhost/kautuka-mcpb-uv:sys",
            [new("UV_INDEX_URL", "http://kautuka-pkgs-proxy:20101/pypi/simple/")],
            "uv sync --no-dev",
            "10.247.1.11",
            isBraked: true));
    }

    [Fact]
    public async Task AcceptingDesiredStateDoesNotBuildConnectorImages()
    {
        var runner = new PodmanCommandRunner((_, _) => throw new InvalidOperationException("Podman must not be called during desired-state acceptance."));
        await using var service = new WorkspaceContainerConnectorExecutionService(
            new WorkspaceDesiredStateStore(),
            runner,
            new McpbImageMaterializer(runner, NullLogger<McpbImageMaterializer>.Instance),
            new PodmanContainerTool(runner),
            NullLogger<WorkspaceContainerConnectorExecutionService>.Instance);
        var connector = new WorkspaceMcpContainerConnectorDefinition(
            "test", "ghcr.io/devkrity/kautuka-base@sha256:" + new string('a', 64),
            "uv", [], true, true, "unavailable-bundle.mcpb", "uv", [], 30, 600);

        await service.ApplyDesiredStateUpdateAsync([], [connector], TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task BuildPodmanRunArgumentsUsePreparedBundleImageWithoutMounts()
    {
        string bundlePath = CreateBundleArchive();
        string runtimeImage = "localhost/kautuka-mcpb-uv:abc123";
        var podmanCommandRunner = new PodmanCommandRunner();
        await using var service = new WorkspaceContainerConnectorExecutionService(
            new WorkspaceDesiredStateStore(),
            podmanCommandRunner,
            new McpbImageMaterializer(podmanCommandRunner, NullLogger<McpbImageMaterializer>.Instance),
            new PodmanContainerTool(podmanCommandRunner),
            NullLogger<WorkspaceContainerConnectorExecutionService>.Instance);

        try
        {
            WorkspaceMcpContainerConnectorDefinition connector = new(
                "user-1::splose-mcp",
            "localhost/fedora-minimal:44",
                "uv",
                ["run", "-m", "splose_mcp.server"],
                true,
                false,
                bundlePath,
                "uv",
                [new KeyValuePair<string, string>("SPLOSE_ROOT", "/opt/kautuka/mcpb/config")],
                45,
                600);

            WorkspaceMcpContainerConnectorDefinition prepared = InvokeApplyBundleRuntimeContract(connector, runtimeImage);
            List<string> arguments = InvokeBuildPodmanRunArguments(service, prepared, "test-container", null);

            Assert.DoesNotContain("--volume", arguments, StringComparer.Ordinal);
            Assert.Contains(runtimeImage, arguments, StringComparer.Ordinal);
            Assert.DoesNotContain("SPLOSE_ROOT", arguments, StringComparer.Ordinal);
            Assert.DoesNotContain("/opt/kautuka/mcpb/config", arguments, StringComparer.Ordinal);
            Assert.Empty(ConnectorContainerSpecBuilder.BuildTransportEnvironment(prepared));
            Assert.Equal("/opt/kautuka/mcpb/config", ConnectorContainerSpecBuilder.FilterContainerEnvironment(prepared.Environment)["SPLOSE_ROOT"]);
            Assert.Contains("/opt/kautuka/mcpb/.venv/bin/python", arguments, StringComparer.Ordinal);
            Assert.DoesNotContain("uv", arguments, StringComparer.Ordinal);
            Assert.Contains("-m", arguments, StringComparer.Ordinal);
            Assert.Contains("splose_mcp.server", arguments, StringComparer.Ordinal);
            Assert.Null(prepared.BundlePath);
        }
        finally
        {
            File.Delete(bundlePath);
        }
    }

    [Fact]
    public async Task BuildPodmanRunArgumentsDoesNotRewriteAbsolutePathArgs()
    {
        var podmanCommandRunner = new PodmanCommandRunner();
        await using var service = new WorkspaceContainerConnectorExecutionService(
            new WorkspaceDesiredStateStore(),
            podmanCommandRunner,
            new McpbImageMaterializer(podmanCommandRunner, NullLogger<McpbImageMaterializer>.Instance),
            new PodmanContainerTool(podmanCommandRunner),
            NullLogger<WorkspaceContainerConnectorExecutionService>.Instance);

        WorkspaceMcpContainerConnectorDefinition connector = new(
            "user-1::filesystem",
            "localhost/fedora-minimal:44",
            "npx",
            ["-y", "@modelcontextprotocol/server-filesystem", "/Users/username/Desktop"],
            true,
            false,
            null,
            null,
            [],
            30,
            600);

        List<string> arguments = InvokeBuildPodmanRunArguments(service, connector, "test-container", null);

        int networkIndex = arguments.IndexOf("--network");
        Assert.True(networkIndex >= 0);
        Assert.StartsWith(ConnectorContainerSpecBuilder.WorkloadNetworkName, arguments[networkIndex + 1], StringComparison.Ordinal);
        Assert.DoesNotContain("/Users/username/Desktop:/opt/kautuka/hostpaths/0:ro", arguments, StringComparer.Ordinal);
        Assert.Contains("/Users/username/Desktop", arguments, StringComparer.Ordinal);
    }

    [Fact]
    public void IsolatedAgentAndProbeRunsKeepIdentityWhenWorkspaceMountIsFilteredOut()
    {
        WorkspaceMcpContainerConnectorDefinition connector = new(
            "connector-one",
            "ghcr.io/devkrity/kautuka-base@sha256:" + new string('a', 64),
            "node",
            ["server.js"],
            true,
            false,
            null,
            null,
            [],
            30,
            600);
        ContainerContext agentContext = CreateWorkspaceContext();

        List<string> agentArgs = WorkspaceContainerConnectorExecutionService.BuildConnectorWorkloadRunArguments(
            connector,
            "isolated-agent",
            WorkspaceMcpExecutionContext.Isolated,
            agentContext,
            persistent: false,
            allocatedIp: "10.247.1.20",
            isBraked: false);

        Assert.Contains("io.kautuka.user=test-user", agentArgs, StringComparer.Ordinal);
        Assert.Contains("io.kautuka.agent=test-agent", agentArgs, StringComparer.Ordinal);
        Assert.DoesNotContain("--volume", agentArgs, StringComparer.Ordinal);

        var probeContext = new ContainerContext(
            "user",
            "test-user",
            "test-user",
            Path.GetTempPath(),
            "/workspace",
            "image@sha256:digest",
            1,
            "512m",
            30,
            [],
            new Dictionary<string, string>());
        List<string> probeArgs = WorkspaceContainerConnectorExecutionService.BuildConnectorWorkloadRunArguments(
            connector,
            "isolated-probe",
            WorkspaceMcpExecutionContext.Isolated,
            probeContext,
            persistent: false,
            allocatedIp: "10.247.1.21",
            isBraked: false,
            purpose: "probe");

        Assert.Contains("io.kautuka.user=test-user", probeArgs, StringComparer.Ordinal);
        Assert.DoesNotContain(probeArgs, static arg => arg.StartsWith("io.kautuka.agent=", StringComparison.Ordinal));
        Assert.Contains("io.kautuka.purpose=probe", probeArgs, StringComparer.Ordinal);
        Assert.DoesNotContain("--volume", probeArgs, StringComparer.Ordinal);
    }

    [Fact]
    public async Task IsolatedProbeWithoutUserFailsBeforeRegisteringWorkload()
    {
        string stateDirectory = Path.Combine(Path.GetTempPath(), "nac-v7-probe-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(stateDirectory);
        try
        {
            var registry = new Kautuka.NetworkAccessController.WorkloadRegistry();
            var runner = new PodmanCommandRunner((_, _) => throw new InvalidOperationException("Podman must not run without identity."));
            await using var service = new WorkspaceContainerConnectorExecutionService(
                new WorkspaceDesiredStateStore(),
                runner,
                new McpbImageMaterializer(runner, NullLogger<McpbImageMaterializer>.Instance),
                new PodmanContainerTool(runner),
                NullLogger<WorkspaceContainerConnectorExecutionService>.Instance,
                new WorkloadAddressAllocator(Path.Combine(stateDirectory, "ip-cursor.json")),
                registry,
                workloadLaunchGate: TestOpenWorkloadLaunchGate);
            var connector = new WorkspaceMcpContainerConnectorDefinition(
                "connector-one",
                "ghcr.io/devkrity/kautuka-base@sha256:" + new string('a', 64),
                "",
                [],
                true,
                false,
                null,
                null,
                [],
                30,
                600);

            var result = await service.ProbeConnectorAsync(connector, 15, CancellationToken.None);

            Assert.False(result.Ok);
            Assert.Equal(WorkspaceMcpConnectorUnavailabilityReason.InitializeFailed, result.UnavailabilityReason);
            Assert.Contains("user identity is required", result.FailureMessage, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(0, registry.Count);
        }
        finally
        {
            Directory.Delete(stateDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task AgentFileConnectorReusesMatchingFingerprintAndRecreatesWhenAgentChanges()
    {
        string hostWorkspace = Path.Combine(Path.GetTempPath(), "nac-v7-fingerprint-workspace");
        const string containerName = "kautuka-mcp-user-alpha-agent-alpha-shared-files";
        var commands = new List<string[]>();
        bool containerExists = false;
        string? startedFingerprint = null;
        string? inspectJson = null;
        var registry = new Kautuka.NetworkAccessController.WorkloadRegistry();
        int launchCount = 0;
        string? latestContainerId = null;
        string? latestWorkloadIp = null;
        var runner = new PodmanCommandRunner((args, _) =>
        {
            commands.Add([.. args]);
            return Task.FromResult(args[0] switch
            {
                "inspect" when containerExists && args.Count > 1 && (args[1] == containerName || args[1] == latestContainerId) => Success(inspectJson!),
                "inspect" => new PodmanCommandResult(125, "", "missing", ProcessTerminationReason.NonZeroExit),
                "container" => new PodmanCommandResult(1, "", "missing", ProcessTerminationReason.NonZeroExit),
                "image" => Success("sha256:current"),
                "run" => Start(args),
                "rm" => Remove(),
                "unshare" or "machine" => Success(args.Contains("cat", StringComparer.Ordinal) ? "1" : BoundaryRulesetRenderer.ResolveTemplate("kautuka_workload.nft.json.tmpl").Replace("{{IP}}", latestWorkloadIp!, StringComparison.Ordinal)),
                _ => throw new InvalidOperationException("Unexpected Podman command: " + string.Join(' ', args)),
            });
        });
        PodmanCommandResult Start(IReadOnlyList<string> args)
        {
            int fingerprintIndex = args.ToList().IndexOf("--label") + 1;
            startedFingerprint = args.Skip(fingerprintIndex).First(static label => label.StartsWith("kautuka.spec-fingerprint=", StringComparison.Ordinal))["kautuka.spec-fingerprint=".Length..];
            containerExists = true;
            latestContainerId = new string('0', 63) + (++launchCount).ToString("x", System.Globalization.CultureInfo.InvariantCulture);
            latestWorkloadIp = registry.GetAll().Single(entry => string.Equals(entry.ContainerId, containerName, StringComparison.Ordinal)).Ip;
            string activeWorkspace = launchCount == 1 ? hostWorkspace : hostWorkspace + "-beta";
            inspectJson = ConnectorInspectJson(activeWorkspace, startedFingerprint, desiredVersion: 7, latestContainerId, latestWorkloadIp);
            Assert.True(PodmanContainerIdentity.TryRead(inspectJson, out string parsedId, out string parsedIp));
            Assert.Equal(latestContainerId, parsedId);
            Assert.Equal(latestWorkloadIp, parsedIp);
            return Success(latestContainerId);
        }
        PodmanCommandResult Remove()
        {
            containerExists = false;
            return Success(string.Empty);
        }

        string stateDirectory = Path.Combine(Path.GetTempPath(), "nac-v7-fingerprint-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(stateDirectory);
        try
        {
            var desiredState = new WorkspaceDesiredStateStore();
            ContainerContext agentAlpha = FingerprintContext("agent-alpha", hostWorkspace);
            ContainerContext agentBeta = FingerprintContext("agent-beta", hostWorkspace + "-beta");
            _ = desiredState.ApplyDesiredState(
                7,
                WorkspaceDesiredStateSyncReason.Bootstrap,
                new ManagerRuntimeSettings(
                    new ManagerWorkspaceDefinition { BaseRoot = Path.GetTempPath() },
                    new ManagerSandboxRuntimeDefinition { Image = "kautuka-base:latest", DefaultTimeout = 30 },
                    new WorkspaceTelemetryDefinition()),
                [agentAlpha, agentBeta],
                [],
                []);
            await using var service = new WorkspaceContainerConnectorExecutionService(
                desiredState,
                runner,
                new McpbImageMaterializer(runner, NullLogger<McpbImageMaterializer>.Instance),
                new PodmanContainerTool(runner),
                NullLogger<WorkspaceContainerConnectorExecutionService>.Instance,
                new WorkloadAddressAllocator(Path.Combine(stateDirectory, "ip-cursor.json")),
                registry,
                postStartVerifier: new WorkloadPostStartVerifier(runner, desiredState),
                workloadLaunchGate: TestOpenWorkloadLaunchGate);
            var connector = new WorkspaceMcpContainerConnectorDefinition(
                "user-alpha::shared",
                "ghcr.io/devkrity/kautuka-base@sha256:" + new string('a', 64),
                "node",
                ["server.js"], true, false, null, null, [], 30, 600)
            {
                DesiredStateVersion = 7,
                SpecFingerprint = "state-store-fingerprint-without-agent",
            };
            MethodInfo ensure = typeof(WorkspaceContainerConnectorExecutionService)
                .GetMethod("EnsureConnectorContainerRunningAsync", BindingFlags.Instance | BindingFlags.NonPublic)
                ?? throw new InvalidOperationException("EnsureConnectorContainerRunningAsync method was not found.");
            async Task EnsureAsync(ContainerContext context)
                => await ((Task)ensure.Invoke(service, [
                    connector,
                    "runtime-key",
                    containerName,
                    WorkspaceMcpExecutionContext.IsolatedWithAgentFiles,
                    context,
                    CancellationToken.None,
                    false,
                    context,
                    null,
                ])!).ConfigureAwait(false);
            await EnsureAsync(agentAlpha);
            string alphaContainerId = latestContainerId!;
            Assert.True(registry.IsVerified(latestWorkloadIp!, alphaContainerId));
            int runCount = commands.Count(static command => command[0] == "run");
            await EnsureAsync(agentAlpha);

            Assert.Equal(runCount, commands.Count(static command => command[0] == "run"));
            Assert.DoesNotContain(commands, static command => command[0] == "rm");
            Assert.Equal(1, registry.Count);

            await service.ReconcileDesiredConnectorsAsync([connector], null, CancellationToken.None);
            Assert.Equal(runCount, commands.Count(static command => command[0] == "run"));
            Assert.DoesNotContain(commands, static command => command[0] == "rm");

            await EnsureAsync(agentBeta);
            Assert.Equal(runCount + 1, commands.Count(static command => command[0] == "run"));
            Assert.Contains(commands, static command => command[0] == "rm");
            Assert.NotEqual(startedFingerprint, connector.SpecFingerprint);
            Assert.True(registry.IsVerified(latestWorkloadIp!, latestContainerId!));
            RegistryEntry betaEntry = Assert.Single(registry.GetAll());
            Assert.Equal("agent-beta", betaEntry.AgentId);
            Assert.Equal(latestContainerId, betaEntry.ContainerId);
            Assert.NotEqual(alphaContainerId, betaEntry.ContainerId);
        }
        finally
        {
            Directory.Delete(stateDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task V7_39_ReconcileInspectsTheDesiredAgentFileContainer_WithNegativeControl()
    {
        string workspaceRoot = Path.Combine(Path.GetTempPath(), "nac-v7-reconcile-agent-files");
        const string connectorId = "user-alpha::shared-reconcile";
        var workspace = FingerprintContext("agent-alpha", Path.Combine(workspaceRoot, "users", "user-alpha", "agents", "agent-alpha"));
        var sourceConnector = new WorkspaceMcpContainerConnectorDefinition(
            connectorId,
            "ghcr.io/devkrity/kautuka-base@sha256:" + new string('a', 64),
            "custom-connector-command",
            [],
            true,
            false,
            null,
            null,
            [],
            30,
            600);
        var desired = new WorkspaceDesiredStateStore();
        _ = desired.ApplyDesiredState(
            1,
            WorkspaceDesiredStateSyncReason.Bootstrap,
            new ManagerRuntimeSettings(
                new ManagerWorkspaceDefinition { BaseRoot = workspaceRoot },
                new ManagerSandboxRuntimeDefinition { Image = "kautuka-base:latest", DefaultTimeout = 30 },
                new WorkspaceTelemetryDefinition()),
            [workspace],
            [sourceConnector],
            []);
        WorkspaceMcpContainerConnectorDefinition connector = Assert.Single(desired.Snapshot.DesiredContainerConnectors);
        string containerName = InvokeBuildContainerName(connectorId, WorkspaceMcpExecutionContext.IsolatedWithAgentFiles, workspace);
        string runtimeKey = string.Join('|', connectorId, WorkspaceMcpExecutionContext.IsolatedWithAgentFiles, workspace.ScopeType, workspace.ScopeId, workspace.UserId);
        string allocatorDirectory = Path.Combine(Path.GetTempPath(), "nac-v7-39-ip-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(allocatorDirectory);
        var commands = new List<string[]>();
        bool containerExists = false;
        string? inspectJson = null;
        var registry = new WorkloadRegistry();
        const string containerId = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
        var runner = new PodmanCommandRunner((args, _) =>
        {
            commands.Add([.. args]);
            if (args[0] == "inspect" && args.Count > 1 && (args[1] == containerName || args[1] == containerId) && containerExists)
            {
                return Task.FromResult(Success(inspectJson!));
            }

            if (args[0] == "inspect")
            {
                return Task.FromResult(new PodmanCommandResult(125, string.Empty, "missing", ProcessTerminationReason.NonZeroExit));
            }

            if (args[0] == "container")
            {
                return Task.FromResult(new PodmanCommandResult(1, string.Empty, "missing", ProcessTerminationReason.NonZeroExit));
            }

            if (args[0] == "image")
            {
                return Task.FromResult(Success("sha256:current"));
            }

            if (args[0] == "run")
            {
                int labelIndex = args.ToList().IndexOf("--label") + 1;
                string fingerprint = args.Skip(labelIndex)
                    .First(static label => label.StartsWith("kautuka.spec-fingerprint=", StringComparison.Ordinal))
                    ["kautuka.spec-fingerprint=".Length..];
                string workloadIp = Assert.Single(registry.GetAll()).Ip;
                inspectJson = ConnectorInspectJson(workspace.HostWorkspacePath, fingerprint, desiredVersion: 1, containerId, workloadIp);
                containerExists = true;
                return Task.FromResult(Success(containerId));
            }

            if (args[0] is "unshare" or "machine")
            {
                string workloadIp = Assert.Single(registry.GetAll()).Ip;
                return Task.FromResult(Success(args.Contains("cat", StringComparer.Ordinal) ? "1" : BoundaryRulesetRenderer.ResolveTemplate("kautuka_workload.nft.json.tmpl").Replace("{{IP}}", workloadIp, StringComparison.Ordinal)));
            }

            if (args[0] == "rm")
            {
                containerExists = false;
                return Task.FromResult(Success(string.Empty));
            }

            throw new InvalidOperationException("Unexpected Podman command: " + string.Join(' ', args));
        });
        await using var service = new WorkspaceContainerConnectorExecutionService(
            desired,
            runner,
            new McpbImageMaterializer(runner, NullLogger<McpbImageMaterializer>.Instance),
            new PodmanContainerTool(runner),
            NullLogger<WorkspaceContainerConnectorExecutionService>.Instance,
            addressAllocator: new WorkloadAddressAllocator(Path.Combine(allocatorDirectory, "ip-cursor.json")),
            workloadRegistry: registry,
            postStartVerifier: new WorkloadPostStartVerifier(runner, desired),
            workloadLaunchGate: TestOpenWorkloadLaunchGate);
        Assert.Contains(containerName, service.DesiredIsolatedContainerNames([connector]), StringComparer.OrdinalIgnoreCase);

        MethodInfo ensure = typeof(WorkspaceContainerConnectorExecutionService)
            .GetMethod("EnsureConnectorContainerRunningAsync", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("EnsureConnectorContainerRunningAsync method was not found.");
        await ((Task)ensure.Invoke(service, [
            connector,
            runtimeKey,
            containerName,
            WorkspaceMcpExecutionContext.IsolatedWithAgentFiles,
            workspace,
            CancellationToken.None,
            false,
            workspace,
            null,
        ])!).ConfigureAwait(false);

        Assert.Equal(1, commands.Count(static args => args[0] == "run"));
        int commandsBeforeReconcile = commands.Count;
        _ = await service.ReconcileDesiredConnectorsAsync(
            [connector], null, CancellationToken.None, new HashSet<string>());
        Assert.Equal(commandsBeforeReconcile, commands.Count);

        _ = await service.ReconcileDesiredConnectorsAsync(
            [connector], null, CancellationToken.None, new HashSet<string>(StringComparer.OrdinalIgnoreCase) { containerName });

        string[][] inspected = [.. commands.Skip(commandsBeforeReconcile).Where(static args => args[0] == "inspect")];
        Assert.Contains(inspected, args => args[1] == containerName);
        Assert.All(inspected, args => Assert.Contains(args[1], new[] { containerName, containerId }));
        Assert.Equal(1, commands.Count(static args => args[0] == "run"));
        Assert.DoesNotContain(commands, static args => args[0] == "rm");
        Assert.Equal(1, registry.Count);
        Directory.Delete(allocatorDirectory, recursive: true);
    }

    [Fact]
    public async Task CompletingProbeClearsTemporaryGrantAndRequestWithoutContainerDeregistration()
    {
        string stateDirectory = Path.Combine(Path.GetTempPath(), "nac-v7-probe-complete-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(stateDirectory);
        try
        {
            var policyStore = new NetworkAccessPolicyStore(Path.Combine(stateDirectory, "policy.json"));
            var admin = new NetworkAccessActor("admin", IsAdmin: true);
            var user = new NetworkAccessActor("user-alpha", IsAdmin: false);
            _ = policyStore.SetUserPrivileges(admin, user.UserId, canApproveOwn: true);
            const string connectorId = "connector-one";
            ContainerContext probeContext = new(
                "user", user.UserId, user.UserId, "/tmp", "/tmp", string.Empty, 0, string.Empty, 0, [], new Dictionary<string, string>());
            string containerId = InvokeBuildContainerName(connectorId, WorkspaceMcpExecutionContext.Isolated, probeContext);
            string requestId = Assert.IsType<string>(policyStore.RecordOrUpdatePendingProbeRequest(
                containerId, user.UserId, null, connectorId, attribution: "user",
                destKind: "host", destination: "probe.example", port: 443, actorKind: "connector",
                origin: "probe"));
            _ = policyStore.DecideRequest(admin, requestId, allow: true, scope: null, policyStore.Revision);
            Assert.True(policyStore.TryEvaluateTemporaryProbeDecision(
                containerId, user.UserId, connectorId, "host", "probe.example", 443, out bool allowedBefore, out _));
            Assert.True(allowedBefore);

            var registry = new WorkloadRegistry();
            var runner = new PodmanCommandRunner((_, _) => throw new InvalidOperationException("No Podman command should run without an address allocator."));
            await using var service = new WorkspaceContainerConnectorExecutionService(
                new WorkspaceDesiredStateStore(), runner,
                new McpbImageMaterializer(runner, NullLogger<McpbImageMaterializer>.Instance),
                new PodmanContainerTool(runner), NullLogger<WorkspaceContainerConnectorExecutionService>.Instance,
                workloadRegistry: registry,
                probePolicyStore: policyStore);
            var connector = new WorkspaceMcpContainerConnectorDefinition(
                connectorId, "ghcr.io/devkrity/kautuka-base@sha256:" + new string('a', 64),
                string.Empty, [], true, false, null, null, [], 30, 600);
            (bool ok, _, _, _, _) = await service.ProbeConnectorAsync(connector, 15, CancellationToken.None, user.UserId);
            Assert.False(ok);

            Assert.False(policyStore.TryEvaluateTemporaryProbeDecision(
                containerId, user.UserId, connectorId, "host", "probe.example", 443, out _, out _));
            Assert.DoesNotContain(policyStore.ListRequests(user).Requests, view => view.Request.Id == requestId);
            Assert.Equal(0, registry.Count);
        }
        finally
        {
            Directory.Delete(stateDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task V7_14_ProbeCompletionClearsOnlyItsOwnContainer_WithNegativeControl()
    {
        string stateDirectory = Path.Combine(Path.GetTempPath(), "nac-v7-probe-isolation-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(stateDirectory);
        try
        {
            const string userId = "user-alpha";
            const string connectorId = "connector-overlap";
            ContainerContext probeContext = new(
                "user", userId, userId, "/tmp", "/tmp", string.Empty, 0, string.Empty, 0, [], new Dictionary<string, string>());
            string completingContainerId = InvokeBuildContainerName(connectorId, WorkspaceMcpExecutionContext.Isolated, probeContext);
            using var policyStore = new NetworkAccessPolicyStore(Path.Combine(stateDirectory, "policy.json"));
            _ = policyStore.RecordOrUpdatePendingProbeRequest(
                completingContainerId, userId, null, connectorId, attribution: "user",
                destKind: "host", destination: "first.example", port: 443, actorKind: "connector", origin: "probe");
            string overlappingRequestId = Assert.IsType<string>(policyStore.RecordOrUpdatePendingProbeRequest(
                "overlapping-probe-container", userId, null, connectorId, attribution: "user",
                destKind: "host", destination: "second.example", port: 443, actorKind: "connector", origin: "probe"));

            var registry = new WorkloadRegistry();
            registry.Register(new RegistryEntry(
                "10.247.1.131", userId, null, completingContainerId, "connector", connectorId, "isolated",
Egress: "normal", Attribution: "user", CompatProfile: null, DateTimeOffset.UtcNow, Purpose: "probe"));
            registry.Register(new RegistryEntry(
                "10.247.1.132", userId, null, "overlapping-probe-container", "connector", connectorId, "isolated",
Egress: "normal", Attribution: "user", CompatProfile: null, DateTimeOffset.UtcNow, Purpose: "probe"));

            var runner = new PodmanCommandRunner((_, _) => throw new InvalidOperationException("No Podman command should run."));
            await using var service = new WorkspaceContainerConnectorExecutionService(
                new WorkspaceDesiredStateStore(), runner,
                new McpbImageMaterializer(runner, NullLogger<McpbImageMaterializer>.Instance),
                new PodmanContainerTool(runner), NullLogger<WorkspaceContainerConnectorExecutionService>.Instance,
                workloadRegistry: registry,
                probePolicyStore: policyStore);

            service.CompleteProbeAuthorization(connectorId, probeContext);

            NetworkAccessRequestView[] remaining = policyStore.ListRequests(new NetworkAccessActor(userId, IsAdmin: false))
                .Requests
                .Where(static view => string.Equals(view.Request.Purpose, "probe", StringComparison.Ordinal))
                .ToArray();
            Assert.DoesNotContain(remaining, view => view.Request.Destination == "first.example");
            Assert.Contains(remaining, view => view.Request.Id == overlappingRequestId && view.Request.Destination == "second.example");
        }
        finally
        {
            Directory.Delete(stateDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task PrepareRuntimeConnectorAsyncMaterializesCommandBasedConnectorsWithManagedRuntime()
    {
        var podmanCommandRunner = new PodmanCommandRunner();
        await using var service = new WorkspaceContainerConnectorExecutionService(
            new WorkspaceDesiredStateStore(),
            podmanCommandRunner,
            new McpbImageMaterializer(podmanCommandRunner, NullLogger<McpbImageMaterializer>.Instance),
            new PodmanContainerTool(podmanCommandRunner),
            NullLogger<WorkspaceContainerConnectorExecutionService>.Instance);

        WorkspaceMcpContainerConnectorDefinition connector = new(
            "user-1::filesystem",
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

        InvalidOperationException exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => InvokePrepareRuntimeConnectorAsync(service, connector));
        Assert.Contains("must be pinned", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task BuildPodmanPersistentContainerArgumentsStartsKeepAliveContainer()
    {
        var podmanCommandRunner = new PodmanCommandRunner();
        await using var service = new WorkspaceContainerConnectorExecutionService(
            new WorkspaceDesiredStateStore(),
            podmanCommandRunner,
            new McpbImageMaterializer(podmanCommandRunner, NullLogger<McpbImageMaterializer>.Instance),
            new PodmanContainerTool(podmanCommandRunner),
            NullLogger<WorkspaceContainerConnectorExecutionService>.Instance);

        WorkspaceMcpContainerConnectorDefinition connector = new(
            "user-1::filesystem",
            "localhost/fedora-minimal:44",
            "npx",
            ["-y", "@modelcontextprotocol/server-filesystem"],
            true,
            false,
            null,
            null,
            [],
            30,
            600);

        List<string> arguments = InvokeBuildPodmanPersistentContainerArguments(service, connector, "test-container", null);

        Assert.Contains("-d", arguments, StringComparer.Ordinal);
        Assert.DoesNotContain("--rm", arguments, StringComparer.Ordinal);
        int imageIndex = arguments.IndexOf("localhost/fedora-minimal:44");
        int entrypointIndex = arguments.IndexOf("--entrypoint");
        Assert.InRange(entrypointIndex, 0, imageIndex - 2);
        Assert.Equal("tail", arguments[entrypointIndex + 1]);
        Assert.Equal(["-f", "/dev/null"], arguments.Skip(imageIndex + 1));
    }

    [Fact]
    public void BuildPodmanExecArgumentsUsesExistingContainer()
    {
        WorkspaceMcpContainerConnectorDefinition connector = new(
            "user-1::filesystem",
            "localhost/fedora-minimal:44",
            "npx",
            ["-y", "@modelcontextprotocol/server-filesystem", "/workspace/documents"],
            true,
            false,
            null,
            null,
            [],
            30,
            600);

        List<string> arguments = InvokeBuildPodmanExecArguments(connector, "test-container");

        Assert.Equal(["exec", "-i", "test-container", "npx", "-y", "@modelcontextprotocol/server-filesystem", "/workspace/documents"], arguments);
    }

    [Fact]
    public void BuildPodmanExecArgumentsCanSetWorkingDirectory()
    {
        WorkspaceMcpContainerConnectorDefinition connector = new(
            "user-1::kautuka-web-tools",
            "localhost/kautuka-mcpb-uv:deadbeef",
            "/opt/kautuka/mcpb/.venv/bin/python",
            ["-m", "kautuka_web_tools.server"],
            true,
            false,
            null,
            "uv",
            [],
            30,
            600);

        List<string> arguments = InvokeBuildPodmanExecArguments(
            connector,
            "test-container",
            "/home/node/.kautuka/connectors/user-1--kautuka-web-tools");

        Assert.Equal(
            [
                "exec",
                "-i",
                "--workdir",
                "/home/node/.kautuka/connectors/user-1--kautuka-web-tools",
                "test-container",
                "/opt/kautuka/mcpb/.venv/bin/python",
                "-m",
                "kautuka_web_tools.server"
            ],
            arguments);
    }

    [Fact]
    public void BuildPodmanExecArgumentsIncludesEnvironmentBeforeContainer()
    {
        WorkspaceMcpContainerConnectorDefinition connector = new(
            "user-1::kautuka-web-tools",
            "localhost/kautuka-mcpb-uv:deadbeef",
            "/home/node/.kautuka/connectors/user-1--kautuka-web-tools/.venv/bin/python",
            ["-m", "kautuka_web_tools.server"],
            true,
            false,
            null,
            "uv",
            [new KeyValuePair<string, string>("PYTHONPATH", "/home/node/.kautuka/connectors/user-1--kautuka-web-tools/src")],
            30,
            600);

        List<string> arguments = ConnectorContainerSpecBuilder.BuildExec(
            connector,
            "test-container",
            "/home/node/.kautuka/connectors/user-1--kautuka-web-tools",
            "/tmp/kautuka-env-test");

        Assert.Equal(
            [
                "exec",
                "-i",
                "--workdir",
                "/home/node/.kautuka/connectors/user-1--kautuka-web-tools",
                "--env-file",
                "/tmp/kautuka-env-test",
                "test-container",
                "/home/node/.kautuka/connectors/user-1--kautuka-web-tools/.venv/bin/python",
                "-m",
                "kautuka_web_tools.server"
            ],
            arguments);
        Assert.Equal(
            "/home/node/.kautuka/connectors/user-1--kautuka-web-tools/src",
            ConnectorContainerSpecBuilder.FilterContainerEnvironment(connector.Environment)["PYTHONPATH"]);
    }

    [Fact]
    public async Task CommandlessConnectorsUseImageDefaultCommand()
    {
        var podmanCommandRunner = new PodmanCommandRunner();
        await using var service = new WorkspaceContainerConnectorExecutionService(
            new WorkspaceDesiredStateStore(),
            podmanCommandRunner,
            new McpbImageMaterializer(podmanCommandRunner, NullLogger<McpbImageMaterializer>.Instance),
            new PodmanContainerTool(podmanCommandRunner),
            NullLogger<WorkspaceContainerConnectorExecutionService>.Instance);

        WorkspaceMcpContainerConnectorDefinition connector = new(
            "markitdown",
            $"docker.io/mcp/markitdown@sha256:{new string('a', 64)}",
            string.Empty,
            [],
            true,
            true,
            null,
            null,
            [],
            45,
            600);

        Assert.True(InvokeUsesImageDefaultCommand(connector));

        List<string> arguments = InvokeBuildPodmanRunArguments(service, connector, "test-container", null);

        Assert.Equal("run", arguments[0]);
        Assert.DoesNotContain("--entrypoint", arguments, StringComparer.Ordinal);
        Assert.Equal(connector.Image, arguments[^1]);
    }

    [Fact]
    public void RequiresContainerRecycleReturnsTrueWhenBundlePathChanges()
    {
        WorkspaceMcpContainerConnectorDefinition previous = new(
            "user-1::splose-mcp",
            "localhost/fedora-minimal:44",
            "uv",
            ["run", "-m", "splose_mcp.server"],
            true,
            false,
            "/tmp/connectors/first.mcpb",
            "uv",
            [new KeyValuePair<string, string>("SPLOSE_ROOT", "/opt/kautuka/mcpb/config")],
            45,
            600);

        WorkspaceMcpContainerConnectorDefinition next = previous with
        {
            BundlePath = "/tmp/connectors/second.mcpb",
        };

        Assert.True(InvokeRequiresContainerRecycle(previous, next));
    }

    [Fact]
    public void RequiresContainerRecycleIgnoresEnvironmentOrdering()
    {
        WorkspaceMcpContainerConnectorDefinition previous = new(
            "user-1::splose-mcp",
            "localhost/fedora-minimal:44",
            "uv",
            ["run", "-m", "splose_mcp.server"],
            true,
            false,
            "/tmp/connectors/splose.mcpb",
            "uv",
            [
                new KeyValuePair<string, string>("SPLOSE_ROOT", "/opt/kautuka/mcpb/config"),
                new KeyValuePair<string, string>("UV_INDEX_URL", "http://kautuka-pkgs-proxy:20101/pypi/simple/")
            ],
            45,
            600);

        WorkspaceMcpContainerConnectorDefinition next = previous with
        {
            Environment =
            [
                new KeyValuePair<string, string>("UV_INDEX_URL", "http://kautuka-pkgs-proxy:20101/pypi/simple/"),
                new KeyValuePair<string, string>("SPLOSE_ROOT", "/opt/kautuka/mcpb/config")
            ]
        };

        Assert.False(InvokeRequiresContainerRecycle(previous, next));
    }

    [Fact]
    public void BuildEffectiveConnectorDefinitionMergesEnvironmentOverrides()
    {
        WorkspaceMcpContainerConnectorDefinition connector = new(
            "user-1::splose-mcp",
            "localhost/fedora-minimal:44",
            "uv",
            ["run", "-m", "splose_mcp.server"],
            true,
            false,
            "/tmp/connectors/splose.mcpb",
            "uv",
            [
                new KeyValuePair<string, string>("SPLOSE_ROOT", "/opt/kautuka/mcpb/config"),
                new KeyValuePair<string, string>("LOG_LEVEL", "info")
            ],
            45,
            600);

        WorkspaceMcpContainerConnectorDefinition effective = InvokeBuildEffectiveConnectorDefinition(
            connector,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["LOG_LEVEL"] = "debug",
                ["TOKEN"] = "secret",
            },
            timeoutSeconds: 30,
            enabledOverride: null);

        Assert.Equal("/tmp/connectors/splose.mcpb", effective.BundlePath);
        Assert.Equal("uv", effective.ServerType);
        Assert.Equal(30, effective.ReadinessTimeoutSeconds);
        Assert.True(effective.Enabled);

        Dictionary<string, string> environment = effective.Environment.ToDictionary(static pair => pair.Key, static pair => pair.Value, StringComparer.Ordinal);
        Assert.Equal("/opt/kautuka/mcpb/config", environment["SPLOSE_ROOT"]);
        Assert.Equal("debug", environment["LOG_LEVEL"]);
        Assert.Equal("secret", environment["TOKEN"]);
    }

    [Fact]
    public void BuildEffectiveConnectorDefinitionCanDisableConnectorRequirementForProbe()
    {
        WorkspaceMcpContainerConnectorDefinition connector = new(
            "user-1::splose-mcp",
            "localhost/fedora-minimal:44",
            "uv",
            ["run", "-m", "splose_mcp.server"],
            false,
            false,
            "/tmp/connectors/splose.mcpb",
            "uv",
            [],
            45,
            600);

        WorkspaceMcpContainerConnectorDefinition effective = InvokeBuildEffectiveConnectorDefinition(
            connector,
            overrides: null,
            timeoutSeconds: 0,
            enabledOverride: false);

        Assert.False(effective.Enabled);
        Assert.Equal(45, effective.ReadinessTimeoutSeconds);
        Assert.Equal("/tmp/connectors/splose.mcpb", effective.BundlePath);
        Assert.Equal("uv", effective.ServerType);
    }

    [Fact]
    public void ApplyBundleRuntimeContractRewritesUvRunPythonModuleToBuiltVenv()
    {
        WorkspaceMcpContainerConnectorDefinition connector = new(
            "user-1::kautuka-web-tools",
            "localhost/fedora-minimal:44",
            "uv",
            ["run", "python", "-m", "kautuka_web_tools.server"],
            true,
            false,
            "/tmp/connectors/kautuka-web-tools.mcpb",
            "uv",
            [],
            60,
            600);

        WorkspaceMcpContainerConnectorDefinition prepared = InvokeApplyBundleRuntimeContract(
            connector,
            "localhost/kautuka-mcpb-uv:deadbeef");

        Assert.Equal("/opt/kautuka/mcpb/.venv/bin/python", prepared.Command);
        Assert.Equal(["-m", "kautuka_web_tools.server"], prepared.Args);
        Assert.Null(prepared.BundlePath);
    }

    [Fact]
    public async Task BuildPodmanRunArgumentsMountAgentWorkspaceWhenRequested()
    {
        var podmanCommandRunner = new PodmanCommandRunner();
        await using var service = new WorkspaceContainerConnectorExecutionService(
            new WorkspaceDesiredStateStore(),
            podmanCommandRunner,
            new McpbImageMaterializer(podmanCommandRunner, NullLogger<McpbImageMaterializer>.Instance),
            new PodmanContainerTool(podmanCommandRunner),
            NullLogger<WorkspaceContainerConnectorExecutionService>.Instance);

        WorkspaceMcpContainerConnectorDefinition connector = new(
            "user-1::filesystem",
            "localhost/fedora-minimal:44",
            "node",
            ["server.js"],
            true,
            false,
            null,
            null,
            [],
            30,
            600);
        ContainerContext workspaceContext = new(
            "agent",
            "agent-alpha",
            "user-1",
            "/host/workspace",
            "/workspace",
            "localhost/kautuka-base:latest",
            1,
            "512m",
            30,
            [],
            new Dictionary<string, string>())
        {
            WorkspaceBaseRoot = "/host",
        };

        List<string> arguments = InvokeBuildPodmanRunArguments(service, connector, "test-container", workspaceContext);

        Assert.Contains("--volume", arguments, StringComparer.Ordinal);
        Assert.Contains("/host/workspace:/workspace:ro", arguments, StringComparer.Ordinal);
    }

    [Fact]
    public void BuildContainerNameScopesPersistentContainersByExecutionContextAndWorkspace()
    {
        ContainerContext agentAlphaWorkspace = new(
            "agent",
            "agent-alpha",
            "user-1",
            "/host/workspace-a",
            "/workspace",
            "localhost/kautuka-base:latest",
            1,
            "512m",
            30,
            [],
            new Dictionary<string, string>());
        ContainerContext agentBetaWorkspace = new(
            "agent",
            "agent-beta",
            "user-1",
            "/host/workspace-b",
            "/workspace",
            "localhost/kautuka-base:latest",
            1,
            "512m",
            30,
            [],
            new Dictionary<string, string>());

        string isolatedName = InvokeBuildContainerName(
            "user-1::kautuka-web-tools",
            WorkspaceMcpExecutionContext.Isolated,
            null);
        string filesAlphaName = InvokeBuildContainerName(
            "user-1::kautuka-web-tools",
            WorkspaceMcpExecutionContext.IsolatedWithAgentFiles,
            agentAlphaWorkspace);
        string filesBetaName = InvokeBuildContainerName(
            "user-1::kautuka-web-tools",
            WorkspaceMcpExecutionContext.IsolatedWithAgentFiles,
            agentBetaWorkspace);

        Assert.StartsWith("kautuka-mcp-user-1-kautuka-web-tools-isolated-", isolatedName, StringComparison.Ordinal);
        Assert.StartsWith("kautuka-mcp-user-1-agent-alpha-kautuka-web-tools-files-", filesAlphaName, StringComparison.Ordinal);
        Assert.StartsWith("kautuka-mcp-user-1-agent-beta-kautuka-web-tools-files-", filesBetaName, StringComparison.Ordinal);
        Assert.NotEqual(isolatedName, filesAlphaName);
        Assert.NotEqual(filesAlphaName, filesBetaName);
    }

    [Fact]
    public void BuildAgentSandboxInstallRootUsesDigestStablePath()
    {
        string bundlePath = CreateBundleArchive();

        try
        {
            WorkspaceMcpContainerConnectorDefinition connector = new(
                "user-1::kautuka-web-tools",
            "localhost/fedora-minimal:44",
                "uv",
                ["run", "python", "-m", "kautuka_web_tools.server"],
                true,
                false,
                bundlePath,
                "uv",
                [],
                60,
                600);

            string installRoot = InvokeBuildAgentSandboxInstallRoot(connector);

            Assert.StartsWith("/home/node/.kautuka/connectors/by-digest/", installRoot, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(bundlePath);
        }
    }

    [Fact]
    public void StoredProtocolVersionIsRequestedOnConnect()
    {
        McpClientOptions? options = WorkspaceContainerConnectorExecutionService.ClientOptionsFor("2025-11-25");

        Assert.NotNull(options);
        Assert.Equal("2025-11-25", options.ProtocolVersion);
        Assert.Null(WorkspaceContainerConnectorExecutionService.ClientOptionsFor(null));
        Assert.Null(WorkspaceContainerConnectorExecutionService.ClientOptionsFor("  "));
    }

    [Fact]
    public void RewriteBundleLaunchContractUsesSandboxInstallRootForUv()
    {
        string installRoot = "/home/node/.kautuka/connectors/by-digest/deadbeef";

        (string command, IReadOnlyList<string> args) = InvokeRewriteBundleLaunchContract(
            "uv",
            "uv",
            ["run", "python", "-m", "kautuka_web_tools.server"],
            installRoot);

        Assert.Equal(installRoot + "/.venv/bin/python", command);
        Assert.Equal(["-m", "kautuka_web_tools.server"], args);
    }

    private static List<string> InvokeBuildPodmanRunArguments(
        WorkspaceContainerConnectorExecutionService service,
        WorkspaceMcpContainerConnectorDefinition connector,
        string containerName,
        ContainerContext? workspaceContext)
    {
        _ = service;
        return ConnectorContainerSpecBuilder.BuildRun(
            connector,
            containerName,
            workspaceContext is null ? WorkspaceMcpExecutionContext.Isolated : WorkspaceMcpExecutionContext.IsolatedWithAgentFiles,
            workspaceContext ?? CreateWorkspaceContext(),
            persistent: false,
            allocatedIp: "10.247.1.1",
            isBraked: false);
    }

    private static string InvokeBuildContainerName(
        string connectorId,
        WorkspaceMcpExecutionContext executionContext,
        ContainerContext? workspaceContext)
    {
        MethodInfo method = typeof(WorkspaceContainerConnectorExecutionService)
            .GetMethod("BuildContainerName", BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("BuildContainerName method was not found.");

        object? result = method.Invoke(null, [connectorId, executionContext, workspaceContext]);
        return Assert.IsType<string>(result);
    }

    private static WorkspaceMcpContainerConnectorDefinition InvokeApplyBundleRuntimeContract(
        WorkspaceMcpContainerConnectorDefinition connector,
        string runtimeImage)
        => AgentSandboxInstallContract.ApplyRuntime(connector, runtimeImage);

    private static WorkspaceMcpContainerConnectorDefinition InvokeBuildEffectiveConnectorDefinition(
        WorkspaceMcpContainerConnectorDefinition connector,
        IReadOnlyDictionary<string, string>? overrides,
        int timeoutSeconds,
        bool? enabledOverride)
    {
        MethodInfo method = typeof(WorkspaceContainerConnectorExecutionService)
            .GetMethod("BuildEffectiveConnectorDefinition", BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("BuildEffectiveConnectorDefinition method was not found.");

        object? result = method.Invoke(null, [connector, overrides, timeoutSeconds, enabledOverride]);
        return Assert.IsType<WorkspaceMcpContainerConnectorDefinition>(result);
    }

    private static string InvokeBuildAgentSandboxInstallRoot(WorkspaceMcpContainerConnectorDefinition connector)
        => AgentSandboxInstallContract.BuildInstallRoot(connector);

    private static (string Command, IReadOnlyList<string> Args) InvokeRewriteBundleLaunchContract(
        string? serverType,
        string command,
        IReadOnlyList<string> args,
        string bundleRoot)
        => AgentSandboxInstallContract.RewriteLaunch(serverType, command, args, bundleRoot);

    private static List<string> InvokeBuildPodmanPersistentContainerArguments(
        WorkspaceContainerConnectorExecutionService service,
        WorkspaceMcpContainerConnectorDefinition connector,
        string containerName,
        ContainerContext? workspaceContext)
    {
        _ = service;
        return ConnectorContainerSpecBuilder.BuildRun(
            connector,
            containerName,
            workspaceContext is null ? WorkspaceMcpExecutionContext.Isolated : WorkspaceMcpExecutionContext.IsolatedWithAgentFiles,
            workspaceContext ?? CreateWorkspaceContext(),
            persistent: true,
            allocatedIp: "10.247.1.1",
            isBraked: false);
    }

    private static ContainerContext FingerprintContext(string agentId, string hostPath)
        => new("agent", agentId, "user-alpha", hostPath, "/workspace", "image@sha256:digest",
            1, "512m", 30, [], new Dictionary<string, string>());

    private static string ConnectorInspectJson(string hostPath, string fingerprint, long desiredVersion, string containerId, string workloadIp)
        => JsonSerializer.Serialize(new[]
        {
            new
            {
                Id = containerId,
                Image = "sha256:current",
                EffectiveCaps = Array.Empty<string>(),
                BoundingCaps = Array.Empty<string>(),
                NetworkSettings = new { Networks = new Dictionary<string, object> { ["kautuka-workload"] = new { IPAddress = workloadIp } } },
                State = new { Status = "running", Pid = 4242 },
                Config = new
                {
                    User = "65532:65532",
                    Env = new[] { "PATH=/usr/bin", "HOME=/tmp", "NPM_CONFIG_CACHE=/tmp/.npm", "PIP_CACHE_DIR=/tmp/.pip", "UV_CACHE_DIR=/tmp/.uv" },
                    Labels = new Dictionary<string, string>
                    {
                        ["kautuka.desired-version"] = desiredVersion.ToString(System.Globalization.CultureInfo.InvariantCulture),
                        ["kautuka.spec-fingerprint"] = fingerprint,
                    },
                },
                Mounts = new[] { new { Type = "bind", Source = OperatingSystem.IsWindows() ? Path.GetFullPath(hostPath) : hostPath, Destination = "/workspace", RW = false } },
                HostConfig = new
                {
                    NetworkMode = "bridge",
                    Binds = new[] { hostPath + ":/workspace:ro" },
                    CapDrop = new[] { "ALL" },
                    CapAdd = Array.Empty<string>(),
                    Privileged = false,
                    Devices = Array.Empty<object>(),
                    PublishAllPorts = false,
                    PortBindings = new Dictionary<string, object[]>(),
                    ReadonlyRootfs = true,
                    PidsLimit = 128,
                    SecurityOpt = new[] { "no-new-privileges", "label=disable" },
                    Sysctls = new Dictionary<string, string> { ["net.ipv6.conf.all.disable_ipv6"] = "1" },
                    Tmpfs = new Dictionary<string, string> { ["/tmp"] = "rw,noexec,nosuid,nodev,size=64m" },
                },
            },
        });

    private static PodmanCommandResult Success(string stdout)
        => new(0, stdout, string.Empty, ProcessTerminationReason.Completed);

    private static ContainerContext CreateWorkspaceContext()
        => new("agent", "test-agent", "test-user", Path.GetTempPath(), "/workspace", "image@sha256:digest",
            1, "512m", 30, [], new Dictionary<string, string>());

    private static List<string> InvokeBuildPodmanExecArguments(
        WorkspaceMcpContainerConnectorDefinition connector,
        string containerName)
    {
        return ConnectorContainerSpecBuilder.BuildExec(connector, containerName, null);
    }

    private static List<string> InvokeBuildPodmanExecArguments(
        WorkspaceMcpContainerConnectorDefinition connector,
        string containerName,
        string? workingDirectory)
    {
        return ConnectorContainerSpecBuilder.BuildExec(connector, containerName, workingDirectory);
    }

    private static bool InvokeRequiresContainerRecycle(
        WorkspaceMcpContainerConnectorDefinition previous,
        WorkspaceMcpContainerConnectorDefinition next)
    {
        MethodInfo method = typeof(WorkspaceContainerConnectorExecutionService)
            .GetMethod("RequiresContainerRecycle", BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("RequiresContainerRecycle method was not found.");

        object? result = method.Invoke(null, [previous, next]);
        return Assert.IsType<bool>(result);
    }

    private static bool InvokeUsesImageDefaultCommand(WorkspaceMcpContainerConnectorDefinition connector)
    {
        MethodInfo method = typeof(WorkspaceContainerConnectorExecutionService)
            .GetMethod("UsesImageDefaultCommand", BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("UsesImageDefaultCommand method was not found.");

        object? result = method.Invoke(null, [connector]);
        return Assert.IsType<bool>(result);
    }

    private static async Task<WorkspaceMcpContainerConnectorDefinition> InvokePrepareRuntimeConnectorAsync(
        WorkspaceContainerConnectorExecutionService service,
        WorkspaceMcpContainerConnectorDefinition connector)
    {
        MethodInfo method = typeof(WorkspaceContainerConnectorExecutionService)
            .GetMethod("PrepareRuntimeConnectorAsync", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("PrepareRuntimeConnectorAsync method was not found.");

        var task = (Task)method.Invoke(service, [connector, false, CancellationToken.None])!;
        await task.ConfigureAwait(false);
        var resultProperty = task.GetType().GetProperty("Result")!;
        return (WorkspaceMcpContainerConnectorDefinition)resultProperty.GetValue(task)!;
    }

    private static string CreateBundleArchive()
    {
        string archivePath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.mcpb");

        using ZipArchive archive = ZipFile.Open(archivePath, ZipArchiveMode.Create);
        ZipArchiveEntry manifest = archive.CreateEntry("manifest.json");
        using (StreamWriter writer = new(manifest.Open()))
        {
            writer.Write("{}");
        }

        archive.CreateEntry("splose_mcp/server.py");
        return archivePath;
    }

    private sealed class TestWorkloadStartupState(bool isStartupGateOpen) : IWorkloadStartupState
    {
        public bool IsStartupGateOpen { get; } = isStartupGateOpen;
    }

    private sealed class CancellableProbeAdmission : IWorkloadStartupState, IWorkloadLaunchAdmission, IWorkloadLaunchLease, IDisposable
    {
        private readonly CancellationTokenSource cancellation = new();
        public bool IsStartupGateOpen => !cancellation.IsCancellationRequested;
        public bool LeaseReleased { get; private set; }
        public CancellationToken CancellationToken => cancellation.Token;
        public void CloseLaunchAdmission() => cancellation.Cancel();
        public ValueTask<IWorkloadLaunchLease> AcquireLaunchLeaseAsync(CancellationToken cancellationToken) => ValueTask.FromResult<IWorkloadLaunchLease>(this);
        public ValueTask DisposeAsync()
        {
            LeaseReleased = true;
            return ValueTask.CompletedTask;
        }
        public void Dispose() => cancellation.Dispose();
    }
}
