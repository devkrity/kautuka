using System.Security.Cryptography;
using System.Text;

using Kautuka.Gateway.Domain.Connectors;
using Kautuka.Gateway.Domain.Sandbox;
using Kautuka.Manager.Protos;

using Microsoft.Data.Sqlite;

namespace Kautuka.Gateway.Infrastructure.Sandbox;

internal sealed class WorkspaceRuntimeContextResolver(
    IPlatformConfigurationProvider configurationProvider,
    IConfiguration configuration,
    KautukaDatabase database)
{
    private const string managerProxyModeKey = "ManagerProxy:Mode";
    private const string managerProxyNpmRegistryUrlKey = "ManagerProxy:NpmRegistryUrl";
    private const string managerProxyUvIndexUrlKey = "ManagerProxy:UvIndexUrl";
    private const string managerProxyPipIndexUrlKey = "ManagerProxy:PipIndexUrl";
    private const string defaultProxyNpmRegistryUrl = "http://kautuka-pkgs-proxy:20101/npm/";
    private const string defaultProxyPythonIndexUrl = "http://kautuka-pkgs-proxy:20101/pypi/simple/";
    private static readonly string[] connectorProxyKeys = [
        "HTTP_PROXY", "HTTPS_PROXY", "NO_PROXY",
        "http_proxy", "https_proxy", "no_proxy",
        "NPM_CONFIG_REGISTRY", "PIP_INDEX_URL", "PIP_TRUSTED_HOST",
        "UV_INDEX_URL", "UV_DEFAULT_INDEX"];
    private static readonly string[] disallowedPublicPackageIndexHosts =
    [
        "registry.npmjs.org",
        "registry.npmjs.com",
        "pypi.org",
        "www.pypi.org",
        "pypi.python.org",
        "files.pythonhosted.org",
    ];
    private static readonly string[] baseCapabilities = ["spawn_sandbox", "run_shell", "inspect_sandbox", "close_sandbox"];

    public WorkspaceRuntimeContext ResolveAgentWorkspace(string agentId, string? userId = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agentId);

        PlatformConfiguration platformConfiguration = configurationProvider.GetCurrent();
        return ResolveAgentWorkspace(platformConfiguration, agentId, userId);
    }

    public WorkspaceDesiredStatePayload ResolveDesiredState()
    {
        PlatformConfiguration platformConfiguration = configurationProvider.GetCurrent();
        ManagerRuntimeSettingsPayload runtimeSettings = ResolveRuntimeSettings(platformConfiguration);
        string[] activeUserIds = LoadActiveUserIds(database);
        WorkspaceRuntimeContext[] workspaces = [.. platformConfiguration.Agents
            .SelectMany(agent => ResolveDesiredAgentWorkspaces(platformConfiguration, agent.Id, activeUserIds))];
        Dictionary<string, WorkspaceRuntimeContext> workspaceByAgentId = platformConfiguration.Agents
            .ToDictionary(
                static agent => agent.Id,
                agent => ResolveAgentWorkspace(platformConfiguration, agent.Id, null),
                StringComparer.Ordinal);

        WorkspaceMcpContainerConnectorPayload[] containerConnectors = [.. platformConfiguration.McpServers
            .Where(server => server.Transport == McpTransport.Container && (server.Enabled || server.IsInternalSystemConnector))
            .SelectMany(server => ResolveConnectorScopeUserIds(server, activeUserIds).Select(userId =>
                new WorkspaceMcpContainerConnectorPayload(
                    WorkspaceMcpConnectorIdentity.BuildScopedConnectorId(userId, server.Id),
                    server.Id,
                    ReleasedSandboxImage.Select(ReleasedSandboxImage.Current, server.ContainerImage, server.ServerType),
                    server.ContainerCommand ?? string.Empty,
                    server.ContainerArgs,
                    server.Enabled,
                    server.IsInternalSystemConnector,
                    server.BundlePath,
                    server.ServerType,
                    ResolveConnectorEnvironment(server, platformConfiguration.Kautuka.Sandbox.ContainerEnvironment),
                    server.ReadinessTimeoutSeconds ?? 30,
                    server.JitCooldownSeconds ?? 600,
                    server.McpProtocolVersion)))];
        WorkspaceSandboxConnectorBindingsPayload[] sandboxConnectorBindings = [.. platformConfiguration.Agents
            .Select(agent => ResolveSandboxConnectorBindings(platformConfiguration, agent, workspaceByAgentId[agent.Id], activeUserIds))];

        return new WorkspaceDesiredStatePayload(runtimeSettings, workspaces, containerConnectors, sandboxConnectorBindings);
    }

    private static WorkspaceSandboxConnectorBindingsPayload ResolveSandboxConnectorBindings(
        PlatformConfiguration configuration,
        AgentDefinition agent,
        WorkspaceRuntimeContext workspace,
        IReadOnlyList<string> activeUserIds)
    {
        Dictionary<string, McpServerDefinition> connectorsById = configuration.McpServers
            .ToDictionary(static server => server.Id, StringComparer.Ordinal);

        string[] connectorIds = [.. ResolveSandboxConnectorIds(agent)
            .SelectMany(connectorId =>
            {
                if (!connectorsById.TryGetValue(connectorId, out McpServerDefinition? server))
                {
                    return [];
                }

                if (!GatewayOwnershipAccess.CallerCanUse(agent.OwnerUserId, server.Scope, server.OwnerUserId))
                {
                    return [];
                }

                if (string.IsNullOrWhiteSpace(server.BundlePath))
                {
                    return [];
                }

                return ResolveConnectorScopeUserIds(server, activeUserIds)
                    .Select(userId => WorkspaceMcpConnectorIdentity.BuildScopedConnectorId(userId, server.Id));
            })
            .Distinct(StringComparer.Ordinal)
            .OrderBy(static connectorId => connectorId, StringComparer.Ordinal)];

        return new WorkspaceSandboxConnectorBindingsPayload(
            workspace.ScopeType,
            workspace.ScopeId,
            connectorIds);
    }

    private static IEnumerable<string> ResolveSandboxConnectorIds(AgentDefinition agent)
    {
        if (!(agent.Sandbox?.Enabled ?? true))
        {
            return [];
        }

        IReadOnlyList<AgentMcpBindingDefinition> bindings = agent.McpBindings ?? [];

        if (bindings.Count > 0)
        {
            return bindings
                .Where(static binding => !string.IsNullOrWhiteSpace(binding.ConnectorId)
                    && NormalizeBindingContext(binding.Context) == WorkspaceMcpExecutionContext.AgentSandbox)
                .Select(static binding => binding.ConnectorId)
                .Distinct(StringComparer.Ordinal);
        }

        return [];
    }

    private static WorkspaceMcpExecutionContext NormalizeBindingContext(string? context)
    {
        if (string.Equals(context, "AgentContext", StringComparison.OrdinalIgnoreCase)
            || string.Equals(context, "agent", StringComparison.OrdinalIgnoreCase))
        {
            return WorkspaceMcpExecutionContext.AgentSandbox;
        }

        if (string.Equals(context, "OwnContextWithAgentFiles", StringComparison.OrdinalIgnoreCase))
        {
            return WorkspaceMcpExecutionContext.IsolatedWithAgentFiles;
        }

        return WorkspaceMcpExecutionContext.Isolated;
    }

    private static string[] LoadActiveUserIds(KautukaDatabase database)
    {
        using var connection = new SqliteConnection(database.ConnectionString);
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT user_id
            FROM runtime_users
            WHERE is_active = 1
            """;

        using SqliteDataReader reader = command.ExecuteReader();
        var userIds = new List<string>();
        while (reader.Read())
        {
            string userId = reader.IsDBNull(0) ? string.Empty : reader.GetString(0);
            if (string.IsNullOrWhiteSpace(userId))
            {
                continue;
            }

            userIds.Add(userId);
        }

        return [.. userIds.Distinct(StringComparer.Ordinal)];
    }

    private List<KeyValuePair<string, string>> ResolveConnectorEnvironment(
        McpServerDefinition server,
        IReadOnlyDictionary<string, string> sandboxEnvironment)
    {
        _ = server;

        Dictionary<string, string> mergedEnvironment = BuildResolvedContainerEnvironment(sandboxEnvironment);

        var resolved = new List<KeyValuePair<string, string>>();
        var seenKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string key in connectorProxyKeys)
        {
            if (!mergedEnvironment.TryGetValue(key, out string? value) || string.IsNullOrWhiteSpace(value))
            {
                continue;
            }

            if (!seenKeys.Add(key))
            {
                continue;
            }

            resolved.Add(new KeyValuePair<string, string>(key, value));
        }

        return resolved;
    }

    private Dictionary<string, string> BuildGatewayPackageProxyEnvironment()
    {
        if (string.Equals(configuration[managerProxyModeKey], "upstream", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "ManagerProxy:Mode=upstream is refused. Package indexes must stay on kautuka-pkgs-proxy; there is no public-registry fallback.");
        }

        string npmRegistry = configuration[managerProxyNpmRegistryUrlKey] ?? defaultProxyNpmRegistryUrl;
        string uvIndexUrl = configuration[managerProxyUvIndexUrlKey] ?? defaultProxyPythonIndexUrl;
        string pipIndexUrl = configuration[managerProxyPipIndexUrlKey] ?? defaultProxyPythonIndexUrl;

        EnsureNotPublicPackageIndex(npmRegistry, "NPM_CONFIG_REGISTRY");
        EnsureNotPublicPackageIndex(uvIndexUrl, "UV_INDEX_URL");
        EnsureNotPublicPackageIndex(pipIndexUrl, "PIP_INDEX_URL");

        var environment = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["NPM_CONFIG_REGISTRY"] = npmRegistry,
            ["UV_INDEX_URL"] = uvIndexUrl,
            ["UV_DEFAULT_INDEX"] = uvIndexUrl,
            ["PIP_INDEX_URL"] = pipIndexUrl,
        };

        string? pipTrustedHost = GetHttpTrustedHost(pipIndexUrl);
        if (pipTrustedHost is not null)
        {
            environment["PIP_TRUSTED_HOST"] = pipTrustedHost;
        }

        return environment;
    }

    private static string? GetHttpTrustedHost(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? uri)
            || !string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
            || string.IsNullOrWhiteSpace(uri.Host))
        {
            return null;
        }

        return uri.Host;
    }

    private static readonly HashSet<string> unsafeProbeEnvironmentOverrides = new(StringComparer.OrdinalIgnoreCase)
    {
        "PATH", "PATHEXT",
        "LD_PRELOAD", "LD_LIBRARY_PATH", "LD_AUDIT",
        "DYLD_INSERT_LIBRARIES", "DYLD_LIBRARY_PATH",
        "NODE_OPTIONS", "NODE_PATH",
        "PYTHONPATH", "PYTHONSTARTUP", "PYTHONHOME",
        "BASH_ENV", "SHELLOPTS",
        "HTTP_PROXY", "HTTPS_PROXY", "ALL_PROXY", "NO_PROXY",
        "SSLKEYLOGFILE",
        "HOME", "USERPROFILE", "HOMEDRIVE", "HOMEPATH",
        "TMPDIR", "TEMP", "TMP",
        "XDG_CONFIG_HOME", "XDG_RUNTIME_DIR", "XDG_DATA_HOME", "XDG_CACHE_HOME",
        "APPDATA", "LOCALAPPDATA",
        "COMSPEC", "SYSTEMROOT", "WINDIR",
        "CONTAINERS_CONF", "CONTAINERS_CONF_OVERRIDE", "CONTAINERS_STORAGE_CONF", "CONTAINERS_REGISTRIES_CONF",
        "CONTAINERS_HELPER_BINARY_DIR",
        "CONTAINER_HOST", "CONTAINER_SSHKEY", "CONTAINER_PASSPHRASE", "CONTAINER_CONNECTION",
        "DOCKER_HOST", "DOCKER_CONFIG", "DOCKER_CERT_PATH", "DOCKER_TLS_VERIFY", "DOCKER_CONTEXT",
        "REGISTRY_AUTH_FILE", "PODMAN_TMPDIR",
        "STORAGE_OPTS",
    };

    private static bool IsUnsafeProbeEnvironmentOverride(string key)
        => unsafeProbeEnvironmentOverrides.Contains(key);

    private Dictionary<string, string> BuildResolvedContainerEnvironment(
        IReadOnlyDictionary<string, string> baseEnvironment,
        IReadOnlyDictionary<string, string>? overrides = null)
    {
        var mergedEnvironment = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach ((string key, string value) in baseEnvironment)
        {
            if (!string.IsNullOrWhiteSpace(key) && !string.IsNullOrWhiteSpace(value))
            {
                mergedEnvironment[key] = value;
            }
        }

        if (overrides is not null)
        {
            foreach ((string key, string value) in overrides)
            {
                if (!string.IsNullOrWhiteSpace(key) && !string.IsNullOrWhiteSpace(value) && !IsUnsafeProbeEnvironmentOverride(key))
                {
                    mergedEnvironment[key] = value;
                }
            }
        }

        Dictionary<string, string> proxyEnvironment = BuildGatewayPackageProxyEnvironment();
        foreach ((string key, string value) in proxyEnvironment)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                mergedEnvironment[key] = value;
            }
        }

        mergedEnvironment.Remove("PIP_EXTRA_INDEX_URL");
        mergedEnvironment.Remove("UV_EXTRA_INDEX_URL");
        mergedEnvironment.Remove("UV_INDEX");

        if (!proxyEnvironment.ContainsKey("PIP_TRUSTED_HOST"))
        {
            mergedEnvironment.Remove("PIP_TRUSTED_HOST");
        }

        return mergedEnvironment;
    }

    private static void EnsureNotPublicPackageIndex(string url, string settingName)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? uri) || string.IsNullOrWhiteSpace(uri.Host))
        {
            throw new InvalidOperationException($"{settingName} '{url}' is not an absolute package-index URL.");
        }

        if (disallowedPublicPackageIndexHosts.Contains(uri.Host, StringComparer.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"{settingName} '{url}' is a public package index. Refusing public-registry fallback; use kautuka-pkgs-proxy.");
        }
    }

    private static IEnumerable<string> ResolveConnectorScopeUserIds(McpServerDefinition server, IReadOnlyList<string> activeUserIds)
    {
        if (string.Equals(server.Scope, GatewayOwnershipScopes.User, StringComparison.OrdinalIgnoreCase))
        {
            if (!string.IsNullOrWhiteSpace(server.OwnerUserId)
                && activeUserIds.Contains(server.OwnerUserId, StringComparer.Ordinal))
            {
                yield return server.OwnerUserId;
            }

            yield break;
        }

        foreach (string userId in activeUserIds)
        {
            yield return userId;
        }

        yield return WorkspaceMcpConnectorIdentity.SystemUserScope;
    }

    public ManagerRuntimeSettingsPayload ResolveRuntimeSettings()
        => ResolveRuntimeSettings(configurationProvider.GetCurrent());

    public IReadOnlyDictionary<string, string> ResolveContainerProbeEnvironment(IReadOnlyDictionary<string, string>? overrides = null)
    {
        PlatformConfiguration platformConfiguration = configurationProvider.GetCurrent();
        return BuildResolvedContainerEnvironment(platformConfiguration.Kautuka.Sandbox.ContainerEnvironment, overrides);
    }

    private WorkspaceRuntimeContext ResolveAgentWorkspace(
        PlatformConfiguration configuration,
        string agentId,
        string? userId)
    {
        string root = ResolveBaseRoot(configuration.Kautuka.Workspace);
        AgentDefinition? agent = configuration.Agents.FirstOrDefault(candidate =>
            string.Equals(candidate.Id, agentId, StringComparison.Ordinal));
        string scopeName = agent is not null ? StableScopeId(agent) : DistinctSegment(agentId);
        string hostWorkspace = Path.Combine(root, "agents", scopeName);
        if (IsSharedSystemAgent(configuration, agentId))
        {
            hostWorkspace = Path.Combine(root, "agents", WorkspaceTooling.BuildAgentScopeId(agentId));
            if (!string.IsNullOrWhiteSpace(userId))
            {
                hostWorkspace = Path.Combine(hostWorkspace, WorkspaceTooling.BuildAgentScopeId(userId));
            }
        }
        SandboxRuntimeDefinition sandbox = configuration.Kautuka.Sandbox;

        string sandboxImage = ReleasedSandboxImage.Select(ReleasedSandboxImage.Current, sandbox.Image);
        if (string.IsNullOrWhiteSpace(sandboxImage))
        {
            throw new InvalidOperationException("infra.sandbox.image is not configured in system_settings");
        }

        return new WorkspaceRuntimeContext(
            "agent",
            scopeName,
            userId,
            hostWorkspace,
            "/workspace",
            hostWorkspace,
            sandboxImage,
            sandbox.DefaultTimeout,
            baseCapabilities,
            BuildResolvedContainerEnvironment(sandbox.ContainerEnvironment));
    }

    private ManagerRuntimeSettingsPayload ResolveRuntimeSettings(PlatformConfiguration configuration)
    {
        SandboxRuntimeDefinition sandbox = configuration.Kautuka.Sandbox;
        WorkspaceTelemetrySettingsPayload telemetry = ResolveTelemetrySettings();
        int defaultCpuLimit = sandbox.DefaultCpus > 0
            ? sandbox.DefaultCpus
            : throw new InvalidOperationException("infra.sandbox.default_cpus must be greater than zero in system_settings");
        string defaultMemoryLimit = !string.IsNullOrWhiteSpace(sandbox.DefaultMemory)
            ? sandbox.DefaultMemory
            : throw new InvalidOperationException("infra.sandbox.default_memory is not configured in system_settings");

        string sandboxImage = ReleasedSandboxImage.Select(ReleasedSandboxImage.Current, sandbox.Image);
        if (string.IsNullOrWhiteSpace(sandboxImage))
        {
            throw new InvalidOperationException("infra.sandbox.image is not configured in system_settings");
        }

        return new ManagerRuntimeSettingsPayload(
            ResolveBaseRoot(configuration.Kautuka.Workspace),
            sandboxImage,
            defaultCpuLimit,
            defaultMemoryLimit,
            sandbox.DefaultTimeout,
            telemetry.TracesEndpoint,
            telemetry.LogsEndpoint,
            telemetry.MetricsEndpoint,
            telemetry.OtlpProtocol,
            telemetry.OtlpHeaders);
    }

    private WorkspaceTelemetrySettingsPayload ResolveTelemetrySettings()
        => new(
            KautukaOtlpSettings.ResolveTracesEndpoint(configuration),
            KautukaOtlpSettings.ResolveLogsEndpoint(configuration),
            KautukaOtlpSettings.ResolveMetricsEndpoint(configuration),
            KautukaOtlpSettings.ResolveProtocol(configuration),
            KautukaOtlpSettings.ResolveHeaders(configuration));

    private string ResolveBaseRoot(WorkspaceDefinition workspace)
        => WorkspaceHostRootPolicy.ResolveWorkspaceBaseRoot(configuration, workspace.BaseRoot);

    private IEnumerable<WorkspaceRuntimeContext> ResolveDesiredAgentWorkspaces(
        PlatformConfiguration configuration,
        string agentId,
        IReadOnlyList<string> activeUserIds)
    {
        if (!IsSharedSystemAgent(configuration, agentId))
        {
            yield return ResolveAgentWorkspace(configuration, agentId, null);
            yield break;
        }

        foreach (string userId in activeUserIds)
        {
            yield return ResolveAgentWorkspace(configuration, agentId, userId);
        }
    }

    private static string StableScopeId(AgentDefinition agent)
    {
        string ownerId = string.IsNullOrWhiteSpace(agent.OwnerUserId) ? agent.Id : agent.OwnerUserId;
        string scopeId = DistinctSegment(ownerId) + "-" + DistinctSegment(agent.Id);
        if (scopeId.Length <= 128)
        {
            return scopeId;
        }

        // Manager rejects a workspace id longer than 128 characters, and one rejected
        // id fails the whole desired-state update. The hash is taken from the raw ids
        // so two long ids that sanitize alike stay apart, and it cannot match a short
        // id because those begin with the owner segment.
        string hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(ownerId + "\n" + agent.Id)));
        return "ws-" + hash;
    }

    private static string DistinctSegment(string value)
    {
        string sanitized = WorkspaceTooling.BuildAgentScopeId(value);
        if (string.Equals(sanitized, value, StringComparison.Ordinal))
        {
            return sanitized;
        }

        string hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)).AsSpan(0, 4));
        return sanitized + "-" + hash;
    }

    private static bool IsSharedSystemAgent(PlatformConfiguration configuration, string agentId)
        => configuration.Agents.Any(agent =>
            string.Equals(agent.Id, agentId, StringComparison.Ordinal)
            && string.Equals(agent.Scope, GatewayOwnershipScopes.System, StringComparison.Ordinal));
}

internal sealed record ManagerRuntimeSettingsPayload(
    string WorkspaceBaseRoot,
    string Image,
    int DefaultCpuLimit,
    string DefaultMemoryLimit,
    int DefaultTimeoutSeconds,
    string? TracesEndpoint,
    string? LogsEndpoint,
    string? MetricsEndpoint,
    string? OtlpProtocol,
    string? OtlpHeaders);

internal sealed record WorkspaceTelemetrySettingsPayload(
    string? TracesEndpoint,
    string? LogsEndpoint,
    string? MetricsEndpoint,
    string? OtlpProtocol,
    string? OtlpHeaders);

internal sealed record WorkspaceMcpContainerConnectorPayload(
    string ConnectorId,
    string ServerId,
    string Image,
    string Command,
    IReadOnlyList<string> Args,
    bool Enabled,
    bool IsInternalSystemConnector,
    string? BundlePath,
    string? ServerType,
    IReadOnlyList<KeyValuePair<string, string>> Environment,
    int ReadinessTimeoutSeconds,
    int JitCooldownSeconds,
    string? McpProtocolVersion = null);

internal sealed record WorkspaceDesiredStatePayload(
    ManagerRuntimeSettingsPayload RuntimeSettings,
    IReadOnlyList<WorkspaceRuntimeContext> Workspaces,
    IReadOnlyList<WorkspaceMcpContainerConnectorPayload> ContainerConnectors,
    IReadOnlyList<WorkspaceSandboxConnectorBindingsPayload> SandboxConnectorBindings);

internal sealed record WorkspaceSandboxConnectorBindingsPayload(
    string ScopeType,
    string ScopeId,
    IReadOnlyList<string> ConnectorIds);
