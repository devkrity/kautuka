using System.Reflection;

using Kautuka.Manager.Protos;
using Kautuka.Manager.RuntimeContainer.Hosting;
using Kautuka.Manager.RuntimeContainer.Sandbox.Models;
using Kautuka.NetworkAccessController;

namespace Kautuka.IntegrationTests;

public sealed class ConnectorContainerSpecBuilderTests
{
    [Fact]
    public void ResolveIdentityRequiresOwnerAndAgentForRuntimeConnector()
    {
        WorkspaceMcpContainerConnectorDefinition connector = CreateConnector();
        Assert.Throws<InvalidOperationException>(() =>
            ConnectorContainerSpecBuilder.ResolveIdentity(connector, null, purpose: null));

        ContainerContext userOnly = CreateWorkspaceContext() with { ScopeType = "user", ScopeId = "user-alpha" };
        Assert.Throws<InvalidOperationException>(() =>
            ConnectorContainerSpecBuilder.ResolveIdentity(connector, userOnly, purpose: null));

        ConnectorContainerSpecBuilder.ConnectorIdentity identity =
            ConnectorContainerSpecBuilder.ResolveIdentity(connector, CreateWorkspaceContext(), purpose: null);
        Assert.Equal("user-alpha", identity.UserId);
        Assert.Equal("agent-alpha", identity.AgentId);
        Assert.False(identity.Systemwide);
        Assert.Equal("normal", identity.Egress);
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

        Assert.True(identity.Systemwide);
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
            persistent: true, allocatedIp: "10.247.1.30");
        List<string> betaArgs = ConnectorContainerSpecBuilder.BuildRun(
            connector, "beta-container", WorkspaceMcpExecutionContext.Isolated, userBeta,
            persistent: true, allocatedIp: "10.247.1.31");

