using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

using Kautuka.Gateway.Domain.Assets;
using Kautuka.Gateway.Domain.Auth;
using Kautuka.Gateway.Domain.Execution;
using Kautuka.Gateway.Domain.Memory;
using Kautuka.Gateway.Domain.Sandbox;
using Kautuka.Gateway.Domain.Skills;
using Kautuka.Gateway.Domain.Stores;
using Kautuka.Gateway.Infrastructure.Updates;
using Kautuka.Hosting.Hosting;
using Kautuka.Telemetry.Telemetry;
using Kautuka.UpdateService.Protos;

using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Kautuka.IntegrationTests;

public sealed class WorkspaceDesiredStatePublicationTests
{
    [Fact]
    public void ResolveDesiredStateAppliesManagerProxyDefaultsToContainerConnectors()
    {
        using var temp = new TemporaryGatewayStateDirectory();
        IConfiguration configuration = CreateConfiguration(temp.SqlitePath);
        var database = new KautukaDatabase(configuration);
        database.EnsureReady();

        PlatformConfiguration platformConfiguration = CreatePlatformConfiguration(
            mcpServers:
            [
                new McpServerDefinition
                {
                    Id = "filesystem",
                    Name = "Filesystem",
                    Transport = McpTransport.Container,
                    ContainerImage = "quay.io/fedora/fedora-minimal:44",
                    ContainerCommand = "npx",
                    ContainerArgs = ["-y", "@modelcontextprotocol/server-filesystem"],
                    Enabled = true,
                },
            ]);

        var resolver = new WorkspaceRuntimeContextResolver(
            new StaticPlatformConfigurationProvider(platformConfiguration),
            configuration,
            database);

        WorkspaceDesiredStatePayload payload = resolver.ResolveDesiredState();
        WorkspaceMcpContainerConnectorPayload connector = Assert.Single(payload.ContainerConnectors, item =>
            string.Equals(item.ServerId, "filesystem", StringComparison.Ordinal));

        Dictionary<string, string> environment = connector.Environment.ToDictionary(static pair => pair.Key, static pair => pair.Value, StringComparer.OrdinalIgnoreCase);
        Assert.Equal("http://kautuka-pkgs-proxy:20101/npm/", environment["NPM_CONFIG_REGISTRY"]);
        Assert.Equal("http://kautuka-pkgs-proxy:20101/pypi/simple/", environment["UV_INDEX_URL"]);
        Assert.Equal("http://kautuka-pkgs-proxy:20101/pypi/simple/", environment["UV_DEFAULT_INDEX"]);
        Assert.Equal("http://kautuka-pkgs-proxy:20101/pypi/simple/", environment["PIP_INDEX_URL"]);
        Assert.Equal("kautuka-pkgs-proxy", environment["PIP_TRUSTED_HOST"]);
        Assert.False(environment.ContainsKey("PIP_EXTRA_INDEX_URL"));
        Assert.False(environment.ContainsKey("UV_EXTRA_INDEX_URL"));
        Assert.False(environment.ContainsKey("UV_INDEX"));
        Assert.False(environment.ContainsKey("UV_PYTHON_PREFERENCE"));
        Assert.False(environment.ContainsKey("UV_SYSTEM_PYTHON"));
    }

    [Fact]
    public void ResolveDesiredStateRefusesUpstreamProxyMode()
    {
        using var temp = new TemporaryGatewayStateDirectory();
        IConfiguration configuration = CreateConfiguration(
            temp.SqlitePath,
            additionalSettings: new Dictionary<string, string?>
            {
                ["ManagerProxy:Mode"] = "upstream",
            });
        var database = new KautukaDatabase(configuration);
        database.EnsureReady();

        var resolver = new WorkspaceRuntimeContextResolver(
            new StaticPlatformConfigurationProvider(CreatePlatformConfiguration()),
            configuration,
            database);

        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() => resolver.ResolveAgentWorkspace("kautuka"));
        Assert.Contains("Mode=upstream", ex.Message, StringComparison.Ordinal);
        Assert.Contains("public-registry fallback", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ResolveDesiredStateRefusesPublicPackageIndexes()
    {
        using var temp = new TemporaryGatewayStateDirectory();
        IConfiguration configuration = CreateConfiguration(
            temp.SqlitePath,
            additionalSettings: new Dictionary<string, string?>
            {
                ["ManagerProxy:NpmRegistryUrl"] = "https://registry.npmjs.org/",
            });
        var database = new KautukaDatabase(configuration);
        database.EnsureReady();

        var resolver = new WorkspaceRuntimeContextResolver(
            new StaticPlatformConfigurationProvider(CreatePlatformConfiguration()),
            configuration,
            database);

        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() => resolver.ResolveAgentWorkspace("kautuka"));
        Assert.Contains("registry.npmjs.org", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ResolveDesiredStateStripsPipExtraIndexUrl()
    {
        using var temp = new TemporaryGatewayStateDirectory();
        IConfiguration configuration = CreateConfiguration(temp.SqlitePath);
        var database = new KautukaDatabase(configuration);
        database.EnsureReady();

        PlatformConfiguration platformConfiguration = CreatePlatformConfiguration(
            sandboxEnvironment: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["PIP_EXTRA_INDEX_URL"] = "https://pypi.org/simple",
                ["UV_EXTRA_INDEX_URL"] = "https://pypi.org/simple",
                ["UV_INDEX"] = "https://pypi.org/simple",
            });

        var resolver = new WorkspaceRuntimeContextResolver(
            new StaticPlatformConfigurationProvider(platformConfiguration),
            configuration,
            database);

        WorkspaceRuntimeContext workspace = resolver.ResolveAgentWorkspace("kautuka");
        Assert.False(workspace.ContainerEnvironment.ContainsKey("PIP_EXTRA_INDEX_URL"));
        Assert.False(workspace.ContainerEnvironment.ContainsKey("UV_EXTRA_INDEX_URL"));
        Assert.False(workspace.ContainerEnvironment.ContainsKey("UV_INDEX"));
        Assert.Equal("http://kautuka-pkgs-proxy:20101/pypi/simple/", workspace.ContainerEnvironment["PIP_INDEX_URL"]);
    }

    [Fact]
    public void ResolveDesiredStateUsesGatewayProxySettingsForAgentSandboxes()
    {
        using var temp = new TemporaryGatewayStateDirectory();
        IConfiguration configuration = CreateConfiguration(
            temp.SqlitePath,
            additionalSettings: new Dictionary<string, string?>
            {
                ["ManagerProxy:NpmRegistryUrl"] = "https://gateway.example.invalid/npm/",
                ["ManagerProxy:UvIndexUrl"] = "https://gateway.example.invalid/uv/simple",
                ["ManagerProxy:PipIndexUrl"] = "https://gateway.example.invalid/pip/simple",
            });
        var database = new KautukaDatabase(configuration);
        database.EnsureReady();

        PlatformConfiguration platformConfiguration = CreatePlatformConfiguration(
            sandboxEnvironment: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["NPM_CONFIG_REGISTRY"] = "https://sandbox.example.invalid/npm/",
                ["UV_INDEX_URL"] = "https://sandbox.example.invalid/uv/simple",
                ["PIP_INDEX_URL"] = "https://sandbox.example.invalid/pip/simple",
                ["LOG_LEVEL"] = "debug",
            });

        var resolver = new WorkspaceRuntimeContextResolver(
            new StaticPlatformConfigurationProvider(platformConfiguration),
            configuration,
            database);

        WorkspaceRuntimeContext workspace = resolver.ResolveAgentWorkspace("kautuka");
        Assert.Equal("https://gateway.example.invalid/npm/", workspace.ContainerEnvironment["NPM_CONFIG_REGISTRY"]);
        Assert.Equal("https://gateway.example.invalid/uv/simple", workspace.ContainerEnvironment["UV_INDEX_URL"]);
        Assert.Equal("https://gateway.example.invalid/pip/simple", workspace.ContainerEnvironment["PIP_INDEX_URL"]);
        Assert.False(workspace.ContainerEnvironment.ContainsKey("PIP_TRUSTED_HOST"));
        Assert.Equal("debug", workspace.ContainerEnvironment["LOG_LEVEL"]);
    }

    [Fact]
    public void ResolveDesiredStateUsesGatewayProxySettingsForContainerConnectors()
    {
        using var temp = new TemporaryGatewayStateDirectory();
        IConfiguration configuration = CreateConfiguration(
            temp.SqlitePath,
            additionalSettings: new Dictionary<string, string?>
            {
                ["ManagerProxy:NpmRegistryUrl"] = "https://gateway.example.invalid/npm/",
                ["ManagerProxy:UvIndexUrl"] = "https://gateway.example.invalid/uv/simple",
                ["ManagerProxy:PipIndexUrl"] = "https://gateway.example.invalid/pip/simple",
            });
        var database = new KautukaDatabase(configuration);
        database.EnsureReady();

        PlatformConfiguration platformConfiguration = CreatePlatformConfiguration(
            mcpServers:
            [
                new McpServerDefinition
                {
                    Id = "filesystem",
                    Name = "Filesystem",
                    Transport = McpTransport.Container,
                    ContainerImage = "quay.io/fedora/fedora-minimal:44",
                    ContainerCommand = "npx",
                    ContainerArgs = ["-y", "@modelcontextprotocol/server-filesystem"],
                    Enabled = true,
                },
            ],
            sandboxEnvironment: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["NPM_CONFIG_REGISTRY"] = "https://sandbox.example.invalid/npm/",
                ["UV_INDEX_URL"] = "https://sandbox.example.invalid/uv/simple",
                ["PIP_INDEX_URL"] = "https://sandbox.example.invalid/pip/simple",
                ["HTTPS_PROXY"] = "http://shared-proxy.example.invalid:3128",
            });

        var resolver = new WorkspaceRuntimeContextResolver(
            new StaticPlatformConfigurationProvider(platformConfiguration),
            configuration,
            database);

        WorkspaceDesiredStatePayload payload = resolver.ResolveDesiredState();
        WorkspaceMcpContainerConnectorPayload connector = Assert.Single(payload.ContainerConnectors, item =>
            string.Equals(item.ServerId, "filesystem", StringComparison.Ordinal));

