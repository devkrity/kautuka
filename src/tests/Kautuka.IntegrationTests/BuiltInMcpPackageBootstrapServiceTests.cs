using System.Text.Json;

using Kautuka.Gateway.Domain.Connectors;
using Kautuka.Gateway.Domain.Stores;
using Kautuka.Manager.RuntimeContainer.Hosting;

using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging.Abstractions;

namespace Kautuka.IntegrationTests;

public sealed class BuiltInMcpPackageBootstrapServiceTests
{
    [Fact]
    public async Task StartAsyncMissingBundleSkipsImport()
    {
        string root = CreateTempDirectory();
        try
        {
            var catalog = new FakeCatalogStore();
            var mcpb = new FakeMcpbPackageService();
            var service = new BuiltInMcpPackageBootstrapService(
                new FakeWebHostEnvironment(root),
                catalog,
                mcpb,
                new FakeMcpConnectorTestService(),
                NullLogger<BuiltInMcpPackageBootstrapService>.Instance);

            await service.StartAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);

            Assert.Equal(0, mcpb.InspectCallCount);
            Assert.Equal(0, catalog.SaveCallCount);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task StartAsyncExistingConnectorPreservesEnabledState()
    {
        string root = CreateTempDirectory();
        try
        {
            string bundleDirectory = Path.Combine(root, "built-in-mcpb");
            Directory.CreateDirectory(bundleDirectory);
            string bundlePath = Path.Combine(bundleDirectory, "kautuka-web-tools.mcpb");
            await File.WriteAllTextAsync(bundlePath, "placeholder", TestContext.Current.CancellationToken).ConfigureAwait(true);

            var catalog = new FakeCatalogStore
            {
                ExistingServer = new GatewayMcpServer(
                    "kautuka-web-tools",
                    "Existing",
                    string.Empty,
                    string.Empty,
                    "kautuka-web-tools",
                    "container",
                    null,
                    "quay.io/fedora/fedora-minimal:44",
                    "node",
                    [],
                    null,
                    "/tmp/existing.mcpb",
                    "node",
                    45,
                    600,
                    null,
                    null,
                    false,
                    false,
                    DateTimeOffset.UtcNow.ToString("O"),
                    DateTimeOffset.UtcNow.ToString("O"),
                    GatewayOwnershipScopes.System,
                    null,
                    null,
                    null,
                    null)
            };

            var mcpb = new FakeMcpbPackageService
            {
                Inspection = new McpbConnectorInspection(
                    "kautuka-web-tools",
                    new GatewayConnectorUpsertRequest
                    {
                        Name = "Kautuka Web Tools",
                        Description = "Built-in web evidence fetcher.",
                        Transport = "container",
                        ContainerImage = McpCommandTransportMapper.NodeImage,
                        ContainerCommand = "node",
                        ContainerArgs = ["${__dirname}/server.js"],
                        BundlePath = "/tmp/kautuka-web-tools.mcpb",
                        ServerType = "node",
                        Enabled = true,
                        ReadinessTimeoutSeconds = 45,
                        JitCooldownSeconds = 600,
                    },
                    "0.4",
                    "node",
                    "kautuka-web-tools")
            };

            var service = new BuiltInMcpPackageBootstrapService(
                new FakeWebHostEnvironment(root),
                catalog,
                mcpb,
                new FakeMcpConnectorTestService(),
                NullLogger<BuiltInMcpPackageBootstrapService>.Instance);

            await service.StartAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);

            Assert.Equal(1, catalog.SaveCallCount);
            Assert.Equal("kautuka-web-tools", catalog.SavedServerId);
            Assert.NotNull(catalog.SavedRequest);
            Assert.False(catalog.SavedRequest!.Enabled);
            Assert.Equal("kautuka-web-tools", catalog.SavedRequest.PresetId);
            Assert.Equal(McpCommandTransportMapper.NodeImage, catalog.SavedRequest.ContainerImage);
            ContainerImageProvenancePolicy.EnsureTrusted(
                catalog.SavedRequest.ContainerImage!,
                "Built-in MCP bootstrap test");
            Assert.Equal(GatewayOwnershipScopes.System, catalog.SavedScope);
            Assert.Null(catalog.SavedOwnerUserId);
            Assert.Equal(1, catalog.SaveSystemCallCount);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task StartAsyncInvalidBundleWarnsAndContinues()
    {
        string root = CreateTempDirectory();
        try
        {
            string bundleDirectory = Path.Combine(root, "built-in-mcpb");
            Directory.CreateDirectory(bundleDirectory);
            string bundlePath = Path.Combine(bundleDirectory, "kautuka-web-tools.mcpb");
            await File.WriteAllTextAsync(bundlePath, "placeholder", TestContext.Current.CancellationToken).ConfigureAwait(true);

            var catalog = new FakeCatalogStore();
            var mcpb = new FakeMcpbPackageService
            {
                Exception = new InvalidOperationException("broken bundle")
            };
            var service = new BuiltInMcpPackageBootstrapService(
                new FakeWebHostEnvironment(root),
                catalog,
                mcpb,
                new FakeMcpConnectorTestService(),
                NullLogger<BuiltInMcpPackageBootstrapService>.Instance);

            await service.StartAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);

            Assert.Equal(1, mcpb.InspectCallCount);
            Assert.Equal(0, catalog.SaveCallCount);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task StartAsyncRepositoryBundleFallbackImportsConnector()
    {
        string root = CreateTempDirectory();
        try
        {
            string solutionRoot = Path.Combine(root, "repo");
            Directory.CreateDirectory(Path.Combine(solutionRoot, "src"));
            Directory.CreateDirectory(Path.Combine(solutionRoot, "src", "backend", "Kautuka.Gateway"));
            Directory.CreateDirectory(Path.Combine(solutionRoot, "artifacts", "mcpb"));

            await File.WriteAllTextAsync(
                Path.Combine(solutionRoot, "src", "Kautuka.slnx"),
                "solution placeholder",
                TestContext.Current.CancellationToken).ConfigureAwait(true);
            await File.WriteAllTextAsync(
                Path.Combine(solutionRoot, "artifacts", "mcpb", "kautuka-web-tools-0.1.0.mcpb"),
                "placeholder",
                TestContext.Current.CancellationToken).ConfigureAwait(true);

            var catalog = new FakeCatalogStore();
            var testService = new FakeMcpConnectorTestService
            {
                Result = new McpConnectorTestResult(true, "Connected. 1 tool(s) available.", 1, ToolNames: ["kautuka_web_fetch"])
            };
            var mcpb = new FakeMcpbPackageService
            {
                Inspection = new McpbConnectorInspection(
                    "kautuka-web-tools",
                    new GatewayConnectorUpsertRequest
                    {
                        Name = "Kautuka Web Tools",
                        Description = "Built-in web evidence fetcher.",
                        Transport = "container",
                        ContainerImage = McpCommandTransportMapper.NodeImage,
                        ContainerCommand = "node",
                        ContainerArgs = ["${__dirname}/server.js"],
                        BundlePath = "/tmp/kautuka-web-tools.mcpb",
                        ServerType = "node",
                        Enabled = true,
                        ReadinessTimeoutSeconds = 45,
                        JitCooldownSeconds = 600,
                    },
                    "0.4",
                    "node",
                    "kautuka-web-tools")
            };

            var service = new BuiltInMcpPackageBootstrapService(
                new FakeWebHostEnvironment(Path.Combine(solutionRoot, "src", "backend", "Kautuka.Gateway")),
                catalog,
                mcpb,
                testService,
                NullLogger<BuiltInMcpPackageBootstrapService>.Instance);

            await service.StartAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);

            Assert.Equal(1, mcpb.InspectCallCount);
            Assert.Equal(1, catalog.SaveCallCount);
            Assert.Equal("kautuka-web-tools", catalog.SavedServerId);
            Assert.Equal(1, catalog.SaveDiscoveryCallCount);
            Assert.Equal(1, testService.TestCallCount);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task StartAsyncChangedBundleRefreshesDiscoveredTools()
    {
        string root = CreateTempDirectory();
        try
        {
            string bundleDirectory = Path.Combine(root, "built-in-mcpb");
            Directory.CreateDirectory(bundleDirectory);
            string bundlePath = Path.Combine(bundleDirectory, "kautuka-web-tools.mcpb");
            await File.WriteAllTextAsync(bundlePath, "placeholder", TestContext.Current.CancellationToken).ConfigureAwait(true);

            var catalog = new FakeCatalogStore
            {
                ExistingServer = new GatewayMcpServer(
                    "kautuka-web-tools",
                    "Existing",
                    string.Empty,
                    string.Empty,
                    "kautuka-web-tools",
                    "container",
                    null,
                    "quay.io/fedora/fedora-minimal:44",
                    "node",
                    [],
                    null,
                    "/tmp/old-bundle.mcpb",
                    "node",
                    45,
                    600,
                    null,
                    null,
                    false,
                    true,
                    DateTimeOffset.UtcNow.ToString("O"),
                    DateTimeOffset.UtcNow.ToString("O"),
                    GatewayOwnershipScopes.System,
                    null,
                    1,
                    ["old_tool"],
                    null)
            };
            var testService = new FakeMcpConnectorTestService
            {
                Result = new McpConnectorTestResult(true, "Connected. 1 tool(s) available.", 1, ToolNames: ["kautuka_web_fetch"])
            };
            var mcpb = new FakeMcpbPackageService
            {
                Inspection = new McpbConnectorInspection(
                    "kautuka-web-tools",
                    new GatewayConnectorUpsertRequest
                    {
                        Name = "Kautuka Web Tools",
                        Description = "Built-in web evidence fetcher.",
                        Transport = "container",
                        ContainerImage = McpCommandTransportMapper.NodeImage,
                        ContainerCommand = "node",
                        ContainerArgs = ["${__dirname}/server.js"],
                        BundlePath = "/tmp/new-bundle.mcpb",
                        ServerType = "node",
                        Enabled = true,
                        ReadinessTimeoutSeconds = 45,
                        JitCooldownSeconds = 600,
                    },
                    "0.4",
                    "node",
                    "kautuka-web-tools")
            };

            var service = new BuiltInMcpPackageBootstrapService(
                new FakeWebHostEnvironment(root),
                catalog,
                mcpb,
                testService,
                NullLogger<BuiltInMcpPackageBootstrapService>.Instance);

            await service.StartAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);

            Assert.Equal(1, catalog.SaveSystemCallCount);
            Assert.Equal(1, testService.TestCallCount);
            Assert.Equal(1, catalog.SaveDiscoveryCallCount);
            Assert.Equal(["kautuka_web_fetch"], catalog.DiscoveredToolNames);
            Assert.Equal(1, catalog.DiscoveredToolCount);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task StartAsyncImportsMicrosoftMailBundleEnabled()
    {
        string root = CreateTempDirectory();
        try
        {
            string bundleDirectory = Path.Combine(root, "built-in-mcpb");
            Directory.CreateDirectory(bundleDirectory);
            await File.WriteAllTextAsync(
                Path.Combine(bundleDirectory, "kautuka-m365-mail.mcpb"),
                "placeholder",
                TestContext.Current.CancellationToken).ConfigureAwait(true);

            var catalog = new FakeCatalogStore();
            var mcpb = new FakeMcpbPackageService
            {
                Inspection = new McpbConnectorInspection(
                    "kautuka-m365-mail",
                    new GatewayConnectorUpsertRequest
                    {
                        Name = "Microsoft 365 Mail",
                        Description = "Read, draft, and send.",
                        Transport = "container",
                        ContainerImage = McpCommandTransportMapper.BinaryImage,
                        ContainerCommand = "${__dirname}/kautuka-m365-mail",
                        ContainerArgs = [],
                        BundlePath = "/tmp/kautuka-m365-mail.mcpb",
                        ServerType = "binary",
                        Enabled = true,
                    },
                    "0.4",
                    "binary",
                    "kautuka-m365-mail")
            };

            var service = new BuiltInMcpPackageBootstrapService(
                new FakeWebHostEnvironment(root),
                catalog,
                mcpb,
                new FakeMcpConnectorTestService(),
                NullLogger<BuiltInMcpPackageBootstrapService>.Instance);

            await service.StartAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);

            Assert.Equal(1, mcpb.InspectCallCount);
            Assert.Equal("kautuka-m365-mail", catalog.SavedServerId);
            Assert.NotNull(catalog.SavedRequest);
            Assert.True(catalog.SavedRequest!.Enabled);
            Assert.Equal("kautuka-m365-mail", catalog.SavedRequest.PresetId);
            Assert.Equal("binary", catalog.SavedRequest.ServerType);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static string CreateTempDirectory()
    {
        string root = Path.Combine(Path.GetTempPath(), "kautuka-built-in-mcp-tests", Guid.NewGuid().ToString("N", System.Globalization.CultureInfo.InvariantCulture));
        Directory.CreateDirectory(root);
        return root;
    }

    private sealed class FakeMcpbPackageService : IMcpbPackageService
    {
        public int InspectCallCount { get; private set; }

        public McpbConnectorInspection? Inspection { get; init; }

        public Exception? Exception { get; init; }

        public Task<McpbConnectorInspection> InspectAsync(Stream archiveStream, string fileName, CancellationToken cancellationToken)
        {
            InspectCallCount++;

            if (Exception is not null)
            {
                throw Exception;
            }

            return Task.FromResult(Inspection!);
        }
    }

    private sealed class FakeMcpConnectorTestService : IMcpConnectorTestService
    {
        public int TestCallCount { get; private set; }

        public McpConnectorTestResult Result { get; init; } = new(true, "Connected. 1 tool(s) available.", 1, ToolNames: ["kautuka_web_fetch"]);

        public Task<McpConnectorTestResult> TestAsync(GatewayConnectorTestRequest request, CancellationToken ct)
        {
            TestCallCount++;
            return Task.FromResult(Result);
        }
    }

    private sealed class FakeCatalogStore : IGatewayCatalogStore
    {
        public GatewayMcpServer? ExistingServer { get; init; }

        public int SaveCallCount { get; private set; }

        public int SaveSystemCallCount { get; private set; }

        public int SaveDiscoveryCallCount { get; private set; }

        public string? SavedServerId { get; private set; }

        public GatewayConnectorUpsertRequest? SavedRequest { get; private set; }

        public string? SavedScope { get; private set; }

        public string? SavedOwnerUserId { get; private set; }

        public int? DiscoveredToolCount { get; private set; }

        public IReadOnlyList<string>? DiscoveredToolNames { get; private set; }

        public bool TryGetMcpServer(string id, out GatewayMcpServer? server)
        {
            if (ExistingServer is not null && string.Equals(ExistingServer.Id, id, StringComparison.Ordinal))
            {
                server = ExistingServer;
                return true;
            }

            server = null;
            return false;
        }

        public Task<GatewayMcpServer> SaveMcpServerAsync(string serverId, GatewayConnectorUpsertRequest request, string scope, string? ownerUserId, CancellationToken cancellationToken = default)
        {
            SaveCallCount++;
            SavedServerId = serverId;
            SavedRequest = request;
            SavedScope = scope;
            SavedOwnerUserId = ownerUserId;

            return Task.FromResult(new GatewayMcpServer(
                serverId,
                request.Name ?? serverId,
                request.Description ?? string.Empty,
                request.IconUrl ?? string.Empty,
                request.PresetId,
                request.Transport ?? "container",
                request.HttpUrl,
                request.ContainerImage,
                request.ContainerCommand,
                request.ContainerArgs ?? [],
                request.ContainerSidecarName,
                request.BundlePath,
                request.ServerType,
                request.ReadinessTimeoutSeconds,
                request.JitCooldownSeconds,
                request.HealthcheckUrl,
                request.HealthcheckIntervalMilliseconds,
                request.Enabled ?? true,
                false,
                DateTimeOffset.UtcNow.ToString("O"),
                DateTimeOffset.UtcNow.ToString("O"),
                scope,
                ownerUserId,
                request.ToolCount,
                request.ToolNames,
                request.EnvKeys,
                request.RuntimeArtifactType,
                request.RuntimeArtifactSourceDigest,
                request.RuntimeArtifactBuildStatus));
        }

        public Task<GatewayMcpServer> SaveSystemMcpServerAsync(string serverId, GatewayConnectorUpsertRequest request, CancellationToken cancellationToken = default)
        {
            SaveSystemCallCount++;
            return SaveMcpServerAsync(serverId, request, GatewayOwnershipScopes.System, ownerUserId: null, cancellationToken);
        }

        public Task<GatewayMcpServer> SaveMcpServerDiscoveryAsync(string serverId, int? toolCount, IReadOnlyList<string>? toolNames, CancellationToken cancellationToken = default)
        {
            SaveDiscoveryCallCount++;
            DiscoveredToolCount = toolCount;
            DiscoveredToolNames = toolNames;
            return Task.FromResult(new GatewayMcpServer(
                serverId,
                SavedRequest?.Name ?? serverId,
                SavedRequest?.Description ?? string.Empty,
                SavedRequest?.IconUrl ?? string.Empty,
                SavedRequest?.PresetId,
                SavedRequest?.Transport ?? "container",
                SavedRequest?.HttpUrl,
                SavedRequest?.ContainerImage,
                SavedRequest?.ContainerCommand,
                SavedRequest?.ContainerArgs ?? [],
                SavedRequest?.ContainerSidecarName,
                SavedRequest?.BundlePath,
                SavedRequest?.ServerType,
                SavedRequest?.ReadinessTimeoutSeconds,
                SavedRequest?.JitCooldownSeconds,
                SavedRequest?.HealthcheckUrl,
                SavedRequest?.HealthcheckIntervalMilliseconds,
                SavedRequest?.Enabled ?? true,
                true,
                DateTimeOffset.UtcNow.ToString("O"),
                DateTimeOffset.UtcNow.ToString("O"),
                GatewayOwnershipScopes.System,
                null,
                toolCount,
                toolNames,
                SavedRequest?.EnvKeys,
                SavedRequest?.RuntimeArtifactType,
                SavedRequest?.RuntimeArtifactSourceDigest,
                SavedRequest?.RuntimeArtifactBuildStatus));
        }

        public bool TryGetProvider(string id, out GatewayProvider? provider) => throw new NotSupportedException();
        public IReadOnlyList<GatewayProvider> ListProviders() => throw new NotSupportedException();
        public GatewayProvider SaveProvider(JsonElement body) => throw new NotSupportedException();
        public bool DeleteProvider(string id) => throw new NotSupportedException();
        public bool TryGetModel(string id, out GatewayModel? model) => throw new NotSupportedException();
        public IReadOnlyList<GatewayModel> ListModels() => throw new NotSupportedException();
        public GatewayModel SaveModel(JsonElement body) => throw new NotSupportedException();
        public void UpdateModelCapabilities(string modelId, GatewayModelCapabilities capabilities) => throw new NotSupportedException();
        public bool DeleteModel(string id) => throw new NotSupportedException();
        public bool TryGetAgent(string id, out GatewayAgent? agent) => throw new NotSupportedException();
        public IReadOnlyList<GatewayAgent> ListAgents() => throw new NotSupportedException();
        public GatewayAgent SaveAgent(string agentId, GatewayAgentUpsertRequest request, string ownerUserId) => throw new NotSupportedException();
        public bool DeleteAgent(string agentId) => throw new NotSupportedException();
        public IReadOnlyList<GatewayMcpServer> ListMcpServers() => throw new NotSupportedException();
        public GatewayMcpServer SaveMcpServer(string serverId, GatewayConnectorUpsertRequest request, string scope, string? ownerUserId) => throw new NotSupportedException();
        public GatewayMcpServer SaveMcpServerDiscovery(string serverId, int? toolCount, IReadOnlyList<string>? toolNames) => throw new NotSupportedException();
        public bool DeleteMcpServer(string serverId) => throw new NotSupportedException();
        public IReadOnlyList<GatewaySkill> ListSkills() => throw new NotSupportedException();
        public bool TryGetSkill(string id, out GatewaySkill? skill) => throw new NotSupportedException();
        public GatewaySkill SaveSkill(string skillId, JsonElement body, string scope, string? ownerUserId) => throw new NotSupportedException();
        public bool DeleteSkill(string skillId) => throw new NotSupportedException();
    }

    private sealed class FakeWebHostEnvironment(string root) : IWebHostEnvironment
    {
        public string ApplicationName { get; set; } = nameof(BuiltInMcpPackageBootstrapServiceTests);
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
        public string WebRootPath { get; set; } = root;
        public string EnvironmentName { get; set; } = "Development";
        public string ContentRootPath { get; set; } = root;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