        Assert.Contains("io.kautuka.user=user-alpha", alphaArgs, StringComparer.Ordinal);
        Assert.Contains("io.kautuka.user=user-beta", betaArgs, StringComparer.Ordinal);
        Assert.Contains("io.kautuka.systemwide=true", alphaArgs, StringComparer.Ordinal);
        Assert.Contains("io.kautuka.systemwide=true", betaArgs, StringComparer.Ordinal);
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
            persistent: true, allocatedIp: "10.247.1.30");
        List<string> betaArgs = ConnectorContainerSpecBuilder.BuildRun(
            connector with { ConnectorId = "user-beta::shared-provider" }, "beta-container", WorkspaceMcpExecutionContext.Isolated, userBeta,
            persistent: true, allocatedIp: "10.247.1.31");

        Assert.True(alpha.Systemwide);
        Assert.True(beta.Systemwide);
        Assert.Equal("user-alpha", alpha.UserId);
        Assert.Equal("user-beta", beta.UserId);
        Assert.Equal(string.Empty, alpha.AgentId);
        Assert.Equal(string.Empty, beta.AgentId);
        Assert.Equal("connector", alpha.Kind);
        Assert.Equal("normal", alpha.Egress);
        Assert.Contains("io.kautuka.systemwide=true", alphaArgs, StringComparer.Ordinal);
        Assert.Contains("io.kautuka.systemwide=true", betaArgs, StringComparer.Ordinal);
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
                "10.247.1.30", alpha.UserId, null, "alpha-container", alpha.Kind, connector.ConnectorId,
                "isolated", alpha.Systemwide, alpha.Egress, "user", null, DateTimeOffset.UtcNow));
            registry.Register(new RegistryEntry(
                "10.247.1.31", beta.UserId, null, "beta-container", beta.Kind, "user-beta::shared-provider",
                "isolated", beta.Systemwide, beta.Egress, "user", null, DateTimeOffset.UtcNow));
            using var policyStore = new NetworkAccessPolicyStore(Path.Combine(stateDirectory, "policy.json"));
            var controller = new Kautuka.NetworkAccessController.NetworkAccessController(policyStore, registry);
            var admin = new NetworkAccessActor("admin-1", IsAdmin: true);
            _ = policyStore.SetUserPrivileges(admin, "user-alpha", canApproveOwn: true, canAllowAllAgents: true);
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
            allocatedIp: "10.247.1.1");

        AssertOption(arguments, "--network", $"{ConnectorContainerSpecBuilder.WorkloadNetworkName}:ip=10.247.1.1");
        AssertOption(arguments, "--cap-drop", "ALL");
        AssertOption(arguments, "--pids-limit", "128");
        AssertOption(arguments, "--read-only", "--tmpfs");
        Assert.Contains("/tmp:rw,noexec,nosuid", arguments, StringComparer.Ordinal);
        Assert.DoesNotContain(arguments, static argument => argument.Contains("size=", StringComparison.Ordinal));
        Assert.DoesNotContain(arguments, static argument => argument.Contains("canary-secret", StringComparison.Ordinal));
        Assert.Equal("canary-secret", ConnectorContainerSpecBuilder.BuildTransportEnvironment(connector)["TOKEN"]);
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
                allocatedIp: "10.247.1.1"));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
            Directory.Delete(outside, recursive: true);
        }
    }

    [Fact]
    public void RuntimeEnvironmentOverrideCannotSetHostProcessControls()
    {
        Assert.False(ConnectorContainerSpecBuilder.AllowsRuntimeEnvironmentOverride("PATH"));
        Assert.False(ConnectorContainerSpecBuilder.AllowsRuntimeEnvironmentOverride("LD_PRELOAD"));
        Assert.False(ConnectorContainerSpecBuilder.AllowsRuntimeEnvironmentOverride("HTTP_PROXY"));
        Assert.False(ConnectorContainerSpecBuilder.AllowsRuntimeEnvironmentOverride("CONTAINERS_CONF"));
        Assert.False(ConnectorContainerSpecBuilder.AllowsRuntimeEnvironmentOverride("CONTAINERS_HELPER_BINARY_DIR"));
        Assert.False(ConnectorContainerSpecBuilder.AllowsRuntimeEnvironmentOverride("STORAGE_OPTS"));
        Assert.False(ConnectorContainerSpecBuilder.AllowsRuntimeEnvironmentOverride("HOME"));
        Assert.False(ConnectorContainerSpecBuilder.AllowsRuntimeEnvironmentOverride("CONTAINER_HOST"));
        Assert.True(ConnectorContainerSpecBuilder.AllowsRuntimeEnvironmentOverride("TOKEN"));
        Assert.True(ConnectorContainerSpecBuilder.ForwardsEnvironmentKey("HTTP_PROXY"));
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
            allocatedIp: "10.247.1.1");

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
                allocatedIp: "10.247.1.1");

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
            allocatedIp: "10.247.1.1");

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
                allocatedIp: "10.247.1.1"));

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
            allocatedIp: "10.247.1.1");

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
            allocatedIp: "10.247.1.1");

        int commandIndex = arguments.IndexOf("npx");
        Assert.True(commandIndex >= 0);
        Assert.Equal(
            ["npx", "--registry", "http://kautuka-pkgs-proxy:20101/npm/", "-y", "@modelcontextprotocol/server-filesystem"],
            arguments[commandIndex..]);
        Assert.DoesNotContain("https://registry.npmjs.org", arguments, StringComparer.Ordinal);
        Assert.DoesNotContain("/tmp/evil.npmrc", arguments, StringComparer.Ordinal);
        Assert.DoesNotContain(arguments, static argument => argument.Contains("canary-secret", StringComparison.Ordinal));
        Dictionary<string, string?> forwarded = ConnectorContainerSpecBuilder.BuildTransportEnvironment(connector);
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
            allocatedIp: "10.247.1.1");

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
    public void MaterializerStoresBytesBesideTheView()
    {
        string root = Directory.CreateTempSubdirectory("kautuka-view-files-").FullName;
        try
        {
            const string result = """
                {"structured_content":{"view":{"blocks":[{"type":"file","fileId":"f1","name":"notes.txt","mediaType":"text/plain"}]},"files":[{"fileId":"f1","name":"notes.txt","base64":"aGVsbG8="}]}}
                """;
            string stored = ViewFileMaterializer.Materialize(result, root);
            Assert.DoesNotContain("aGVsbG8=", stored, StringComparison.Ordinal);
            Assert.DoesNotContain("\"files\"", stored, StringComparison.Ordinal);
            Assert.Contains("view-files/", stored, StringComparison.Ordinal);
            string saved = Directory.GetFiles(root, "notes.txt", SearchOption.AllDirectories).Single();
            Assert.Equal("hello", File.ReadAllText(saved));

            string stripped = ViewFileMaterializer.Materialize(result, hostDirectory: null);
            Assert.Contains("\"unavailable\":true", stripped, StringComparison.Ordinal);
            Assert.DoesNotContain("aGVsbG8=", stripped, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
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
