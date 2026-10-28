using System.Text.RegularExpressions;

using Kautuka.Gateway.Hosting.Architecture;
using Kautuka.Gateway.Infrastructure.Configuration;
using Kautuka.Gateway.RuntimePlane.Projection;

namespace Kautuka.ContractTests;

public sealed partial class GatewayArchitectureConformanceTests
{
    private static readonly string gatewayRoot = ResolveGatewayRoot();

    [Fact]
    public void ProductionGatewaySourceForbidsAgentInvocationEventTypes()
    {
        foreach ((string path, string source) in ReadSources(string.Empty))
        {
            Assert.DoesNotContain("agent.invocation.", source, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void ProductionGatewaySourceForbidsLegacyRuntimeAuthoritySurface()
    {
        string[] forbiddenTypeNames =
        [
            "GatewayRuntimeEvent",
            "GatewayRuntimeEventTypes",
            "GatewayRuntimeEventContext",
            "NaradLegacyEventTypeMapping",
            "CanonicalAguiStreamDelivery",
            "CanonicalRuntimeEventProjector",
        ];
        string[] forbiddenEventLiterals =
        [
            "run.delivery.completed",
            "run.failed",
            "tool.approval.requested",
            "tool.approval.resolved",
            "agent.session.ready",
            "workflow.event",
            "manager.message",
            "runtime_events",
        ];

        foreach ((string path, string source) in ReadSources(string.Empty))
        {
            string relativePath = Path.GetRelativePath(ResolveGatewayRoot(), path).Replace('\\', '/');
            bool channelBoundary = relativePath.StartsWith("Domain/Channels/", StringComparison.Ordinal);

            foreach (string forbidden in forbiddenTypeNames)
            {
                Assert.DoesNotContain(forbidden, source, StringComparison.Ordinal);
            }

            foreach (string literal in forbiddenEventLiterals)
            {
                if (channelBoundary && literal.StartsWith("tool.approval.", StringComparison.Ordinal))
                {
                    continue;
                }

                Assert.DoesNotContain(literal, source, StringComparison.Ordinal);
            }

            Assert.DoesNotContain("/api/agent-runtime", source, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void AgentExecutionEventWriterEmitsNativeNaradEventTypesDirectly()
    {
        string source = File.ReadAllText(Path.Combine(
            ResolveGatewayRoot(),
            "Domain",
            "Execution",
            "AgentExecutionEventWriter.cs"));
        Assert.Contains("NaradEventTypes.", source, StringComparison.Ordinal);
        Assert.DoesNotContain("NaradLegacyEventTypeMapping", source, StringComparison.Ordinal);
        Assert.DoesNotContain("GatewayRuntimeEventTypes", source, StringComparison.Ordinal);
        Assert.DoesNotContain("PublishMappedAsync", source, StringComparison.Ordinal);
    }

    [Fact]
    public void ProductionDatabaseSchemaHasNoMigrationCompatibilityLayer()
    {
        string databaseRoot = Path.Combine(gatewayRoot, "Infrastructure", "Database");
        string[] forbiddenFileMarkers = ["Normalizer", "MigrationRunner", "MigrationUtility", "DropAllTables"];
        string[] productionFiles = Directory.GetFiles(databaseRoot, "*", SearchOption.AllDirectories)
            .Where(path => path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)
                || path.EndsWith(".sql", StringComparison.OrdinalIgnoreCase))
            .ToArray();

        foreach (string path in productionFiles)
        {
            string fileName = Path.GetFileNameWithoutExtension(path);
            Assert.DoesNotContain(
                forbiddenFileMarkers,
                marker => fileName.Contains(marker, StringComparison.Ordinal));

            string source = File.ReadAllText(path);
            Assert.DoesNotContain("ALTER TABLE", source, StringComparison.OrdinalIgnoreCase);
            if (string.Equals(fileName, "SqliteGatewaySchemaBootstrapper", StringComparison.Ordinal))
            {
                Assert.Contains("DROP TABLE", source, StringComparison.Ordinal);
                Assert.Contains("workflow_agip_events", source, StringComparison.Ordinal);
                continue;
            }

            Assert.DoesNotContain("DROP TABLE", source, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void AgentDefinitionStoreDoesNotOwnRuntimeMetadataRepositories()
    {
        Assert.False(typeof(IAgentRuntimeSessionRepository).IsAssignableFrom(typeof(SqliteAgentDefinitionStore)));
        Assert.False(typeof(IAgentRuntimeRunRepository).IsAssignableFrom(typeof(SqliteAgentDefinitionStore)));
        Assert.False(typeof(IAgentMemoryFactRepository).IsAssignableFrom(typeof(SqliteAgentDefinitionStore)));
        Assert.True(typeof(IAgentRuntimeSessionRepository).IsAssignableFrom(typeof(SqliteAgentRuntimeSessionRepository)));
        Assert.True(typeof(IAgentRuntimeRunRepository).IsAssignableFrom(typeof(SqliteAgentRuntimeRunRepository)));
        Assert.True(typeof(IAgentMemoryFactRepository).IsAssignableFrom(typeof(SqliteAgentMemoryFactRepository)));

        string configurationRoot = Path.Combine(gatewayRoot, "Infrastructure", "Configuration");
        string definitionStore = File.ReadAllText(Path.Combine(configurationRoot, "SqliteAgentDefinitionStore.cs"));
        Assert.DoesNotContain("IAgentRuntimeSessionRepository", definitionStore, StringComparison.Ordinal);
        Assert.DoesNotContain("IAgentRuntimeRunRepository", definitionStore, StringComparison.Ordinal);
        Assert.DoesNotContain("IAgentMemoryFactRepository", definitionStore, StringComparison.Ordinal);

        string gatewayState = File.ReadAllText(Path.Combine(gatewayRoot, "Domain", "State", "GatewayState.cs"));
        Assert.DoesNotContain("agentDefinitionStore as IAgentRuntime", gatewayState, StringComparison.Ordinal);

        Assert.True(File.Exists(Path.Combine(configurationRoot, "SqliteAgentRuntimeSessionRepository.cs")));
        Assert.True(File.Exists(Path.Combine(configurationRoot, "SqliteAgentRuntimeRunRepository.cs")));
        Assert.True(File.Exists(Path.Combine(configurationRoot, "SqliteAgentMemoryFactRepository.cs")));
    }

    [Fact]
    public void EndpointModulesDoNotAccessGatewayStateDirectly()
    {
        foreach ((string path, string source) in ReadSources("Endpoints"))
        {
            Assert.DoesNotContain("GatewayState", source, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void DurableHitlDispatchersDoNotUseRuntimeDapperCodeGeneration()
    {
        string continuationSource = File.ReadAllText(Path.Combine(
            gatewayRoot, "Domain", "Hitl", "ApprovalContinuationQueue.cs"));
        Assert.DoesNotContain("using Dapper;", continuationSource, StringComparison.Ordinal);
        Assert.DoesNotContain("connection.Query", continuationSource, StringComparison.Ordinal);
        Assert.DoesNotContain("connection.Execute", continuationSource, StringComparison.Ordinal);

        string approvalStoreSource = File.ReadAllText(Path.Combine(
            gatewayRoot, "Domain", "Stores", "GatewayApprovalStore.cs"));
        int durableStoreStart = approvalStoreSource.IndexOf(
            "public ApprovalResolutionEventLease? TryLeaseResolutionEvent",
            StringComparison.Ordinal);
        int durableStoreEnd = approvalStoreSource.IndexOf(
            "public void SetChannelTetherMode",
            durableStoreStart,
            StringComparison.Ordinal);
        Assert.True(durableStoreStart >= 0 && durableStoreEnd > durableStoreStart);
        string durableStoreSource = approvalStoreSource[durableStoreStart..durableStoreEnd];
        Assert.DoesNotContain("connection.Query", durableStoreSource, StringComparison.Ordinal);
        Assert.DoesNotContain("connection.Execute", durableStoreSource, StringComparison.Ordinal);
    }

    [Fact]
    public void CorrectnessCriticalTasksAndChannelsRequireReviewedOwnership()
    {
        string[] markers = ["Task.Run(", "Channel.CreateUnbounded", "Channel.CreateBounded"];
        var observedSuppressions = new HashSet<string>(StringComparer.Ordinal);
        foreach ((string path, string source) in ReadSources("Domain").Concat(ReadSources("Infrastructure")))
        {
            if (!markers.Any(marker => source.Contains(marker, StringComparison.Ordinal)))
            {
                continue;
            }

            string relativePath = Path.GetRelativePath(gatewayRoot, path).Replace('\\', '/');
            Assert.True(
                GatewayArchitecturePolicy.InMemoryDeliverySuppressions.TryGetValue(relativePath, out string? rationale),
                $"{relativePath} creates process-local work without a reviewed architecture suppression.");
            Assert.False(string.IsNullOrWhiteSpace(rationale), $"{relativePath} suppression requires a rationale.");
            observedSuppressions.Add(relativePath);
        }

        Assert.Equal(
            GatewayArchitecturePolicy.InMemoryDeliverySuppressions.Keys.Order(StringComparer.Ordinal),
            observedSuppressions.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void StructuredLogsDoNotCaptureSensitivePayloadPlaceholders()
    {
        foreach ((string path, string source) in ReadSources(string.Empty))
        {
            Match match = SensitiveLogPlaceholder().Match(source);
            Assert.False(match.Success, $"Sensitive structured-log placeholder '{match.Value}' in {path}.");
        }
    }

    [Fact]
    public void EndpointCompositionHasAuthenticatedFallbackAndExplicitAnonymousRoutes()
    {
        string moduleSource = File.ReadAllText(Path.Combine(gatewayRoot, "Hosting", "Modules", "GatewayModules.cs"));
        Assert.Contains("SetFallbackPolicy", moduleSource, StringComparison.Ordinal);
        Assert.Contains("RequireAuthenticatedUser", moduleSource, StringComparison.Ordinal);

        string endpointSource = File.ReadAllText(Path.Combine(gatewayRoot, "Endpoints", "Auth", "GatewayAuthEndpointMappings.cs"));
        string[] anonymousRoutes = ["/api/auth/setup/status", "/api/auth/setup", "/api/auth/login", "/api/auth/refresh"];
        foreach (string route in anonymousRoutes)
        {
            Match declaration = Regex.Match(
                endpointSource,
                "RouteHandlerBuilder\\s+(?<variable>\\w+)\\s*=\\s*app\\.Map(?:Get|Post)\\(\"" + Regex.Escape(route) + "\"",
                RegexOptions.CultureInvariant);
            Assert.True(declaration.Success, $"Expected anonymous route {route}.");
            string variable = declaration.Groups["variable"].Value;
            Assert.Matches(
                new Regex(Regex.Escape(variable) + "\\.WithTags[\\s\\S]*?\\.AllowAnonymous\\(\\)", RegexOptions.CultureInvariant),
                endpointSource[declaration.Index..]);
        }
    }

    [Fact]
    public void ModuleAndLayerDependencyDirectionIsEnforced()
    {
        string moduleSource = File.ReadAllText(Path.Combine(gatewayRoot, "Hosting", "Modules", "GatewayModules.cs"));
        string[] modules = ["Security", "Catalog", "Execution", "Hitl", "Channels", "Memory", "Usage", "Operations"];
        foreach (string module in modules)
        {
            Assert.Contains($"class Gateway{module}Module", moduleSource, StringComparison.Ordinal);
            Assert.Contains("public static void AddServices", ExtractClassBody(moduleSource, $"Gateway{module}Module"), StringComparison.Ordinal);
            Assert.Contains("public static void MapEndpoints", ExtractClassBody(moduleSource, $"Gateway{module}Module"), StringComparison.Ordinal);
        }

        var observedDependencySuppressions = new HashSet<string>(StringComparer.Ordinal);
        var observedEndpointSuppressions = new HashSet<string>(StringComparer.Ordinal);
        foreach ((string path, string source) in ReadSources("Domain"))
        {
            if (source.Contains("Kautuka.Gateway.Infrastructure", StringComparison.Ordinal))
            {
                string relativePath = Path.GetRelativePath(gatewayRoot, path).Replace('\\', '/');
                Assert.True(
                    GatewayArchitecturePolicy.DomainInfrastructureSuppressions.TryGetValue(relativePath, out string? rationale),
                    $"{relativePath} introduces a Domain-to-Infrastructure dependency without reviewed suppression.");
                Assert.False(string.IsNullOrWhiteSpace(rationale), $"{relativePath} suppression requires a rationale.");
                observedDependencySuppressions.Add(relativePath);
            }

            if (source.Contains("Kautuka.Gateway.Endpoints", StringComparison.Ordinal))
            {
                string relativePath = Path.GetRelativePath(gatewayRoot, path).Replace('\\', '/');
                Assert.True(
                    GatewayArchitecturePolicy.DomainEndpointSuppressions.TryGetValue(relativePath, out string? rationale),
                    $"{relativePath} introduces a Domain-to-Endpoints dependency without reviewed suppression.");
                Assert.False(string.IsNullOrWhiteSpace(rationale), $"{relativePath} suppression requires a rationale.");
                observedEndpointSuppressions.Add(relativePath);
            }

            Assert.DoesNotContain("Kautuka.Gateway.Hosting.Modules", source, StringComparison.Ordinal);
        }

        Assert.Equal(
            GatewayArchitecturePolicy.DomainInfrastructureSuppressions.Keys.Order(StringComparer.Ordinal),
            observedDependencySuppressions.Order(StringComparer.Ordinal));
        Assert.Equal(
            GatewayArchitecturePolicy.DomainEndpointSuppressions.Keys.Order(StringComparer.Ordinal),
            observedEndpointSuppressions.Order(StringComparer.Ordinal));

        var observedInfrastructureEndpointSuppressions = new HashSet<string>(StringComparer.Ordinal);
        foreach ((string path, string source) in ReadSources("Infrastructure"))
        {
            if (source.Contains("Kautuka.Gateway.Endpoints", StringComparison.Ordinal))
            {
                string relativePath = Path.GetRelativePath(gatewayRoot, path).Replace('\\', '/');
                Assert.True(
                    GatewayArchitecturePolicy.InfrastructureEndpointSuppressions.TryGetValue(relativePath, out string? rationale),
                    $"{relativePath} introduces an Infrastructure-to-Endpoints dependency without reviewed suppression.");
                Assert.False(string.IsNullOrWhiteSpace(rationale), $"{relativePath} suppression requires a rationale.");
                observedInfrastructureEndpointSuppressions.Add(relativePath);
            }

            Assert.DoesNotContain("Kautuka.Gateway.Hosting.Modules", source, StringComparison.Ordinal);
        }

        Assert.Equal(
            GatewayArchitecturePolicy.InfrastructureEndpointSuppressions.Keys.Order(StringComparer.Ordinal),
            observedInfrastructureEndpointSuppressions.Order(StringComparer.Ordinal));

        foreach ((string path, string source) in ReadSources("Endpoints"))
        {
            Assert.DoesNotContain("Kautuka.Gateway.Hosting.Modules", source, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void NaradProjectionConcernsHaveFocusedOwnersAndRatchetedSize()
    {
        Assert.NotNull(typeof(NaradSessionProjection).GetMethod("Fold"));
        Assert.NotNull(typeof(NaradGraphProjectionState).GetMethod("Apply"));
        Assert.NotNull(typeof(NaradMessagesSnapshotBuilder).GetMethod("Build"));
        Assert.NotNull(typeof(NaradEventJsonParser).GetMethod("ParseBody"));
        Assert.NotNull(typeof(NaradPublicAuthorityFilter).GetMethod("TryBuildPublicPayload"));
        Assert.NotNull(typeof(NaradProgressLedger).GetMethod("Fold"));
        Assert.NotNull(typeof(NaradProfileDiscovery).GetMethod("BuildCapabilities"));

        IReadOnlyDictionary<string, int> maximumLinesByFile = new Dictionary<string, int>(StringComparer.Ordinal)
        {
            ["NaradSessionProjection.cs"] = 250,
            ["NaradGraphProjectionState.cs"] = 420,
            ["NaradMessagesSnapshotBuilder.cs"] = 240,
            ["NaradEventJsonParser.cs"] = 180,
            ["NaradPublicAuthorityFilter.cs"] = 70,
            ["NaradProgressLedger.cs"] = 90,
            ["NaradProfileDiscovery.cs"] = 70,
        };
        string projectionDirectory = Path.Combine(gatewayRoot, "RuntimePlane", "Projection");
        foreach ((string fileName, int maximumLines) in maximumLinesByFile)
        {
            string path = Path.Combine(projectionDirectory, fileName);
            Assert.True(File.Exists(path), $"Missing Narad projection owner '{fileName}'.");
            int lineCount = File.ReadLines(path).Count();
            Assert.True(
                lineCount <= maximumLines,
                $"Narad projection owner '{fileName}' has {lineCount} lines; ratcheted maximum is {maximumLines}.");
        }

        foreach (string path in Directory.EnumerateFiles(projectionDirectory, "Canonical*.cs"))
        {
            Assert.Fail($"Legacy canonical projection file remains on disk: {Path.GetFileName(path)}.");
        }

        foreach (string path in Directory.EnumerateFiles(
            Path.Combine(gatewayRoot, "Endpoints", "Runtime"),
            "Canonical*.cs"))
        {
            Assert.Fail($"Legacy canonical runtime endpoint file remains on disk: {Path.GetFileName(path)}.");
        }
    }

    private static IEnumerable<(string Path, string Source)> ReadSources(string relativeDirectory)
    {
        string directory = Path.Combine(gatewayRoot, relativeDirectory);
        return Directory.EnumerateFiles(directory, "*.cs", SearchOption.AllDirectories)
            .Where(static path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Select(static path => (path, File.ReadAllText(path)));
    }

    private static string ResolveGatewayRoot()
    {
        foreach (string origin in new[] { AppContext.BaseDirectory, Environment.CurrentDirectory })
        {
            for (DirectoryInfo? directory = new(origin); directory is not null; directory = directory.Parent)
            {
                string candidate = Path.Combine(directory.FullName, "src", "backend", "Kautuka.Gateway");
                if (Directory.Exists(candidate))
                {
                    return candidate;
                }
            }
        }

        throw new DirectoryNotFoundException("Could not locate the Kautuka.Gateway source directory for architecture conformance tests.");
    }

    private static string ExtractClassBody(string source, string className)
    {
        int start = source.IndexOf($"class {className}", StringComparison.Ordinal);
        Assert.True(start >= 0, $"Missing module {className}.");
        int next = source.IndexOf("internal static class Gateway", start + className.Length, StringComparison.Ordinal);
        return next < 0 ? source[start..] : source[start..next];
    }

    [GeneratedRegex("\\{(?:Delta|ResultJson|Arguments|Prompt|Content|Token|ToolResult)\\}", RegexOptions.CultureInvariant)]
    private static partial Regex SensitiveLogPlaceholder();
}
