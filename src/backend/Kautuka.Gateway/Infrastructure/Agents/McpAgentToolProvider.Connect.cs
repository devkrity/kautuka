using Kautuka.Gateway.Domain.Execution;

using Grpc.Core;

using Kautuka.Gateway.Domain.Connectors;
using Kautuka.Gateway.Domain.Sandbox;
using Kautuka.Gateway.Infrastructure.Credentials;
using Kautuka.Manager.Protos;

using ModelContextProtocol.Client;

namespace Kautuka.Gateway.Infrastructure.Agents;

internal sealed partial class McpAgentToolProvider
{
    private const string noInnerMessage = "(none)";
    private static readonly TimeSpan remoteDiscoveryTimeout = TimeSpan.FromSeconds(10);

    private async Task<(string Id, IReadOnlyList<AgentToolSurfaceEntry>? Entries)> ConnectSafeAsync(
        McpServerDefinition serverDef,
        AgentToolContext context,
        CancellationToken ct)
    {
        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        if (serverDef.Transport == McpTransport.Http) timeout.CancelAfter(mcpHttpClientFactory.ApprovalHold + remoteDiscoveryTimeout);
        try
        {
            IReadOnlyList<AgentToolSurfaceEntry> entries = await ListSurfaceEntriesAsync(serverDef, context, timeout).WaitAsync(timeout.Token).ConfigureAwait(false);
            foreach (AgentToolSurfaceEntry entry in entries)
                entry.ExecutionConfigHash = AgentToolingFingerprint.ConnectorExecutionHash(serverDef);
            McpAgentToolProviderLog.ConnectorToolsLoaded(logger, serverDef.Id, entries.Count);
            return (serverDef.Id, entries);
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            McpAgentToolProviderLog.LoadToolsFailed(logger, new TimeoutException("Remote connector discovery timed out."), serverDef.Id, "Remote connector discovery timed out.", noInnerMessage);
            return (serverDef.Id, null);
        }
        catch (Exception ex) when (serverDef.Transport == McpTransport.Http && ex is not OperationCanceledException)
        {
            McpAgentToolProviderLog.LoadToolsFailed(logger, new InvalidOperationException("Remote MCP connection failed."), serverDef.Id, "Remote MCP connection failed.", noInnerMessage);
            return (serverDef.Id, null);
        }
        catch (InvalidOperationException ex)
        {
            McpAgentToolProviderLog.LoadToolsFailed(logger, ex, serverDef.Id, ex.Message, ex.InnerException?.Message ?? noInnerMessage);
            return (serverDef.Id, null);
        }
        catch (NotSupportedException ex)
        {
            McpAgentToolProviderLog.LoadToolsFailed(logger, ex, serverDef.Id, ex.Message, ex.InnerException?.Message ?? noInnerMessage);
            return (serverDef.Id, null);
        }
        catch (HttpRequestException ex)
        {
            McpAgentToolProviderLog.LoadToolsFailed(logger, ex, serverDef.Id, ex.Message, ex.InnerException?.Message ?? noInnerMessage);
            return (serverDef.Id, null);
        }
        catch (IOException ex)
        {
            McpAgentToolProviderLog.LoadToolsFailed(logger, ex, serverDef.Id, ex.Message, ex.InnerException?.Message ?? noInnerMessage);
            return (serverDef.Id, null);
        }
        catch (RpcException ex)
        {
            McpAgentToolProviderLog.LoadToolsFailed(logger, ex, serverDef.Id, ex.Status.Detail, ex.InnerException?.Message ?? noInnerMessage);
            return (serverDef.Id, null);
        }
    }