        Dictionary<string, string> environment = connector.Environment.ToDictionary(static pair => pair.Key, static pair => pair.Value, StringComparer.OrdinalIgnoreCase);
        Assert.Equal("https://gateway.example.invalid/npm/", environment["NPM_CONFIG_REGISTRY"]);
        Assert.Equal("https://gateway.example.invalid/uv/simple", environment["UV_INDEX_URL"]);
        Assert.Equal("https://gateway.example.invalid/pip/simple", environment["PIP_INDEX_URL"]);
        Assert.False(environment.ContainsKey("PIP_TRUSTED_HOST"));
        Assert.Equal("http://shared-proxy.example.invalid:3128", environment["HTTPS_PROXY"]);
    }

    [Fact]
    public void ResolveDesiredStatePublishesOnlyBundleBackedSandboxConnectorBindings()
    {
        using var temp = new TemporaryGatewayStateDirectory();
        IConfiguration configuration = CreateConfiguration(temp.SqlitePath);
        var database = new KautukaDatabase(configuration);
        database.EnsureReady();

        PlatformConfiguration platformConfiguration = CreatePlatformConfiguration(
            mcpServers:
            [
                new McpServerDefinition
                {
                    Id = "bundle-mcp",
                    Name = "Bundle MCP",
                    Transport = McpTransport.Container,
                    ContainerImage = "ghcr.io/example/bundle:latest",
                    BundlePath = "/tmp/bundle.mcpb",
                    ServerType = "uv",
                    Enabled = true,
                },
                new McpServerDefinition
                {
                    Id = "plain-container",
                    Name = "Plain Container",
                    Transport = McpTransport.Container,
                    ContainerImage = "ghcr.io/example/plain:latest",
                    Enabled = true,
                },
            ]);
        platformConfiguration.Agents.Add(new AgentDefinition
        {
            Id = "agent-alpha",
            Scope = GatewayOwnershipScopes.User,
            Enabled = true,
            Name = "Agent Alpha",
            ModelId = "model-alpha",
            SystemPrompt = "You are alpha.",
            McpBindings =
            [
                new AgentMcpBindingDefinition
                {
                    ConnectorId = "bundle-mcp",
                    Context = "AgentContext",
                },
                new AgentMcpBindingDefinition
                {
                    ConnectorId = "plain-container",
                    Context = "AgentContext",
                },
            ],
        });

        var resolver = new WorkspaceRuntimeContextResolver(
            new StaticPlatformConfigurationProvider(platformConfiguration),
            configuration,
            database);

        WorkspaceDesiredStatePayload payload = resolver.ResolveDesiredState();

        WorkspaceSandboxConnectorBindingsPayload binding = Assert.Single(payload.SandboxConnectorBindings, item =>
            item.ScopeId.Contains("agent-alpha", StringComparison.Ordinal));
        string scopedBundleConnectorId = Assert.Single(binding.ConnectorIds);
        Assert.Equal("system::bundle-mcp", scopedBundleConnectorId);
    }

    [Fact]
    public void ResolveDesiredStateDoesNotMountAnotherUsersConnectorBundle()
    {
        using var temp = new TemporaryGatewayStateDirectory();
        IConfiguration configuration = CreateConfiguration(temp.SqlitePath);
        var database = new KautukaDatabase(configuration);
        database.EnsureReady();
        var users = new SqliteGatewayUserCommandRepository(database);
        users.Upsert(new GatewayUser("user-a", "a", "Testing1", "user", true, null, null));
        users.Upsert(new GatewayUser("user-b", "b", "Testing1", "user", true, null, null));

        PlatformConfiguration platformConfiguration = CreatePlatformConfiguration(
            mcpServers:
            [
                new McpServerDefinition
                {
                    Id = "own-bundle",
                    Name = "Own Bundle",
                    Transport = McpTransport.Container,
                    ContainerImage = "ghcr.io/example/own:latest",
                    BundlePath = "/tmp/own.mcpb",
                    ServerType = "uv",
                    Enabled = true,
                    Scope = GatewayOwnershipScopes.User,
                    OwnerUserId = "user-a",
                },
                new McpServerDefinition
                {
                    Id = "foreign-bundle",
                    Name = "Foreign Bundle",
                    Transport = McpTransport.Container,
                    ContainerImage = "ghcr.io/example/foreign:latest",
                    BundlePath = "/tmp/foreign.mcpb",
                    ServerType = "uv",
                    Enabled = true,
                    Scope = GatewayOwnershipScopes.User,
                    OwnerUserId = "user-b",
                },
            ]);
        platformConfiguration.Agents.Add(new AgentDefinition
        {
            Id = "agent-alpha",
            Scope = GatewayOwnershipScopes.User,
            OwnerUserId = "user-a",
            Enabled = true,
            Name = "Agent Alpha",
            ModelId = "model-alpha",
            SystemPrompt = "You are alpha.",
            McpBindings =
            [
                new AgentMcpBindingDefinition { ConnectorId = "own-bundle", Context = "AgentContext" },
                new AgentMcpBindingDefinition { ConnectorId = "foreign-bundle", Context = "AgentContext" },
            ],
        });

        var resolver = new WorkspaceRuntimeContextResolver(
            new StaticPlatformConfigurationProvider(platformConfiguration),
            configuration,
            database);

        WorkspaceDesiredStatePayload payload = resolver.ResolveDesiredState();
        WorkspaceSandboxConnectorBindingsPayload binding = Assert.Single(payload.SandboxConnectorBindings, item =>
            item.ScopeId.Contains("agent-alpha", StringComparison.Ordinal));

        Assert.Equal(["user-a::own-bundle"], binding.ConnectorIds);
    }

    [Fact]
    public void ResolveContainerProbeEnvironmentUsesGatewayProxySettings()
    {
        using var temp = new TemporaryGatewayStateDirectory();
        IConfiguration configuration = CreateConfiguration(
            temp.SqlitePath,
            additionalSettings: new Dictionary<string, string?>
            {
                ["ManagerProxy:NpmRegistryUrl"] = "https://gateway.example.invalid/npm/",
                ["ManagerProxy:UvIndexUrl"] = "https://gateway.example.invalid/uv/simple",
                ["ManagerProxy:PipIndexUrl"] = "https://gateway.example.invalid/pip/simple",
            });
        var database = new KautukaDatabase(configuration);
        database.EnsureReady();

        PlatformConfiguration platformConfiguration = CreatePlatformConfiguration(
            sandboxEnvironment: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["NPM_CONFIG_REGISTRY"] = "https://sandbox.example.invalid/npm/",
                ["UV_INDEX_URL"] = "https://sandbox.example.invalid/uv/simple",
                ["PIP_INDEX_URL"] = "https://sandbox.example.invalid/pip/simple",
                ["HTTPS_PROXY"] = "http://shared-proxy.example.invalid:3128",
            });

        var resolver = new WorkspaceRuntimeContextResolver(
            new StaticPlatformConfigurationProvider(platformConfiguration),
            configuration,
            database);

        IReadOnlyDictionary<string, string> environment = resolver.ResolveContainerProbeEnvironment(
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["SPLOSE_ROOT"] = "/opt/kautuka/mcpb/config",
                ["NPM_CONFIG_REGISTRY"] = "https://override.example.invalid/npm/",
                ["HTTPS_PROXY"] = "http://attacker.example.invalid:3128",
                ["LD_PRELOAD"] = "/tmp/lib.so",
                ["PATH"] = "/tmp",
                ["CONTAINERS_CONF"] = "/tmp/containers.conf",
                ["CONTAINERS_HELPER_BINARY_DIR"] = "/tmp/helpers",
                ["STORAGE_OPTS"] = "overlay.mount_program=/tmp/mount",
                ["HOME"] = "/tmp/home",
                ["CONTAINER_HOST"] = "unix:///tmp/podman.sock",
            });

        Assert.Equal("https://gateway.example.invalid/npm/", environment["NPM_CONFIG_REGISTRY"]);
        Assert.Equal("https://gateway.example.invalid/uv/simple", environment["UV_INDEX_URL"]);
        Assert.Equal("https://gateway.example.invalid/pip/simple", environment["PIP_INDEX_URL"]);
        Assert.False(environment.ContainsKey("PIP_TRUSTED_HOST"));
        Assert.Equal("http://shared-proxy.example.invalid:3128", environment["HTTPS_PROXY"]);
        Assert.Equal("/opt/kautuka/mcpb/config", environment["SPLOSE_ROOT"]);
        Assert.False(environment.ContainsKey("LD_PRELOAD"));
        Assert.False(environment.ContainsKey("PATH"));
        Assert.False(environment.ContainsKey("CONTAINERS_CONF"));
        Assert.False(environment.ContainsKey("CONTAINERS_HELPER_BINARY_DIR"));
        Assert.False(environment.ContainsKey("STORAGE_OPTS"));
        Assert.False(environment.ContainsKey("HOME"));
        Assert.False(environment.ContainsKey("CONTAINER_HOST"));
    }

    [Fact]
    public void SaveAgentPublishesDesiredState()
    {
        using var temp = new TemporaryGatewayStateDirectory();
        GatewayState state = CreateState(temp.SqlitePath);
        var topologyWriter = new RecordingTopologyWriter();
        var workspaceTool = new NoOpWorkspaceRuntimeTool();
        var publisher = new RecordingDesiredStatePublisher();
        var store = new GatewayCatalogStore(
            state,
            new SqliteAgentDefinitionStore(CreateConfiguration(temp.SqlitePath)),
            topologyWriter,
            workspaceTool,
            new NoOpSkillPackageService(),
            publisher,
            new NoOpContinuationStore(),
            new NoOpConversationAssetStore());

        GatewayAgent agent = store.SaveAgent(
            "agent-alpha",
            new GatewayAgentUpsertRequest
            {
                Name = "Alpha Agent",
                ModelId = "model-alpha",
                SystemPrompt = "You are alpha.",
                Session = new GatewayAgentSessionPayload
                {
                    HistoryEnabled = true,
                    MaxHistoryRuns = 5,
                    SummariesEnabled = true,
                },
                Sandbox = new GatewayAgentSandboxPayload
                {
                    Enabled = true,
                    AllowedLanguages = ["bash"],
                    MaxMemory = "2g",
                    MaxTimeout = 120,
                },
                ApprovalConfig = new GatewayApprovalConfigPayload(),
                ToolExposure = new GatewayAgentToolExposurePayload(),
            },
            "owner-user");

        Assert.Equal("agent-alpha", agent.Id);
        Assert.Equal("disabled", agent.SuperExecMode);
        Assert.Contains("agent-save", publisher.Reasons);
        Assert.Contains(topologyWriter.SavedAgents, static item => item.Id == "agent-alpha");
    }

    [Fact]
    public void SaveAgentDefaultsBlankSuperExecModeToDisabled()
    {
        using var temp = new TemporaryGatewayStateDirectory();
        GatewayState state = CreateState(temp.SqlitePath);
        var store = new GatewayCatalogStore(
            state,
            new SqliteAgentDefinitionStore(CreateConfiguration(temp.SqlitePath)),
            new RecordingTopologyWriter(),
            new NoOpWorkspaceRuntimeTool(),
            new NoOpSkillPackageService(),
            new RecordingDesiredStatePublisher(),
            new NoOpContinuationStore(),
            new NoOpConversationAssetStore());

        GatewayAgent agent = store.SaveAgent(
            "agent-alpha",
            new GatewayAgentUpsertRequest
            {
                Name = "Alpha Agent",
                ModelId = "model-alpha",
                SuperExecMode = "   ",
                ApprovalConfig = new GatewayApprovalConfigPayload(),
                ToolExposure = new GatewayAgentToolExposurePayload(),
            },
            "owner-user");

        Assert.Equal("disabled", agent.SuperExecMode);
    }

    [Fact]
    public void SaveAgentPersistsThinRegistryRowToSharedDatabase()
    {
        using var temp = new TemporaryGatewayStateDirectory();
        IConfiguration configuration = CreateConfiguration(temp.SqlitePath);
        GatewayState state = CreateState(temp.SqlitePath, configuration);
        var database = new KautukaDatabase(configuration);
        var topologyWriter = new RecordingTopologyWriter();
        var workspaceTool = new NoOpWorkspaceRuntimeTool();
        var publisher = new RecordingDesiredStatePublisher();
        var store = new GatewayCatalogStore(
            state,
            new SqliteAgentDefinitionStore(configuration, new KautukaDatabase(configuration)),
            topologyWriter,
            workspaceTool,
            new NoOpSkillPackageService(),
            publisher,
            new NoOpContinuationStore(),
            new NoOpConversationAssetStore());

        _ = store.SaveAgent(
            "agent-alpha",
            new GatewayAgentUpsertRequest
            {
                Name = "Alpha Agent",
                ModelId = "model-alpha",
                SystemPrompt = "You are alpha.",
                ApprovalConfig = new GatewayApprovalConfigPayload(),
                ToolExposure = new GatewayAgentToolExposurePayload(),
            },
            "owner-user");

        using var connection = new SqliteConnection($"Data Source={KautukaDatabase.ResolvePath(configuration)}");
        connection.Open();

        using var columnCommand = connection.CreateCommand();
        columnCommand.CommandText = "SELECT name FROM pragma_table_info('agents') ORDER BY cid";
        List<string> columns = [];
        using (SqliteDataReader reader = columnCommand.ExecuteReader())
        {
            while (reader.Read())
            {
                columns.Add(reader.GetString(0));
            }
        }

        Assert.Contains("storage_locator_key", columns);
        Assert.DoesNotContain("name", columns);
        Assert.DoesNotContain("model_id", columns);

        using var rowCommand = connection.CreateCommand();
        rowCommand.CommandText = "SELECT scope, enabled, owner_user_id, storage_locator_key FROM agents WHERE id = $id";
        rowCommand.Parameters.AddWithValue("$id", "agent-alpha");
        using SqliteDataReader rowReader = rowCommand.ExecuteReader();
        Assert.True(rowReader.Read());
        Assert.Equal(GatewayOwnershipScopes.User, rowReader.GetString(0));
        Assert.Equal(1L, rowReader.GetInt64(1));
        Assert.Equal("owner-user", rowReader.GetString(2));
        Assert.False(string.IsNullOrWhiteSpace(rowReader.GetString(3)));
    }

    [Fact]
    public void InviteUserCommitsAuthoritativeRowBeforeProjectionAndReconstructsFromDatabase()
    {
        using var temp = new TemporaryGatewayStateDirectory();
        IConfiguration configuration = CreateConfiguration(temp.SqlitePath);
        GatewayState state = CreateState(temp.SqlitePath, configuration);
        var repository = new SqliteGatewayUserCommandRepository(new KautukaDatabase(configuration));
        var store = new GatewayUserStore(state, new NoOpTokenStore(), repository);
        using JsonDocument body = JsonDocument.Parse("""{"username":"reader","password":"password-123","role":"user"}""");

        GatewayUserListItem? invited = store.InviteUser(body.RootElement, out string? error);

        Assert.Null(error);
        Assert.NotNull(invited);
        GatewayState reconstructed = CreateState(temp.SqlitePath, configuration);
        Assert.True(reconstructed.Users.TryGetValue("reader", out GatewayUser? persisted));
        Assert.Equal(invited!.Id, persisted!.Id);
    }

    [Fact]
    public void InviteUserDoesNotMutateProjectionWhenAuthoritativeWriteFails()
    {
        using var temp = new TemporaryGatewayStateDirectory();
        IConfiguration configuration = CreateConfiguration(temp.SqlitePath);
        GatewayState state = CreateState(temp.SqlitePath, configuration);
        var store = new GatewayUserStore(state, new NoOpTokenStore(), new FailingUserCommandRepository());
        using JsonDocument body = JsonDocument.Parse("""{"username":"reader","password":"password-123","role":"user"}""");

        Assert.Throws<InvalidOperationException>(() => store.InviteUser(body.RootElement, out _));
        Assert.False(state.Users.ContainsKey("reader"));
    }

    [Fact]
    public async Task SaveAgentAsyncAppendsDefinitionVersionAndKeepsReadableSettingSnapshots()
    {
        using var temp = new TemporaryGatewayStateDirectory();
        IConfiguration configuration = CreateConfiguration(temp.SqlitePath);
        var database = new KautukaDatabase(configuration);
        database.EnsureReady();

        var definitionStore = new SqliteAgentDefinitionStore(configuration);
        GatewayState state = GatewayState.FromDatabase(
            database,
            NullLogger<GatewayState>.Instance,
            new GatewayMemoryStore(),
            definitionStore,
            configuration,
            new NoOpWorkspaceRuntimeTool());
        state.Models["model-alpha"] = new GatewayModel("model-alpha", "provider-alpha", "Alpha Model", new JsonObject(), null, DateTimeOffset.UtcNow.ToString("O"));
        state.Models["model-beta"] = new GatewayModel("model-beta", "provider-beta", "Beta Model", new JsonObject(), null, DateTimeOffset.UtcNow.ToString("O"));
        state.McpServers["mcp-shell"] = new GatewayMcpServer(
            "mcp-shell",
            "Shell Tools",
            string.Empty,
            string.Empty,
            null,
            "container",
            null,
            null,
            null,
            [],
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            true,
            false,
            DateTimeOffset.UtcNow.ToString("O"),
            DateTimeOffset.UtcNow.ToString("O"),
            GatewayOwnershipScopes.User,
            "owner-user");
        state.Skills["skill-triage"] = new GatewaySkill(
            "skill-triage",
            "Triage Skill",
            string.Empty,
            "hash",
            "ready",
            "/tmp/skill-triage",
            false,
            true,
            true,
            [],
            DateTimeOffset.UtcNow.ToString("O"),
            new Dictionary<string, string>(),
            GatewayOwnershipScopes.User,
            "owner-user");
        var topologyWriter = new SqliteTopologyConfigurationWriter(
            database,
            new SqlitePlatformConfigurationProvider(database, definitionStore));
        var store = new GatewayCatalogStore(
            state,
            definitionStore,
            topologyWriter,
            new NoOpWorkspaceRuntimeTool(),
            new NoOpSkillPackageService(),
            new RecordingDesiredStatePublisher(),
            new NoOpContinuationStore(),
            new NoOpConversationAssetStore());

        _ = await store.SaveAgentAsync(
            "agent-alpha",
            new GatewayAgentUpsertRequest
            {
                Name = "Alpha Agent",
                ModelId = "model-alpha",
                SystemPrompt = "You are alpha.",
                ApprovalConfig = new GatewayApprovalConfigPayload(),
                ToolExposure = new GatewayAgentToolExposurePayload(),
            },
            "owner-user",
            TestContext.Current.CancellationToken).ConfigureAwait(true);

        _ = await store.SaveAgentAsync(
            "agent-alpha",
            new GatewayAgentUpsertRequest
            {
                Name = "Alpha Agent v2",
                ModelId = "model-beta",
                SystemPrompt = "You are alpha v2.",
                McpBindings =
                [
                    new GatewayAgentMcpBindingPayload
                    {
                        ConnectorId = "mcp-shell",
                        Context = "OwnContext",
                    },
                ],
                SkillAttachments = [new GatewayAgentSkillAttachmentPayload { SkillId = "skill-triage", Mode = "trusted" }],
                Persona = new JsonObject
                {
                    ["temperature"] = 0.5,
                    ["style"] = "professional",
                    ["tone"] = "neutral",
                    ["verbosity"] = "concise",
                },
                ApprovalConfig = new GatewayApprovalConfigPayload(),
                ToolExposure = new GatewayAgentToolExposurePayload
                {
                    Description = "Expose selected tools",
                },
            },
            "owner-user",
            TestContext.Current.CancellationToken).ConfigureAwait(true);

        AgentDefinition? latest = definitionStore.TryLoadLatestDefinition(definitionStore.GetStorageLocatorKey("agent-alpha"));
        Assert.NotNull(latest);
        Assert.Equal("Alpha Agent v2", latest!.Name);
        Assert.Equal("model-beta", latest.ModelId);
        Assert.Equal("You are alpha v2.", latest.SystemPrompt);

        string agentDatabasePath = Path.Combine(
            KautukaHostPathPolicy.ResolveSharedStateRoot(configuration),
            "agents",
            "agent-alpha",
            "agent.db");
        using var connection = new SqliteConnection($"Data Source={agentDatabasePath}");
        await connection.OpenAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);

        using var versionCommand = connection.CreateCommand();
        versionCommand.CommandText = "SELECT COUNT(*) FROM agent_definition_versions";
        object? versionCount = await versionCommand.ExecuteScalarAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);
        Assert.Equal(2L, Convert.ToInt64(versionCount, CultureInfo.InvariantCulture));

        using var currentCommand = connection.CreateCommand();
        currentCommand.CommandText = "SELECT version FROM agent_definition_current WHERE singleton_id = 1";
        object? currentVersion = await currentCommand.ExecuteScalarAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);
        Assert.Equal(2L, Convert.ToInt64(currentVersion, CultureInfo.InvariantCulture));

        using var settingsCommand = connection.CreateCommand();
        settingsCommand.CommandText = "SELECT setting_id, setting_name, value_json FROM agent_definition_settings WHERE version = 2 ORDER BY setting_id";
        using SqliteDataReader reader = await settingsCommand.ExecuteReaderAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);
        List<(string Id, string Name, string ValueJson)> settings = [];
        while (await reader.ReadAsync(TestContext.Current.CancellationToken).ConfigureAwait(true))
        {
            settings.Add((reader.GetString(0), reader.GetString(1), reader.GetString(2)));
        }

        Assert.Contains(settings, static item => item.Id == "name" && item.Name == "Name" && item.ValueJson == "\"Alpha Agent v2\"");
        Assert.Contains(settings, static item => item.Id == "model_id" && item.Name == "Model" && item.ValueJson.Contains("\"id\":\"model-beta\"", StringComparison.Ordinal) && item.ValueJson.Contains("\"name\":\"Beta Model\"", StringComparison.Ordinal));
        Assert.Contains(settings, static item => item.Id == "mcp_bindings" && item.Name == "MCP bindings" && item.ValueJson.Contains("\"connectorId\":\"mcp-shell\"", StringComparison.Ordinal) && item.ValueJson.Contains("\"connectorName\":\"Shell Tools\"", StringComparison.Ordinal) && item.ValueJson.Contains("\"context\":\"OwnContext\"", StringComparison.Ordinal));
        Assert.Contains(settings, static item => item.Id == "skill_attachments" && item.Name == "Skill attachments" && item.ValueJson.Contains("\"skillId\":\"skill-triage\"", StringComparison.Ordinal) && item.ValueJson.Contains("\"skillName\":\"Triage Skill\"", StringComparison.Ordinal));
        Assert.Contains(settings, static item => item.Id == "persona" && item.Name == "Persona" && item.ValueJson.Contains("\"temperature\":0.5", StringComparison.Ordinal) && item.ValueJson.Contains("\"style\":\"professional\"", StringComparison.Ordinal));
        Assert.Contains(settings, static item => item.Id == "tool_exposure" && item.Name == "Tool exposure" && item.ValueJson.Contains("Expose selected tools", StringComparison.Ordinal));

        IReadOnlyList<AgentDefinitionRevision> revisions = definitionStore.ListDefinitionRevisions(
            definitionStore.GetStorageLocatorKey("agent-alpha"));
        Assert.Equal(2, revisions.Count);
        Assert.Equal(2, revisions[0].Revision);
        Assert.Equal(1, revisions[1].Revision);
        Assert.NotEmpty(definitionStore.ListDefinitionSettings(definitionStore.GetStorageLocatorKey("agent-alpha"), 1));
        Assert.NotEmpty(definitionStore.ListDefinitionSettings(definitionStore.GetStorageLocatorKey("agent-alpha"), 2));

        GatewayAgentRevisionsResponse? listed = store.TryListAgentRevisions("agent-alpha");
        Assert.NotNull(listed);
        Assert.Equal(2, listed.CurrentRevision);
        GatewayAgentRevisionListItem revisionTwo = Assert.Single(listed.Revisions, static item => item.Revision == 2);
        Assert.Contains("name", revisionTwo.ChangedSettingIds);
        Assert.Contains("Name", revisionTwo.ChangedSettingNames);
        Assert.DoesNotContain("scope", revisionTwo.ChangedSettingIds);

        GatewayAgentRevisionDiffResponse? initial = store.TryGetAgentRevisionDiff("agent-alpha", 1, fromRevision: null);
        Assert.NotNull(initial);
        Assert.Null(initial.FromRevision);
        Assert.Empty(initial.Changes);

        GatewayAgentRevisionDiffResponse? diff = store.TryGetAgentRevisionDiff("agent-alpha", 2, fromRevision: null);
        Assert.NotNull(diff);
        Assert.Equal(1, diff.FromRevision);
        Assert.Contains(diff.Changes, static change => change.SettingId == "name");
        Assert.Contains(diff.Changes, static change => change.SettingId == "model_id");
        Assert.Contains(diff.Changes, static change => change.SettingId == "system_prompt");
        Assert.Contains(diff.Changes, static change => change.SettingId == "mcp_bindings");
        Assert.Contains(diff.Changes, static change => change.SettingId == "skill_attachments");
        Assert.Contains(diff.Changes, static change => change.SettingId == "persona");
        Assert.Contains(diff.Changes, static change => change.SettingId == "tool_exposure");
        Assert.DoesNotContain(diff.Changes, static change => change.SettingId == "scope");
    }

    [Fact]
    public async Task DeleteSkillPersistsAgentAttachmentRemovalAcrossRestart()
    {
        using var temp = new TemporaryGatewayStateDirectory();
        IConfiguration configuration = CreateConfiguration(temp.SqlitePath);
        var database = new KautukaDatabase(configuration);
        database.EnsureReady();
        var definitionStore = new SqliteAgentDefinitionStore(configuration, database);
        GatewayState state = GatewayState.FromDatabase(
            database,
            NullLogger<GatewayState>.Instance,
            new GatewayMemoryStore(),
            definitionStore,
            configuration,
            new NoOpWorkspaceRuntimeTool());
        var topologyWriter = new SqliteTopologyConfigurationWriter(
            database,
            new SqlitePlatformConfigurationProvider(database, definitionStore));
        var store = new GatewayCatalogStore(
            state,
            definitionStore,
            topologyWriter,
            new NoOpWorkspaceRuntimeTool(),
            new NoOpSkillPackageService(),
            new RecordingDesiredStatePublisher(),
            new NoOpContinuationStore(),
            new NoOpConversationAssetStore());

        using JsonDocument skillBody = JsonDocument.Parse(
            """{"name":"Restart Skill","packageHash":"hash","status":"ready","enabled":true}""");
        _ = store.SaveSkill("skill-restart", skillBody.RootElement, GatewayOwnershipScopes.User, "owner-user");
        _ = await store.SaveAgentAsync(
            "agent-skill-restart",
            new GatewayAgentUpsertRequest
            {
                Name = "Skill Restart Agent",
                ModelId = "model-alpha",
                SystemPrompt = "prompt",
                Sandbox = new GatewayAgentSandboxPayload { Enabled = true },
                SkillAttachments =
                [
                    new GatewayAgentSkillAttachmentPayload { SkillId = "skill-restart", Mode = "trusted" },
                ],
                ApprovalConfig = new GatewayApprovalConfigPayload(),
                ToolExposure = new GatewayAgentToolExposurePayload(),
            },
            "owner-user",
            TestContext.Current.CancellationToken).ConfigureAwait(true);

        Assert.True(store.DeleteSkill("skill-restart"));

        GatewayState reloaded = GatewayState.FromDatabase(
            database,
            NullLogger<GatewayState>.Instance,
            new GatewayMemoryStore(),
            new SqliteAgentDefinitionStore(configuration, database),
            configuration,
            new NoOpWorkspaceRuntimeTool());
        GatewayAgent reloadedAgent = Assert.IsType<GatewayAgent>(reloaded.Agents["agent-skill-restart"]);
        Assert.Empty(reloadedAgent.SkillAttachments);
        Assert.False(reloaded.Skills.ContainsKey("skill-restart"));
    }

    [Fact]
    public void SaveSkillRejectsACaseVariantOfAnExistingId()
    {
        using var temp = new TemporaryGatewayStateDirectory();
        GatewayState state = CreateState(temp.SqlitePath);
        var store = new GatewayCatalogStore(
            state,
            new SqliteAgentDefinitionStore(CreateConfiguration(temp.SqlitePath)),
            new RecordingTopologyWriter(),
            new NoOpWorkspaceRuntimeTool(),
            new NoOpSkillPackageService(),
            new RecordingDesiredStatePublisher(),
            new NoOpContinuationStore(),
            new NoOpConversationAssetStore());
        using JsonDocument body = JsonDocument.Parse(
            """{"name":"Audit","packageHash":"hash","status":"ready","enabled":true}""");

        GatewaySkill saved = store.SaveSkill("AuditSkill", body.RootElement, GatewayOwnershipScopes.System, null);
        InvalidOperationException error = Assert.Throws<InvalidOperationException>(() =>
            store.SaveSkill("auditskill", body.RootElement, GatewayOwnershipScopes.System, null));

        Assert.Equal("AuditSkill", saved.Id);
        Assert.Contains("auditskill", error.Message, StringComparison.Ordinal);
        Assert.False(state.Skills.ContainsKey("auditskill"));
    }

    [Fact]
    public async Task ConcurrentSkillWritersCannotPersistCaseVariantIds()
    {
        using var temp = new TemporaryGatewayStateDirectory();
        var database = new KautukaDatabase(CreateConfiguration(temp.SqlitePath));
        database.EnsureReady();
        System.Reflection.MethodInfo upsert = typeof(SqliteTopologyConfigurationWriter)
            .GetMethod("UpsertSkill", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<bool>[] writes = Enumerable.Range(0, 8).Select(index => Task.Run(async () =>
        {
            await start.Task;
            var writer = new SqliteTopologyConfigurationWriter(database, null!);
            var skill = new SkillDefinition { SkillId = index % 2 == 0 ? "AuditRace" : "auditrace", Name = "Race", PackageHash = "test", Status = "ready" };
            try
            {
                upsert.Invoke(writer, [skill]);
                return true;
            }
            catch (System.Reflection.TargetInvocationException ex) when (ex.InnerException is InvalidOperationException)
            {
                return false;
            }
        }, TestContext.Current.CancellationToken)).ToArray();
        start.SetResult();
        bool[] outcomes = await Task.WhenAll(writes);

        Assert.Equal(4, outcomes.Count(static success => success));
        using var connection = new Microsoft.Data.Sqlite.SqliteConnection(database.ConnectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        using Microsoft.Data.Sqlite.SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM skills WHERE id IN ('AuditRace', 'auditrace')";
        Assert.Equal(1L, await command.ExecuteScalarAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public void DeleteAgentPublishesDesiredState()
    {
        using var temp = new TemporaryGatewayStateDirectory();
        GatewayState state = CreateState(temp.SqlitePath);
        state.Agents["agent-alpha"] = new GatewayAgent(
            "agent-alpha",
            "Alpha Agent",
            "model-alpha",
            "prompt",
            "trusted",
            new JsonObject(),
            new JsonObject(),
            new JsonObject(),
            [],
            "owner-user",
            DateTimeOffset.UtcNow.ToString("O"),
            new JsonObject(),
            new JsonObject());

        var topologyWriter = new RecordingTopologyWriter();
        var workspaceTool = new NoOpWorkspaceRuntimeTool();
        var publisher = new RecordingDesiredStatePublisher();
        var store = new GatewayCatalogStore(
            state,
            new SqliteAgentDefinitionStore(CreateConfiguration(temp.SqlitePath)),
            topologyWriter,
            workspaceTool,
            new NoOpSkillPackageService(),
            publisher,
            new NoOpContinuationStore(),
            new NoOpConversationAssetStore());

        bool removed = store.DeleteAgent("agent-alpha");

        Assert.True(removed);
        Assert.Contains("agent-delete", publisher.Reasons);
        Assert.Contains("agent-alpha", topologyWriter.DeletedAgents);
    }

    [Fact]
    public void DeleteAgentIsBlockedWhileAnotherAgentUsesItAsATool()
    {
        using var temp = new TemporaryGatewayStateDirectory();
        GatewayState state = CreateState(temp.SqlitePath);
        state.Agents["agent-child"] = new GatewayAgent(
            "agent-child",
            "Child Agent",
            "model-alpha",
            "prompt",
            "trusted",
            new JsonObject(),
            new JsonObject(),
            new JsonObject(),
            [],
            "owner-user",
            DateTimeOffset.UtcNow.ToString("O"),
            new JsonObject(),
            new JsonObject());
        state.Agents["agent-parent"] = new GatewayAgent(
            "agent-parent",
            "Parent Agent",
            "model-alpha",
            "prompt",
            "trusted",
            new JsonObject(),
            new JsonObject(),
            new JsonObject(),
            [],
            "owner-user",
            DateTimeOffset.UtcNow.ToString("O"),
            new JsonObject
            {
                ["agentToolIds"] = new JsonArray("agent-child"),
            },
            new JsonObject(),
            Scope: GatewayOwnershipScopes.User);

        var topologyWriter = new RecordingTopologyWriter();
        var publisher = new RecordingDesiredStatePublisher();
        var store = new GatewayCatalogStore(
            state,
            new SqliteAgentDefinitionStore(CreateConfiguration(temp.SqlitePath)),
            topologyWriter,
            new NoOpWorkspaceRuntimeTool(),
            new NoOpSkillPackageService(),
            publisher,
            new NoOpContinuationStore(),
            new NoOpConversationAssetStore());

        bool removed = store.DeleteAgent("agent-child");

        Assert.False(removed);
        Assert.True(state.Agents.ContainsKey("agent-child"));
        Assert.DoesNotContain("agent-delete", publisher.Reasons);
        Assert.DoesNotContain("agent-child", topologyWriter.DeletedAgents);
    }

    [Fact]
    public async Task DeleteAgentRemovesThinRegistryRowAndAgentStore()
    {
        using var temp = new TemporaryGatewayStateDirectory();
        IConfiguration configuration = CreateConfiguration(temp.SqlitePath);
        var database = new KautukaDatabase(configuration);
        database.EnsureReady();

        var definitionStore = new SqliteAgentDefinitionStore(configuration, database);
        GatewayState state = GatewayState.FromDatabase(
            database,
            NullLogger<GatewayState>.Instance,
            new GatewayMemoryStore(),
            definitionStore,
            configuration,
            new NoOpWorkspaceRuntimeTool());
        var topologyWriter = new SqliteTopologyConfigurationWriter(
            database,
            new SqlitePlatformConfigurationProvider(database, definitionStore));
        var store = new GatewayCatalogStore(
            state,
            definitionStore,
            topologyWriter,
            new NoOpWorkspaceRuntimeTool(),
            new NoOpSkillPackageService(),
            new RecordingDesiredStatePublisher(),
            new NoOpContinuationStore(),
            new NoOpConversationAssetStore());

        _ = await store.SaveAgentAsync(
            "agent-delete",
            new GatewayAgentUpsertRequest
            {
                Name = "Delete Me",
                ModelId = "model-alpha",
                SystemPrompt = "prompt",
                ApprovalConfig = new GatewayApprovalConfigPayload(),
                ToolExposure = new GatewayAgentToolExposurePayload(),
            },
            "owner-user",
            TestContext.Current.CancellationToken).ConfigureAwait(true);

        string agentDirectoryPath = Path.Combine(
            KautukaHostPathPolicy.ResolveSharedStateRoot(configuration),
            "agents",
            "agent-delete");
        Assert.True(Directory.Exists(agentDirectoryPath));

        bool removed = await store.DeleteAgentAsync("agent-delete", TestContext.Current.CancellationToken).ConfigureAwait(true);

        Assert.True(removed);
        Assert.False(Directory.Exists(agentDirectoryPath));

        using var connection = new SqliteConnection($"Data Source={KautukaDatabase.ResolvePath(configuration)}");
        await connection.OpenAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM agents WHERE id = $id";
        command.Parameters.AddWithValue("$id", "agent-delete");
        object? count = await command.ExecuteScalarAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);
        Assert.Equal(0L, Convert.ToInt64(count, CultureInfo.InvariantCulture));
        Assert.Null(definitionStore.TryLoadLatestDefinition(definitionStore.GetStorageLocatorKey("agent-delete")));
    }

    [Fact]
    public async Task SaveAgentAsyncRejectsAgentSandboxBindingWhenSandboxDisabled()
    {
        using var temp = new TemporaryGatewayStateDirectory();
        IConfiguration configuration = CreateConfiguration(temp.SqlitePath);
        var database = new KautukaDatabase(configuration);
        database.EnsureReady();

        var definitionStore = new SqliteAgentDefinitionStore(configuration, database);
        GatewayState state = GatewayState.FromDatabase(
            database,
            NullLogger<GatewayState>.Instance,
            new GatewayMemoryStore(),
            definitionStore,
            configuration,
            new NoOpWorkspaceRuntimeTool());
        var topologyWriter = new SqliteTopologyConfigurationWriter(
            database,
            new SqlitePlatformConfigurationProvider(database, definitionStore));
        var store = new GatewayCatalogStore(
            state,
            definitionStore,
            topologyWriter,
            new NoOpWorkspaceRuntimeTool(),
            new NoOpSkillPackageService(),
            new RecordingDesiredStatePublisher(),
            new NoOpContinuationStore(),
            new NoOpConversationAssetStore());

        ArgumentException ex = await Assert.ThrowsAsync<ArgumentException>(() => store.SaveAgentAsync(
            "agent-disabled-sandbox",
            new GatewayAgentUpsertRequest
            {
                Name = "Disabled Sandbox Agent",
                ModelId = "model-alpha",
                SystemPrompt = "prompt",
                Sandbox = new GatewayAgentSandboxPayload
                {
                    Enabled = false,
                },
                McpBindings =
                [
                    new GatewayAgentMcpBindingPayload
                    {
                        ConnectorId = "bundle-mcp",
                        Context = "AgentContext",
                    },
                ],
                ApprovalConfig = new GatewayApprovalConfigPayload(),
                ToolExposure = new GatewayAgentToolExposurePayload(),
            },
            "owner-user",
            TestContext.Current.CancellationToken));

        Assert.Equal("Agent sandbox execution bindings require sandbox to be enabled for that agent.", ex.Message);
    }

    [Fact]
    public async Task SaveAgentAsyncRejectsActiveSkillAttachmentWhenSandboxDisabled()
    {
        using var temp = new TemporaryGatewayStateDirectory();
        IConfiguration configuration = CreateConfiguration(temp.SqlitePath);
        var database = new KautukaDatabase(configuration);
        database.EnsureReady();

        var definitionStore = new SqliteAgentDefinitionStore(configuration, database);
        GatewayState state = GatewayState.FromDatabase(
            database,
            NullLogger<GatewayState>.Instance,
            new GatewayMemoryStore(),
            definitionStore,
            configuration,
            new NoOpWorkspaceRuntimeTool());
        var topologyWriter = new SqliteTopologyConfigurationWriter(
            database,
            new SqlitePlatformConfigurationProvider(database, definitionStore));
        var store = new GatewayCatalogStore(
            state,
            definitionStore,
            topologyWriter,
            new NoOpWorkspaceRuntimeTool(),
            new NoOpSkillPackageService(),
            new RecordingDesiredStatePublisher(),
            new NoOpContinuationStore(),
            new NoOpConversationAssetStore());

        ArgumentException ex = await Assert.ThrowsAsync<ArgumentException>(() => store.SaveAgentAsync(
            "agent-disabled-sandbox-skills",
            new GatewayAgentUpsertRequest
            {
                Name = "Disabled Sandbox Skills Agent",
                ModelId = "model-alpha",
                SystemPrompt = "prompt",
                Sandbox = new GatewayAgentSandboxPayload
                {
                    Enabled = false,
                },
                SkillAttachments =
                [
                    new GatewayAgentSkillAttachmentPayload
                    {
                        SkillId = "skill-triage",
                        Mode = "trusted",
                    },
                ],
                ApprovalConfig = new GatewayApprovalConfigPayload(),
                ToolExposure = new GatewayAgentToolExposurePayload(),
            },
            "owner-user",
            TestContext.Current.CancellationToken));

        Assert.Equal("Active skill attachments require sandbox to be enabled for that agent.", ex.Message);
    }

    [Fact]
    public void TryLoadLatestDefinitionRejectsLegacyAgentStoreWithoutMutation()
    {
        using var temp = new TemporaryGatewayStateDirectory();
        IConfiguration configuration = CreateConfiguration(temp.SqlitePath);
        var store = new SqliteAgentDefinitionStore(configuration);
        string storageLocatorKey = store.GetStorageLocatorKey("agent-legacy");
        string agentDatabasePath = Path.Combine(
            KautukaHostPathPolicy.ResolveSharedStateRoot(configuration),
            "agents",
            storageLocatorKey,
            "agent.db");

        Directory.CreateDirectory(Path.GetDirectoryName(agentDatabasePath) ?? throw new InvalidOperationException("Expected agent database directory."));

        AgentDefinitionSerializer.SerializedAgentDefinition serialized = AgentDefinitionSerializer.Serialize(new AgentDefinition
        {
            Id = "agent-legacy",
            Scope = GatewayOwnershipScopes.User,
            Enabled = true,
            Name = "Legacy Agent",
            ModelId = "model-alpha",
            SystemPrompt = "Legacy prompt",
            SuperExecMode = "trusted",
            ApprovalPolicy = new ApprovalPolicy(),
            Session = new AgentSessionDefinition(),
            Sandbox = new AgentSandboxDefinition(),
            McpBindings = [],
            SkillAttachments = [],
            Persona = new JsonObject(),
            ToolExposure = new AgentToolExposureDefinition(),
        });

        using (var connection = new SqliteConnection($"Data Source={agentDatabasePath}"))
        {
            connection.Open();

            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = """
                CREATE TABLE agent_definition_versions (
                    version                  INTEGER PRIMARY KEY,
                    agent_id                 TEXT NOT NULL,
                    scope                    TEXT NOT NULL,
                    enabled                  INTEGER NOT NULL,
                    name                     TEXT NOT NULL,
                    model_id                 TEXT NOT NULL,
                    system_prompt            TEXT NOT NULL,
                    super_exec_mode          TEXT NOT NULL,
                    session_json             TEXT NOT NULL,
                    sandbox_json             TEXT NOT NULL,
                    skill_attachments_json   TEXT NOT NULL,
                    persona_json             TEXT NOT NULL,
                    approval_config_json     TEXT NOT NULL,
                    tool_exposure_json       TEXT NOT NULL,
                    continuation_mode_json   TEXT NOT NULL,
                    created_at               TEXT NOT NULL
                );

                CREATE TABLE agent_definition_settings (
                    version      INTEGER NOT NULL,
                    setting_id   TEXT NOT NULL,
                    setting_name TEXT NOT NULL,
                    value_json   TEXT NOT NULL,
                    PRIMARY KEY (version, setting_id)
                );

                CREATE TABLE agent_definition_current (
                    singleton_id INTEGER PRIMARY KEY CHECK (singleton_id = 1),
                    version      INTEGER NOT NULL,
                    activated_at TEXT NOT NULL
                );
                """;
            command.ExecuteNonQuery();

            using SqliteCommand insert = connection.CreateCommand();
            insert.CommandText = """
                INSERT INTO agent_definition_versions (
                    version, agent_id, scope, enabled, name, model_id, system_prompt, super_exec_mode,
                    session_json, sandbox_json, skill_attachments_json, persona_json,
                    approval_config_json, tool_exposure_json, continuation_mode_json, created_at)
                VALUES (
                    1, $agentId, $scope, $enabled, $name, $modelId, $systemPrompt, $superExecMode,
                    $sessionJson, $sandboxJson, $skillAttachmentsJson, $personaJson,
                    $approvalConfigJson, $toolExposureJson, '{}', $createdAt);

                INSERT INTO agent_definition_current (singleton_id, version, activated_at)
                VALUES (1, 1, $createdAt);
                """;
            insert.Parameters.AddWithValue("$agentId", serialized.Id);
            insert.Parameters.AddWithValue("$scope", serialized.Scope);
            insert.Parameters.AddWithValue("$enabled", serialized.Enabled);
            insert.Parameters.AddWithValue("$name", serialized.Name);
            insert.Parameters.AddWithValue("$modelId", serialized.ModelId);
            insert.Parameters.AddWithValue("$systemPrompt", serialized.SystemPrompt);
            insert.Parameters.AddWithValue("$superExecMode", serialized.SuperExecMode);
            insert.Parameters.AddWithValue("$sessionJson", serialized.SessionJson);
            insert.Parameters.AddWithValue("$sandboxJson", serialized.SandboxJson);
            insert.Parameters.AddWithValue("$skillAttachmentsJson", serialized.SkillAttachmentsJson);
            insert.Parameters.AddWithValue("$personaJson", serialized.PersonaJson);
            insert.Parameters.AddWithValue("$approvalConfigJson", serialized.ApprovalConfigJson);
            insert.Parameters.AddWithValue("$toolExposureJson", serialized.ToolExposureJson);
            insert.Parameters.AddWithValue("$createdAt", DateTimeOffset.UtcNow.ToString("O"));
            insert.ExecuteNonQuery();
        }

        InvalidOperationException error = Assert.Throws<InvalidOperationException>(
            () => store.TryLoadLatestDefinition(storageLocatorKey));
        Assert.Contains("Automatic migration and destructive repair are disabled", error.Message, StringComparison.Ordinal);

        using var verifyConnection = new SqliteConnection($"Data Source={agentDatabasePath}");
        verifyConnection.Open();
        using (SqliteCommand count = verifyConnection.CreateCommand())
        {
            count.CommandText = "SELECT COUNT(*) FROM agent_definition_versions WHERE agent_id = 'agent-legacy'";
            Assert.Equal(1L, count.ExecuteScalar());
        }

        using SqliteCommand pragma = verifyConnection.CreateCommand();
        pragma.CommandText = "PRAGMA table_info(agent_definition_versions);";
        using SqliteDataReader reader = pragma.ExecuteReader();
        bool legacyColumnPreserved = false;
        while (reader.Read())
        {
            legacyColumnPreserved |= string.Equals("continuation_mode_json", reader.GetString(1), StringComparison.Ordinal);
        }

        Assert.True(legacyColumnPreserved);
    }

    [Fact]
    public async Task SaveAgentAsyncDoesNotPersistRegistryRowWhenAgentStoreBootstrapFails()
    {
        using var temp = new TemporaryGatewayStateDirectory();
        IConfiguration configuration = CreateConfiguration(
            temp.SqlitePath,
            additionalSettings: new Dictionary<string, string?>
            {
                ["Kautuka:SqliteVec:ExtensionPath"] = Path.Combine(temp.Root, "missing", "vec0.so"),
            });
        var database = new KautukaDatabase(configuration);
        database.EnsureReady();

        var definitionStore = new SqliteAgentDefinitionStore(configuration, database);
        GatewayState state = GatewayState.FromDatabase(
            database,
            NullLogger<GatewayState>.Instance,
            new GatewayMemoryStore(),
            definitionStore,
            configuration,
            new NoOpWorkspaceRuntimeTool());
        var store = new GatewayCatalogStore(
            state,
            definitionStore,
            new RecordingTopologyWriter(),
            new NoOpWorkspaceRuntimeTool(),
            new NoOpSkillPackageService(),
            new RecordingDesiredStatePublisher(),
            new NoOpContinuationStore(),
            new NoOpConversationAssetStore());

        await Assert.ThrowsAsync<InvalidOperationException>(() => store.SaveAgentAsync(
            "agent-broken",
            new GatewayAgentUpsertRequest
            {
                Name = "Broken Agent",
                ModelId = "model-alpha",
                SystemPrompt = "prompt",
                ApprovalConfig = new GatewayApprovalConfigPayload(),
                ToolExposure = new GatewayAgentToolExposurePayload(),
            },
            "owner-user",
            TestContext.Current.CancellationToken)).ConfigureAwait(true);

        using var connection = new SqliteConnection($"Data Source={KautukaDatabase.ResolvePath(configuration)}");
        await connection.OpenAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM agents WHERE id = $id";
        command.Parameters.AddWithValue("$id", "agent-broken");
        object? count = await command.ExecuteScalarAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);
        Assert.Equal(0L, Convert.ToInt64(count, CultureInfo.InvariantCulture));

        string agentDirectoryPath = Path.Combine(KautukaHostPathPolicy.ResolveSharedStateRoot(configuration), "agents", "agent-broken");
        Assert.False(Directory.Exists(agentDirectoryPath));
        Assert.False(state.Agents.ContainsKey("agent-broken"));
    }

    [Fact]
    public async Task DeleteAgentAsyncRestoresStoreWhenAtomicDeleteFails()
    {
        using var temp = new TemporaryGatewayStateDirectory();
        IConfiguration configuration = CreateConfiguration(temp.SqlitePath);
        var database = new KautukaDatabase(configuration);
        database.EnsureReady();

        var backingStore = new SqliteAgentDefinitionStore(configuration);
        string storageLocatorKey = backingStore.GetStorageLocatorKey("agent-delete-fail");
        backingStore.SaveDefinition(
            storageLocatorKey,
            new AgentDefinition
            {
                Id = "agent-delete-fail",
                Scope = GatewayOwnershipScopes.User,
                Enabled = true,
                Name = "Delete Fail",
                ModelId = "model-alpha",
                SystemPrompt = "prompt",
                SuperExecMode = "trusted",
                McpBindings = [],
                SkillAttachments = [],
                Session = new AgentSessionDefinition(),
                Sandbox = new AgentSandboxDefinition(),
                ApprovalPolicy = new ApprovalPolicy(),
                ToolExposure = new AgentToolExposureDefinition(),
            });

        GatewayState state = GatewayState.FromDatabase(
            database,
            NullLogger<GatewayState>.Instance,
            new GatewayMemoryStore(),
            backingStore,
            configuration,
            new NoOpWorkspaceRuntimeTool());
        state.AgentStorageLocators["agent-delete-fail"] = storageLocatorKey;
        state.Agents["agent-delete-fail"] = new GatewayAgent(
            "agent-delete-fail",
            "Delete Fail",
            "model-alpha",
            "prompt",
            "trusted",
            new JsonObject(),
            new JsonObject(),
            new JsonObject(),
            [],
            "owner-user",
            DateTimeOffset.UtcNow.ToString("O"),
            new JsonObject(),
            new JsonObject());

        var store = new GatewayCatalogStore(
            state,
            new ThrowingAtomicAgentDefinitionStore(backingStore, storageLocatorKey),
            new RecordingTopologyWriter(),
            new NoOpWorkspaceRuntimeTool(),
            new NoOpSkillPackageService(),
            new RecordingDesiredStatePublisher(),
            new NoOpContinuationStore(),
            new NoOpConversationAssetStore());

        await Assert.ThrowsAsync<InvalidOperationException>(() => store.DeleteAgentAsync("agent-delete-fail", TestContext.Current.CancellationToken)).ConfigureAwait(true);

        string agentDirectoryPath = Path.Combine(KautukaHostPathPolicy.ResolveSharedStateRoot(configuration), "agents", "agent-delete-fail");
        Assert.True(Directory.Exists(agentDirectoryPath));
        Assert.True(state.Agents.ContainsKey("agent-delete-fail"));
        Assert.True(state.AgentStorageLocators.ContainsKey("agent-delete-fail"));
    }

    [Fact]
    public void GatewayStateFromDatabaseSkipsBrokenAgentStore()
    {
        using var temp = new TemporaryGatewayStateDirectory();
        IConfiguration configuration = CreateConfiguration(temp.SqlitePath);
        var database = new KautukaDatabase(configuration);
        database.EnsureReady();

        using (var connection = new SqliteConnection($"Data Source={KautukaDatabase.ResolvePath(configuration)}"))
        {
            connection.Open();
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = "INSERT INTO agents (id, scope, enabled, owner_user_id, storage_locator_key, created_at, updated_at) VALUES ($id, 'user', 1, '', $storageLocatorKey, $now, $now);";
            command.Parameters.AddWithValue("$id", "agent-bad-load");
            command.Parameters.AddWithValue("$storageLocatorKey", "agent-bad-load");
            command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
            command.ExecuteNonQuery();
        }

        GatewayState state = GatewayState.FromDatabase(
            database,
            NullLogger<GatewayState>.Instance,
            new GatewayMemoryStore(),
            new SqliteAgentDefinitionStore(configuration),
            configuration,
            new NoOpWorkspaceRuntimeTool());

        Assert.False(state.Agents.ContainsKey("agent-bad-load"));
        Assert.False(state.AgentStorageLocators.ContainsKey("agent-bad-load"));
    }

    [Fact]
    public void ResolverBuildsHumanReadableOwnerAwareAgentScopeName()
    {
        using var temp = new TemporaryGatewayStateDirectory();
        IConfiguration configuration = CreateConfiguration(temp.SqlitePath);
        GatewayState state = CreateState(temp.SqlitePath, configuration);
        var database = new KautukaDatabase(configuration);
        var user = new GatewayUser(
            "user-ash",
            "ash",
            "Testing1",
            "user",
            true,
            null,
            null);
        new SqliteGatewayUserCommandRepository(database).Upsert(user);
        state.Users[user.Username] = user;
        var catalogStore = new GatewayCatalogStore(
            state,
            new SqliteAgentDefinitionStore(configuration, database),
            new RecordingTopologyWriter(),
            new NoOpWorkspaceRuntimeTool(),
            new NoOpSkillPackageService(),
            new RecordingDesiredStatePublisher(),
            new NoOpContinuationStore(),
            new NoOpConversationAssetStore());
        _ = catalogStore.SaveAgent(
            "agent-alpha",
            new GatewayAgentUpsertRequest
            {
                Name = "Faster Brain",
                ModelId = "model-alpha",
                SystemPrompt = "prompt",
                ApprovalConfig = new GatewayApprovalConfigPayload(),
                ToolExposure = new GatewayAgentToolExposurePayload(),
            },
            "user-ash");

        var resolver = new WorkspaceRuntimeContextResolver(
            new SqlitePlatformConfigurationProvider(database, new SqliteAgentDefinitionStore(configuration, database)),
            configuration,
            database);

        WorkspaceRuntimeContext context = resolver.ResolveAgentWorkspace("agent-alpha");

        Assert.Equal("user-ash-agent-alpha", context.ScopeId);
        Assert.EndsWith(Path.Combine("agents", "user-ash-agent-alpha"), context.HostWorkspacePath, StringComparison.Ordinal);
    }

    [Fact]
    public void ResolveDesiredStateSeparatesAgentsWhoseNamesSanitizeToOneScope()
    {
        using var temp = new TemporaryGatewayStateDirectory();
        IConfiguration configuration = CreateConfiguration(temp.SqlitePath);
        GatewayState state = CreateState(temp.SqlitePath, configuration);
        var database = new KautukaDatabase(configuration);
        var user = new GatewayUser("user-ash", "ash", "Testing1", "user", true, null, null);
        new SqliteGatewayUserCommandRepository(database).Upsert(user);
        state.Users[user.Username] = user;
        var catalogStore = new GatewayCatalogStore(
            state,
            new SqliteAgentDefinitionStore(configuration, database),
            new RecordingTopologyWriter(),
            new NoOpWorkspaceRuntimeTool(),
            new NoOpSkillPackageService(),
            new RecordingDesiredStatePublisher(),
            new NoOpContinuationStore(),
            new NoOpConversationAssetStore());
        catalogStore.SaveAgent(
            "agent-b",
            new GatewayAgentUpsertRequest
            {
                Name = "Notes!",
                ModelId = "model-alpha",
                SystemPrompt = "prompt",
                ApprovalConfig = new GatewayApprovalConfigPayload(),
                ToolExposure = new GatewayAgentToolExposurePayload(),
            },
            "user-ash");
        catalogStore.SaveAgent(
            "agent-a",
            new GatewayAgentUpsertRequest
            {
                Name = "Notes",
                ModelId = "model-alpha",
                SystemPrompt = "prompt",
                ApprovalConfig = new GatewayApprovalConfigPayload(),
                ToolExposure = new GatewayAgentToolExposurePayload(),
            },
            "user-ash");

        var resolver = new WorkspaceRuntimeContextResolver(
            new SqlitePlatformConfigurationProvider(database, new SqliteAgentDefinitionStore(configuration, database)),
            configuration,
            database);

        WorkspaceDesiredStatePayload payload = resolver.ResolveDesiredState();
        WorkspaceRuntimeContext[] notes =
        [
            .. payload.Workspaces.Where(workspace =>
                workspace.HostWorkspacePath.EndsWith(Path.Combine("agents", "user-ash-agent-a"), StringComparison.OrdinalIgnoreCase)
                || workspace.HostWorkspacePath.EndsWith(Path.Combine("agents", "user-ash-agent-b"), StringComparison.OrdinalIgnoreCase)),
        ];

        Assert.Equal(2, notes.Length);
        Assert.DoesNotContain(notes, workspace => workspace.HostWorkspacePath.EndsWith(Path.Combine("agents", "ash-notes"), StringComparison.OrdinalIgnoreCase));
        Assert.Contains(notes, workspace => workspace.ScopeId.Contains("agent-a", StringComparison.Ordinal));
        Assert.Contains(notes, workspace => workspace.ScopeId.Contains("agent-b", StringComparison.Ordinal));
        Assert.Equal(2, notes.Select(workspace => workspace.HostWorkspacePath).Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Fact]
    public void ResolveDesiredStateDoesNotShareAHostPathAcrossOwnerNameAliases()
    {
        using var temp = new TemporaryGatewayStateDirectory();
        IConfiguration configuration = CreateConfiguration(temp.SqlitePath);
        GatewayState state = CreateState(temp.SqlitePath, configuration);
        var database = new KautukaDatabase(configuration);
        var users = new SqliteGatewayUserCommandRepository(database);
        var victim = new GatewayUser("user-ab", "ab", "Testing1", "user", true, null, null);
        var attacker = new GatewayUser("user-abc", "ab-c", "Testing1", "user", true, null, null);
        users.Upsert(victim);
        users.Upsert(attacker);
        state.Users[victim.Username] = victim;
        state.Users[attacker.Username] = attacker;
        var catalogStore = new GatewayCatalogStore(
            state,
            new SqliteAgentDefinitionStore(configuration, database),
            new RecordingTopologyWriter(),
            new NoOpWorkspaceRuntimeTool(),
            new NoOpSkillPackageService(),
            new RecordingDesiredStatePublisher(),
            new NoOpContinuationStore(),
            new NoOpConversationAssetStore());
        catalogStore.SaveAgent(
            "agent-victim",
            new GatewayAgentUpsertRequest
            {
                Name = "c-secret",
                ModelId = "model-alpha",
                SystemPrompt = "prompt",
                ApprovalConfig = new GatewayApprovalConfigPayload(),
                ToolExposure = new GatewayAgentToolExposurePayload(),
            },
            victim.Id);
        catalogStore.SaveAgent(
            "a",
            new GatewayAgentUpsertRequest
            {
                Name = "secret",
                ModelId = "model-alpha",
                SystemPrompt = "prompt",
                ApprovalConfig = new GatewayApprovalConfigPayload(),
                ToolExposure = new GatewayAgentToolExposurePayload(),
            },
            attacker.Id);

        var resolver = new WorkspaceRuntimeContextResolver(
            new SqlitePlatformConfigurationProvider(database, new SqliteAgentDefinitionStore(configuration, database)),
            configuration,
            database);

        WorkspaceDesiredStatePayload payload = resolver.ResolveDesiredState();
        WorkspaceRuntimeContext[] aliased =
        [
            .. payload.Workspaces.Where(workspace =>
                workspace.HostWorkspacePath.EndsWith(Path.Combine("agents", "user-ab-agent-victim"), StringComparison.OrdinalIgnoreCase)
                || workspace.HostWorkspacePath.EndsWith(Path.Combine("agents", "user-abc-a"), StringComparison.OrdinalIgnoreCase)),
        ];

        Assert.Equal(2, aliased.Length);
        Assert.DoesNotContain(aliased, workspace => workspace.HostWorkspacePath.EndsWith(Path.Combine("agents", "ab-c-secret"), StringComparison.OrdinalIgnoreCase));
        Assert.Equal(2, aliased.Select(workspace => workspace.HostWorkspacePath).Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Fact]
    public void ResolveDesiredStateMountsEachSystemAgentUserSeparately()
    {
        using var temp = new TemporaryGatewayStateDirectory();
        IConfiguration configuration = CreateConfiguration(temp.SqlitePath);
        GatewayState state = CreateState(temp.SqlitePath, configuration);
        var database = new KautukaDatabase(configuration);
        var first = new GatewayUser("user-ash", "ash", "Testing1", "user", true, null, null);
        var second = new GatewayUser("user-bee", "bee", "Testing1", "user", true, null, null);
        var users = new SqliteGatewayUserCommandRepository(database);
        users.Upsert(first);
        users.Upsert(second);
        state.Users[first.Username] = first;
        state.Users[second.Username] = second;
        var catalogStore = new GatewayCatalogStore(
            state,
            new SqliteAgentDefinitionStore(configuration, database),
            new RecordingTopologyWriter(),
            new NoOpWorkspaceRuntimeTool(),
            new NoOpSkillPackageService(),
            new RecordingDesiredStatePublisher(),
            new NoOpContinuationStore(),
            new NoOpConversationAssetStore());
        _ = catalogStore.SaveAgent(
            "shared-brain",
            new GatewayAgentUpsertRequest
            {
                Name = "Shared Brain",
                ModelId = "model-alpha",
                SystemPrompt = "prompt",
                ApprovalConfig = new GatewayApprovalConfigPayload(),
                ToolExposure = new GatewayAgentToolExposurePayload(),
            },
            first.Id);
        _ = catalogStore.SaveAgent(
            "private-brain",
            new GatewayAgentUpsertRequest
            {
                Name = "Private Brain",
                ModelId = "model-alpha",
                SystemPrompt = "prompt",
                ApprovalConfig = new GatewayApprovalConfigPayload(),
                ToolExposure = new GatewayAgentToolExposurePayload(),
            },
            first.Id);

        using (var connection = new SqliteConnection(database.ConnectionString))
        {
            connection.Open();
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = "UPDATE agents SET scope = 'system' WHERE id = $id;";
            command.Parameters.AddWithValue("$id", "shared-brain");
            command.ExecuteNonQuery();
        }

        var resolver = new WorkspaceRuntimeContextResolver(
            new SqlitePlatformConfigurationProvider(database, new SqliteAgentDefinitionStore(configuration, database)),
            configuration,
            database);
        WorkspaceDesiredStatePayload payload = resolver.ResolveDesiredState();
        WorkspaceRuntimeContext[] shared = [.. payload.Workspaces.Where(workspace => workspace.ScopeId == "user-ash-shared-brain")];
        Assert.Equal(2, shared.Length);
        Assert.Contains(shared, workspace => workspace.HostWorkspacePath.EndsWith(Path.Combine("shared-brain", "user-ash"), StringComparison.OrdinalIgnoreCase));
        Assert.Contains(shared, workspace => workspace.HostWorkspacePath.EndsWith(Path.Combine("shared-brain", "user-bee"), StringComparison.OrdinalIgnoreCase));
        Assert.All(shared, workspace => Assert.False(string.Equals(
            Path.GetFileName(workspace.HostWorkspacePath),
            "shared-brain",
            StringComparison.OrdinalIgnoreCase)));
        WorkspaceRuntimeContext privateWorkspace = Assert.Single(payload.Workspaces, workspace => workspace.ScopeId == "user-ash-private-brain");
        Assert.EndsWith(Path.Combine("agents", "user-ash-private-brain"), privateWorkspace.HostWorkspacePath, StringComparison.Ordinal);
    }

    [Fact]
    public void WorkspaceIdentityKeepsExistingFilesWhenCollidingAgentsChange()
    {
        using var temp = new TemporaryGatewayStateDirectory();
        IConfiguration configuration = CreateConfiguration(temp.SqlitePath);
        GatewayState state = CreateState(temp.SqlitePath, configuration);
        var database = new KautukaDatabase(configuration);
        var user = new GatewayUser("user-ash", "ash", "Testing1", "user", true, null, null);
        new SqliteGatewayUserCommandRepository(database).Upsert(user);
        state.Users[user.Username] = user;
        var catalogStore = new GatewayCatalogStore(
            state,
            new SqliteAgentDefinitionStore(configuration, database),
            new RecordingTopologyWriter(),
            new NoOpWorkspaceRuntimeTool(),
            new NoOpSkillPackageService(),
            new RecordingDesiredStatePublisher(),
            new NoOpContinuationStore(),
            new NoOpConversationAssetStore());
        GatewayAgentUpsertRequest Request(string name) => new()
        {
            Name = name,
            ModelId = "model-alpha",
            SystemPrompt = "prompt",
            ApprovalConfig = new GatewayApprovalConfigPayload(),
            ToolExposure = new GatewayAgentToolExposurePayload(),
        };
        catalogStore.SaveAgent("agent-a", Request("Notes"), user.Id);

        string agentsRoot = Path.Combine(WorkspaceHostRootPolicy.ResolveWorkspaceBaseRoot(configuration, null), "agents");
        string legacyDirectory = Path.Combine(agentsRoot, "ash-notes");
        Directory.CreateDirectory(legacyDirectory);
        File.WriteAllText(Path.Combine(legacyDirectory, "kept.txt"), "kept-note");

        var provider = new SqlitePlatformConfigurationProvider(database, new SqliteAgentDefinitionStore(configuration, database));
        var resolver = new WorkspaceRuntimeContextResolver(provider, configuration, database);
        WorkspaceRuntimeContext first = resolver.ResolveAgentWorkspace("agent-a");
        Assert.EndsWith(Path.Combine("agents", "user-ash-agent-a"), first.HostWorkspacePath, StringComparison.OrdinalIgnoreCase);
        Directory.CreateDirectory(first.HostWorkspacePath);
        File.WriteAllText(Path.Combine(first.HostWorkspacePath, "kept.txt"), "kept-note");
        Assert.Equal("kept-note", File.ReadAllText(Path.Combine(first.HostWorkspacePath, "kept.txt")));
        Assert.Equal("kept-note", File.ReadAllText(Path.Combine(legacyDirectory, "kept.txt")));

        catalogStore.SaveAgent("agent-a", Request("Other Notes"), user.Id);
        WorkspaceRuntimeContext renamed = resolver.ResolveAgentWorkspace("agent-a");
        Assert.Equal(first.HostWorkspacePath, renamed.HostWorkspacePath);
        Assert.Equal("kept-note", File.ReadAllText(Path.Combine(renamed.HostWorkspacePath, "kept.txt")));

        catalogStore.SaveAgent("agent-b", Request("Other Notes"), user.Id);
        WorkspaceRuntimeContext collided = resolver.ResolveAgentWorkspace("agent-a");
        WorkspaceRuntimeContext other = resolver.ResolveAgentWorkspace("agent-b");
        Assert.Equal(first.HostWorkspacePath, collided.HostWorkspacePath);
        Assert.NotEqual(collided.HostWorkspacePath, other.HostWorkspacePath);
        Assert.Equal("kept-note", File.ReadAllText(Path.Combine(collided.HostWorkspacePath, "kept.txt")));
        Directory.CreateDirectory(other.HostWorkspacePath);
        File.WriteAllText(Path.Combine(other.HostWorkspacePath, "other.txt"), "other-note");

        Assert.True(catalogStore.DeleteAgent("agent-b"));
        WorkspaceRuntimeContext remaining = resolver.ResolveAgentWorkspace("agent-a");
        Assert.Equal(first.HostWorkspacePath, remaining.HostWorkspacePath);
        Assert.Equal("kept-note", File.ReadAllText(Path.Combine(remaining.HostWorkspacePath, "kept.txt")));
        Assert.False(File.Exists(Path.Combine(remaining.HostWorkspacePath, "other.txt")));

        string abandoned = Path.Combine(agentsRoot, "ash-other-notes");
        Directory.CreateDirectory(abandoned);
        File.WriteAllText(Path.Combine(abandoned, "stolen.txt"), "stolen-note");
        WorkspaceRuntimeContext afterAbandoned = resolver.ResolveAgentWorkspace("agent-a");
        Assert.Equal(first.HostWorkspacePath, afterAbandoned.HostWorkspacePath);
        Assert.False(File.Exists(Path.Combine(afterAbandoned.HostWorkspacePath, "stolen.txt")));
        Assert.Equal("stolen-note", File.ReadAllText(Path.Combine(abandoned, "stolen.txt")));
    }

    [Fact]
    public void WorkspaceIdentityDoesNotCollapseIdsThatSanitizeAlike()
    {
        using var temp = new TemporaryGatewayStateDirectory();
        IConfiguration configuration = CreateConfiguration(temp.SqlitePath);
        GatewayState state = CreateState(temp.SqlitePath, configuration);
        var database = new KautukaDatabase(configuration);
        var user = new GatewayUser("user-ash", "ash", "Testing1", "user", true, null, null);
        new SqliteGatewayUserCommandRepository(database).Upsert(user);
        state.Users[user.Username] = user;
        var catalogStore = new GatewayCatalogStore(
            state,
            new SqliteAgentDefinitionStore(configuration, database),
            new RecordingTopologyWriter(),
            new NoOpWorkspaceRuntimeTool(),
            new NoOpSkillPackageService(),
            new RecordingDesiredStatePublisher(),
            new NoOpContinuationStore(),
            new NoOpConversationAssetStore());
        GatewayAgentUpsertRequest request = new()
        {
            Name = "Notes",
            ModelId = "model-alpha",
            SystemPrompt = "prompt",
            ApprovalConfig = new GatewayApprovalConfigPayload(),
            ToolExposure = new GatewayAgentToolExposurePayload(),
        };
        catalogStore.SaveAgent("my_agent", request, user.Id);
        catalogStore.SaveAgent("my-agent", request, user.Id);

        var resolver = new WorkspaceRuntimeContextResolver(
            new SqlitePlatformConfigurationProvider(database, new SqliteAgentDefinitionStore(configuration, database)),
            configuration,
            database);
        WorkspaceRuntimeContext underscored = resolver.ResolveAgentWorkspace("my_agent");
        Directory.CreateDirectory(underscored.HostWorkspacePath);
        File.WriteAllText(Path.Combine(underscored.HostWorkspacePath, "kept.txt"), "underscored");
        WorkspaceRuntimeContext dashed = resolver.ResolveAgentWorkspace("my-agent");

        Assert.NotEqual(underscored.HostWorkspacePath, dashed.HostWorkspacePath);
        Assert.Equal("underscored", File.ReadAllText(Path.Combine(underscored.HostWorkspacePath, "kept.txt")));
        Assert.False(File.Exists(Path.Combine(dashed.HostWorkspacePath, "kept.txt")));
    }

    [Fact]
    public void ResolveDesiredStateKeepsCaseDistinctAgentIdsOnSeparateWorkspaces()
    {
        using var temp = new TemporaryGatewayStateDirectory();
        IConfiguration configuration = CreateConfiguration(temp.SqlitePath);
        var database = new KautukaDatabase(configuration);
        database.EnsureReady();
        PlatformConfiguration platformConfiguration = new()
        {
            Kautuka = new InfrastructureDefinition
            {
                Workspace = new WorkspaceDefinition
                {
                    BaseRoot = Path.Combine(temp.Root, "workspaces"),
                },
                Sandbox = new SandboxRuntimeDefinition
                {
                    Image = "kautuka-base:latest",
                    DefaultCpus = 2,
                    DefaultMemory = "2g",
                    DefaultTimeout = 120,
                    ContainerEnvironment = new Dictionary<string, string>(),
                },
            },
            Agents =
            [
                new AgentDefinition
                {
                    Id = "notes",
                    Name = "Notes",
                    OwnerUserId = "user-b",
                    Scope = "user",
                },
                new AgentDefinition
                {
                    Id = "Notes",
                    Name = "Notes",
                    OwnerUserId = "user-a",
                    Scope = "user",
                },
            ],
        };
        var resolver = new WorkspaceRuntimeContextResolver(
            new StaticPlatformConfigurationProvider(platformConfiguration),
            configuration,
            database);

        WorkspaceDesiredStatePayload desired = resolver.ResolveDesiredState();
        WorkspaceRuntimeContext lower = resolver.ResolveAgentWorkspace("notes");
        WorkspaceRuntimeContext upper = resolver.ResolveAgentWorkspace("Notes");

        Assert.Equal(2, desired.Workspaces.Count);
        Assert.NotEqual(lower.HostWorkspacePath, upper.HostWorkspacePath);
        Assert.Contains($"{Path.DirectorySeparatorChar}user-b-", lower.HostWorkspacePath, StringComparison.Ordinal);
        Assert.Contains($"{Path.DirectorySeparatorChar}user-a-", upper.HostWorkspacePath, StringComparison.Ordinal);
        Directory.CreateDirectory(lower.HostWorkspacePath);
        File.WriteAllText(Path.Combine(lower.HostWorkspacePath, "kept.txt"), "lower-note");
        Assert.False(File.Exists(Path.Combine(upper.HostWorkspacePath, "kept.txt")));
    }

    [Fact]
    public void ResolveDesiredStateKeepsALongAgentIdWithinTheWorkspaceIdLimit()
    {
        using var temp = new TemporaryGatewayStateDirectory();
        IConfiguration configuration = CreateConfiguration(temp.SqlitePath);
        var database = new KautukaDatabase(configuration);
        database.EnsureReady();
        string longAgentId = new string('a', 200);
        PlatformConfiguration platformConfiguration = new()
        {
            Kautuka = new InfrastructureDefinition
            {
                Workspace = new WorkspaceDefinition
                {
                    BaseRoot = Path.Combine(temp.Root, "workspaces"),
                },
                Sandbox = new SandboxRuntimeDefinition
                {
                    Image = "kautuka-base:latest",
                    DefaultCpus = 2,
                    DefaultMemory = "2g",
                    DefaultTimeout = 120,
                    ContainerEnvironment = new Dictionary<string, string>(),
                },
            },
            Agents =
            [
                new AgentDefinition
                {
                    Id = "agent-a",
                    Name = "Notes",
                    OwnerUserId = "user-ash",
                    Scope = "user",
                },
                new AgentDefinition
                {
                    Id = longAgentId,
                    Name = "Long",
                    OwnerUserId = "user-ash",
                    Scope = "user",
                },
                new AgentDefinition
                {
                    Id = new string('b', 200),
                    Name = "Longer",
                    OwnerUserId = "user-ash",
                    Scope = "user",
                },
            ],
        };
        var resolver = new WorkspaceRuntimeContextResolver(
            new StaticPlatformConfigurationProvider(platformConfiguration),
            configuration,
            database);

        WorkspaceDesiredStatePayload desired = resolver.ResolveDesiredState();
        WorkspaceRuntimeContext shortAgent = resolver.ResolveAgentWorkspace("agent-a");
        WorkspaceRuntimeContext longAgent = resolver.ResolveAgentWorkspace(longAgentId);
        WorkspaceRuntimeContext otherLongAgent = resolver.ResolveAgentWorkspace(new string('b', 200));

        Assert.Equal(3, desired.Workspaces.Count);
        Assert.Equal("user-ash-agent-a", shortAgent.ScopeId);
        Assert.True(longAgent.ScopeId.Length <= 128);
        Assert.True(otherLongAgent.ScopeId.Length <= 128);
        Assert.StartsWith("ws-", longAgent.ScopeId, StringComparison.Ordinal);
        Assert.NotEqual(longAgent.ScopeId, otherLongAgent.ScopeId);
        Assert.NotEqual(shortAgent.HostWorkspacePath, longAgent.HostWorkspacePath);
        Assert.EndsWith($"{Path.DirectorySeparatorChar}agents{Path.DirectorySeparatorChar}{longAgent.ScopeId}", longAgent.HostWorkspacePath, StringComparison.Ordinal);
        Directory.CreateDirectory(longAgent.HostWorkspacePath);
        File.WriteAllText(Path.Combine(longAgent.HostWorkspacePath, "kept.txt"), "long-note");
        WorkspaceRuntimeContext again = resolver.ResolveAgentWorkspace(longAgentId);
        Assert.Equal(longAgent.HostWorkspacePath, again.HostWorkspacePath);
        Assert.Equal("long-note", File.ReadAllText(Path.Combine(again.HostWorkspacePath, "kept.txt")));
    }

    [Fact]
    public void WorkspaceIdentityForNulAgentIdDoesNotAdoptADirectoryPlantedLater()
    {
        using var temp = new TemporaryGatewayStateDirectory();
        IConfiguration configuration = CreateConfiguration(temp.SqlitePath);
        var database = new KautukaDatabase(configuration);
        database.EnsureReady();
        PlatformConfiguration platformConfiguration = new()
        {
            Kautuka = new InfrastructureDefinition
            {
                Workspace = new WorkspaceDefinition
                {
                    BaseRoot = Path.Combine(temp.Root, "workspaces"),
                },
                Sandbox = new SandboxRuntimeDefinition
                {
                    Image = "kautuka-base:latest",
                    DefaultCpus = 2,
                    DefaultMemory = "2g",
                    DefaultTimeout = 120,
                    ContainerEnvironment = new Dictionary<string, string>(),
                },
            },
            Agents =
            [
                new AgentDefinition
                {
                    Id = "nul",
                    Name = "Notes",
                    OwnerUserId = "user-ash",
                    Scope = "user",
                },
            ],
        };
        var resolver = new WorkspaceRuntimeContextResolver(
            new StaticPlatformConfigurationProvider(platformConfiguration),
            configuration,
            database);

        WorkspaceRuntimeContext first = resolver.ResolveAgentWorkspace("nul");
        string stablePath = first.HostWorkspacePath;
        if (Directory.Exists(stablePath))
        {
            Directory.Delete(stablePath, recursive: true);
        }

        string planted = Path.Combine(temp.Root, "workspaces", "agents", "nul-notes");
        Directory.CreateDirectory(planted);
        File.WriteAllText(Path.Combine(planted, "stolen.txt"), "planted");

        WorkspaceRuntimeContext second = resolver.ResolveAgentWorkspace("nul");

        Assert.Equal("user-ash-nul", second.ScopeId);
        Assert.Equal(stablePath, second.HostWorkspacePath);
        Assert.Equal("planted", File.ReadAllText(Path.Combine(planted, "stolen.txt")));
        Assert.False(File.Exists(Path.Combine(stablePath, "stolen.txt")));
        string identityDirectory = Path.Combine(temp.Root, "workspaces", ".workspace-identities");
        Assert.False(Directory.Exists(identityDirectory));
    }

    [Fact]
    public void ResolveDesiredStateKeepsCaseDistinctConnectorIds()
    {
        using var temp = new TemporaryGatewayStateDirectory();
        IConfiguration configuration = CreateConfiguration(temp.SqlitePath);
        var database = new KautukaDatabase(configuration);
        database.EnsureReady();
        var users = new SqliteGatewayUserCommandRepository(database);
        users.Upsert(new GatewayUser("user-a", "user-a", "Testing1", "user", true, null, null));
        users.Upsert(new GatewayUser("user-b", "user-b", "Testing1", "user", true, null, null));
        PlatformConfiguration platformConfiguration = new()
        {
            Kautuka = new InfrastructureDefinition
            {
                Workspace = new WorkspaceDefinition
                {
                    BaseRoot = Path.Combine(temp.Root, "workspaces"),
                },
                Sandbox = new SandboxRuntimeDefinition
                {
                    Image = "kautuka-base:latest",
                    DefaultCpus = 2,
                    DefaultMemory = "2g",
                    DefaultTimeout = 120,
                    ContainerEnvironment = new Dictionary<string, string>(),
                },
            },
            Agents =
            [
                new AgentDefinition
                {
                    Id = "agent-a",
                    Name = "Agent A",
                    OwnerUserId = "user-a",
                    Scope = "user",
                    McpBindings =
                    [
                        new AgentMcpBindingDefinition
                        {
                            ConnectorId = "mail",
                            Context = "agent",
                        },
                    ],
                },
            ],
            McpServers =
            [
                new McpServerDefinition
                {
                    Id = "mail",
                    Name = "Mail",
                    Transport = McpTransport.Container,
                    ContainerImage = "kautuka-base:latest",
                    Enabled = true,
                    Scope = "user",
                    OwnerUserId = "user-a",
                    BundlePath = Path.Combine(temp.Root, "mail.mcpb"),
                },
                new McpServerDefinition
                {
                    Id = "Mail",
                    Name = "Other mail",
                    Transport = McpTransport.Container,
                    ContainerImage = "kautuka-base:latest",
                    Enabled = true,
                    Scope = "user",
                    OwnerUserId = "user-b",
                    BundlePath = Path.Combine(temp.Root, "Mail.mcpb"),
                },
            ],
        };
        var resolver = new WorkspaceRuntimeContextResolver(
            new StaticPlatformConfigurationProvider(platformConfiguration),
            configuration,
            database);

        WorkspaceDesiredStatePayload desired = resolver.ResolveDesiredState();
        Assert.Contains(desired.ContainerConnectors, connector => connector.ServerId == "mail");
        Assert.Contains(desired.ContainerConnectors, connector => connector.ServerId == "Mail");
        WorkspaceSandboxConnectorBindingsPayload bindings = Assert.Single(desired.SandboxConnectorBindings);
        Assert.Contains(bindings.ConnectorIds, id => id.EndsWith("::mail", StringComparison.Ordinal));
        Assert.DoesNotContain(bindings.ConnectorIds, id => id.EndsWith("::Mail", StringComparison.Ordinal));
    }

    [Fact]
    public void AgentJournalPathRejectsAnAliasOfAnotherAgent()
    {
        using var temp = new TemporaryGatewayStateDirectory();
        IConfiguration configuration = CreateConfiguration(temp.SqlitePath);
        string shared = KautukaHostPathPolicy.ResolveSharedStateRoot(configuration);
        var store = new SqliteAgentDefinitionStore(configuration, new KautukaDatabase(configuration));
        store.SaveDefinition("shared-journal", new AgentDefinition
        {
            Id = "shared-journal",
            Name = "Shared",
        });
        string journal = Path.Combine(shared, "agents", "shared-journal", "agent.db");
        Assert.True(File.Exists(journal));
        Assert.Equal("shared-journal ", store.GetStorageLocatorKey("shared-journal "));
        if (OperatingSystem.IsWindows())
        {
            Assert.Throws<ArgumentException>(() => store.SaveDefinition("shared-journal ", new AgentDefinition
            {
                Id = "shared-journal ",
                Name = "Alias",
            }));
            Assert.Throws<ArgumentException>(() => store.SaveDefinition("Shared-Journal", new AgentDefinition
            {
                Id = "Shared-Journal",
                Name = "Alias",
            }));
        }
        else
        {
            Assert.NotEqual(Path.GetDirectoryName(journal), AgentStoragePath.RequireAgentDirectory(shared, "shared-journal "));
            Assert.NotEqual(Path.GetDirectoryName(journal), AgentStoragePath.RequireAgentDirectory(shared, "Shared-Journal"));
        }
        Assert.Throws<ArgumentException>(() => AgentStoragePath.RequireAgentDirectory(shared, ".."));
        Assert.NotNull(store.TryLoadLatestDefinition("shared-journal"));
        Assert.True(File.Exists(journal));
    }

    [Fact]
    public void StateHydrationDoesNotSynchronouslyRebuildWorkspacesWhenTopologyHashChanges()
    {
        using var temp = new TemporaryGatewayStateDirectory();
        IConfiguration configuration = CreateConfiguration(temp.SqlitePath);
        var database = new KautukaDatabase(configuration);
        database.EnsureReady();

        GatewayState seededState = GatewayState.FromDatabase(
            database,
            NullLogger<GatewayState>.Instance,
            new GatewayMemoryStore(),
            new SqliteAgentDefinitionStore(configuration),
            configuration,
            new NoOpWorkspaceRuntimeTool());
        var catalogStore = new GatewayCatalogStore(
            seededState,
            new SqliteAgentDefinitionStore(configuration),
            new RecordingTopologyWriter(),
            new NoOpWorkspaceRuntimeTool(),
            new NoOpSkillPackageService(),
            new RecordingDesiredStatePublisher(),
            new NoOpContinuationStore(),
            new NoOpConversationAssetStore());
        _ = catalogStore.SaveAgent(
            "agent-alpha",
            new GatewayAgentUpsertRequest
            {
                Name = "Alpha Agent",
                ModelId = "model-alpha",
                SystemPrompt = "prompt",
                ApprovalConfig = new GatewayApprovalConfigPayload(),
                ToolExposure = new GatewayAgentToolExposurePayload(),
            },
            "owner-user");
        seededState.TopologyHash = "stale-topology-hash";
        seededState.PersistRawSettingsAuthoritatively(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["topology_hash"] = seededState.TopologyHash,
        });

        var workspaceTool = new CountingWorkspaceRuntimeTool();

        GatewayState hydratedState = GatewayState.FromDatabase(
            database,
            NullLogger<GatewayState>.Instance,
            new GatewayMemoryStore(),
            new SqliteAgentDefinitionStore(configuration),
            configuration,
            workspaceTool);

        Assert.Equal(0, workspaceTool.RebuildCalls);
        Assert.NotEqual("stale-topology-hash", hydratedState.TopologyHash);
        Assert.Equal(hydratedState.ComputeTopologyHashFromState(), hydratedState.TopologyHash);
    }

    [Fact]
    public void SaveSystemSettingsPublishesDesiredStateAndIgnoresWorkspaceOverride()
    {
        using var temp = new TemporaryGatewayStateDirectory();
        string configuredWorkspaceRoot = Path.Combine(temp.Root, "alt-root");
        GatewayState state = CreateState(temp.SqlitePath);
        var publisher = new RecordingDesiredStatePublisher();
        var store = new GatewaySettingsStore(
            state,
            new KautukaLogLevelSwitch(LogLevel.Warning),
            publisher,
            new NoOpUpdateServiceGrpcService(),
            CreateConfiguration(temp.SqlitePath, new Dictionary<string, string?>
            {
                ["Kautuka:Workspace:BaseRoot"] = null,
            }),
            new StaticPlatformConfigurationProvider(CreatePlatformConfiguration()));

        string bodyJson = $$"""
        {
            "kautuka": {
                "workspace": { "baseRoot": "{{configuredWorkspaceRoot.Replace("\\", "\\\\", StringComparison.Ordinal)}}" },
                "sandbox": {
                    "image": "kautuka-base:latest",
                    "defaultCpus": 4,
                    "defaultMemory": "4g",
                    "defaultTimeout": 240
                }
            }
        }
        """;
        using JsonDocument body = JsonDocument.Parse(bodyJson);

        store.Save(body.RootElement);

        Assert.Contains("system-settings-save", publisher.Reasons);
        Assert.Equal(
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".kautuka", "workspaces"),
            state.SystemSettings.WorkspaceBaseRoot);
    }

    [Fact]
    public async Task SaveSystemSettingsPersistsReleaseChannelAndPropagatesUpdatePolicyAsync()
    {
        using var temp = new TemporaryGatewayStateDirectory();
        GatewayState state = CreateState(temp.SqlitePath);
        var publisher = new RecordingDesiredStatePublisher();
        var updateService = new RecordingUpdateServiceGrpcService();
        var store = new GatewaySettingsStore(
            state,
            new KautukaLogLevelSwitch(LogLevel.Warning),
            publisher,
            updateService,
            CreateConfiguration(temp.SqlitePath),
            new StaticPlatformConfigurationProvider(CreatePlatformConfiguration()));

        using JsonDocument body = JsonDocument.Parse(
            """
            {
              "system": {
                "updates": {
                  "releaseChannel": "preview"
                }
              }
            }
            """);

        await store.SaveAsync(body.RootElement, TestContext.Current.CancellationToken).ConfigureAwait(true);

        Assert.Equal("preview", state.SystemSettings.ReleaseChannel);
        Assert.Equal(1, updateService.SetPolicyCalls);
        Assert.Equal("preview", updateService.LastReleaseChannel);
        Assert.Contains("system-settings-save", publisher.Reasons);
        Assert.Equal("preview", store.GetPayload().System.Updates.ReleaseChannel);
    }

    [Fact]
    public async Task SaveSystemSettingsPersistsToolInvocationLimitsAndRoundTripsAsync()
    {
        using var temp = new TemporaryGatewayStateDirectory();
        IConfiguration configuration = CreateConfiguration(temp.SqlitePath);
        GatewayState state = CreateState(temp.SqlitePath, configuration);
        var store = new GatewaySettingsStore(
            state,
            new KautukaLogLevelSwitch(LogLevel.Warning),
            new RecordingDesiredStatePublisher(),
            new NoOpUpdateServiceGrpcService(),
            configuration,
            new StaticPlatformConfigurationProvider(CreatePlatformConfiguration()));

        using JsonDocument body = JsonDocument.Parse(
            """
            {
              "system": {
                "runtimeLimits": {
                  "allowMultipleToolCalls": true,
                  "allowConcurrentInvocation": true,
                  "maxParallelTools": 4
                }
              }
            }
            """);

        await store.SaveAsync(body.RootElement, TestContext.Current.CancellationToken).ConfigureAwait(true);

        Assert.True(state.SystemSettings.AllowMultipleToolCalls);
        Assert.True(state.SystemSettings.AllowConcurrentInvocation);
        Assert.Equal(4, state.SystemSettings.MaxParallelTools);
        Assert.True(store.GetPayload().System.RuntimeLimits.AllowMultipleToolCalls);
        Assert.True(store.GetPayload().System.RuntimeLimits.AllowConcurrentInvocation);
        Assert.Equal(4, store.GetPayload().System.RuntimeLimits.MaxParallelTools);

        GatewayState hydratedState = GatewayState.FromDatabase(
            new KautukaDatabase(configuration),
            NullLogger<GatewayState>.Instance,
            new GatewayMemoryStore(),
            new SqliteAgentDefinitionStore(configuration),
            configuration,
            new NoOpWorkspaceRuntimeTool());

        Assert.True(hydratedState.SystemSettings.AllowMultipleToolCalls);
        Assert.True(hydratedState.SystemSettings.AllowConcurrentInvocation);
        Assert.Equal(4, hydratedState.SystemSettings.MaxParallelTools);
    }

    [Fact]
    public void LoadSystemSettingsDefaultsCaptureMessageContentToFalse()
    {
        using var temp = new TemporaryGatewayStateDirectory();
        GatewayState state = CreateState(temp.SqlitePath);

        Assert.False(state.SystemSettings.CaptureMessageContent);
    }

    [Fact]
    public async Task SaveSystemSettingsPersistsCaptureMessageContentAndRoundTripsAsync()
    {
        string? previousValue = Environment.GetEnvironmentVariable(
            GenAiTelemetryEnvironment.CaptureMessageContentEnvVar,
            EnvironmentVariableTarget.Process);

        try
        {
            using var temp = new TemporaryGatewayStateDirectory();
            IConfiguration configuration = CreateConfiguration(temp.SqlitePath);
            GatewayState state = CreateState(temp.SqlitePath, configuration);
            var publisher = new RecordingDesiredStatePublisher();
            var store = new GatewaySettingsStore(
                state,
                new KautukaLogLevelSwitch(LogLevel.Warning),
                publisher,
                new NoOpUpdateServiceGrpcService(),
                configuration,
                new StaticPlatformConfigurationProvider(CreatePlatformConfiguration()));

            using JsonDocument body = JsonDocument.Parse(
                """
                {
                  "system": {
                    "observability": {
                      "captureMessageContent": true
                    }
                  }
                }
                """);

            await store.SaveAsync(body.RootElement, TestContext.Current.CancellationToken).ConfigureAwait(true);

            Assert.True(state.SystemSettings.CaptureMessageContent);
            Assert.True(store.GetPayload().System.Observability.CaptureMessageContent);

            GatewayState hydratedState = GatewayState.FromDatabase(
                new KautukaDatabase(configuration),
                NullLogger<GatewayState>.Instance,
                new GatewayMemoryStore(),
                new SqliteAgentDefinitionStore(configuration),
                configuration,
                new NoOpWorkspaceRuntimeTool());

            Assert.True(hydratedState.SystemSettings.CaptureMessageContent);
        }
        finally
        {
            Environment.SetEnvironmentVariable(
                GenAiTelemetryEnvironment.CaptureMessageContentEnvVar,
                previousValue,
                EnvironmentVariableTarget.Process);
        }
    }

    [Fact]
    public async Task SaveSystemSettingsDoesNotMutateProjectionWhenAuthoritativeWriteFailsAsync()
    {
        using var temp = new TemporaryGatewayStateDirectory();
        IConfiguration configuration = CreateConfiguration(temp.SqlitePath);
        GatewayState state = CreateState(temp.SqlitePath, configuration);
        var database = new KautukaDatabase(configuration);
        var store = new GatewaySettingsStore(
            state,
            new KautukaLogLevelSwitch(LogLevel.Warning),
            new RecordingDesiredStatePublisher(),
            new NoOpUpdateServiceGrpcService(),
            configuration,
            new StaticPlatformConfigurationProvider(CreatePlatformConfiguration()));

        using (var connection = new SqliteConnection(database.ConnectionString))
        {
            await connection.OpenAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = "DROP TABLE system_settings";
            await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);
        }

        using JsonDocument body = JsonDocument.Parse(
            """
            { "system": { "observability": { "captureMessageContent": true } } }
            """);

        await Assert.ThrowsAsync<SqliteException>(() =>
            store.SaveAsync(body.RootElement, TestContext.Current.CancellationToken)).ConfigureAwait(true);
        Assert.False(state.SystemSettings.CaptureMessageContent);
    }

    [Fact]
    public async Task SaveConnectorDiscoveryDoesNotPublishDesiredStateAsync()
    {
        using var temp = new TemporaryGatewayStateDirectory();
        GatewayState state = CreateState(temp.SqlitePath);
        state.McpServers["connector-a"] = new GatewayMcpServer(
            "connector-a",
            "Connector A",
            string.Empty,
            string.Empty,
            null,
            "container",
            null,
            "docker.io/example/image:latest",
            "node",
            [],
            null,
            null,
            null,
            30,
            600,
            null,
            null,
            true,
            false,
            DateTimeOffset.UtcNow.ToString("O"),
            DateTimeOffset.UtcNow.ToString("O"),
            GatewayOwnershipScopes.System,
            null,
            null,
            null,
            null);

        var topologyWriter = new RecordingTopologyWriter();
        var workspaceTool = new NoOpWorkspaceRuntimeTool();
        var publisher = new RecordingDesiredStatePublisher();
        var store = new GatewayCatalogStore(
            state,
            new SqliteAgentDefinitionStore(CreateConfiguration(temp.SqlitePath)),
            topologyWriter,
            workspaceTool,
            new NoOpSkillPackageService(),
            publisher,
            new NoOpContinuationStore(),
            new NoOpConversationAssetStore());

        GatewayMcpServer saved = await store.SaveMcpServerDiscoveryAsync("connector-a", 4, ["tool-a", "tool-b"], TestContext.Current.CancellationToken).ConfigureAwait(true);

        Assert.Equal(4, saved.ToolCount);
        Assert.DoesNotContain("connector-discovery-save", publisher.Reasons);
    }

    [Fact]
    public async Task SaveMcpServerAsyncPreservesDiscoveredToolMetadataWhenUpsertOmitsIt()
    {
        using var temp = new TemporaryGatewayStateDirectory();
        GatewayState state = CreateState(temp.SqlitePath);
        state.McpServers["kautuka-web-tools"] = new GatewayMcpServer(
            "kautuka-web-tools",
            "Kautuka Web Tools",
            "Built-in web fetch tools.",
            string.Empty,
            "kautuka-web-tools",
            "container",
            null,
            "docker.io/example/image:latest",
            "python",
            [],
            null,
            "/var/lib/kautuka/mcpb-bundles/kautuka-web-tools/example.mcpb",
            "uv",
            30,
            600,
            null,
            null,
            true,
            true,
            DateTimeOffset.UtcNow.ToString("O"),
            DateTimeOffset.UtcNow.ToString("O"),
            GatewayOwnershipScopes.System,
            null,
            1,
            ["kautuka_web_fetch"],
            null);

        var topologyWriter = new RecordingTopologyWriter();
        var workspaceTool = new NoOpWorkspaceRuntimeTool();
        var publisher = new RecordingDesiredStatePublisher();
        var store = new GatewayCatalogStore(
            state,
            new SqliteAgentDefinitionStore(CreateConfiguration(temp.SqlitePath)),
            topologyWriter,
            workspaceTool,
            new NoOpSkillPackageService(),
            publisher,
            new NoOpContinuationStore(),
            new NoOpConversationAssetStore());

        GatewayMcpServer saved = await store.SaveMcpServerAsync(
            "kautuka-web-tools",
            new GatewayConnectorUpsertRequest
            {
                Name = "Kautuka Web Tools",
                Description = "Built-in web fetch tools.",
                PresetId = "kautuka-web-tools",
                Transport = "container",
                ContainerImage = "docker.io/example/image:latest",
                ContainerCommand = "python",
                ContainerArgs = [],
                BundlePath = "/var/lib/kautuka/mcpb-bundles/kautuka-web-tools/example.mcpb",
                ServerType = "uv",
                Enabled = true,
                ToolCount = null,
                ToolNames = null,
            },
            GatewayOwnershipScopes.System,
            ownerUserId: null,
            TestContext.Current.CancellationToken).ConfigureAwait(true);

        Assert.Equal(1, saved.ToolCount);
        Assert.Equal(["kautuka_web_fetch"], saved.ToolNames);
    }

    [Fact]
    public async Task SaveSystemMcpServerAsyncMarksConnectorAsInternalSystem()
    {
        using var temp = new TemporaryGatewayStateDirectory();
        GatewayState state = CreateState(temp.SqlitePath);
        var topologyWriter = new RecordingTopologyWriter();
        var workspaceTool = new NoOpWorkspaceRuntimeTool();
        var publisher = new RecordingDesiredStatePublisher();
        var store = new GatewayCatalogStore(
            state,
            new SqliteAgentDefinitionStore(CreateConfiguration(temp.SqlitePath)),
            topologyWriter,
            workspaceTool,
            new NoOpSkillPackageService(),
            publisher,
            new NoOpContinuationStore(),
            new NoOpConversationAssetStore());

        GatewayMcpServer saved = await store.SaveSystemMcpServerAsync(
            "kautuka-web-tools",
            new GatewayConnectorUpsertRequest
            {
                Name = "Kautuka Web Tools",
                Description = "Built-in web fetch tools.",
                PresetId = "kautuka-web-tools",
                Transport = "container",
                ContainerImage = "docker.io/example/image:latest",
                ContainerCommand = "python",
                ContainerArgs = [],
                BundlePath = "/var/lib/kautuka/mcpb-bundles/kautuka-web-tools/example.mcpb",
                ServerType = "uv",
                Enabled = false,
            },
            TestContext.Current.CancellationToken).ConfigureAwait(true);

        Assert.True(saved.IsInternalSystemConnector);
        Assert.False(saved.Enabled);
        Assert.Contains("connector-save", publisher.Reasons);
        Assert.True(store.TryGetMcpServer("kautuka-web-tools", out GatewayMcpServer? persisted));
        Assert.NotNull(persisted);
        Assert.True(persisted!.IsInternalSystemConnector);
    }

    [Fact]
    public void BlankWorkspaceRootResolvesUnderProdVisibleRootByDefaultOutsideDevelopment()
    {
        using var temp = new TemporaryGatewayStateDirectory();
        IConfiguration configuration = CreateConfiguration(
            temp.SqlitePath,
            new Dictionary<string, string?>
            {
                ["DOTNET_ENVIRONMENT"] = "Production",
                ["Kautuka:Workspace:BaseRoot"] = null,
            });
        GatewayState state = CreateState(temp.SqlitePath, configuration);
        var publisher = new RecordingDesiredStatePublisher();
        var store = new GatewaySettingsStore(
            state,
            new KautukaLogLevelSwitch(LogLevel.Warning),
            publisher,
            new NoOpUpdateServiceGrpcService(),
            configuration,
            new StaticPlatformConfigurationProvider(CreatePlatformConfiguration()));

        using JsonDocument body = JsonDocument.Parse(
            """
            {
              "kautuka": {
                "workspace": { "baseRoot": "" }
              }
            }
            """);

        store.Save(body.RootElement);

        Assert.Contains("system-settings-save", publisher.Reasons);
        Assert.Equal(Path.GetFullPath("/var/lib/kautuka/workspaces"), state.SystemSettings.WorkspaceBaseRoot);
    }

    private static GatewayState CreateState(string sqlitePath, IConfiguration? configuration = null)
    {
        configuration ??= CreateConfiguration(sqlitePath);

        var database = new KautukaDatabase(configuration);
        database.EnsureReady();

        return GatewayState.FromDatabase(
            database,
            NullLogger<GatewayState>.Instance,
            new GatewayMemoryStore(),
            new SqliteAgentDefinitionStore(configuration),
            configuration,
            new NoOpWorkspaceRuntimeTool());
    }

    private static IConfiguration CreateConfiguration(
        string sqlitePath,
        IReadOnlyDictionary<string, string?>? additionalSettings = null)
    {
        Dictionary<string, string?> settings = new()
        {
            ["Kautuka:ContainerStateRoot"] = Path.GetDirectoryName(sqlitePath) ?? throw new InvalidOperationException("Expected sqlite path to include a parent directory."),
            ["Kautuka:Workspace:BaseRoot"] = Path.Combine(Path.GetDirectoryName(sqlitePath)!, "workspaces"),
            ["DOTNET_ENVIRONMENT"] = "Development",
        };

        if (additionalSettings is not null)
        {
            foreach ((string key, string? value) in additionalSettings)
            {
                if (value is null)
                {
                    settings.Remove(key);
                }
                else
                {
                    settings[key] = value;
                }
            }
        }

        return new ConfigurationBuilder()
            .AddInMemoryCollection(settings)
            .Build();
    }

    private static PlatformConfiguration CreatePlatformConfiguration(
        IReadOnlyList<McpServerDefinition>? mcpServers = null,
        IReadOnlyDictionary<string, string>? sandboxEnvironment = null)
        => new()
        {
            Kautuka = new InfrastructureDefinition
            {
                Workspace = new WorkspaceDefinition
                {
                    BaseRoot = Path.Combine(Path.GetTempPath(), "kautuka-desired-state-tests"),
                },
                Sandbox = new SandboxRuntimeDefinition
                {
                    Image = "kautuka-base:latest",
                    DefaultCpus = 2,
                    DefaultMemory = "2g",
                    DefaultTimeout = 120,
                    ContainerEnvironment = sandboxEnvironment ?? new Dictionary<string, string>(),
                },
            },
            Agents = [],
            McpServers = mcpServers is null
                ? []
                : [.. mcpServers],
        };

    private sealed class RecordingDesiredStatePublisher : IWorkspaceDesiredStatePublisher
    {
        public List<string> Reasons { get; } = [];

        public DesiredStatePublishResult TryPublishCurrent(string reason)
        {
            Reasons.Add(reason);
            return DesiredStatePublishResult.Succeeded;
        }
    }

    private sealed class FailingUserCommandRepository : IGatewayUserCommandRepository
    {
        public bool TryCreateFirstAdmin(GatewayUser user) => throw new InvalidOperationException("Simulated authoritative write failure.");

        public void Upsert(GatewayUser user, bool passwordless = false) => throw new InvalidOperationException("Simulated authoritative write failure.");

        public bool TryCreateSocialAdmin(GatewayUser user, ExternalLoginLink link) => throw new InvalidOperationException("Simulated authoritative write failure.");

        public bool TryInsertExternalLogin(ExternalLoginLink link) => throw new InvalidOperationException("Simulated authoritative write failure.");

        public void DeleteExternalLogin(string provider, string subject) => throw new InvalidOperationException("Simulated authoritative write failure.");

        public void SetPasswordless(string userId, bool passwordless) => throw new InvalidOperationException("Simulated authoritative write failure.");
    }

    private sealed class NoOpTokenStore : IGatewayTokenStore
    {
        public string IssueAccessToken(string userId) => string.Empty;
        public string IssueRefreshToken(string userId) => string.Empty;
        public bool TryResolveAccessToken(string token, out string? userId) { userId = null; return false; }
        public bool TryResolveRefreshToken(string token, out string? userId) { userId = null; return false; }
        public void RevokeRefreshToken(string token) { }
        public bool TryRotateRefreshToken(string token, out string? userId, out string? replacementToken) { userId = null; replacementToken = null; return false; }
        public void RevokeUserSessions(string userId) { }
    }

    private sealed class StaticPlatformConfigurationProvider(PlatformConfiguration config) : IPlatformConfigurationProvider
    {
        public PlatformConfiguration GetCurrent() => config;
    }

    private sealed class NoOpUpdateServiceGrpcService : IUpdateServiceGrpcService
    {
        public Task<UpdateServiceBoolResponse> PingAsync(UpdateServicePingRequest request, CancellationToken cancellationToken = default)
            => Task.FromResult(new UpdateServiceBoolResponse { Ok = true, Message = request.Message });

        public Task<CatalogSnapshotResponse> GetCatalogSnapshotAsync(UpdateServicePingRequest request, CancellationToken cancellationToken = default)
            => Task.FromResult(new CatalogSnapshotResponse { Ok = true, Message = request.Message });

        public Task<UpdateStatusResponse> GetUpdateStatusAsync(UpdateServicePingRequest request, CancellationToken cancellationToken = default)
            => Task.FromResult(new UpdateStatusResponse { Ok = true, Message = "ok", WorkflowState = "Current" });

        public Task<UpdateStatusResponse> SetUpdatePolicyAsync(UpdatePolicyRequest request, CancellationToken cancellationToken = default)
            => Task.FromResult(new UpdateStatusResponse { Ok = true, Message = "ok", WorkflowState = "Current" });

        public Task<UpdateStatusResponse> RefreshUpdateDiscoveryAsync(UpdateServicePingRequest request, CancellationToken cancellationToken = default)
            => Task.FromResult(new UpdateStatusResponse { Ok = true, Message = "ok", WorkflowState = "Current" });

        public Task<UpdateStatusResponse> ApplyUpdateAsync(UpdateServicePingRequest request, CancellationToken cancellationToken = default)
            => Task.FromResult(new UpdateStatusResponse { Ok = true, Message = "ok", WorkflowState = "Applying" });

        public Task<UpdateStatusResponse> RevertUpdateAsync(UpdateServicePingRequest request, CancellationToken cancellationToken = default)
            => Task.FromResult(new UpdateStatusResponse { Ok = true, Message = "ok", WorkflowState = "Applying" });
    }

    private sealed class RecordingUpdateServiceGrpcService : IUpdateServiceGrpcService
    {
        public int SetPolicyCalls { get; private set; }

        public string? LastReleaseChannel { get; private set; }

        public Task<UpdateServiceBoolResponse> PingAsync(UpdateServicePingRequest request, CancellationToken cancellationToken = default)
            => Task.FromResult(new UpdateServiceBoolResponse { Ok = true, Message = request.Message });

        public Task<CatalogSnapshotResponse> GetCatalogSnapshotAsync(UpdateServicePingRequest request, CancellationToken cancellationToken = default)
            => Task.FromResult(new CatalogSnapshotResponse { Ok = true, Message = request.Message });

        public Task<UpdateStatusResponse> GetUpdateStatusAsync(UpdateServicePingRequest request, CancellationToken cancellationToken = default)
            => Task.FromResult(new UpdateStatusResponse { Ok = true, Message = "ok", WorkflowState = "Current" });

        public Task<UpdateStatusResponse> SetUpdatePolicyAsync(UpdatePolicyRequest request, CancellationToken cancellationToken = default)
        {
            SetPolicyCalls++;
            LastReleaseChannel = request.ReleaseChannel;
            return Task.FromResult(new UpdateStatusResponse
            {
                Ok = true,
                Message = "ok",
                WorkflowState = "Current",
                EffectiveReleaseChannel = request.ReleaseChannel,
            });
        }

        public Task<UpdateStatusResponse> RefreshUpdateDiscoveryAsync(UpdateServicePingRequest request, CancellationToken cancellationToken = default)
            => Task.FromResult(new UpdateStatusResponse { Ok = true, Message = "ok", WorkflowState = "Current" });

        public Task<UpdateStatusResponse> ApplyUpdateAsync(UpdateServicePingRequest request, CancellationToken cancellationToken = default)
            => Task.FromResult(new UpdateStatusResponse { Ok = true, Message = "ok", WorkflowState = "Applying" });

        public Task<UpdateStatusResponse> RevertUpdateAsync(UpdateServicePingRequest request, CancellationToken cancellationToken = default)
            => Task.FromResult(new UpdateStatusResponse { Ok = true, Message = "ok", WorkflowState = "Applying" });
    }

    private sealed class StaticGatewayUserStore : IGatewayUserStore
    {
        public bool TryGetUserById(string userId, out GatewayUser? user)
        {
            user = null;
            return false;
        }

        public bool TryGetUserByUsername(string username, out GatewayUser? user)
        {
            user = null;
            return false;
        }

        public IReadOnlyList<GatewayUserListItem> ListUsers() => [];
        public bool CanSetup => false;
        public GatewayUserListItem? CreateFirstAdmin(string username, string password, out string? error) { error = "not used"; return null; }
        public GatewayUserListItem? InviteUser(JsonElement body, out string? error) { error = "not used"; return null; }
        public bool UpdateRole(string userId, GatewayUserRoleUpdateRequest request, out string? error) { error = "not used"; return false; }
        public bool UpdateIsActive(string userId, bool isActive, out string? error) { error = "not used"; return false; }
        public bool UpdateImportPermissions(string userId, GatewayUserImportPermissionsUpdateRequest request) => false;
        public bool UpdateProfile(string userId, string? email, string? displayName) => false;
        public bool UpdatePassword(string userId, string currentPassword, string newPassword, out string? error) { error = "not used"; return false; }


        public bool TryClearExternalLogin(string userId, string provider, string actorId, out string? error)
        {
            error = null;
            return false;
        }

        public string[] ExternalProvidersFor(string userId) => [];
    }

    private sealed class RecordingTopologyWriter : ITopologyConfigurationWriter
    {
        public List<AgentRegistryEntry> SavedAgents { get; } = [];
        public List<string> DeletedAgents { get; } = [];

        public PlatformConfiguration SaveProvider(ProviderDefinition provider) => new();
        public PlatformConfiguration DeleteProvider(string providerId) => new();
        public PlatformConfiguration SaveModel(ModelDefinition model) => new();
        public PlatformConfiguration DeleteModel(string modelId) => new();

        public PlatformConfiguration SaveAgent(AgentRegistryEntry agent)
        {
            SavedAgents.Add(agent);
            return new();
        }

        public PlatformConfiguration DeleteAgent(string agentId)
        {
            DeletedAgents.Add(agentId);
            return new();
        }

        public PlatformConfiguration SaveMcpServer(McpServerDefinition server) => new();
        public PlatformConfiguration DeleteMcpServer(string serverId) => new();
        public PlatformConfiguration SaveSkill(SkillDefinition skill) => new();
        public PlatformConfiguration DeleteSkill(string skillId) => new();
    }

    private sealed class ThrowingAtomicAgentDefinitionStore(IAgentDefinitionStore innerStore, string failingStorageLocatorKey) : IAtomicAgentDefinitionStore, IAgentRuntimeSessionRepository, IAgentRuntimeRunRepository, IAgentMemoryFactRepository, IAgentContinuationCheckpointRepository
    {
        public bool SupportsAtomicRegistryTransactions => true;

        public string GetStorageLocatorKey(string agentId) => innerStore.GetStorageLocatorKey(agentId);

        public long SaveDefinition(string storageLocatorKey, AgentDefinition definition, AgentDefinitionSnapshotMetadata? snapshotMetadata = null)
            => innerStore.SaveDefinition(storageLocatorKey, definition, snapshotMetadata);

        public long SaveDefinitionAndRegistryAtomically(AgentRegistryEntry registryEntry, AgentDefinition definition, AgentDefinitionSnapshotMetadata? snapshotMetadata = null)
            => throw new NotSupportedException();

        public AgentDefinition? TryLoadLatestDefinition(string storageLocatorKey) => innerStore.TryLoadLatestDefinition(storageLocatorKey);

        public AgentDefinition? TryLoadLatestDefinition(string storageLocatorKey, out long revision)
            => innerStore.TryLoadLatestDefinition(storageLocatorKey, out revision);

        public IReadOnlyList<AgentDefinitionRevision> ListDefinitionRevisions(string storageLocatorKey)
            => innerStore.ListDefinitionRevisions(storageLocatorKey);

        public IReadOnlyList<AgentDefinitionSettingSnapshot> ListDefinitionSettings(string storageLocatorKey, long revision)
            => innerStore.ListDefinitionSettings(storageLocatorKey, revision);

        public IReadOnlyList<AgentDefinitionSettingSnapshot> ListAllDefinitionSettings(string storageLocatorKey)
            => innerStore.ListAllDefinitionSettings(storageLocatorKey);

        public IReadOnlyList<GatewaySession> ListRuntimeSessions(string storageLocatorKey)
            => ((IAgentRuntimeSessionRepository)innerStore).ListRuntimeSessions(storageLocatorKey);

        public void CreateRuntimeSession(string storageLocatorKey, GatewaySession session)
            => ((IAgentRuntimeSessionRepository)innerStore).CreateRuntimeSession(storageLocatorKey, session);

        public void UpdateRuntimeSession(string storageLocatorKey, GatewaySession session)
            => ((IAgentRuntimeSessionRepository)innerStore).UpdateRuntimeSession(storageLocatorKey, session);

        public bool DeleteRuntimeSession(string storageLocatorKey, string sessionId)
            => ((IAgentRuntimeSessionRepository)innerStore).DeleteRuntimeSession(storageLocatorKey, sessionId);

        public void AppendRuntimeSessionMessages(string storageLocatorKey, GatewaySession session, IReadOnlyList<GatewayMessage> appendedMessages)
            => ((IAgentRuntimeSessionRepository)innerStore).AppendRuntimeSessionMessages(storageLocatorKey, session, appendedMessages);

        public IReadOnlyList<GatewayRun> ListRuntimeRuns(string storageLocatorKey)
            => ((IAgentRuntimeRunRepository)innerStore).ListRuntimeRuns(storageLocatorKey);

        public void CreateRuntimeRun(string storageLocatorKey, GatewayRun run)
            => ((IAgentRuntimeRunRepository)innerStore).CreateRuntimeRun(storageLocatorKey, run);

        public void UpdateRuntimeRun(string storageLocatorKey, GatewayRun run)
            => ((IAgentRuntimeRunRepository)innerStore).UpdateRuntimeRun(storageLocatorKey, run);

        public IReadOnlyList<GatewayMemoryFact> ListMemoryFacts(string storageLocatorKey)
            => ((IAgentMemoryFactRepository)innerStore).ListMemoryFacts(storageLocatorKey);

        public void UpsertMemoryFacts(string storageLocatorKey, IReadOnlyList<GatewayMemoryFact> facts)
            => ((IAgentMemoryFactRepository)innerStore).UpsertMemoryFacts(storageLocatorKey, facts);

        public bool DeleteMemoryFact(string storageLocatorKey, string factId)
            => ((IAgentMemoryFactRepository)innerStore).DeleteMemoryFact(storageLocatorKey, factId);

        public void CreateContinuationCheckpoint(string storageLocatorKey, GatewayContinuationCheckpoint checkpoint)
            => ((IAgentContinuationCheckpointRepository)innerStore).CreateContinuationCheckpoint(storageLocatorKey, checkpoint);

        public int GetNextContinuationCheckpointIndex(string storageLocatorKey, string continuationRunId)
            => ((IAgentContinuationCheckpointRepository)innerStore).GetNextContinuationCheckpointIndex(storageLocatorKey, continuationRunId);

        public GatewayContinuationCheckpoint? GetContinuationCheckpointById(string storageLocatorKey, string checkpointId)
            => ((IAgentContinuationCheckpointRepository)innerStore).GetContinuationCheckpointById(storageLocatorKey, checkpointId);

        public GatewayContinuationCheckpoint? GetContinuationCheckpointByResumeTokenHash(string storageLocatorKey, string resumeTokenHash)
            => ((IAgentContinuationCheckpointRepository)innerStore).GetContinuationCheckpointByResumeTokenHash(storageLocatorKey, resumeTokenHash);

        public void DeleteContinuationCheckpointsBySessions(string storageLocatorKey, IReadOnlyCollection<string> sessionIds)
            => ((IAgentContinuationCheckpointRepository)innerStore).DeleteContinuationCheckpointsBySessions(storageLocatorKey, sessionIds);

        public NaradPersistedEvent AppendNaradAuthorityEvent(string rootStorageLocatorKey, string sourceStorageLocatorKey, NaradAuthorityEventDraft draft)
            => innerStore.AppendNaradAuthorityEvent(rootStorageLocatorKey, sourceStorageLocatorKey, draft);

        public NaradPersistedEvent AppendNaradParentSessionProjection(
            string rootStorageLocatorKey,
            NaradPersistedEvent sourceEvent,
            string parentSessionId)
            => innerStore.AppendNaradParentSessionProjection(rootStorageLocatorKey, sourceEvent, parentSessionId);

        public void MirrorNaradAuthorityEvent(string sourceStorageLocatorKey, NaradPersistedEvent persistedEvent)
            => innerStore.MirrorNaradAuthorityEvent(sourceStorageLocatorKey, persistedEvent);

        public IReadOnlyList<NaradPersistedEvent> ListNaradSessionHistory(string storageLocatorKey, string sessionId)
            => innerStore.ListNaradSessionHistory(storageLocatorKey, sessionId);

        public IReadOnlyList<NaradPersistedEvent> ListNaradRootHistory(string storageLocatorKey, string rootRunId)
            => innerStore.ListNaradRootHistory(storageLocatorKey, rootRunId);

        public string? FindNaradRootStorageLocatorKey(string rootRunId)
            => innerStore.FindNaradRootStorageLocatorKey(rootRunId);

        public IReadOnlyList<NaradPersistedEvent> ListNaradSessionHistory(string storageLocatorKey, string sessionId, long afterSessionSequence, int limit)
            => innerStore.ListNaradSessionHistory(storageLocatorKey, sessionId, afterSessionSequence, limit);

        public IReadOnlyList<NaradPersistedEvent> ListNaradSessionHistoryFull(string storageLocatorKey, string sessionId)
            => innerStore.ListNaradSessionHistoryFull(storageLocatorKey, sessionId);

        public IReadOnlyList<NaradPersistedEvent> ListNaradRootHistory(string storageLocatorKey, string rootRunId, long afterRootSequence, int limit)
            => innerStore.ListNaradRootHistory(storageLocatorKey, rootRunId, afterRootSequence, limit);

        public IReadOnlyList<NaradPersistedEvent> ListNaradRootHistoryFull(string storageLocatorKey, string rootRunId)
            => innerStore.ListNaradRootHistoryFull(storageLocatorKey, rootRunId);

        public long ReadNaradSessionProjectionRevision(string storageLocatorKey, string sessionId)
            => innerStore.ReadNaradSessionProjectionRevision(storageLocatorKey, sessionId);

        public bool IsNaradRootRunAdmissionClosed(string storageLocatorKey, string rootRunId)
            => innerStore.IsNaradRootRunAdmissionClosed(storageLocatorKey, rootRunId);

        public IReadOnlyList<string> ListNaradRootRunIdsForSession(string storageLocatorKey, string sessionId)
            => innerStore.ListNaradRootRunIdsForSession(storageLocatorKey, sessionId);

        public IReadOnlyList<string> ListInterruptedNaradRootRuns(string startedBefore)
            => innerStore.ListInterruptedNaradRootRuns(startedBefore);

        public IReadOnlyList<NaradRuntimeEventOutboxEntry> ListPendingNaradEventOutbox(string rootStorageLocatorKey, int limit)
            => innerStore.ListPendingNaradEventOutbox(rootStorageLocatorKey, limit);

        public IReadOnlyList<NaradRuntimeEventOutboxEntry> ClaimNaradEventOutbox(string rootStorageLocatorKey, string leaseOwner, TimeSpan leaseDuration, int limit)
            => innerStore.ClaimNaradEventOutbox(rootStorageLocatorKey, leaseOwner, leaseDuration, limit);

        public void MarkNaradEventPublished(string rootStorageLocatorKey, string eventId, string leaseOwner)
            => innerStore.MarkNaradEventPublished(rootStorageLocatorKey, eventId, leaseOwner);

        public void MarkNaradEventMirrored(string rootStorageLocatorKey, string eventId, string leaseOwner)
            => innerStore.MarkNaradEventMirrored(rootStorageLocatorKey, eventId, leaseOwner);

        public void RecordNaradEventOutboxFailure(string rootStorageLocatorKey, string eventId, string leaseOwner, string safeError)
            => innerStore.RecordNaradEventOutboxFailure(rootStorageLocatorKey, eventId, leaseOwner, safeError);

        public IReadOnlyList<NaradRuntimeEventOutboxDeadLetter> ListNaradEventOutboxDeadLetters(string rootStorageLocatorKey, int limit)
            => innerStore.ListNaradEventOutboxDeadLetters(rootStorageLocatorKey, limit);

        public bool RetryNaradEventOutboxDeadLetter(string rootStorageLocatorKey, string eventId)
            => innerStore.RetryNaradEventOutboxDeadLetter(rootStorageLocatorKey, eventId);

        public bool DismissNaradEventOutboxDeadLetter(string rootStorageLocatorKey, string eventId)
            => innerStore.DismissNaradEventOutboxDeadLetter(rootStorageLocatorKey, eventId);

        public IReadOnlyList<string> ListNaradEventOutboxStorageLocatorKeys()
            => innerStore.ListNaradEventOutboxStorageLocatorKeys();

        public string? FindNaradRootRunIdForRun(string storageLocatorKey, string runId)
            => innerStore.FindNaradRootRunIdForRun(storageLocatorKey, runId);

        public void UpsertMemoryEmbeddings(string storageLocatorKey, IReadOnlyList<AgentMemoryEmbeddingRecord> embeddings)
            => innerStore.UpsertMemoryEmbeddings(storageLocatorKey, embeddings);

        public void DeleteMemoryEmbedding(string storageLocatorKey, string factId)
            => innerStore.DeleteMemoryEmbedding(storageLocatorKey, factId);

        public IReadOnlyDictionary<string, AgentMemoryEmbeddingState> ListMemoryEmbeddingStates(string storageLocatorKey)
            => innerStore.ListMemoryEmbeddingStates(storageLocatorKey);

        public IReadOnlyList<AgentMemorySearchHit> SearchMemoryEmbeddings(string storageLocatorKey, string modelKey, ReadOnlySpan<float> queryVector, int limit)
            => innerStore.SearchMemoryEmbeddings(storageLocatorKey, modelKey, queryVector, limit);

        public bool DeleteStore(string storageLocatorKey) => innerStore.DeleteStore(storageLocatorKey);

        public bool DeleteStoreAndRegistryAtomically(string agentId, string storageLocatorKey)
        {
            if (string.Equals(storageLocatorKey, failingStorageLocatorKey, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Simulated atomic delete failure.");
            }

            return innerStore.DeleteStore(storageLocatorKey);
        }
    }

    private sealed class NoOpContinuationStore : IGatewayContinuationStore
    {
        public void CreateCheckpoint(GatewayContinuationCheckpoint checkpoint)
        {
        }

        public int GetNextCheckpointIndex(string continuationRunId) => 0;

        public GatewayContinuationCheckpoint? GetById(string checkpointId) => null;

        public GatewayContinuationCheckpoint? GetByResumeTokenHash(string resumeTokenHash) => null;

        public void DeleteBySessions(IReadOnlyCollection<string> sessionIds)
        {
        }
    }

    private sealed class NoOpConversationAssetStore : IConversationAssetStore
    {
        public Task<GatewayConversationAsset> CreateUploadAsync(string sessionId, string? userId, AssetUploadCommand command, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public IReadOnlyDictionary<string, IReadOnlyList<GatewayConversationAsset>> ListAssetsByMessage(string sessionId, IReadOnlyList<string> messageIds)
            => new Dictionary<string, IReadOnlyList<GatewayConversationAsset>>();

        public IReadOnlyList<GatewayConversationAsset> ListSessionAssets(string sessionId, string? userId, IReadOnlyList<string>? assetIds = null)
            => [];

        public IReadOnlyList<StoredConversationAsset> GetAssetsByIds(string sessionId, IReadOnlyList<string> assetIds, string? userId)
            => [];

        public bool HasRetainedWorkspaceReference(string agentId, string relativePath, string ownerUserId)
            => false;

        public IReadOnlyList<WorkspaceAttachmentReferenceSummary> ListRetainedWorkspaceAttachmentReferences(string agentId)
            => [];

        public IReadOnlyList<WorkspaceAttachmentFileSummary> ListUnreferencedWorkspaceAttachmentFiles(string agentId)
            => [];

        public string? FindLatestAssetIdByFileName(string sessionId, string fileName, string? userId) => null;

        public (string SessionId, string AssetId)? FindLatestAssetReferenceByFileName(string fileName, string? userId) => null;

        public void BindAssetsToMessage(string sessionId, string messageId, IReadOnlyList<string> assetIds, string? userId)
        {
        }

        public bool TryOpenAsset(string sessionId, string assetId, string? userId, out AssetDownloadResult? result)
        {
            result = null;
            return false;
        }

        public void DeleteSessionAssets(IReadOnlyList<string> sessionIds)
        {
        }
    }

    private sealed class NoOpWorkspaceRuntimeTool : IWorkspaceRuntimeTool
    {
        public WorkspaceRuntimeContext DescribeAgentWorkspace(string agentId, string? userId = null) => CreateContext(agentId, userId);
        public WorkspaceRuntimeContext RebuildAgentWorkspace(string agentId, string? userId = null) => CreateContext(agentId, userId);
        public bool DeleteAgentWorkspace(string agentId) => true;
        public Task<WorkspaceSandboxSession> SpawnAgentSandboxAsync(string agentId, string? userId = null, CancellationToken cancellationToken = default) => Task.FromResult(CreateSession(agentId, userId));
        public Task<WorkspaceCommandResult> RunAgentShellAsync(string agentId, string command, string? userId = null, int? timeoutSeconds = null, CancellationToken cancellationToken = default) => Task.FromResult(new WorkspaceCommandResult("completed", 0, string.Empty, string.Empty));
        public Task<WorkspaceSandboxSession> InspectAgentSandboxAsync(string agentId, string? userId = null, CancellationToken cancellationToken = default) => Task.FromResult(CreateSession(agentId, userId));
        public Task CloseAgentSandboxAsync(string agentId, string? userId = null, CancellationToken cancellationToken = default) => Task.CompletedTask;

        private static WorkspaceRuntimeContext CreateContext(string agentId, string? userId)
            => new("agent", agentId, userId, Path.GetTempPath(), "/workspace", "/runtime-workspace", "kautuka-base:latest", 120, [], new Dictionary<string, string>());

        private static WorkspaceSandboxSession CreateSession(string agentId, string? userId)
            => new($"agent-{agentId}", false, "agent", agentId, userId);
    }

    private sealed class CountingWorkspaceRuntimeTool : IWorkspaceRuntimeTool
    {
        public int RebuildCalls { get; private set; }

        public WorkspaceRuntimeContext DescribeAgentWorkspace(string agentId, string? userId = null) => CreateContext(agentId, userId);

        public WorkspaceRuntimeContext RebuildAgentWorkspace(string agentId, string? userId = null)
        {
            RebuildCalls++;
            return CreateContext(agentId, userId);
        }

        public bool DeleteAgentWorkspace(string agentId) => true;

        public Task<WorkspaceSandboxSession> SpawnAgentSandboxAsync(string agentId, string? userId = null, CancellationToken cancellationToken = default)
            => Task.FromResult(CreateSession(agentId, userId));

        public Task<WorkspaceCommandResult> RunAgentShellAsync(string agentId, string command, string? userId = null, int? timeoutSeconds = null, CancellationToken cancellationToken = default)
            => Task.FromResult(new WorkspaceCommandResult("completed", 0, string.Empty, string.Empty));

        public Task<WorkspaceSandboxSession> InspectAgentSandboxAsync(string agentId, string? userId = null, CancellationToken cancellationToken = default)
            => Task.FromResult(CreateSession(agentId, userId));

        public Task CloseAgentSandboxAsync(string agentId, string? userId = null, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        private static WorkspaceRuntimeContext CreateContext(string agentId, string? userId)
            => new("agent", agentId, userId, Path.GetTempPath(), "/workspace", "/runtime-workspace", "kautuka-base:latest", 120, [], new Dictionary<string, string>());

        private static WorkspaceSandboxSession CreateSession(string agentId, string? userId)
            => new($"agent-{agentId}", false, "agent", agentId, userId);
    }

    private sealed class NoOpSkillPackageService : ISkillPackageService
    {
        public Task<SkillPackageImportOutcome> ValidateImportOutcomeAsync(Stream archiveStream, SkillPackageImportRequest request, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task<SkillPackageImportOutcome> ImportOutcomeAsync(Stream archiveStream, SkillPackageImportRequest request, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public SkillPackageInspection Inspect(SkillDefinition skill)
            => throw new NotSupportedException();

        public string ReadResourceText(SkillDefinition skill, string resourcePath)
            => throw new NotSupportedException();

        public string StagePackageForWorkspace(SkillDefinition skill, string workspaceHostPath, string containerWorkspacePath)
            => throw new NotSupportedException();

        public string ConfinePackagePath(string skillId, string? requestedPath, string? existingPath)
            => string.IsNullOrWhiteSpace(requestedPath) ? existingPath ?? string.Empty : requestedPath;

        public void DeletePackage(string skillId, string packagePath)
        {
        }
    }

    private sealed class TemporaryGatewayStateDirectory : IDisposable
    {
        private readonly string root = Path.Combine(Path.GetTempPath(), "kautuka-workspace-boundary-tests", Guid.NewGuid().ToString("N"));

        public string Root => root;

        public TemporaryGatewayStateDirectory()
        {
            Directory.CreateDirectory(root);
        }

        public string SqlitePath => Path.Combine(root, "gateway.db");

        public void Dispose()
        {
            SqliteTestPoolCleanup.ClearPoolsUnderDirectory(root);
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }
}
