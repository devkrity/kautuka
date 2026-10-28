using Grpc.Core;

using Kautuka.Manager.Protos;

using ModelContextProtocol.Client;

namespace Kautuka.Gateway.Domain.Connectors;

internal sealed class McpConnectorTestService(
    ILogger<McpConnectorTestService> logger,
    IManagerGrpcService workspaceGrpcService,
    WorkspaceRuntimeContextResolver contextResolver) : IMcpConnectorTestService
{
    private static readonly SemaphoreSlim probeSlots = new(2, 2);

    public async Task<McpConnectorTestResult> TestAsync(GatewayConnectorTestRequest request, CancellationToken ct)
    {
        if (!await probeSlots.WaitAsync(TimeSpan.FromSeconds(1), ct).ConfigureAwait(false))
        {
            return new McpConnectorTestResult(false, "Too many connector probes are already running.");
        }

        try
        {
            return await TestCoreAsync(request, ct).ConfigureAwait(false);
        }
        finally
        {
            probeSlots.Release();
        }
    }

    private Task<McpConnectorTestResult> TestCoreAsync(GatewayConnectorTestRequest request, CancellationToken ct)
    {
        string transport = string.IsNullOrWhiteSpace(request.Transport) ? "LOCAL" : request.Transport.Trim().ToUpperInvariant();
        Dictionary<string, string> testEnv = ReadTestEnv(request);
        return transport switch
        {
            "HTTP" => TestHttpAsync(request, testEnv, ct),
            "CONTAINER" => TestContainerAsync(request, testEnv, ct),
            _ => TestLocalAsync(request, testEnv, ct),
        };
    }

    private async Task<McpConnectorTestResult> TestHttpAsync(GatewayConnectorTestRequest request, IReadOnlyDictionary<string, string> testEnv, CancellationToken ct)
    {
        string? httpUrl = string.IsNullOrWhiteSpace(request.HttpUrl) ? null : request.HttpUrl.Trim();
        if (string.IsNullOrWhiteSpace(httpUrl))
            return new McpConnectorTestResult(false, "HTTP transport requires a URL.");

        if (!Uri.TryCreate(httpUrl, UriKind.Absolute, out Uri? uri))
            return new McpConnectorTestResult(false, $"'{httpUrl}' is not a valid URL.");

        if (!ProbeNetworkPolicy.IsAllowed(httpUrl, request.AllowPrivateDestination))
            return new McpConnectorTestResult(false, "Connector test destination is not allowed.");

        try
        {
            var transportOptions = new HttpClientTransportOptions
            {
                Endpoint = uri,
                Name = "probe",
                AdditionalHeaders = McpHttpAuthHeaders.Create(testEnv),
            };
            using var probeHandler = ProbeNetworkPolicy.CreateProbeHandler(request.AllowPrivateDestination);
            using var probeClient = new HttpClient(probeHandler, disposeHandler: false);
            var transport = new HttpClientTransport(transportOptions, probeClient, loggerFactory: null, ownsHttpClient: false);
            await using (transport.ConfigureAwait(false))
            {
                McpClient client = await McpClient.CreateAsync(transport, cancellationToken: ct).ConfigureAwait(false);
                await using (client.ConfigureAwait(false))
                {
                    IList<McpClientTool> tools = await client.ListToolsAsync(cancellationToken: ct).ConfigureAwait(false);
                    IReadOnlyList<string> toolNames = tools.Select(t => t.Name).ToList();
                    IReadOnlyDictionary<string, string?> toolDescriptions = tools.ToDictionary(t => t.Name, t => (string?)t.Description);
                    return new McpConnectorTestResult(true, $"Connected. {tools.Count} tool(s) available.", tools.Count, ToolNames: toolNames, ToolDescriptions: toolDescriptions);
                }
            }
        }
        catch (HttpRequestException ex)
        {
            McpConnectorTestServiceLog.HttpProbeFailed(logger, ex, httpUrl);
            return new McpConnectorTestResult(false, $"Connection failed: {ex.Message}");
        }
    }

    private async Task<McpConnectorTestResult> TestContainerAsync(GatewayConnectorTestRequest request, IReadOnlyDictionary<string, string> testEnv, CancellationToken ct)
    {
        string? image = string.IsNullOrWhiteSpace(request.ContainerImage) ? null : request.ContainerImage.Trim();
        if (string.IsNullOrWhiteSpace(image))
            return new McpConnectorTestResult(false, "Container transport requires container_image.");

        string? command = string.IsNullOrWhiteSpace(request.ContainerCommand) ? string.Empty : request.ContainerCommand.Trim();
        string[] args = NormalizeStringArray(request.ContainerArgs);
        int timeoutSeconds = ClampProbeTimeoutSeconds(request.ReadinessTimeoutSeconds);

        try
        {
            ManagerRuntimeSettingsPayload runtimeSettings = contextResolver.ResolveRuntimeSettings();
            IReadOnlyDictionary<string, string> probeEnvironment = contextResolver.ResolveContainerProbeEnvironment(testEnv);
            var probeRequest = new WorkspaceMcpContainerProbeRequest
            {
                Runtime = new ManagerRuntimeSettingsMessage
                {
                    WorkspaceBaseRoot = runtimeSettings.WorkspaceBaseRoot,
                    Image = runtimeSettings.Image,
                    DefaultCpuLimit = runtimeSettings.DefaultCpuLimit,
                    DefaultMemoryLimit = runtimeSettings.DefaultMemoryLimit,
                    DefaultTimeoutSeconds = runtimeSettings.DefaultTimeoutSeconds,
                    LogsEndpoint = runtimeSettings.LogsEndpoint ?? string.Empty,
                    MetricsEndpoint = runtimeSettings.MetricsEndpoint ?? string.Empty,
                    OtlpProtocol = runtimeSettings.OtlpProtocol ?? string.Empty,
                    OtlpHeaders = runtimeSettings.OtlpHeaders ?? string.Empty,
                },
                ConnectorId = string.IsNullOrWhiteSpace(request.Id) ? "probe-container" : request.Id.Trim(),
                Image = image,
                Command = command,
                TimeoutSeconds = timeoutSeconds,
                BundlePath = string.IsNullOrWhiteSpace(request.BundlePath) ? string.Empty : request.BundlePath.Trim(),
                ServerType = string.IsNullOrWhiteSpace(request.ServerType) ? string.Empty : request.ServerType.Trim(),
                McpProtocolVersion = string.IsNullOrWhiteSpace(request.McpProtocolVersion) ? string.Empty : request.McpProtocolVersion.Trim(),
            };
            probeRequest.Args.AddRange(args);
            probeRequest.Environment.AddRange(probeEnvironment.Select(static kv => new WorkspaceKvPair { Key = kv.Key, Value = kv.Value }));

            WorkspaceMcpProbeResponse response = await workspaceGrpcService.ProbeMcpContainerAsync(probeRequest, cancellationToken: ct).ConfigureAwait(false);
            if (!response.Ok)
            {
                return new McpConnectorTestResult(false, response.Message);
            }

            IReadOnlyList<string> toolNames = response.Tools.Select(static t => t.Name).ToList();
            IReadOnlyDictionary<string, string?> toolDescriptions = response.Tools.ToDictionary(static t => t.Name, static t => (string?)t.Description);
            return new McpConnectorTestResult(true, $"Connected. {response.ToolCount} tool(s) available.", response.ToolCount, ToolNames: toolNames, ToolDescriptions: toolDescriptions);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new McpConnectorTestResult(false, "Container connector probe timed out.");
        }
        catch (RpcException ex)
        {
            return new McpConnectorTestResult(false, $"Container connector probe failed: {ex.Message}");
        }
    }

    private async Task<McpConnectorTestResult> TestLocalAsync(GatewayConnectorTestRequest request, IReadOnlyDictionary<string, string> testEnv, CancellationToken ct)
    {
        string[] args = NormalizeStringArray(request.LocalArgs);
        string? command = string.IsNullOrWhiteSpace(request.LocalCommand) ? null : request.LocalCommand.Trim();
        if (string.IsNullOrWhiteSpace(command))
            return new McpConnectorTestResult(false, "Local command transport requires a command.");

        if (!McpCommandTransportMapper.TryMapLocalCommandToContainer(command, args, out MappedContainerTransport mapped))
        {
            return new McpConnectorTestResult(false, McpCommandTransportMapper.BuildUnsupportedCommandMessage(command));
        }

        string? package = command.Equals("npx", StringComparison.OrdinalIgnoreCase) ? ExtractNpmPackage(args) : null;
        return await ProbeMappedContainerAsync(request, mapped, testEnv, package, ct).ConfigureAwait(false);
    }

    private async Task<McpConnectorTestResult> ProbeMappedContainerAsync(
        GatewayConnectorTestRequest request,
        MappedContainerTransport mapped,
        IReadOnlyDictionary<string, string> testEnv,
        string? package,
        CancellationToken ct)
    {
        try
        {
            ManagerRuntimeSettingsPayload runtimeSettings = contextResolver.ResolveRuntimeSettings();
            IReadOnlyDictionary<string, string> probeEnvironment = contextResolver.ResolveContainerProbeEnvironment(testEnv);
            var probRequest = new WorkspaceMcpContainerProbeRequest
            {
                Runtime = new ManagerRuntimeSettingsMessage
                {
                    WorkspaceBaseRoot = runtimeSettings.WorkspaceBaseRoot,
                    Image = runtimeSettings.Image,
                    DefaultCpuLimit = runtimeSettings.DefaultCpuLimit,
                    DefaultMemoryLimit = runtimeSettings.DefaultMemoryLimit,
                    DefaultTimeoutSeconds = runtimeSettings.DefaultTimeoutSeconds,
                },
                ConnectorId = string.IsNullOrWhiteSpace(request.Id) ? "probe-local-normalized" : request.Id.Trim(),
                Image = mapped.ContainerImage,
                Command = mapped.ContainerCommand,
                TimeoutSeconds = ClampProbeTimeoutSeconds(request.ReadinessTimeoutSeconds),
            };
            probRequest.Args.AddRange(mapped.ContainerArgs);
            probRequest.Environment.AddRange(probeEnvironment.Select(static kv => new WorkspaceKvPair { Key = kv.Key, Value = kv.Value }));
            WorkspaceMcpProbeResponse response = await workspaceGrpcService.ProbeMcpContainerAsync(probRequest, cancellationToken: ct).ConfigureAwait(false);

            if (!response.Ok)
            {
                string failureMessage = package is not null
                    ? $"Package '{package}' container probe failed: {response.Message}"
                    : $"Container probe failed: {response.Message}";
                return new McpConnectorTestResult(false, failureMessage, PackageChecked: package);
            }

            IReadOnlyList<string> toolNames = response.Tools.Select(static t => t.Name).ToList();
            IReadOnlyDictionary<string, string?> toolDescriptions = response.Tools.ToDictionary(static t => t.Name, static t => (string?)t.Description);
            string message = package is not null
                ? $"Package '{package}' probed. {response.ToolCount} tool(s) available."
                : $"Connected. {response.ToolCount} tool(s) available.";

            return new McpConnectorTestResult(true,
                message,
                response.ToolCount,
                PackageChecked: package,
                ToolNames: toolNames,
                ToolDescriptions: toolDescriptions);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            string msg = package is not null
                ? $"Container probe started but timed out listing tools for package '{package}'."
                : "Container probe started but timed out listing tools.";
            return new McpConnectorTestResult(false, msg, PackageChecked: package);
        }
        catch (RpcException ex)
        {
            McpConnectorTestServiceLog.ContainerProbeFailed(logger, ex, mapped.ContainerCommand, mapped.ContainerImage);
            string msg = package is not null
                ? $"Package '{package}' container probe failed: {ex.Message}"
                : $"Container probe failed: {ex.Message}";
            return new McpConnectorTestResult(false, msg, PackageChecked: package);
        }
        catch (InvalidOperationException ex)
        {
            McpConnectorTestServiceLog.ContainerProbeFailed(logger, ex, mapped.ContainerCommand, mapped.ContainerImage);
            string msg = package is not null
                ? $"Package '{package}' container probe failed: {ex.Message}"
                : $"Container probe failed: {ex.Message}";
            return new McpConnectorTestResult(false, msg, PackageChecked: package);
        }
    }

    internal static int ClampProbeTimeoutSeconds(int? requested)
        => requested.HasValue ? Math.Clamp(requested.Value, 5, 30) : 30;

    private static Dictionary<string, string> ReadTestEnv(GatewayConnectorTestRequest request)
    {
        if (request.TestEnv is not { Count: > 0 })
        {
            return [];
        }

        var env = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach ((string key, string value) in request.TestEnv)
        {
            if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(value))
            {
                continue;
            }

            env[key] = value.Trim();
        }

        return env;
    }

    private static string[] NormalizeStringArray(string[]? values) =>
        values is null
            ? []
            : [.. values.Where(static value => !string.IsNullOrWhiteSpace(value)).Select(static value => value.Trim())];

    /// <summary>
    /// Extracts the npm package name from local command args.
    /// Handles both patterns:
    ///   - <c>["−y", "--package", "mcp-sqlite", ...]</c>  → "mcp-sqlite"
    ///   - <c>["−y", "@modelcontextprotocol/server-github", ...]</c>  → "@modelcontextprotocol/server-github"
    /// </summary>
    private static string? ExtractNpmPackage(string[] args)
    {
        // Explicit --package flag takes priority.
        int pkgIdx = Array.IndexOf(args, "--package");
        if (pkgIdx >= 0 && pkgIdx + 1 < args.Length)
            return args[pkgIdx + 1];

        // Fall back to the first non-flag arg.
        return Array.Find(args, a => !a.StartsWith('-'));
    }

}

internal static partial class McpConnectorTestServiceLog
{
    [LoggerMessage(EventId = 1, Level = LogLevel.Debug, Message = "[MCP Test] HTTP probe failed for {Url}")]
    public static partial void HttpProbeFailed(ILogger logger, Exception exception, string? url);

    [LoggerMessage(EventId = 3, Level = LogLevel.Debug, Message = "[MCP Test] Container probe failed for command={Command} image={Image}")]
    public static partial void ContainerProbeFailed(ILogger logger, Exception exception, string? command, string? image);
}
