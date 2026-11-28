using System.Reflection;

using Kautuka.Manager.Protos;
using Kautuka.Manager.RuntimeContainer.Hosting;
using Kautuka.Manager.RuntimeContainer.Hosting.NetworkAccess;
using Kautuka.Manager.RuntimeContainer.Sandbox.Models;
using Kautuka.NetworkAccessController;

namespace Kautuka.IntegrationTests;

public sealed class ConnectorContainerSpecBuilderTests
{
    [Fact]
    public void ConnectorFingerprintChangesWhenImageCompatibilityRelaxationsChange()
    {
        WorkspaceMcpContainerConnectorDefinition connector = CreateConnector();
        ContainerContext context = CreateWorkspaceContext();
        var identity = ConnectorContainerSpecBuilder.ResolveIdentity(connector, context, purpose: null);
        var profile = new ImageCompatProfile("sha256:" + new string('a', 64), null, null,
            [ImageCompatibilityStore.WritableRootfs, ImageCompatibilityStore.FileOwnershipCaps], "admin", DateTimeOffset.UtcNow, null);
        var stricter = profile with { Relaxations = [ImageCompatibilityStore.FileOwnershipCaps] };
        var reordered = profile with { Relaxations = [ImageCompatibilityStore.FileOwnershipCaps, ImageCompatibilityStore.WritableRootfs] };
        string original = ConnectorContainerSpecBuilder.ComputeWorkloadFingerprint(connector, identity, WorkspaceMcpExecutionContext.Isolated, profile);
        Assert.NotEqual(original, ConnectorContainerSpecBuilder.ComputeWorkloadFingerprint(connector, identity, WorkspaceMcpExecutionContext.Isolated, stricter));
        Assert.Equal(original, ConnectorContainerSpecBuilder.ComputeWorkloadFingerprint(connector, identity, WorkspaceMcpExecutionContext.Isolated, reordered));
        List<string> args = ConnectorContainerSpecBuilder.BuildRun(connector, "test", WorkspaceMcpExecutionContext.Isolated, context,
            persistent: true, allocatedIp: "10.247.1.30", isBraked: false, compatProfile: profile);
        Assert.Contains($"kautuka.spec-fingerprint={original}", args);
    }

    [Fact]
    public void ResolveIdentityRequiresOwnerButAllowsDiscoveryWithoutAgent()
    {
        WorkspaceMcpContainerConnectorDefinition connector = CreateConnector();
        Assert.Throws<InvalidOperationException>(() =>
            ConnectorContainerSpecBuilder.ResolveIdentity(connector, null, purpose: null));

        ContainerContext userOnly = CreateWorkspaceContext() with { ScopeType = "user", ScopeId = "user-alpha" };
        ConnectorContainerSpecBuilder.ConnectorIdentity discovery =
            ConnectorContainerSpecBuilder.ResolveIdentity(connector, userOnly, purpose: "save-discovery");
        Assert.Equal("user-alpha", discovery.UserId);
        Assert.True(string.IsNullOrEmpty(discovery.AgentId));
        Assert.Equal("normal", discovery.Egress);

        ConnectorContainerSpecBuilder.ConnectorIdentity identity =
            ConnectorContainerSpecBuilder.ResolveIdentity(connector, CreateWorkspaceContext(), purpose: null);
        Assert.Equal("user-alpha", identity.UserId);
        Assert.Equal("agent-alpha", identity.AgentId);
        Assert.Equal("normal", identity.Egress);
    }

