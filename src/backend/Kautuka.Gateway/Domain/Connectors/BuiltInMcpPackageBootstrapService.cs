using Kautuka.Gateway.Domain.Stores;

namespace Kautuka.Gateway.Domain.Connectors;

internal sealed partial class BuiltInMcpPackageBootstrapService(
    IWebHostEnvironment environment,
    IGatewayCatalogStore catalog,
    IMcpbPackageService mcpbPackageService,
    IMcpConnectorTestService mcpConnectorTestService,
    ILogger<BuiltInMcpPackageBootstrapService> logger) : IHostedService
{
    private const string builtInBundleDirectoryName = "built-in-mcpb";
    private const string solutionRelativePath = "src/Kautuka.slnx";

    private static readonly (string FileName, string ConnectorId)[] builtInBundles =
    [
        ("kautuka-web-tools.mcpb", "kautuka-web-tools"),
        ("kautuka-m365-mail.mcpb", "kautuka-m365-mail"),
        ("kautuka-google-mail.mcpb", "kautuka-google-mail"),
    ];

    [LoggerMessage(EventId = 7200, Level = LogLevel.Information,
        Message = "Built-in MCP bootstrap start. bundlePath={BundlePath}")]
    private static partial void LogBootstrapStart(ILogger logger, string bundlePath);

    [LoggerMessage(EventId = 7201, Level = LogLevel.Warning,
        Message = "Built-in MCP bundle was not found at {BundlePath}")]
    private static partial void LogBundleMissing(ILogger logger, string bundlePath);

    [LoggerMessage(EventId = 7202, Level = LogLevel.Warning,
        Message = "Failed to import built-in MCP bundle from {BundlePath}")]
    private static partial void LogImportFailed(ILogger logger, Exception exception, string bundlePath);

    [LoggerMessage(EventId = 7203, Level = LogLevel.Warning,
        Message = "Built-in MCP bundle at {BundlePath} resolved to connector id {ConnectorId}; expected {ExpectedConnectorId}")]
    private static partial void LogUnexpectedConnectorId(ILogger logger, string bundlePath, string connectorId, string expectedConnectorId);

    [LoggerMessage(EventId = 7204, Level = LogLevel.Information,
        Message = "Built-in MCP bootstrap completed. connectorId={ConnectorId}")]
    private static partial void LogBootstrapCompleted(ILogger logger, string connectorId);

    [LoggerMessage(EventId = 7205, Level = LogLevel.Information,
        Message = "Built-in MCP metadata refresh start. connectorId={ConnectorId} bundleChanged={BundleChanged} metadataMissing={MetadataMissing}")]
    private static partial void LogMetadataRefreshStart(ILogger logger, string connectorId, bool bundleChanged, bool metadataMissing);

    [LoggerMessage(EventId = 7206, Level = LogLevel.Information,
        Message = "Built-in MCP metadata refresh completed. connectorId={ConnectorId} toolCount={ToolCount}")]
    private static partial void LogMetadataRefreshCompleted(ILogger logger, string connectorId, int? toolCount);

    [LoggerMessage(EventId = 7207, Level = LogLevel.Warning,
        Message = "Built-in MCP metadata refresh failed. connectorId={ConnectorId} message={Message}")]
    private static partial void LogMetadataRefreshFailed(ILogger logger, string connectorId, string message);

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        foreach ((string fileName, string connectorId) in builtInBundles)
        {
            await ImportBundleAsync(fileName, connectorId, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task ImportBundleAsync(string fileName, string connectorId, CancellationToken cancellationToken)
    {
        string bundlePath = ResolveBundlePath(fileName, connectorId);
        LogBootstrapStart(logger, bundlePath);

        if (!File.Exists(bundlePath))
        {
            LogBundleMissing(logger, bundlePath);
            return;
        }

        try
        {
#pragma warning disable CA2007 // await using on Stream — no SynchronizationContext in ASP.NET Core
            await using FileStream stream = File.OpenRead(bundlePath);
#pragma warning restore CA2007
            McpbConnectorInspection inspection = await mcpbPackageService
                .InspectAsync(stream, Path.GetFileName(bundlePath), cancellationToken)
                .ConfigureAwait(false);

            if (!string.Equals(inspection.ServerId, connectorId, StringComparison.Ordinal))
            {
                LogUnexpectedConnectorId(logger, bundlePath, inspection.ServerId, connectorId);
                return;
            }

            bool hasExisting = catalog.TryGetMcpServer(connectorId, out GatewayMcpServer? existing) && existing is not null;
            bool keepEnabled = hasExisting ? existing!.Enabled : true;
            bool bundleChanged = hasExisting
                && !string.Equals(existing!.BundlePath, inspection.Connector.BundlePath, StringComparison.Ordinal);
            bool metadataMissing = !hasExisting
                || existing!.ToolCount is null
                || existing.ToolNames is not { Count: > 0 };

            GatewayMcpServer saved = await catalog.SaveSystemMcpServerAsync(
                connectorId,
                CreateRequest(inspection, keepEnabled, connectorId),
                cancellationToken).ConfigureAwait(false);

            if (bundleChanged || metadataMissing)
            {
                await RefreshMetadataAsync(saved, bundleChanged, metadataMissing, cancellationToken).ConfigureAwait(false);
            }

            LogBootstrapCompleted(logger, connectorId);
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException)
        {
            LogImportFailed(logger, ex, bundlePath);
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private string ResolveBundlePath(string fileName, string connectorId)
    {
        string outputPath = Path.Combine(AppContext.BaseDirectory, builtInBundleDirectoryName, fileName);
        if (File.Exists(outputPath))
        {
            return outputPath;
        }

        string contentRootPath = Path.Combine(environment.ContentRootPath, builtInBundleDirectoryName, fileName);
        if (File.Exists(contentRootPath))
        {
            return contentRootPath;
        }

        string? repositoryBundlePath = TryResolveRepositoryBundlePath(environment.ContentRootPath, connectorId);

        return repositoryBundlePath ?? contentRootPath;
    }

    private static string? TryResolveRepositoryBundlePath(string startPath, string connectorId)
    {
        if (string.IsNullOrWhiteSpace(startPath))
        {
            return null;
        }

        DirectoryInfo? directory = new(startPath);
        if (!directory.Exists)
        {
            directory = directory.Parent;
        }

        while (directory is not null)
        {
            string solutionPath = Path.Combine(directory.FullName, solutionRelativePath);
            if (File.Exists(solutionPath))
            {
                string artifactsRoot = Path.Combine(directory.FullName, "artifacts", "mcpb");
                if (!Directory.Exists(artifactsRoot))
                {
                    return null;
                }

                return Directory
                    .EnumerateFiles(artifactsRoot, $"{connectorId}-*.mcpb", SearchOption.TopDirectoryOnly)
                    .OrderByDescending(File.GetLastWriteTimeUtc)
                    .FirstOrDefault();
            }

            directory = directory.Parent;
        }

        return null;
    }

    private static GatewayConnectorUpsertRequest CreateRequest(McpbConnectorInspection inspection, bool enabled, string connectorId)
        => new()
        {
            Name = inspection.Connector.Name,
            Description = inspection.Connector.Description,
            IconUrl = inspection.Connector.IconUrl,
            PresetId = connectorId,
            Transport = inspection.Connector.Transport,
            HttpUrl = inspection.Connector.HttpUrl,
            LocalCommand = inspection.Connector.LocalCommand,
            LocalArgs = inspection.Connector.LocalArgs,
            ContainerImage = inspection.Connector.ContainerImage,
            ContainerCommand = inspection.Connector.ContainerCommand,
            ContainerArgs = inspection.Connector.ContainerArgs,
            ContainerSidecarName = inspection.Connector.ContainerSidecarName,
            BundlePath = inspection.Connector.BundlePath,
            ServerType = inspection.Connector.ServerType,
            RuntimeArtifactType = inspection.Connector.RuntimeArtifactType,
            RuntimeArtifactSourceDigest = inspection.Connector.RuntimeArtifactSourceDigest,
            RuntimeArtifactBuildStatus = inspection.Connector.RuntimeArtifactBuildStatus,
            ReadinessTimeoutSeconds = inspection.Connector.ReadinessTimeoutSeconds,
            JitCooldownSeconds = inspection.Connector.JitCooldownSeconds,
            HealthcheckUrl = inspection.Connector.HealthcheckUrl,
            HealthcheckIntervalMilliseconds = inspection.Connector.HealthcheckIntervalMilliseconds,
            EnvKeys = inspection.Connector.EnvKeys,
            Enabled = enabled,
        };

    private async Task RefreshMetadataAsync(GatewayMcpServer server, bool bundleChanged, bool metadataMissing, CancellationToken cancellationToken)
    {
        LogMetadataRefreshStart(logger, server.Id, bundleChanged, metadataMissing);

        McpConnectorTestResult result = await mcpConnectorTestService.TestAsync(
            new GatewayConnectorTestRequest
            {
                Id = server.Id,
                Transport = server.Transport,
                HttpUrl = server.HttpUrl,
                ContainerImage = server.ContainerImage,
                ContainerCommand = server.ContainerCommand,
                ContainerArgs = [.. server.ContainerArgs],
                ContainerSidecarName = server.ContainerSidecarName,
                BundlePath = server.BundlePath,
                ServerType = server.ServerType,
                ReadinessTimeoutSeconds = server.ReadinessTimeoutSeconds,
                EnvKeys = server.EnvKeys is { Count: > 0 } ? [.. server.EnvKeys] : null,
            },
            cancellationToken).ConfigureAwait(false);

        if (!result.Success)
        {
            LogMetadataRefreshFailed(logger, server.Id, result.Message);
            return;
        }

        GatewayMcpServer refreshed = await catalog.SaveMcpServerDiscoveryAsync(
            server.Id,
            result.ToolCount,
            result.ToolNames,
            cancellationToken).ConfigureAwait(false);

        LogMetadataRefreshCompleted(logger, refreshed.Id, refreshed.ToolCount);
    }
}