    private async Task<IReadOnlyList<AgentToolSurfaceEntry>> ListSurfaceEntriesAsync(
        McpServerDefinition serverDef,
        AgentToolContext context,
        CancellationTokenSource timeout)
    {
        CancellationToken ct = timeout.Token;
        if (serverDef.Transport == McpTransport.Container)
        {
            if (serverDef.LocalToolDefinitions is null || string.IsNullOrWhiteSpace(serverDef.LocalImageDigest)
                || (!string.IsNullOrWhiteSpace(serverDef.BundlePath) && string.IsNullOrWhiteSpace(serverDef.LocalArtifactDigest))
                || !string.Equals(serverDef.LocalArtifactDigest, serverDef.RuntimeArtifactSourceDigest, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException($"Connector '{serverDef.Name}' needs Test connection before its tools can be used.");
            }
            return [.. serverDef.LocalToolDefinitions.Select(tool =>
            {
                AgentToolSurfaceEntry entry = AgentToolSurfaceEntry.Describe(AgentToolSurfaceSources.Mcp, serverDef.Id, tool.Name, tool.Description, tool.JsonSchema);
                entry.ArtifactDigest = serverDef.LocalArtifactDigest;
                entry.ImageDigest = serverDef.LocalImageDigest;
                entry.ExecutionConfigHash = AgentToolingFingerprint.ConnectorExecutionHash(serverDef);
                return entry;
            })];
        }

        if (serverDef.Transport == McpTransport.Http)
        {
            string? endpointHash = null;
            McpClient client = await CreateHttpClientAsync(
                serverDef,
                context.UserId,
                context,
                ct,
                captureEndpoint: endpoint => endpointHash = AgentToolingFingerprint.EndpointHash(endpoint),
                networkReady: () => timeout.CancelAfter(remoteDiscoveryTimeout)).ConfigureAwait(false);
            await using (client.ConfigureAwait(false))
            {
                IList<McpClientTool> listed = await client.ListToolsAsync(cancellationToken: ct).ConfigureAwait(false);
                return [.. listed.Select(tool =>
                {
                    AgentToolSurfaceEntry entry = AgentToolSurfaceEntry.Describe(
                        AgentToolSurfaceSources.Mcp,
                        serverDef.Id,
                        tool.Name,
                        tool.Description ?? string.Empty,
                        tool.JsonSchema);
                    entry.EffectiveEndpointHash = endpointHash;
                    return entry;
                })];
            }
        }

        throw new NotSupportedException($"MCP transport '{serverDef.Transport}' is not supported. Only Container and Http transports are supported.");
    }

    private void ValidateRequiredEnv(
        McpServerDefinition serverDef,
        Dictionary<string, string> env)
    {
        if (serverDef.EnvKeys.Count == 0)
        {
            return;
        }

        List<string> missing = [];
        foreach (string key in serverDef.EnvKeys)
        {
            if (string.IsNullOrWhiteSpace(key))
            {
                continue;
            }

            if (env.TryGetValue(key, out string? value) && !string.IsNullOrWhiteSpace(value))
            {
                continue;
            }

            // Refresh tokens are held secrets. Resolve omits them, and Prepare loads
            // the signed-in token from the store when an access token is required.
            if (connectorOAuth?.IsRefreshCredential(serverDef.Id, key) == true)
            {
                continue;
            }

            missing.Add(key);
        }

        if (missing.Count == 0)
        {
            return;
        }

        string missingKeys = string.Join(", ", missing);
        throw new InvalidOperationException($"MCP server '{serverDef.Id}' is missing required credential keys: {missingKeys}.");
    }

    private async Task<Dictionary<string, string>> PrepareConnectorAuthEnvironmentAsync(
        McpServerDefinition serverDef,
        string? userId,
        Dictionary<string, string> env,
        bool exchangeAccessToken,
        CancellationToken ct)
    {
        if (connectorOAuth is null)
        {
            return env;
        }

        return await connectorOAuth.PrepareAsync(serverDef.Id, userId, env, exchangeAccessToken, ct).ConfigureAwait(false);
    }

    private async Task<McpClient> CreateHttpClientAsync(
        McpServerDefinition serverDef,
        string? userId,
        AgentToolContext toolContext,
        CancellationToken ct,
        string? expectedEndpointHash = null,
        Action<Uri>? captureEndpoint = null,
        string? expectedExecutionHash = null,
        Action? networkReady = null)
    {
        string userRevision = credentialRevisions.Get(serverDef.Id, userId ?? string.Empty, includeSystem: false);
        string combinedRevision = credentialRevisions.Get(serverDef.Id, userId ?? string.Empty, includeSystem: true);
        PlatformConfiguration snapshot = configProvider.GetCurrent();
        McpServerDefinition storedDefinition = snapshot.McpServers.FirstOrDefault(candidate => candidate.Id == serverDef.Id)
            ?? throw new HttpRequestException("Remote MCP connector is unavailable.");
        if (expectedExecutionHash is not null && !string.Equals(expectedExecutionHash, AgentToolingFingerprint.ConnectorExecutionHash(storedDefinition), StringComparison.Ordinal))
            throw new InvalidOperationException("The connector configuration changed. Start a new run.");
        string fingerprint = AgentToolingFingerprint.Compute(toolContext.AgentDefinition, snapshot);
        serverDef = await ResolveHttpUrlOverrideAsync(storedDefinition, userId, ct).ConfigureAwait(false);
        if (expectedEndpointHash is not null && !string.Equals(expectedEndpointHash, AgentToolingFingerprint.EndpointHash(serverDef.HttpUrl), StringComparison.Ordinal))
            throw new InvalidOperationException("The connector endpoint changed. Start a new run.");
        bool personalDestination = IsPersonalHttpDestination(storedDefinition, serverDef);
        bool includeSystem = IncludeSystemCredentials(serverDef, personalDestination);
        string revision = includeSystem ? combinedRevision : userRevision;
        if (!string.Equals(revision, credentialRevisions.Get(serverDef.Id, userId ?? string.Empty, includeSystem), StringComparison.Ordinal)
            || !string.Equals(fingerprint, AgentToolingFingerprint.Compute(toolContext.AgentDefinition, configProvider.GetCurrent()), StringComparison.Ordinal))
            throw new HttpRequestException("Remote MCP authorization or configuration changed. Retry the operation.");
        if (serverDef.HttpUrl is null)
            throw new InvalidOperationException($"MCP server '{serverDef.Id}' has http transport but no URL configured.");

        var transportOptions = new HttpClientTransportOptions
        {
            Endpoint = serverDef.HttpUrl,
            Name = serverDef.Name,
        };
        bool allowPrivateDestination = AllowsPrivateRuntimeDestination(
            serverDef.Scope,
            personalDestination,
            IsAdminUser(userId ?? string.Empty));

        Uri approvedEndpoint = serverDef.HttpUrl;
        captureEndpoint?.Invoke(approvedEndpoint);
        async Task RevalidateAsync(CancellationToken token)
        {
            PlatformConfiguration current = configProvider.GetCurrent();
            AgentDefinition? agent = current.Agents.FirstOrDefault(candidate => string.Equals(candidate.Id, toolContext.AgentId, StringComparison.Ordinal));
            McpServerDefinition? stored = current.McpServers.FirstOrDefault(candidate => string.Equals(candidate.Id, serverDef.Id, StringComparison.Ordinal));
            if (agent is null || !agent.Enabled || !GatewayOwnershipAccess.CallerCanUse(userId, agent.Scope, agent.OwnerUserId)
                || stored is null || !IsConnectorEnabled(stored, userId)
                || !GatewayOwnershipAccess.CallerCanUse(userId, stored.Scope, stored.OwnerUserId)
                || AllowsPrivateRuntimeDestination(stored.Scope, personalDestination, IsAdminUser(userId ?? string.Empty)) != allowPrivateDestination
                || (gatewayState is not null && !string.IsNullOrEmpty(userId) && !gatewayState.Users.Values.Any(user => user.IsActive && string.Equals(user.Id, userId, StringComparison.Ordinal)))
                || !string.Equals(fingerprint, AgentToolingFingerprint.Compute(agent, current), StringComparison.Ordinal)
                || !string.Equals(revision, credentialRevisions.Get(serverDef.Id, userId ?? string.Empty, includeSystem), StringComparison.Ordinal))
                throw new HttpRequestException("Remote MCP authorization or configuration changed. Retry the operation.");
            McpServerDefinition effective = await ResolveHttpUrlOverrideAsync(stored, userId, token).ConfigureAwait(false);
            if (effective.HttpUrl is null || effective.HttpUrl != approvedEndpoint || IsPersonalHttpDestination(stored, effective) != personalDestination
                || IncludeSystemCredentials(stored, personalDestination) != includeSystem)
                throw new HttpRequestException("Remote MCP destination changed. Retry the operation.");
        }
        var requestContext = new RemoteMcpRequestContext(approvedEndpoint, serverDef.Id, userId ?? string.Empty,
            includeSystem, [.. serverDef.Credentials], [.. serverDef.HttpHeaders],
            (keys, token) => credentialResolver.ResolveRemoteAsync(serverDef.CredentialAssociationId, serverDef.Id, serverDef.CredentialNamespace, userId ?? string.Empty, keys, includeSystem, token), RevalidateAsync,
            () => credentialRevisions.Acquire(serverDef.Id, userId ?? string.Empty, toolContext.AgentId, includeSystem), serverDef.CredentialNamespace, serverDef.McpProtocolVersion,
            PreferIpv4: approvedEndpoint.IdnHost.Equals("mcp.exa.ai", StringComparison.OrdinalIgnoreCase),
            RequiresCredentialReentry: serverDef.RequiresCredentialReentry,
            NetworkReady: networkReady);
        return await mcpHttpClientFactory.CreateAsync(transportOptions, allowPrivateDestination, requestContext, ct).ConfigureAwait(false);
    }

    private static string? ResolveHostArtifactDirectory(AgentToolContext context)
    {
        string path = context.WorkspaceTool.DescribeAgentWorkspace(context.AgentId, context.UserId).HostWorkspacePath;
        return string.IsNullOrWhiteSpace(path) ? null : path;
    }

    private static WorkspaceContextMessage? ResolveWorkspaceContext(AgentToolContext context)
    {
        return ToMessage(context.WorkspaceTool.DescribeAgentWorkspace(context.AgentId, context.UserId));
    }

    private static WorkspaceContextMessage ToMessage(WorkspaceRuntimeContext workspace)
    {
        var message = new WorkspaceContextMessage
        {
            ScopeType = workspace.ScopeType,
            ScopeId = workspace.ScopeId,
            AgentId = workspace.AgentId ?? string.Empty,
            UserId = workspace.UserId ?? string.Empty,
            HostWorkspacePath = workspace.HostWorkspacePath,
            ContainerWorkspacePath = workspace.ContainerWorkspacePath,
            Image = workspace.Image,
            DefaultTimeoutSeconds = workspace.DefaultTimeoutSeconds,
        };

        message.BaseCapabilities.AddRange(workspace.BaseCapabilities);
        message.ContainerEnvironment.AddRange(workspace.ContainerEnvironment.Select(static pair => new WorkspaceKvPair
        {
            Key = pair.Key,
            Value = pair.Value,
        }));
        return message;
    }

    private async ValueTask<Dictionary<string, string>> ResolveEnvAsync(
        McpServerDefinition serverDef,
        string userId,
        CancellationToken ct)
        => await ResolveEnvAsync(
            serverDef,
            userId,
            includeSystemScope: IncludeSystemCredentials(serverDef, personalHttpDestination: false),
            ct).ConfigureAwait(false);

    private async ValueTask<Dictionary<string, string>> ResolveEnvAsync(
        McpServerDefinition serverDef,
        string userId,
        bool includeSystemScope,
        CancellationToken ct)
    {
        if (serverDef.EnvKeys.Count == 0)
            return [];

        IReadOnlyDictionary<string, string> resolved = await credentialResolver.GetResolvedAsync(
            userId,
            serverDef.EnvKeys,
            includeSystemScope,
            includeHeldSecrets: false,
            ct).ConfigureAwait(false);

        return new Dictionary<string, string>(resolved, StringComparer.Ordinal);
    }

    private static bool IncludeSystemCredentials(McpServerDefinition server, bool personalHttpDestination)
        => !personalHttpDestination
            && !string.Equals(server.Scope, GatewayOwnershipScopes.User, StringComparison.OrdinalIgnoreCase);

    private bool IsAdminUser(string userId)
    {
        GatewayUser? user = gatewayState?.Users.Values.FirstOrDefault(candidate =>
            string.Equals(candidate.Id, userId, StringComparison.Ordinal));
        return GatewayOwnershipAccess.IsAdmin(user);
    }

    internal static bool AllowsPrivateRuntimeDestination(string? scope, bool personalDestination, bool isAdmin)
        => isAdmin
            || (!personalDestination
                && !string.Equals(scope, GatewayOwnershipScopes.User, StringComparison.OrdinalIgnoreCase));

    private static bool IsPersonalHttpDestination(McpServerDefinition stored, McpServerDefinition effective)
    {
        if (ReferenceEquals(stored, effective) || effective.HttpUrl is null)
        {
            return false;
        }

        return stored.HttpUrl is null
            || !string.Equals(stored.HttpUrl.AbsoluteUri, effective.HttpUrl.AbsoluteUri, StringComparison.Ordinal);
    }

    internal async Task<List<(string ConnectorId, string Endpoint)>> DescribeCallerEndpointsAsync(
        AgentToolContext context,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);
        PlatformConfiguration config = configProvider.GetCurrent();
        var endpoints = new List<(string ConnectorId, string Endpoint)>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (ResolvedAgentConnectorBinding binding in ResolveAgentConnectorBindings(context.AgentDefinition, toolExecutionPolicyService)
            .OrderBy(static candidate => candidate.ConnectorId, StringComparer.Ordinal))
        {
            if (!seen.Add(binding.ConnectorId))
            {
                continue;
            }

            McpServerDefinition? server = config.McpServers.FirstOrDefault(candidate =>
                string.Equals(candidate.Id, binding.ConnectorId, StringComparison.Ordinal));
            if (server is null
                || !IsConnectorEnabled(server, context.UserId)
                || !GatewayOwnershipAccess.CallerCanUse(context.UserId, server.Scope, server.OwnerUserId))
            {
                continue;
            }

            if (server.Transport != McpTransport.Http)
            {
                endpoints.Add((server.Id, string.Empty));
                continue;
            }

            McpServerDefinition effective = await ResolveHttpUrlOverrideAsync(server, context.UserId, ct).ConfigureAwait(false);
            endpoints.Add((server.Id, effective.HttpUrl?.AbsoluteUri ?? string.Empty));
        }

        return endpoints;
    }

