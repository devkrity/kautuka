using System.IO.Compression;
using System.Reflection;
using System.Text.Json;

using Kautuka.Manager.Protos;
using Kautuka.Manager.RuntimeContainer.Hosting;
using Kautuka.Manager.RuntimeContainer.Hosting.NetworkAccess;
using Kautuka.Manager.RuntimeContainer.Sandbox;
using Kautuka.Manager.RuntimeContainer.Sandbox.Models;
using Kautuka.NetworkAccessController;

using Microsoft.Extensions.Logging.Abstractions;

using ModelContextProtocol.Client;

namespace Kautuka.IntegrationTests;

public sealed class WorkspaceContainerConnectorExecutionServiceTests
{
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
    public void UvRuntimeContainerfileInstallsManagedPythonInsideImmutableBundleArtifact()
    {
        string containerfile = McpbImageMaterializer.BuildContainerfile(
            "quay.io/fedora/fedora-minimal:44",
            "uv",
            hasBundle: true);
        string installScript = McpbImageMaterializer.BuildLanguagePackageInstallScript("uv", forSandboxArtifact: false);

        Assert.Contains("USER root", containerfile, StringComparison.Ordinal);
        Assert.Contains("RUN chown -R node:node /opt/kautuka/mcpb", containerfile, StringComparison.Ordinal);
        Assert.Contains("USER node", containerfile, StringComparison.Ordinal);
        Assert.Contains("ENV UV_PYTHON_INSTALL_DIR=/opt/kautuka/mcpb/.python", containerfile, StringComparison.Ordinal);
        Assert.Contains("ENV UV_PROJECT_ENVIRONMENT=/opt/kautuka/mcpb/.venv", containerfile, StringComparison.Ordinal);
        Assert.DoesNotContain("UV_PYTHON_PREFERENCE", containerfile, StringComparison.Ordinal);
        Assert.DoesNotContain("uv sync", containerfile, StringComparison.Ordinal);
        Assert.DoesNotContain("pip install", containerfile, StringComparison.Ordinal);
        Assert.Contains("uv==0.11.19", installScript, StringComparison.Ordinal);
        Assert.Contains("--index-url \"$PIP_INDEX_URL\"", installScript, StringComparison.Ordinal);
        Assert.Contains("$pip_trusted", installScript, StringComparison.Ordinal);
        Assert.Contains("uv sync --no-dev --index-url \"$UV_INDEX_URL\"", installScript, StringComparison.Ordinal);
        Assert.DoesNotContain("UV_PYTHON_PREFERENCE", installScript, StringComparison.Ordinal);
    }

    [Fact]
    public void NodeRuntimeContainerfileVerifiesNodeTarballChecksum()
    {
        string containerfile = McpbImageMaterializer.BuildContainerfile(
            "quay.io/fedora/fedora-minimal:44",
            "node",
            hasBundle: true);

        Assert.Contains("sha256sum -c -", containerfile, StringComparison.Ordinal);
        Assert.Contains("0ce210831380ab75a5fec0d3fb6b5af0958898016b2cae51cc2fa11057d2f77e", containerfile, StringComparison.Ordinal);
        Assert.DoesNotContain("npm install", containerfile, StringComparison.Ordinal);
    }

    [Fact]
    public void UvSandboxArtifactContainerfileCopiesManagedPythonWithVirtualEnvironment()
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

