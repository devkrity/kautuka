using Kautuka.Manager.Protos;
using Kautuka.Manager.RuntimeContainer.Hosting;
using Kautuka.Manager.RuntimeContainer.Sandbox.Models;

namespace Kautuka.IntegrationTests;

public sealed class ConnectorContainerSpecBuilderTests
{
    [Fact]
    public void IsolatedSpecIsBoundedAndDoesNotExposeSecretValuesInArguments()
    {
        WorkspaceMcpContainerConnectorDefinition connector = CreateConnector();
        List<string> arguments = ConnectorContainerSpecBuilder.BuildRun(
            connector,
            "connector-test",
            WorkspaceMcpExecutionContext.Isolated,
            null,
            persistent: false);

        AssertOption(arguments, "--network", ConnectorContainerSpecBuilder.SandboxNetworkName);
        AssertOption(arguments, "--cap-drop", "ALL");
        AssertOption(arguments, "--pids-limit", "128");
        AssertOption(arguments, "--read-only", "--tmpfs");
        Assert.Contains("/tmp:rw,noexec,nosuid", arguments, StringComparer.Ordinal);
        Assert.DoesNotContain(arguments, static argument => argument.Contains("size=", StringComparison.Ordinal));
        Assert.DoesNotContain(arguments, static argument => argument.Contains("canary-secret", StringComparison.Ordinal));
        Assert.Equal("canary-secret", ConnectorContainerSpecBuilder.BuildTransportEnvironment(connector)["TOKEN"]);
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
            1, "512m", 30, [], new Dictionary<string, string>());
        List<string> arguments = ConnectorContainerSpecBuilder.BuildRun(
            CreateConnector(),
            "connector-test",
            WorkspaceMcpExecutionContext.IsolatedWithAgentFiles,
            workspace,
            persistent: true);

        AssertOption(arguments, "--network", ConnectorContainerSpecBuilder.SandboxNetworkName);
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
                persistent: false);

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
            null,
            persistent: false);

        AssertOption(arguments, "--network", ConnectorContainerSpecBuilder.SandboxNetworkName);
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
                null,
                persistent: false));

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
            null,
            persistent: false);

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
            null,
            persistent: false);

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
            null,
            persistent: false);

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

    private static void AssertOption(List<string> arguments, string option, string expectedValue)
    {
        int index = arguments.ToList().IndexOf(option);
        Assert.True(index >= 0 && index + 1 < arguments.Count, $"Missing option {option}.");
        Assert.Equal(expectedValue, arguments[index + 1]);
    }
}