    private async ValueTask<McpServerDefinition> ResolveHttpUrlOverrideAsync(
        McpServerDefinition serverDef,
        string? userId,
        CancellationToken ct)
    {
        if (serverDef.Transport != McpTransport.Http || string.IsNullOrWhiteSpace(userId))
        {
            return serverDef;
        }

        string overrideKey = ConnectorCredentialKeys.BuildHttpUrlOverrideKey(serverDef.Id, serverDef.CredentialNamespace);
        IReadOnlyDictionary<string, string> resolved = await credentialResolver
            .ResolveRemoteAsync(serverDef.CredentialAssociationId, serverDef.Id, serverDef.CredentialNamespace, userId, [overrideKey], false, ct)
            .ConfigureAwait(false);

        if (!resolved.TryGetValue(overrideKey, out string? overrideUrl)
            || !Uri.TryCreate(overrideUrl, UriKind.Absolute, out Uri? parsedOverride)
            || (ProbeNetworkPolicy.IsConfirmedPrivate(overrideUrl) && !IsAdminUser(userId)))
        {
            return serverDef;
        }

        return new McpServerDefinition
        {
            Id = serverDef.Id,
            Name = serverDef.Name,
            Description = serverDef.Description,
            IconUrl = serverDef.IconUrl,
            PresetId = serverDef.PresetId,
            Transport = serverDef.Transport,
            HttpUrl = parsedOverride,
            ContainerImage = serverDef.ContainerImage,
            ContainerCommand = serverDef.ContainerCommand,
            ContainerArgs = serverDef.ContainerArgs,
            ContainerSidecarName = serverDef.ContainerSidecarName,
            BundlePath = serverDef.BundlePath,
            ServerType = serverDef.ServerType,
            ReadinessTimeoutSeconds = serverDef.ReadinessTimeoutSeconds,
            JitCooldownSeconds = serverDef.JitCooldownSeconds,
            HealthcheckUrl = serverDef.HealthcheckUrl,
            HealthcheckIntervalMilliseconds = serverDef.HealthcheckIntervalMilliseconds,
            McpProtocolVersion = serverDef.McpProtocolVersion,
            EnvKeys = serverDef.EnvKeys,
            CredentialNamespace = serverDef.CredentialNamespace,
            CredentialAssociationId = serverDef.CredentialAssociationId,
            CredentialOperationId = serverDef.CredentialOperationId,
            CredentialSourceId = serverDef.CredentialSourceId,
            RequiresCredentialReentry = serverDef.RequiresCredentialReentry,
            Credentials = serverDef.Credentials,
            HttpHeaders = serverDef.HttpHeaders,
            CreatedAt = serverDef.CreatedAt,
            UpdatedAt = serverDef.UpdatedAt,
            Enabled = serverDef.Enabled,
            IsInternalSystemConnector = serverDef.IsInternalSystemConnector,
            Scope = serverDef.Scope,
            OwnerUserId = serverDef.OwnerUserId,
            ToolCount = serverDef.ToolCount,
            ToolNames = serverDef.ToolNames,
        };
    }
}