        Assert.Contains($"ENV UV_PYTHON_INSTALL_DIR={installRoot}/.python", systemContainerfile, StringComparison.Ordinal);
        Assert.Contains($"ENV UV_PROJECT_ENVIRONMENT={installRoot}/.venv", systemContainerfile, StringComparison.Ordinal);
        Assert.DoesNotContain("UV_PYTHON_PREFERENCE", systemContainerfile, StringComparison.Ordinal);
        Assert.DoesNotContain("uv sync", systemContainerfile, StringComparison.Ordinal);
        Assert.Contains("uv sync --no-dev --no-editable --index-url \"$UV_INDEX_URL\"", installScript, StringComparison.Ordinal);
        Assert.Contains("$pip_trusted", installScript, StringComparison.Ordinal);
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
            "10.247.1.10");

        Assert.DoesNotContain("--entrypoint", arguments, StringComparer.Ordinal);
        Assert.Contains("--network", arguments, StringComparer.Ordinal);
        Assert.Contains(arguments, static argument => argument.StartsWith(ConnectorContainerSpecBuilder.WorkloadNetworkName, StringComparison.Ordinal));
        Assert.Equal("/bin/sh", arguments[^3]);
        Assert.Equal("-lc", arguments[^2]);
        Assert.Equal("uv sync --no-dev", arguments[^1]);
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
            Assert.Contains("SPLOSE_ROOT", arguments, StringComparer.Ordinal);
            Assert.DoesNotContain("/opt/kautuka/mcpb/config", arguments, StringComparer.Ordinal);
            Assert.Equal("/opt/kautuka/mcpb/config", ConnectorContainerSpecBuilder.BuildTransportEnvironment(prepared)["SPLOSE_ROOT"]);
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
            allocatedIp: "10.247.1.20");

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
                registry);
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
        const string hostWorkspace = "/tmp/nac-v7-fingerprint-workspace";
        const string containerName = "kautuka-mcp-user-alpha-agent-alpha-shared-files";
        var commands = new List<string[]>();
        bool containerExists = false;
        string? startedFingerprint = null;
        string? inspectJson = null;
        var runner = new PodmanCommandRunner((args, _) =>
        {
            commands.Add([.. args]);
            return Task.FromResult(args[0] switch
            {
                "inspect" when containerExists && args.Count > 1 && args[1] == containerName => Success(inspectJson!),
                "inspect" => new PodmanCommandResult(125, "", "missing", ProcessTerminationReason.NonZeroExit),
                "container" => new PodmanCommandResult(1, "", "missing", ProcessTerminationReason.NonZeroExit),
                "image" => Success("sha256:current"),
                "run" => Start(args),
                "rm" => Remove(),
                _ => throw new InvalidOperationException("Unexpected Podman command: " + string.Join(' ', args)),
            });
        });
        PodmanCommandResult Start(IReadOnlyList<string> args)
        {
            int fingerprintIndex = args.ToList().IndexOf("--label") + 1;
            startedFingerprint = args.Skip(fingerprintIndex).First(static label => label.StartsWith("kautuka.spec-fingerprint=", StringComparison.Ordinal))["kautuka.spec-fingerprint=".Length..];
            containerExists = true;
            inspectJson = ConnectorInspectJson(hostWorkspace, startedFingerprint, desiredVersion: 7);
            return Success(containerName);
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
            var registry = new Kautuka.NetworkAccessController.WorkloadRegistry();
            await using var service = new WorkspaceContainerConnectorExecutionService(
                new WorkspaceDesiredStateStore(),
                runner,
                new McpbImageMaterializer(runner, NullLogger<McpbImageMaterializer>.Instance),
                new PodmanContainerTool(runner),
                NullLogger<WorkspaceContainerConnectorExecutionService>.Instance,
                new WorkloadAddressAllocator(Path.Combine(stateDirectory, "ip-cursor.json")),
                registry);
            var connector = new WorkspaceMcpContainerConnectorDefinition(
                "user-alpha::shared",
                "ghcr.io/devkrity/kautuka-base@sha256:" + new string('a', 64),
                "node",
                ["server.js"], true, false, null, null, [], 30, 600)
            {
                DesiredStateVersion = 7,
                SpecFingerprint = "state-store-fingerprint-without-agent",
            };
            ContainerContext agentAlpha = FingerprintContext("agent-alpha", hostWorkspace);

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
                ])!).ConfigureAwait(false);

            await EnsureAsync(agentAlpha);
            int runCount = commands.Count(static command => command[0] == "run");
            await EnsureAsync(agentAlpha);

            Assert.Equal(runCount, commands.Count(static command => command[0] == "run"));
            Assert.DoesNotContain(commands, static command => command[0] == "rm");
            Assert.Equal(1, registry.Count);

            await service.ReconcileDesiredConnectorsAsync([connector], CancellationToken.None);
            Assert.Equal(runCount, commands.Count(static command => command[0] == "run"));
            Assert.DoesNotContain(commands, static command => command[0] == "rm");

            ContainerContext agentBeta = FingerprintContext("agent-beta", hostWorkspace);
            await EnsureAsync(agentBeta);
            Assert.Equal(runCount + 1, commands.Count(static command => command[0] == "run"));
            Assert.Contains(commands, static command => command[0] == "rm");
            Assert.NotEqual(startedFingerprint, connector.SpecFingerprint);
        }
        finally
        {
            Directory.Delete(stateDirectory, recursive: true);
        }
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
            _ = policyStore.SetUserPrivileges(admin, user.UserId, canApproveOwn: true, canAllowAllAgents: false);
            const string connectorId = "connector-one";
            ContainerContext probeContext = new(
                "user", user.UserId, user.UserId, "/tmp", "/tmp", string.Empty, 0, string.Empty, 0, [], new Dictionary<string, string>());
            string containerId = InvokeBuildContainerName(connectorId, WorkspaceMcpExecutionContext.Isolated, probeContext);
            string requestId = policyStore.RecordOrUpdatePendingProbeRequest(
                containerId, user.UserId, null, connectorId, systemwide: false, attribution: "user",
                destKind: "host", destination: "probe.example", port: 443, actorKind: "connector",
                origin: "probe");
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
            (bool ok, _, _, _) = await service.ProbeConnectorAsync(connector, 15, CancellationToken.None, user.UserId);
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
        Assert.Equal("localhost/fedora-minimal:44", arguments[^4]);
        Assert.Equal("sh", arguments[^3]);
        Assert.Equal("-lc", arguments[^2]);
        Assert.Equal("tail -f /dev/null", arguments[^1]);
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
                "--env",
                "PYTHONPATH",
                "test-container",
                "/home/node/.kautuka/connectors/user-1--kautuka-web-tools/.venv/bin/python",
                "-m",
                "kautuka_web_tools.server"
            ],
            arguments);
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
            new Dictionary<string, string>());

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
            allocatedIp: "10.247.1.1");
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
            allocatedIp: "10.247.1.1");
    }

    private static ContainerContext FingerprintContext(string agentId, string hostPath)
        => new("agent", agentId, "user-alpha", hostPath, "/workspace", "image@sha256:digest",
            1, "512m", 30, [], new Dictionary<string, string>());

    private static string ConnectorInspectJson(string hostPath, string fingerprint, long desiredVersion)
        => JsonSerializer.Serialize(new[]
        {
            new
            {
                Image = "sha256:current",
                NetworkSettings = new { Networks = new Dictionary<string, object> { ["kautuka-workload"] = new() } },
                State = new { Status = "running" },
                Config = new
                {
                    Env = new[] { "PATH=/usr/bin", "HOME=/tmp", "NPM_CONFIG_CACHE=/tmp/.npm", "PIP_CACHE_DIR=/tmp/.pip", "UV_CACHE_DIR=/tmp/.uv" },
                    Labels = new Dictionary<string, string>
                    {
                        ["kautuka.desired-version"] = desiredVersion.ToString(System.Globalization.CultureInfo.InvariantCulture),
                        ["kautuka.spec-fingerprint"] = fingerprint,
                    },
                },
                Mounts = new[] { new { Type = "bind", Source = hostPath, Destination = "/workspace", RW = false } },
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

        var task = (Task)method.Invoke(service, [connector, CancellationToken.None])!;
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
}
