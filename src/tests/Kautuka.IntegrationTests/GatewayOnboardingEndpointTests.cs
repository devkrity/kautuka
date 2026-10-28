using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;

using Grpc.Core;

using Kautuka.Gateway.Domain.Assets;
using Kautuka.Gateway.Domain.Auth;
using Kautuka.Gateway.Domain.Connectors;
using Kautuka.Gateway.Domain.Execution;
using Kautuka.Gateway.Domain.Memory;
using Kautuka.Gateway.Domain.Sandbox;
using Kautuka.Gateway.Domain.Skills;
using Kautuka.Gateway.Domain.Stores;
using Kautuka.Gateway.Endpoints.Catalog;
using Kautuka.Gateway.Infrastructure.Catalog;
using Kautuka.Gateway.Infrastructure.Updates;
using Kautuka.UpdateService.Protos;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Kautuka.IntegrationTests;

public sealed class GatewayOnboardingEndpointTests
{
    [Fact]
    public async Task CatalogRecommendationsDoNotMutateSeededMarkitdownConnectorAsync()
    {
        using var temp = new TemporaryGatewayStateDirectory();
        IConfiguration configuration = CreateConfiguration(temp.SqlitePath);
        string databasePath = Kautuka.Gateway.Infrastructure.Database.KautukaDatabase.ResolvePath(configuration);
        GatewayState state = CreateState(configuration);
        var catalogStore = new GatewayCatalogStore(
            state,
            new SqliteAgentDefinitionStore(configuration),
            new NoOpTopologyWriter(),
            new NoOpWorkspaceRuntimeTool(),
            new NoOpSkillPackageService(),
            new NoOpDesiredStatePublisher(),
            new NoOpContinuationStore(),
            new NoOpConversationAssetStore());
        var updateService = new RecordingUpdateServiceGrpcService();

        ConnectorRow seededBefore = ReadConnectorRow(databasePath, "markitdown");

        await using WebApplication app = await CreateAppAsync(catalogStore, updateService).ConfigureAwait(true);

        using HttpResponseMessage response = await app.GetTestClient().GetAsync(
            new Uri("http://localhost/api/catalog/recommendations"),
            TestContext.Current.CancellationToken).ConfigureAwait(true);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        JsonNode? json = JsonNode.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken).ConfigureAwait(true));
        Assert.Equal("kautuka-web-tools", json?["connectors"]?[0]?["id"]?.GetValue<string>());
        Assert.False(json?["connectors"]?[0]?["isAvailable"]?.GetValue<bool>() ?? true);
        Assert.Equal("kautuka-docs", json?["skills"]?[0]?["id"]?.GetValue<string>());

        ConnectorRow seededAfter = ReadConnectorRow(databasePath, "markitdown");
        Assert.Equal(seededBefore, seededAfter);
        Assert.Equal(["markitdown"], catalogStore.ListMcpServers().Select(static server => server.Id).OrderBy(static id => id).ToArray());
    }

    [Fact]
    public async Task CatalogRecommendationsFailureLeavesSeededMarkitdownConnectorUnchangedAsync()
    {
        using var temp = new TemporaryGatewayStateDirectory();
        IConfiguration configuration = CreateConfiguration(temp.SqlitePath);
        string databasePath = Kautuka.Gateway.Infrastructure.Database.KautukaDatabase.ResolvePath(configuration);
        GatewayState state = CreateState(configuration);
        var catalogStore = new GatewayCatalogStore(
            state,
            new SqliteAgentDefinitionStore(configuration),
            new NoOpTopologyWriter(),
            new NoOpWorkspaceRuntimeTool(),
            new NoOpSkillPackageService(),
            new NoOpDesiredStatePublisher(),
            new NoOpContinuationStore(),
            new NoOpConversationAssetStore());
        var updateService = new RecordingUpdateServiceGrpcService
        {
            CatalogSnapshotException = new RpcException(new Status(StatusCode.Unavailable, "catalog refresh failed"))
        };

        ConnectorRow seededBefore = ReadConnectorRow(databasePath, "markitdown");

        await using WebApplication app = await CreateAppAsync(catalogStore, updateService).ConfigureAwait(true);

        using HttpResponseMessage response = await app.GetTestClient().GetAsync(
            new Uri("http://localhost/api/catalog/recommendations"),
            TestContext.Current.CancellationToken).ConfigureAwait(true);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        JsonNode? json = JsonNode.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken).ConfigureAwait(true));
        Assert.Empty(json?["connectors"]?.AsArray() ?? []);
        Assert.Empty(json?["skills"]?.AsArray() ?? []);
        Assert.Equal("Hosted catalog is currently unreachable. Check again later.", json?["message"]?.GetValue<string>());

        ConnectorRow seededAfter = ReadConnectorRow(databasePath, "markitdown");
        Assert.Equal(seededBefore, seededAfter);
        Assert.Equal(["markitdown"], catalogStore.ListMcpServers().Select(static server => server.Id).OrderBy(static id => id).ToArray());
    }

    [Fact]
    public async Task CatalogRecommendationInstallImportsConnectorAsync()
    {
        using var temp = new TemporaryGatewayStateDirectory();
        IConfiguration configuration = CreateConfiguration(temp.SqlitePath);
        GatewayState state = CreateState(configuration);
        var catalogStore = new GatewayCatalogStore(
            state,
            new SqliteAgentDefinitionStore(configuration),
            new NoOpTopologyWriter(),
            new NoOpWorkspaceRuntimeTool(),
            new NoOpSkillPackageService(),
            new NoOpDesiredStatePublisher(),
            new NoOpContinuationStore(),
            new NoOpConversationAssetStore());

        byte[] bundleBytes = Encoding.UTF8.GetBytes("connector-bundle");
        string artifactUrl = "https://catalog.test/artifacts/kautuka-web-tools.mcpb";
        string artifactSha256 = Convert.ToHexString(SHA256.HashData(bundleBytes));
        var updateService = new RecordingUpdateServiceGrpcService
        {
            CatalogEntries =
            [
                CreateCatalogEntry(
                    id: "kautuka-web-tools",
                    kind: "connector",
                    artifactUrl: artifactUrl,
                    artifactSha256: artifactSha256)
            ]
        };

        await using WebApplication app = await CreateAppAsync(
            catalogStore,
            updateService,
            services =>
            {
                services.AddSingleton<IHttpClientFactory>(new StaticHttpClientFactory(new ArtifactResponseMessageHandler(new Dictionary<string, byte[]>(StringComparer.Ordinal)
                {
                    [artifactUrl] = bundleBytes,
                })));
                services.AddSingleton<IMcpbPackageService>(new RecordingMcpbPackageService());
                services.AddSingleton<IMcpConnectorTestService>(new RecordingMcpConnectorTestService());
                services.AddSingleton<ISkillPackageService>(new NoOpSkillPackageService());
                services.AddSingleton<IGatewayHostedCatalogInstaller, GatewayHostedCatalogInstaller>();
            }).ConfigureAwait(true);

        using HttpResponseMessage response = await app.GetTestClient().PostAsync(
            new Uri("http://localhost/api/catalog/recommendations/connector/kautuka-web-tools/install"),
            new StringContent("{}", Encoding.UTF8, "application/json"),
            TestContext.Current.CancellationToken).ConfigureAwait(true);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        JsonNode? json = JsonNode.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken).ConfigureAwait(true));
        Assert.True(json?["saved"]?.GetValue<bool>() ?? false);

        Assert.True(catalogStore.TryGetMcpServer("kautuka-web-tools", out GatewayMcpServer? connector));
        Assert.NotNull(connector);
        Assert.Equal("Kautuka Web Tools", connector!.Name);
        Assert.True(connector.Enabled);
    }

    [Fact]
    public async Task CatalogRecommendationInstallImportsSkillAsync()
    {
        using var temp = new TemporaryGatewayStateDirectory();
        IConfiguration configuration = CreateConfiguration(temp.SqlitePath);
        GatewayState state = CreateState(configuration);
        var catalogStore = new GatewayCatalogStore(
            state,
            new SqliteAgentDefinitionStore(configuration),
            new NoOpTopologyWriter(),
            new NoOpWorkspaceRuntimeTool(),
            new NoOpSkillPackageService(),
            new NoOpDesiredStatePublisher(),
            new NoOpContinuationStore(),
            new NoOpConversationAssetStore());

        byte[] bundleBytes = Encoding.UTF8.GetBytes("skill-bundle");
        string artifactUrl = "https://catalog.test/artifacts/kautuka-docs.zip";
        string artifactSha256 = Convert.ToHexString(SHA256.HashData(bundleBytes));
        var updateService = new RecordingUpdateServiceGrpcService
        {
            CatalogEntries =
            [
                CreateCatalogEntry(
                    id: "kautuka-docs",
                    kind: "skill",
                    artifactUrl: artifactUrl,
                    artifactSha256: artifactSha256)
            ]
        };

        await using WebApplication app = await CreateAppAsync(
            catalogStore,
            updateService,
            services =>
            {
                services.AddSingleton<IHttpClientFactory>(new StaticHttpClientFactory(new ArtifactResponseMessageHandler(new Dictionary<string, byte[]>(StringComparer.Ordinal)
                {
                    [artifactUrl] = bundleBytes,
                })));
                services.AddSingleton<IMcpbPackageService>(new RecordingMcpbPackageService());
                services.AddSingleton<IMcpConnectorTestService>(new RecordingMcpConnectorTestService());
                services.AddSingleton<ISkillPackageService>(new RecordingSkillPackageService());
                services.AddSingleton<IGatewayHostedCatalogInstaller, GatewayHostedCatalogInstaller>();
            }).ConfigureAwait(true);

        using HttpResponseMessage response = await app.GetTestClient().PostAsync(
            new Uri("http://localhost/api/catalog/recommendations/skill/kautuka-docs/install"),
            new StringContent("{}", Encoding.UTF8, "application/json"),
            TestContext.Current.CancellationToken).ConfigureAwait(true);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        JsonNode? json = JsonNode.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken).ConfigureAwait(true));
        Assert.True(json?["saved"]?.GetValue<bool>() ?? false);

        Assert.True(catalogStore.TryGetSkill("kautuka-docs", out GatewaySkill? skill));
        Assert.NotNull(skill);
        Assert.Equal("Kautuka Docs", skill!.Name);
        Assert.True(skill.Enabled);
        Assert.Equal("hosted_catalog", skill.SourceMetadata["source"]);
    }

    private static async Task<WebApplication> CreateAppAsync(
        IGatewayCatalogStore catalogStore,
        RecordingUpdateServiceGrpcService updateService,
        Action<IServiceCollection>? configureServices = null)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.ConfigureHttpJsonOptions(options => options.SerializerOptions.TypeInfoResolver = new DefaultJsonTypeInfoResolver());
        builder.Services.AddSingleton<IGatewayAuthGuard, AllowAllAuthGuard>();
        builder.Services.AddSingleton<IUpdateServiceGrpcService>(updateService);
        builder.Services.AddSingleton(catalogStore);
        builder.Services.AddSingleton<IGatewayHostedCatalogProvider, UpdateServiceHostedCatalogProvider>();
        configureServices?.Invoke(builder.Services);

        WebApplication app = builder.Build();
        app.MapGatewayCatalogEndpoints();
        await app.StartAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);
        return app;
    }

    private static GatewayState CreateState(IConfiguration configuration)
    {
        var database = new Kautuka.Gateway.Infrastructure.Database.KautukaDatabase(configuration);
        database.EnsureReady();

        return GatewayState.FromDatabase(
            database,
            NullLogger<GatewayState>.Instance,
            new GatewayMemoryStore(),
            new SqliteAgentDefinitionStore(configuration),
            configuration,
            new NoOpWorkspaceRuntimeTool());
    }

    private static IConfiguration CreateConfiguration(string sqlitePath)
        => new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Kautuka:ContainerStateRoot"] = Path.GetDirectoryName(sqlitePath) ?? throw new InvalidOperationException("Expected sqlite path to include a parent directory."),
                ["DOTNET_ENVIRONMENT"] = "Development",
            })
            .Build();

    private static ConnectorRow ReadConnectorRow(string sqlitePath, string id)
    {
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = sqlitePath,
            Mode = SqliteOpenMode.ReadWrite,
            Cache = SqliteCacheMode.Shared,
            Pooling = true,
        }.ToString();

        using var connection = new SqliteConnection(connectionString);
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT id, preset_id, transport, container_image, container_sidecar_name, enabled, is_internal_system_connector FROM mcp_servers WHERE id = $id";
        command.Parameters.AddWithValue("$id", id);

        using SqliteDataReader reader = command.ExecuteReader();
        Assert.True(reader.Read());

        return new ConnectorRow(
            Id: reader.GetString(0),
            PresetId: reader.IsDBNull(1) ? null : reader.GetString(1),
            Transport: reader.IsDBNull(2) ? null : reader.GetString(2),
            ContainerImage: reader.IsDBNull(3) ? null : reader.GetString(3),
            ContainerSidecarName: reader.IsDBNull(4) ? null : reader.GetString(4),
            Enabled: !reader.IsDBNull(5) && reader.GetInt64(5) == 1,
            IsInternalSystemConnector: !reader.IsDBNull(6) && reader.GetInt64(6) == 1);
    }

    private sealed record ConnectorRow(
        string Id,
        string? PresetId,
        string? Transport,
        string? ContainerImage,
        string? ContainerSidecarName,
        bool Enabled,
        bool IsInternalSystemConnector);

    private sealed class AllowAllAuthGuard : IGatewayAuthGuard
    {
        public bool TryAuthorize(HttpContext context, out GatewayUser? user)
        {
            user = BuildUser();
            return true;
        }

        public IResult? RequireAuthenticated(HttpContext context, out GatewayUser? user)
        {
            user = BuildUser();
            return null;
        }

        public IResult? RequireAdmin(HttpContext context, out GatewayUser? user)
        {
            user = BuildUser();
            return null;
        }

        private static GatewayUser BuildUser() => new(
            Id: "user-1",
            Username: "tester",
            Password: "pwd",
            Role: "admin",
            IsActive: true,
            Email: null,
            DisplayName: null);
    }

    private sealed class RecordingUpdateServiceGrpcService : IUpdateServiceGrpcService
    {
        public Exception? CatalogSnapshotException { get; init; }

        public IReadOnlyList<CatalogEntryMessage>? CatalogEntries { get; init; }

        public Task<UpdateServiceBoolResponse> PingAsync(UpdateServicePingRequest request, CancellationToken cancellationToken = default)
            => Task.FromResult(new UpdateServiceBoolResponse { Ok = true, Message = request.Message });

        public Task<CatalogSnapshotResponse> GetCatalogSnapshotAsync(UpdateServicePingRequest request, CancellationToken cancellationToken = default)
        {
            if (CatalogSnapshotException is not null)
            {
                return Task.FromException<CatalogSnapshotResponse>(CatalogSnapshotException);
            }

            CatalogSnapshotResponse response = new()
            {
                Ok = true,
                Message = request.Message,
            };

            response.Entries.AddRange(CatalogEntries ??
            [
                CreateCatalogEntry(id: "kautuka-web-tools", kind: "connector", artifactUrl: null, artifactSha256: null),
                CreateCatalogEntry(id: "kautuka-docs", kind: "skill", artifactUrl: null, artifactSha256: null),
            ]);

            return Task.FromResult(response);
        }

        public Task<UpdateStatusResponse> GetUpdateStatusAsync(UpdateServicePingRequest request, CancellationToken cancellationToken = default)
            => Task.FromResult(new UpdateStatusResponse { Ok = true, Message = request.Message, WorkflowState = "Current" });

        public Task<UpdateStatusResponse> SetUpdatePolicyAsync(UpdatePolicyRequest request, CancellationToken cancellationToken = default)
            => Task.FromResult(new UpdateStatusResponse { Ok = true, Message = "ok", WorkflowState = "Current" });

        public Task<UpdateStatusResponse> RefreshUpdateDiscoveryAsync(UpdateServicePingRequest request, CancellationToken cancellationToken = default)
            => Task.FromResult(new UpdateStatusResponse { Ok = true, Message = "ok", WorkflowState = "Current" });

        public Task<UpdateStatusResponse> ApplyUpdateAsync(UpdateServicePingRequest request, CancellationToken cancellationToken = default)
            => Task.FromResult(new UpdateStatusResponse { Ok = true, Message = "ok", WorkflowState = "Applying" });

        public Task<UpdateStatusResponse> RevertUpdateAsync(UpdateServicePingRequest request, CancellationToken cancellationToken = default)
            => Task.FromResult(new UpdateStatusResponse { Ok = true, Message = "ok", WorkflowState = "Applying" });
    }

    private sealed class NoOpTopologyWriter : ITopologyConfigurationWriter
    {
        public PlatformConfiguration SaveProvider(ProviderDefinition provider) => new();
        public PlatformConfiguration DeleteProvider(string providerId) => new();
        public PlatformConfiguration SaveModel(ModelDefinition model) => new();
        public PlatformConfiguration DeleteModel(string modelId) => new();
        public PlatformConfiguration SaveAgent(AgentRegistryEntry agent) => new();
        public PlatformConfiguration DeleteAgent(string agentId) => new();
        public PlatformConfiguration SaveMcpServer(McpServerDefinition server) => new();
        public PlatformConfiguration DeleteMcpServer(string serverId) => new();
        public PlatformConfiguration SaveSkill(SkillDefinition skill) => new();
        public PlatformConfiguration DeleteSkill(string skillId) => new();
    }

    private sealed class NoOpDesiredStatePublisher : Kautuka.Gateway.Infrastructure.Sandbox.IWorkspaceDesiredStatePublisher
    {
        public Kautuka.Gateway.Infrastructure.Sandbox.DesiredStatePublishResult TryPublishCurrent(string reason)
            => Kautuka.Gateway.Infrastructure.Sandbox.DesiredStatePublishResult.Succeeded;
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

    private sealed class RecordingSkillPackageService : ISkillPackageService
    {
        public Task<SkillPackageImportOutcome> ValidateImportOutcomeAsync(Stream archiveStream, SkillPackageImportRequest request, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task<SkillPackageImportOutcome> ImportOutcomeAsync(Stream archiveStream, SkillPackageImportRequest request, CancellationToken cancellationToken)
            => Task.FromResult(new SkillPackageImportOutcome(
                false,
                null,
                [
                    new SkillPackageInspection(
                        new SkillDefinition
                        {
                            SkillId = "kautuka-docs",
                            Name = "Kautuka Docs",
                            Description = "Built-in product guidance and reference material.",
                            PackageHash = "skill-package-hash",
                            Status = "ready",
                            PackagePath = "/tmp/kautuka-docs.zip",
                            HasResources = true,
                            HasScripts = false,
                            Enabled = true,
                            SourceMetadata = request.SourceMetadata ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
                        },
                        "# Docs",
                        [],
                        [],
                        []),
                ]));

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

    private sealed class RecordingMcpbPackageService : IMcpbPackageService
    {
        public Task<McpbConnectorInspection> InspectAsync(Stream archiveStream, string fileName, CancellationToken cancellationToken)
            => Task.FromResult(new McpbConnectorInspection(
                "kautuka-web-tools",
                new GatewayConnectorUpsertRequest
                {
                    Name = "Kautuka Web Tools",
                    Description = "Built-in browser and page-inspection tools.",
                    Transport = "container",
                    ContainerImage = "docker.io/kautuka/web-tools:latest",
                    ContainerCommand = "node",
                    ContainerArgs = ["server.js"],
                    BundlePath = "/tmp/kautuka-web-tools.mcpb",
                    ServerType = "node",
                    RuntimeArtifactType = "oci",
                    RuntimeArtifactSourceDigest = "bundle-digest",
                    RuntimeArtifactBuildStatus = "pending",
                    Enabled = true,
                    ReadinessTimeoutSeconds = 60,
                    JitCooldownSeconds = 600,
                },
                "0.4",
                "node",
                "Kautuka Web Tools"));
    }

    private sealed class RecordingMcpConnectorTestService : IMcpConnectorTestService
    {
        public Task<McpConnectorTestResult> TestAsync(GatewayConnectorTestRequest request, CancellationToken ct)
            => Task.FromResult(new McpConnectorTestResult(
                true,
                "Connector validated.",
                ToolCount: 2,
                PackageChecked: request.BundlePath,
                ToolNames: ["open_page", "extract_text"]));
    }

    private sealed class StaticHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class ArtifactResponseMessageHandler(IReadOnlyDictionary<string, byte[]> artifacts) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string key = request.RequestUri?.ToString() ?? string.Empty;
            if (!artifacts.TryGetValue(key, out byte[]? content))
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(content),
            });
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

    private sealed class TemporaryGatewayStateDirectory : IDisposable
    {
        private readonly string root = Path.Combine(Path.GetTempPath(), "kautuka-onboarding-endpoint-tests", Guid.NewGuid().ToString("N"));

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

    private static CatalogEntryMessage CreateCatalogEntry(string id, string kind, string? artifactUrl, string? artifactSha256)
        => new()
        {
            Id = id,
            Kind = kind,
            Title = string.Equals(kind, "connector", StringComparison.OrdinalIgnoreCase) ? "Kautuka Web Tools" : "Kautuka Docs",
            Summary = string.Equals(kind, "connector", StringComparison.OrdinalIgnoreCase)
                ? "Recommended onboarding connector."
                : "Recommended onboarding skill.",
            Recommended = true,
            TrustKind = "verified_by_kautuka",
            Version = "built-in",
            Publisher = "Kautuka",
            ArtifactUrl = artifactUrl ?? string.Empty,
            ArtifactSha256 = artifactSha256 ?? string.Empty,
        };
}