    [Fact]
    public void BuilderUsesCurrentBrakeStateInsteadOfClearingIt()
    {
        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(() => ConnectorContainerSpecBuilder.BuildRun(
            CreateConnector(),
            "braked-connector",
            WorkspaceMcpExecutionContext.Isolated,
            CreateWorkspaceContext(),
            persistent: true,
            allocatedIp: "10.247.1.9",
            isBraked: true));

        Assert.Contains("Network paused", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void InternalSystemConnectorIsAlwaysDenyAll()
    {
        WorkspaceMcpContainerConnectorDefinition connector = CreateConnector() with
        {
            IsInternalSystemConnector = true,
            CatalogScope = "system",
        };
        ConnectorContainerSpecBuilder.ConnectorIdentity identity =
            ConnectorContainerSpecBuilder.ResolveIdentity(connector, null, purpose: null);

        Assert.Equal("deny-all", identity.Egress);
        Assert.Equal("system", identity.Kind);
    }

    [Fact]
    public void InternalConnectorInstancesCarryUserIdentityButRemainDenyAll()
    {
        WorkspaceMcpContainerConnectorDefinition connector = CreateConnector() with
        {
            ConnectorId = "system::shared-provider",
            IsInternalSystemConnector = true,
            CatalogScope = "system",
        };
        ContainerContext userAlpha = CreateWorkspaceContext() with { UserId = "user-alpha" };
        ContainerContext userBeta = CreateWorkspaceContext() with { UserId = "user-beta" };

        List<string> alphaArgs = ConnectorContainerSpecBuilder.BuildRun(
            connector, "alpha-container", WorkspaceMcpExecutionContext.Isolated, userAlpha,
            persistent: true, allocatedIp: "10.247.1.30", isBraked: false);
        List<string> betaArgs = ConnectorContainerSpecBuilder.BuildRun(
            connector, "beta-container", WorkspaceMcpExecutionContext.Isolated, userBeta,
            persistent: true, allocatedIp: "10.247.1.31", isBraked: false);

        Assert.DoesNotContain(alphaArgs, arg => arg.StartsWith("io.kautuka.systemwide=", StringComparison.Ordinal));

        Assert.Contains("io.kautuka.user=user-alpha", alphaArgs, StringComparer.Ordinal);
        Assert.Contains("io.kautuka.user=user-beta", betaArgs, StringComparer.Ordinal);
        Assert.DoesNotContain(betaArgs, arg => arg.StartsWith("io.kautuka.systemwide=", StringComparison.Ordinal));
        Assert.Contains("io.kautuka.kind=system", alphaArgs, StringComparer.Ordinal);
        Assert.Contains("io.kautuka.kind=system", betaArgs, StringComparer.Ordinal);
        Assert.Contains("io.kautuka.egress=deny-all", alphaArgs, StringComparer.Ordinal);
        Assert.Contains("io.kautuka.egress=deny-all", betaArgs, StringComparer.Ordinal);
        Assert.NotEqual(
            InvokeBuildContainerName(connector.ConnectorId, WorkspaceMcpExecutionContext.Isolated, userAlpha),
            InvokeBuildContainerName(connector.ConnectorId, WorkspaceMcpExecutionContext.Isolated, userBeta));
    }

    [Fact]
    public void SystemScopedConnectorInstancesUsePerUserSystemwideIdentity()
    {
        WorkspaceMcpContainerConnectorDefinition connector = CreateConnector() with
        {
            ConnectorId = "user-alpha::shared-provider",
            CatalogScope = "system",
        };
        ContainerContext userAlpha = CreateWorkspaceContext() with { UserId = "user-alpha" };
        ContainerContext userBeta = CreateWorkspaceContext() with { UserId = "user-beta" };

        ConnectorContainerSpecBuilder.ConnectorIdentity alpha =
            ConnectorContainerSpecBuilder.ResolveIdentity(connector, userAlpha, purpose: null);
        ConnectorContainerSpecBuilder.ConnectorIdentity beta =
            ConnectorContainerSpecBuilder.ResolveIdentity(connector with { ConnectorId = "user-beta::shared-provider" }, userBeta, purpose: null);
        List<string> alphaArgs = ConnectorContainerSpecBuilder.BuildRun(
            connector, "alpha-container", WorkspaceMcpExecutionContext.Isolated, userAlpha,
            persistent: true, allocatedIp: "10.247.1.30", isBraked: false);
        List<string> betaArgs = ConnectorContainerSpecBuilder.BuildRun(
            connector with { ConnectorId = "user-beta::shared-provider" }, "beta-container", WorkspaceMcpExecutionContext.Isolated, userBeta,
            persistent: true, allocatedIp: "10.247.1.31", isBraked: false);

        Assert.Equal("user-alpha", alpha.UserId);
        Assert.Equal("user-beta", beta.UserId);
        Assert.Equal(userAlpha.ScopeId, alpha.AgentId);
        Assert.Equal(userBeta.ScopeId, beta.AgentId);
        Assert.Equal("connector", alpha.Kind);
        Assert.Equal("normal", alpha.Egress);
        Assert.DoesNotContain(alphaArgs, arg => arg.StartsWith("io.kautuka.systemwide=", StringComparison.Ordinal));
        Assert.DoesNotContain(betaArgs, arg => arg.StartsWith("io.kautuka.systemwide=", StringComparison.Ordinal));
        Assert.Contains("io.kautuka.user=user-alpha", alphaArgs, StringComparer.Ordinal);
        Assert.Contains("io.kautuka.user=user-beta", betaArgs, StringComparer.Ordinal);
        Assert.NotEqual(
            InvokeBuildContainerName(connector.ConnectorId, WorkspaceMcpExecutionContext.Isolated, userAlpha),
            InvokeBuildContainerName("user-beta::shared-provider", WorkspaceMcpExecutionContext.Isolated, userBeta));

        string stateDirectory = Path.Combine(Path.GetTempPath(), "nac-v7-system-scope-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(stateDirectory);
        try
        {
            var registry = new WorkloadRegistry();
            registry.Register(new RegistryEntry(
                "10.247.1.30", alpha.UserId, alpha.AgentId, "alpha-container", alpha.Kind, connector.ConnectorId,
                "isolated", alpha.Egress, "user-agent", null, DateTimeOffset.UtcNow));
            registry.Register(new RegistryEntry(
                "10.247.1.31", beta.UserId, beta.AgentId, "beta-container", beta.Kind, "user-beta::shared-provider",
                "isolated", beta.Egress, "user-agent", null, DateTimeOffset.UtcNow));
            using var policyStore = new NetworkAccessPolicyStore(Path.Combine(stateDirectory, "policy.json"));
            var controller = new Kautuka.NetworkAccessController.NetworkAccessController(policyStore, registry);
            var admin = new NetworkAccessActor("admin-1", IsAdmin: true);
            _ = policyStore.SetUserPrivileges(admin, "user-alpha", canApproveOwn: true);
            _ = policyStore.SetRule(
                admin,
                new RuleWriteSpec("user", "user-alpha", null, connector.ConnectorId, "example.com", "host", 443, Allowed: true),
                expectedRevision: policyStore.Revision);

            Assert.True(controller.EvaluateContainerConnection(
                new ContainerConnectionInput("10.247.1.30", "93.184.216.34", 443, "example.com", true)).Allowed);
            NetworkAccessDecisionResult betaDecision = controller.EvaluateContainerConnection(
                new ContainerConnectionInput("10.247.1.31", "93.184.216.34", 443, "example.com", true));
            Assert.False(betaDecision.Allowed);
            Assert.Equal("no_rule", betaDecision.Reason);
        }
        finally
        {
            Directory.Delete(stateDirectory, recursive: true);
        }
    }

    [Fact]
    public void IsolatedSpecIsBoundedAndDoesNotExposeSecretValuesInArguments()
    {
        WorkspaceMcpContainerConnectorDefinition connector = CreateConnector();
        List<string> arguments = ConnectorContainerSpecBuilder.BuildRun(
            connector,
            "connector-test",
            WorkspaceMcpExecutionContext.Isolated,
            CreateWorkspaceContext(),
            persistent: false,
            allocatedIp: "10.247.1.1",
            isBraked: false);

        AssertOption(arguments, "--network", $"{ConnectorContainerSpecBuilder.WorkloadNetworkName}:ip=10.247.1.1");
        AssertOption(arguments, "--add-host", $"kautuka-pkgs-proxy:{Kautuka.NetworkAccessController.NetworkAddressClassifier.PackageProxyServiceAddress}");
        AssertOption(arguments, "--cap-drop", "ALL");
        AssertOption(arguments, "--pids-limit", "128");
        AssertOption(arguments, "--read-only", "--tmpfs");
        Assert.Contains("/tmp:rw,noexec,nosuid", arguments, StringComparer.Ordinal);
        Assert.DoesNotContain(arguments, static argument => argument.Contains("size=", StringComparison.Ordinal));
        Assert.DoesNotContain(arguments, static argument => argument.Contains("canary-secret", StringComparison.Ordinal));
        Assert.DoesNotContain(arguments, static argument => argument.Contains("TOKEN", StringComparison.Ordinal));
        Assert.Empty(ConnectorContainerSpecBuilder.BuildTransportEnvironment(connector));
        Assert.Equal("canary-secret", ConnectorContainerSpecBuilder.FilterContainerEnvironment(connector.Environment)["TOKEN"]);
    }

    [Fact]
    public void V7_23_ConnectorMountMustStayUnderConfiguredWorkspaceBaseRoot()
    {
        string root = Path.Combine(Path.GetTempPath(), "nac-v7-23-root-" + Guid.NewGuid().ToString("N"));
        string outside = Path.Combine(Path.GetTempPath(), "nac-v7-23-outside-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(outside);
        try
        {
            ContainerContext context = CreateWorkspaceContext() with
            {
                HostWorkspacePath = outside,
                WorkspaceBaseRoot = root,
            };

            Assert.Throws<ArgumentException>(() => ConnectorContainerSpecBuilder.BuildRun(
                CreateConnector(),
                "connector-test",
                WorkspaceMcpExecutionContext.IsolatedWithAgentFiles,
                context,
                persistent: true,
                allocatedIp: "10.247.1.1",
                isBraked: false));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
            Directory.Delete(outside, recursive: true);
        }
    }

    [Theory]
    [InlineData("LD_PRELOAD")]
    [InlineData("CONTAINERS_CONF")]
    [InlineData("PATH")]
    [InlineData("PYTHONPATH")]
    [InlineData("NODE_PATH")]
    [InlineData("ld_custom")]
    [InlineData("DYLD_CUSTOM")]
    [InlineData("CONTAINERS_CUSTOM")]
    [InlineData("CONTAINER_CUSTOM")]
    [InlineData("DOCKER_CUSTOM")]
    [InlineData("PODMAN_CUSTOM")]
    [InlineData("GCONV_PATH")]
    public void LaunchSeparatesHostProcessControlsFromContainerEnvironment(string key)
    {
        var connector = CreateConnector() with { Environment = [new(key, "canary"), new("TOKEN", "allowed")] };
        List<string> execArgs = ConnectorContainerSpecBuilder.BuildExec(connector, "sandbox", null, "/tmp/connector.env");
        List<string> runArgs = ConnectorContainerSpecBuilder.BuildRun(connector, "isolated",
            WorkspaceMcpExecutionContext.Isolated, CreateWorkspaceContext(), false, "10.247.1.1", false, environmentFile: "/tmp/connector.env");
        Assert.DoesNotContain(execArgs, arg => arg.Contains(key, StringComparison.OrdinalIgnoreCase) || arg.Contains("canary", StringComparison.Ordinal));
        Assert.DoesNotContain(runArgs, arg => arg.Contains(key, StringComparison.OrdinalIgnoreCase) || arg.Contains("canary", StringComparison.Ordinal));
        AssertOption(execArgs, "--env-file", "/tmp/connector.env");
        AssertOption(runArgs, "--env-file", "/tmp/connector.env");
        Assert.Empty(ConnectorContainerSpecBuilder.BuildTransportEnvironment(connector));
        Assert.Equal("TOKEN", Assert.Single(ConnectorContainerSpecBuilder.FilterHostProcessEnvironment(connector.Environment)).Key);
        Dictionary<string, string> containerEnv = ConnectorContainerSpecBuilder.FilterContainerEnvironment(connector.Environment);
        Assert.Equal("canary", containerEnv[key]);
        Assert.Equal("allowed", containerEnv["TOKEN"]);
    }

    [Fact]
    public void HostAndContainerEnvironmentPoliciesEnforceSeparateBoundaries()
    {
        Assert.False(ConnectorContainerSpecBuilder.AllowsHostProcessEnvironment("PATH"));
        Assert.False(ConnectorContainerSpecBuilder.AllowsHostProcessEnvironment("PYTHONPATH"));
        Assert.False(ConnectorContainerSpecBuilder.AllowsHostProcessEnvironment("NODE_PATH"));
        Assert.False(ConnectorContainerSpecBuilder.AllowsHostProcessEnvironment("LD_PRELOAD"));
        Assert.False(ConnectorContainerSpecBuilder.AllowsHostProcessEnvironment("HTTP_PROXY"));
        Assert.False(ConnectorContainerSpecBuilder.AllowsHostProcessEnvironment("CONTAINERS_CONF"));
        Assert.False(ConnectorContainerSpecBuilder.AllowsHostProcessEnvironment("CONTAINERS_HELPER_BINARY_DIR"));
        Assert.False(ConnectorContainerSpecBuilder.AllowsHostProcessEnvironment("STORAGE_OPTS"));
        Assert.False(ConnectorContainerSpecBuilder.AllowsHostProcessEnvironment("HOME"));
        Assert.False(ConnectorContainerSpecBuilder.AllowsHostProcessEnvironment("CONTAINER_HOST"));
        Assert.False(ConnectorContainerSpecBuilder.AllowsHostProcessEnvironment("DOCKER_HOST"));
        Assert.False(ConnectorContainerSpecBuilder.AllowsHostProcessEnvironment("PODMAN_CUSTOM"));
        Assert.True(ConnectorContainerSpecBuilder.AllowsHostProcessEnvironment("TOKEN"));

        Assert.True(ConnectorContainerSpecBuilder.AllowsContainerEnvironment("PATH"));
        Assert.True(ConnectorContainerSpecBuilder.AllowsContainerEnvironment("PYTHONPATH"));
        Assert.True(ConnectorContainerSpecBuilder.AllowsContainerEnvironment("NODE_PATH"));
        Assert.True(ConnectorContainerSpecBuilder.AllowsContainerEnvironment("DOCKER_HOST"));
        Assert.True(ConnectorContainerSpecBuilder.AllowsContainerEnvironment("CONTAINER_HOST"));
        Assert.True(ConnectorContainerSpecBuilder.AllowsContainerEnvironment("PODMAN_CUSTOM"));
        Assert.True(ConnectorContainerSpecBuilder.AllowsContainerEnvironment("TOKEN"));
        Assert.True(ConnectorContainerSpecBuilder.AllowsContainerEnvironment("NPM_CONFIG_REGISTRY"));
        Assert.False(ConnectorContainerSpecBuilder.AllowsContainerEnvironment("HTTP_PROXY"));
        Assert.False(ConnectorContainerSpecBuilder.AllowsContainerEnvironment("HTTPS_PROXY"));
        Assert.False(ConnectorContainerSpecBuilder.AllowsContainerEnvironment("ALL_PROXY"));
        Assert.False(ConnectorContainerSpecBuilder.AllowsContainerEnvironment("NO_PROXY"));
        Assert.False(ConnectorContainerSpecBuilder.AllowsContainerEnvironment("HOME"));
        Assert.False(ConnectorContainerSpecBuilder.AllowsContainerEnvironment("PIP_EXTRA_INDEX_URL"));
        Assert.False(ConnectorContainerSpecBuilder.AllowsContainerEnvironment("INVALID-NAME"));
    }

    [Fact]
    public void AgentFileMountIsReadOnlyAndUsesSandboxNetwork()
    {
        ContainerContext workspace = new(
            "agent", "alpha", "user", "/srv/kautuka/alpha", "/workspace", "image@sha256:digest",
            1, "512m", 30, [], new Dictionary<string, string>())
        {
            WorkspaceBaseRoot = "/srv/kautuka",
        };
        List<string> arguments = ConnectorContainerSpecBuilder.BuildRun(
            CreateConnector(),
            "connector-test",
            WorkspaceMcpExecutionContext.IsolatedWithAgentFiles,
            workspace,
            persistent: true,
            allocatedIp: "10.247.1.1",
            isBraked: false);

        AssertOption(arguments, "--network", $"{ConnectorContainerSpecBuilder.WorkloadNetworkName}:ip=10.247.1.1");
        Assert.Contains("/srv/kautuka/alpha:/workspace:ro", arguments);
    }

    [Fact]
    public void IsolatedMailConnectorDoesNotMountAWorkspace()
    {
        string host = Path.Combine(Path.GetTempPath(), "kautuka-mail-mount-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(host);
        try
        {
            ContainerContext workspace = new(
                "agent", "alpha", "user", host, "/workspace", "image@sha256:digest",
                1, "512m", 30, [], new Dictionary<string, string>());
            WorkspaceMcpContainerConnectorDefinition connector = CreateConnector() with
            {
                ConnectorId = "user::kautuka-m365-mail",
            };

            List<string> arguments = ConnectorContainerSpecBuilder.BuildRun(
                connector,
                "connector-test",
                WorkspaceMcpExecutionContext.Isolated,
                workspace,
                persistent: false,
                allocatedIp: "10.247.1.1",
                isBraked: false);

            Assert.DoesNotContain(arguments, argument => argument.Contains("/kautuka/mail", StringComparison.Ordinal));
            Assert.DoesNotContain(arguments, argument => argument.Contains(":/workspace", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(host, recursive: true);
        }
    }

    [Theory]
    [InlineData(WorkspaceMcpExecutionContext.Unspecified)]
    [InlineData(WorkspaceMcpExecutionContext.Isolated)]
    [InlineData(WorkspaceMcpExecutionContext.IsolatedWithAgentFiles)]
    [InlineData(WorkspaceMcpExecutionContext.AgentSandbox)]
    public void BuildRunAlwaysJoinsSandboxNetwork(WorkspaceMcpExecutionContext executionContext)
    {
        List<string> arguments = ConnectorContainerSpecBuilder.BuildRun(
            CreateConnector(),
            "connector-test",
            executionContext,
            CreateWorkspaceContext(),
            persistent: false,
            allocatedIp: "10.247.1.1",
            isBraked: false);

        AssertOption(arguments, "--network", $"{ConnectorContainerSpecBuilder.WorkloadNetworkName}:ip=10.247.1.1");
        Assert.DoesNotContain("none", arguments, StringComparer.Ordinal);
    }

    [Fact]
    public void MutableExternalRegistryImageIsRejected()
    {
        WorkspaceMcpContainerConnectorDefinition connector = CreateConnector() with
        {
            Image = "ghcr.io/example/connector:latest",
        };

        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(() =>
            ConnectorContainerSpecBuilder.BuildRun(
                connector,
                "connector-test",
                WorkspaceMcpExecutionContext.Isolated,
                CreateWorkspaceContext(),
                persistent: false,
                allocatedIp: "10.247.1.1",
                isBraked: false));

        Assert.Contains("must be pinned", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("kautuka-local:build-hash")]
    [InlineData("localhost/kautuka-local:build-hash")]
    public void ApplianceLocalImageIsAccepted(string image)
    {
        WorkspaceMcpContainerConnectorDefinition connector = CreateConnector() with { Image = image };

        List<string> arguments = ConnectorContainerSpecBuilder.BuildRun(
            connector,
            "connector-test",
            WorkspaceMcpExecutionContext.Isolated,
            CreateWorkspaceContext(),
            persistent: false,
            allocatedIp: "10.247.1.1",
            isBraked: false);

        Assert.Contains(image, arguments);
    }

    [Fact]
    public void NpxLaunchPinsRegistryFromEnvironmentAndStripsPublicRegistryFlag()
    {
        WorkspaceMcpContainerConnectorDefinition connector = CreateConnector() with
        {
            Command = "npx",
            Args = ["--registry", "https://registry.npmjs.org", "--userconfig", "/tmp/evil.npmrc", "--registry=https://registry.npmjs.org", "-y", "@modelcontextprotocol/server-filesystem"],
            Environment =
            [
                new KeyValuePair<string, string>("NPM_CONFIG_REGISTRY", "http://kautuka-pkgs-proxy:20101/npm/"),
                new KeyValuePair<string, string>("npm_config_registry", "https://registry.npmjs.org"),
                new KeyValuePair<string, string>("NPM_CONFIG_USERCONFIG", "/tmp/evil.npmrc"),
                new KeyValuePair<string, string>("PIP_CONFIG_FILE", "/tmp/pip.conf"),
                new KeyValuePair<string, string>("TOKEN", "canary-secret"),
            ],
        };

        List<string> arguments = ConnectorContainerSpecBuilder.BuildRun(
            connector,
            "connector-test",
            WorkspaceMcpExecutionContext.Isolated,
            CreateWorkspaceContext(),
            persistent: false,
            allocatedIp: "10.247.1.1",
            isBraked: false);

        int commandIndex = arguments.IndexOf("npx");
        Assert.True(commandIndex >= 0);
        Assert.Equal(
            ["npx", "--registry", "http://kautuka-pkgs-proxy:20101/npm/", "-y", "@modelcontextprotocol/server-filesystem"],
            arguments[commandIndex..]);
        Assert.DoesNotContain("https://registry.npmjs.org", arguments, StringComparer.Ordinal);
        Assert.DoesNotContain("/tmp/evil.npmrc", arguments, StringComparer.Ordinal);
        Assert.DoesNotContain(arguments, static argument => argument.Contains("canary-secret", StringComparison.Ordinal));
        Assert.Empty(ConnectorContainerSpecBuilder.BuildTransportEnvironment(connector));
        Dictionary<string, string> forwarded = ConnectorContainerSpecBuilder.FilterContainerEnvironment(connector.Environment);
        Assert.Equal("http://kautuka-pkgs-proxy:20101/npm/", forwarded["NPM_CONFIG_REGISTRY"]);
        Assert.Equal("canary-secret", forwarded["TOKEN"]);
        Assert.False(forwarded.ContainsKey("npm_config_registry"));
        Assert.False(forwarded.ContainsKey("NPM_CONFIG_USERCONFIG"));
        Assert.False(forwarded.ContainsKey("PIP_CONFIG_FILE"));
    }

    [Fact]
    public void UvxLaunchPinsIndexFromEnvironmentAndStripsPublicIndexFlag()
    {
        WorkspaceMcpContainerConnectorDefinition connector = CreateConnector() with
        {
            Command = "uvx",
            Args = ["--index-url", "https://pypi.org/simple", "some-mcp-server"],
            Environment =
            [
                new KeyValuePair<string, string>("UV_INDEX_URL", "http://kautuka-pkgs-proxy:20101/pypi/simple/"),
            ],
        };

        List<string> arguments = ConnectorContainerSpecBuilder.BuildRun(
            connector,
            "connector-test",
            WorkspaceMcpExecutionContext.Isolated,
            CreateWorkspaceContext(),
            persistent: false,
            allocatedIp: "10.247.1.1",
            isBraked: false);

        int commandIndex = arguments.IndexOf("uvx");
        Assert.True(commandIndex >= 0);
        Assert.Equal(
            [
                "uvx",
                "--index-url",
                "http://kautuka-pkgs-proxy:20101/pypi/simple/",
                "--default-index",
                "http://kautuka-pkgs-proxy:20101/pypi/simple/",
                "some-mcp-server",
            ],
            arguments[commandIndex..]);
        Assert.DoesNotContain("https://pypi.org/simple", arguments, StringComparer.Ordinal);
    }

    [Fact]
    public void Version027ArtifactDirectoryIsAnUnknownField()
    {
        // Length-delimited field 10 followed by field 2 (connector_id) from the v0.1.27 wire contract.
        byte[] legacy = [0x52, 4, (byte)'/', (byte)'t', (byte)'m', (byte)'p', 0x12, 1, (byte)'c'];
        var request = WorkspaceMcpContainerCallRequest.Parser.ParseFrom(legacy);
        Assert.Equal("c", request.ConnectorId);
        Assert.Null(WorkspaceMcpContainerCallRequest.Descriptor.FindFieldByNumber(10));
        Assert.Null(request.WorkspaceContext);
    }

    [Fact]
    public void WindowsHostCommandRejectsAQuoteThatCmdWouldReparse()
    {
        WorkspaceMcpContainerConnectorDefinition connector = CreateConnector() with
        {
            Command = "node",
            Args = ["hello\" & echo injected"],
        };

        if (!OperatingSystem.IsWindows())
        {
            List<string> arguments = ConnectorContainerSpecBuilder.BuildExec(connector, "connector-test", null);
            Assert.Contains("hello\" & echo injected", arguments, StringComparer.Ordinal);
            return;
        }

        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(
            () => ConnectorContainerSpecBuilder.BuildExec(connector, "connector-test", null));
        Assert.Contains("quote", exception.Message, StringComparison.Ordinal);

        WorkspaceMcpContainerConnectorDefinition expanded = CreateConnector() with
        {
            Command = "node",
            Args = ["%EVIL%"],
        };
        if (OperatingSystem.IsWindows())
        {
            Assert.Throws<InvalidOperationException>(
                () => ConnectorContainerSpecBuilder.BuildExec(expanded, "connector-test", null));
        }
    }

    private static WorkspaceMcpContainerConnectorDefinition CreateConnector()
        => new(
            "connector", $"ghcr.io/example/image@sha256:{new string('a', 64)}", "server", [], true, false, null, null,
            [new KeyValuePair<string, string>("TOKEN", "canary-secret")], 30, 60);

    private static ContainerContext CreateWorkspaceContext()
        => new("agent", "agent-alpha", "user-alpha", "/tmp/agent-alpha", "/workspace", "image@sha256:digest",
            1, "512m", 30, [], new Dictionary<string, string>())
        {
            WorkspaceBaseRoot = "/tmp",
        };

    private static string InvokeBuildContainerName(
        string connectorId,
        WorkspaceMcpExecutionContext executionContext,
        ContainerContext workspaceContext)
    {
        MethodInfo method = typeof(WorkspaceContainerConnectorExecutionService)
            .GetMethod("BuildContainerName", BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("BuildContainerName method was not found.");
        return Assert.IsType<string>(method.Invoke(null, [connectorId, executionContext, workspaceContext]));
    }

    private static void AssertOption(List<string> arguments, string option, string expectedValue)
    {
        int index = arguments.ToList().IndexOf(option);
        Assert.True(index >= 0 && index + 1 < arguments.Count, $"Missing option {option}.");
        Assert.Equal(expectedValue, arguments[index + 1]);
    }
}
