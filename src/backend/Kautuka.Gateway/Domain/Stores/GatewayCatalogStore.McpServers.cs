using Kautuka.Gateway.Domain.Connectors;

namespace Kautuka.Gateway.Domain.Stores;

internal sealed partial class GatewayCatalogStore
{
    private static McpTransport ParsePersistedTransport(string transport) => transport.ToUpperInvariant() switch
    {
        "HTTP" => McpTransport.Http,
        "CONTAINER" => McpTransport.Container,
        _ => throw new InvalidOperationException($"Unsupported MCP transport '{transport}'.")
    };

    public IReadOnlyList<GatewayMcpServer> ListMcpServers() => [.. state.McpServers.Values];

    public bool TryGetMcpServer(string id, out GatewayMcpServer? server) =>
        state.McpServers.TryGetValue(id, out server);

    public GatewayMcpServer SaveMcpServer(string serverId, GatewayConnectorUpsertRequest request, string scope, string? ownerUserId)
        => SaveMcpServerAsync(serverId, request, scope, ownerUserId).GetAwaiter().GetResult();

    public Task<GatewayMcpServer> SaveSystemMcpServerAsync(string serverId, GatewayConnectorUpsertRequest request, CancellationToken cancellationToken = default)
        => SaveMcpServerCoreAsync(serverId, request, GatewayOwnershipScopes.System, ownerUserId: null, isInternalSystemConnectorOverride: true, cancellationToken);

    public async Task<GatewayMcpServer> SaveMcpServerAsync(string serverId, GatewayConnectorUpsertRequest request, string scope, string? ownerUserId, CancellationToken cancellationToken = default)
        => await SaveMcpServerCoreAsync(serverId, request, scope, ownerUserId, isInternalSystemConnectorOverride: null, cancellationToken).ConfigureAwait(false);

    private async Task<GatewayMcpServer> SaveMcpServerCoreAsync(string serverId, GatewayConnectorUpsertRequest request, string scope, string? ownerUserId, bool? isInternalSystemConnectorOverride, CancellationToken cancellationToken)
    {
        bool hasExisting = state.McpServers.TryGetValue(serverId, out GatewayMcpServer? existingServer);
        string name = string.IsNullOrWhiteSpace(request.Name) ? serverId : request.Name.Trim();
        string description = request.Description?.Trim() ?? string.Empty;
        string iconUrl = request.IconUrl?.Trim() ?? string.Empty;
        string? presetId = request.PresetId ?? (hasExisting ? existingServer!.PresetId : null);
        string requestedTransport = string.IsNullOrWhiteSpace(request.Transport) ? "local" : request.Transport.Trim();
        string? httpUrl = request.HttpUrl?.Trim();
        string? localCommand = string.IsNullOrWhiteSpace(request.LocalCommand) ? null : request.LocalCommand.Trim();
        string[] localArgs = NormalizeStringArray(request.LocalArgs);
        string? containerImage = string.IsNullOrWhiteSpace(request.ContainerImage) ? null : request.ContainerImage.Trim();
        string? containerCommand = string.IsNullOrWhiteSpace(request.ContainerCommand) ? null : request.ContainerCommand.Trim();
        string[] containerArgs = NormalizeStringArray(request.ContainerArgs);
        string? containerSidecarName = string.IsNullOrWhiteSpace(request.ContainerSidecarName) ? null : request.ContainerSidecarName.Trim();
        string? bundlePath = string.IsNullOrWhiteSpace(request.BundlePath) ? null : request.BundlePath.Trim();
        string? serverType = string.IsNullOrWhiteSpace(request.ServerType) ? null : request.ServerType.Trim();
        string? runtimeArtifactType = string.IsNullOrWhiteSpace(request.RuntimeArtifactType) ? null : request.RuntimeArtifactType.Trim();
        string? runtimeArtifactSourceDigest = string.IsNullOrWhiteSpace(request.RuntimeArtifactSourceDigest) ? null : request.RuntimeArtifactSourceDigest.Trim();
        string? runtimeArtifactBuildStatus = string.IsNullOrWhiteSpace(request.RuntimeArtifactBuildStatus) ? null : request.RuntimeArtifactBuildStatus.Trim();
        int? readinessTimeoutSeconds = request.ReadinessTimeoutSeconds;
        int? jitCooldownSeconds = request.JitCooldownSeconds;
        string? healthcheckUrl = string.IsNullOrWhiteSpace(request.HealthcheckUrl) ? null : request.HealthcheckUrl.Trim();
        int? healthcheckIntervalMilliseconds = request.HealthcheckIntervalMilliseconds;
        string? mcpProtocolVersion = McpProtocolVersions.Normalize(request.McpProtocolVersion);
        string[]? envKeys = request.EnvKeys is null ? null : NormalizeStringArray(request.EnvKeys);
        bool enabled = request.Enabled ?? true;
        int? toolCount = request.ToolCount;
        string[]? toolNames = request.ToolNames is null ? null : NormalizeStringArray(request.ToolNames);
        bool isInternalSystemConnector = isInternalSystemConnectorOverride
            ?? (hasExisting && existingServer!.IsInternalSystemConnector);

        if (toolNames is { Length: 0 })
        {
            toolNames = null;
        }
        if (toolCount is null && state.McpServers.TryGetValue(serverId, out GatewayMcpServer? prevForToolCount))
        {
            toolCount = prevForToolCount.ToolCount;
        }
        // Preserve existing tool_names if none provided in body
        if (toolNames is null && state.McpServers.TryGetValue(serverId, out GatewayMcpServer? prev))
            toolNames = prev.ToolNames is { Count: > 0 } ? [.. prev.ToolNames] : null;
        // Preserve existing env_keys if none provided in body
        string[]? envKeysFinal = envKeys is { Length: > 0 } ? envKeys : null;
        if (envKeysFinal is null && state.McpServers.TryGetValue(serverId, out GatewayMcpServer? prevForEnv))
            envKeysFinal = prevForEnv.EnvKeys is { Count: > 0 } ? [.. prevForEnv.EnvKeys] : null;
        if (bundlePath is null && state.McpServers.TryGetValue(serverId, out GatewayMcpServer? prevForBundle))
        {
            bundlePath = prevForBundle.BundlePath;
        }

        if (serverType is null && state.McpServers.TryGetValue(serverId, out GatewayMcpServer? prevForServerType))
        {
            serverType = prevForServerType.ServerType;
        }

        if (runtimeArtifactType is null && state.McpServers.TryGetValue(serverId, out GatewayMcpServer? prevForArtifactType))
        {
            runtimeArtifactType = prevForArtifactType.RuntimeArtifactType;
        }

        if (runtimeArtifactSourceDigest is null && state.McpServers.TryGetValue(serverId, out GatewayMcpServer? prevForArtifactDigest))
        {
            runtimeArtifactSourceDigest = prevForArtifactDigest.RuntimeArtifactSourceDigest;
        }

        if (runtimeArtifactBuildStatus is null && state.McpServers.TryGetValue(serverId, out GatewayMcpServer? prevForArtifactStatus))
        {
            runtimeArtifactBuildStatus = prevForArtifactStatus.RuntimeArtifactBuildStatus;
        }
        string now = Timestamp();
        string createdAt = state.McpServers.TryGetValue(serverId, out GatewayMcpServer? existing)
            ? existing.CreatedAt
            : now;
        string scopeValue = hasExisting ? existingServer!.Scope : scope;
        string? ownerUserIdValue = hasExisting ? existingServer!.OwnerUserId : ownerUserId;

        string transport = requestedTransport;
        if (!string.Equals(requestedTransport, "http", StringComparison.OrdinalIgnoreCase))
        {
            if (string.Equals(requestedTransport, "local", StringComparison.OrdinalIgnoreCase)
                || (string.IsNullOrWhiteSpace(containerImage) && !string.IsNullOrWhiteSpace(localCommand)))
            {
                if (!McpCommandTransportMapper.TryMapLocalCommandToContainer(localCommand, localArgs, out MappedContainerTransport mapped))
                {
                    throw new InvalidOperationException(McpCommandTransportMapper.BuildUnsupportedCommandMessage(localCommand));
                }

                transport = "container";
                containerImage = mapped.ContainerImage;
                containerCommand = mapped.ContainerCommand;
                containerArgs = mapped.ContainerArgs;
                localCommand = null;
                localArgs = [];

                if (string.Equals(containerCommand, "npx", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(containerCommand, "node", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(containerCommand, "npm", StringComparison.OrdinalIgnoreCase))
                {
                    serverType = "node";
                }
                else if (string.Equals(containerCommand, "python", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(containerCommand, "python3", StringComparison.OrdinalIgnoreCase))
                {
                    serverType = "python";
                }
                else if (string.Equals(containerCommand, "uv", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(containerCommand, "uvx", StringComparison.OrdinalIgnoreCase))
                {
                    serverType = "uv";
                }
            }
        }

        var server = new GatewayMcpServer(serverId, name, description, iconUrl, presetId, transport,
            httpUrl, containerImage, containerCommand, containerArgs, containerSidecarName,
            bundlePath, serverType,
            readinessTimeoutSeconds, jitCooldownSeconds, healthcheckUrl, healthcheckIntervalMilliseconds,
            enabled, isInternalSystemConnector, createdAt, now, scopeValue, ownerUserIdValue, toolCount, toolNames, envKeysFinal,
            runtimeArtifactType, runtimeArtifactSourceDigest, runtimeArtifactBuildStatus, mcpProtocolVersion);

        McpTransport parsedTransport = ParsePersistedTransport(transport);

        topologyWriter.SaveMcpServer(new McpServerDefinition
        {
            Id = serverId,
            Name = name,
            Description = description,
            IconUrl = Uri.TryCreate(iconUrl, UriKind.Absolute, out Uri? iconUri) ? iconUri : null,
            PresetId = presetId,
            Transport = parsedTransport,
            HttpUrl = Uri.TryCreate(httpUrl, UriKind.Absolute, out Uri? httpUri) ? httpUri : null,
            ContainerImage = containerImage,
            ContainerCommand = containerCommand,
            ContainerArgs = [.. containerArgs],
            ContainerSidecarName = containerSidecarName,
            BundlePath = bundlePath,
            ServerType = serverType,
            RuntimeArtifactType = runtimeArtifactType,
            RuntimeArtifactSourceDigest = runtimeArtifactSourceDigest,
            RuntimeArtifactBuildStatus = runtimeArtifactBuildStatus,
            ReadinessTimeoutSeconds = readinessTimeoutSeconds,
            JitCooldownSeconds = jitCooldownSeconds,
            HealthcheckUrl = Uri.TryCreate(healthcheckUrl, UriKind.Absolute, out Uri? healthUri) ? healthUri : null,
            HealthcheckIntervalMilliseconds = healthcheckIntervalMilliseconds,
            McpProtocolVersion = mcpProtocolVersion,
            Enabled = enabled,
            IsInternalSystemConnector = isInternalSystemConnector,
            Scope = scopeValue,
            OwnerUserId = ownerUserIdValue,
            CreatedAt = createdAt,
            UpdatedAt = now,
            ToolCount = toolCount,
            ToolNames = toolNames,
            EnvKeys = envKeysFinal ?? [],
        });

        state.McpServers[serverId] = server;

        DesiredStatePublishResult published = await desiredStatePublisher.TryPublishCurrentAsync("connector-save", cancellationToken).ConfigureAwait(false);
        if (!published.Accepted)
        {
            if (published.ManagerUnavailable)
            {
                return server;
            }

            if (hasExisting && existingServer is not null)
            {
                topologyWriter.SaveMcpServer(ToDefinition(existingServer));
                state.McpServers[serverId] = existingServer;
            }
            else
            {
                topologyWriter.DeleteMcpServer(serverId);
                state.McpServers.Remove(serverId);
            }

            throw new InvalidOperationException($"Connector '{serverId}' could not be activated because Manager rejected the desired state update.");
        }

        if (string.Equals(server.RuntimeArtifactType, "oci", StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrWhiteSpace(server.BundlePath)
            && !string.Equals(server.RuntimeArtifactBuildStatus, "ready", StringComparison.OrdinalIgnoreCase))
        {
            GatewayMcpServer activatedServer = server with
            {
                RuntimeArtifactBuildStatus = "ready",
                UpdatedAt = Timestamp(),
            };

            topologyWriter.SaveMcpServer(ToDefinition(activatedServer));
            state.McpServers[serverId] = activatedServer;
            server = activatedServer;
        }

        return server;
    }

    public bool DeleteMcpServer(string serverId)
        => DeleteMcpServerAsync(serverId).GetAwaiter().GetResult();

    public async Task<bool> DeleteMcpServerAsync(string serverId, CancellationToken cancellationToken = default)
    {
        if (state.McpServers.TryGetValue(serverId, out GatewayMcpServer? existing)
            && existing.IsInternalSystemConnector)
        {
            return false;
        }

        topologyWriter.DeleteMcpServer(serverId);
        bool removed = state.McpServers.Remove(serverId);
        if (removed)
        {
            await desiredStatePublisher.TryPublishCurrentAsync("connector-delete", cancellationToken).ConfigureAwait(false);
        }

        return removed;
    }

    public GatewayMcpServer SaveMcpServerDiscovery(string serverId, int? toolCount, IReadOnlyList<string>? toolNames)
        => SaveMcpServerDiscoveryAsync(serverId, toolCount, toolNames).GetAwaiter().GetResult();

    public Task<GatewayMcpServer> SaveMcpServerDiscoveryAsync(string serverId, int? toolCount, IReadOnlyList<string>? toolNames, CancellationToken cancellationToken = default)
    {
        if (!state.McpServers.TryGetValue(serverId, out GatewayMcpServer? existingServer) || existingServer is null)
        {
            throw new InvalidOperationException($"Connector '{serverId}' not found.");
        }

        string[]? normalizedToolNames = toolNames is null
            ? null
            : NormalizeStringArray([.. toolNames]);

        if (normalizedToolNames is { Length: 0 })
        {
            normalizedToolNames = null;
        }

        string now = Timestamp();
        var server = existingServer with
        {
            UpdatedAt = now,
            ToolCount = toolCount,
            ToolNames = normalizedToolNames,
        };

        topologyWriter.SaveMcpServer(new McpServerDefinition
        {
            Id = server.Id,
            Name = server.Name,
            Description = server.Description,
            IconUrl = Uri.TryCreate(server.IconUrl, UriKind.Absolute, out Uri? iconUri) ? iconUri : null,
            PresetId = server.PresetId,
            Transport = ParsePersistedTransport(server.Transport),
            HttpUrl = Uri.TryCreate(server.HttpUrl, UriKind.Absolute, out Uri? httpUri) ? httpUri : null,
            ContainerImage = server.ContainerImage,
            ContainerCommand = server.ContainerCommand,
            ContainerArgs = [.. server.ContainerArgs],
            ContainerSidecarName = server.ContainerSidecarName,
            BundlePath = server.BundlePath,
            ServerType = server.ServerType,
            RuntimeArtifactType = server.RuntimeArtifactType,
            RuntimeArtifactSourceDigest = server.RuntimeArtifactSourceDigest,
            RuntimeArtifactBuildStatus = server.RuntimeArtifactBuildStatus,
            ReadinessTimeoutSeconds = server.ReadinessTimeoutSeconds,
            JitCooldownSeconds = server.JitCooldownSeconds,
            HealthcheckUrl = Uri.TryCreate(server.HealthcheckUrl, UriKind.Absolute, out Uri? healthUri) ? healthUri : null,
            HealthcheckIntervalMilliseconds = server.HealthcheckIntervalMilliseconds,
            McpProtocolVersion = server.McpProtocolVersion,
            Enabled = server.Enabled,
            IsInternalSystemConnector = server.IsInternalSystemConnector,
            Scope = server.Scope,
            OwnerUserId = server.OwnerUserId,
            ToolCount = toolCount,
            ToolNames = normalizedToolNames,
            EnvKeys = [.. server.EnvKeys ?? []],
            CreatedAt = server.CreatedAt,
            UpdatedAt = now,
        });

        state.McpServers[serverId] = server;
        return Task.FromResult(server);
    }

    private static string[] NormalizeStringArray(string[]? values) =>
        values is null
            ? []
            : [.. values.Where(static value => !string.IsNullOrWhiteSpace(value)).Select(static value => value.Trim())];

    private static McpServerDefinition ToDefinition(GatewayMcpServer server)
        => new()
        {
            Id = server.Id,
            Name = server.Name,
            Description = server.Description,
            IconUrl = Uri.TryCreate(server.IconUrl, UriKind.Absolute, out Uri? iconUri) ? iconUri : null,
            PresetId = server.PresetId,
            Transport = ParsePersistedTransport(server.Transport),
            HttpUrl = Uri.TryCreate(server.HttpUrl, UriKind.Absolute, out Uri? httpUri) ? httpUri : null,
            ContainerImage = server.ContainerImage,
            ContainerCommand = server.ContainerCommand,
            ContainerArgs = [.. server.ContainerArgs],
            ContainerSidecarName = server.ContainerSidecarName,
            BundlePath = server.BundlePath,
            ServerType = server.ServerType,
            RuntimeArtifactType = server.RuntimeArtifactType,
            RuntimeArtifactSourceDigest = server.RuntimeArtifactSourceDigest,
            RuntimeArtifactBuildStatus = server.RuntimeArtifactBuildStatus,
            ReadinessTimeoutSeconds = server.ReadinessTimeoutSeconds,
            JitCooldownSeconds = server.JitCooldownSeconds,
            HealthcheckUrl = Uri.TryCreate(server.HealthcheckUrl, UriKind.Absolute, out Uri? healthUri) ? healthUri : null,
            HealthcheckIntervalMilliseconds = server.HealthcheckIntervalMilliseconds,
            McpProtocolVersion = server.McpProtocolVersion,
            Enabled = server.Enabled,
            IsInternalSystemConnector = server.IsInternalSystemConnector,
            Scope = server.Scope,
            OwnerUserId = server.OwnerUserId,
            ToolCount = server.ToolCount,
            ToolNames = server.ToolNames,
            EnvKeys = [.. server.EnvKeys ?? []],
            CreatedAt = server.CreatedAt,
            UpdatedAt = server.UpdatedAt,
        };

    private static string Timestamp() => DateTimeOffset.UtcNow.ToString("O");
}
