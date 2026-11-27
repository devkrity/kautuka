using System.Text.Json;
using System.Text.Json.Nodes;

using Kautuka.Gateway.Domain.Assets;
using Kautuka.Gateway.Domain.Execution;
using Kautuka.Manager.Protos;

using Microsoft.Extensions.AI;

using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace Kautuka.Gateway.Infrastructure.Agents;

internal sealed partial class McpAgentToolProvider
{
    public async ValueTask<AgentToolSurfaceContribution> ContributeAsync(AgentToolContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);

        IReadOnlyList<ResolvedAgentConnectorBinding> bindings = ResolveAgentConnectorBindings(context.AgentDefinition, toolExecutionPolicyService);
        if (bindings.Count == 0)
        {
            return new AgentToolSurfaceContribution([], Complete: true);
        }

        PlatformConfiguration config = configProvider.GetCurrent();
        List<(McpServerDefinition Definition, ResolvedAgentConnectorBinding Binding)> activeDefs = bindings
            .Select(binding => (Definition: config.McpServers.FirstOrDefault(s =>
                string.Equals(s.Id, binding.ConnectorId, StringComparison.Ordinal)), Binding: binding))
            .Where(pair => pair.Definition is not null && IsConnectorEnabled(pair.Definition, context.UserId))
            .Where(pair => GatewayOwnershipAccess.CallerCanUse(context.UserId, pair.Definition!.Scope, pair.Definition.OwnerUserId))
            .Select(static pair => (pair.Definition!, pair.Binding))
            .ToList();

        if (activeDefs.Count == 0)
        {
            return new AgentToolSurfaceContribution([], Complete: true);
        }

        ToolPolicyDecision exposureDecision = toolExecutionPolicyService.EvaluateExposure(
            ToolPolicyCategory.Connector,
            context.AgentDefinition);
        if (!exposureDecision.Allowed)
        {
            return new AgentToolSurfaceContribution([], Complete: true);
        }

        List<string> unavailable = [.. activeDefs.Where(static item => item.Definition.Transport == McpTransport.Http && item.Definition.RequiresCredentialReentry).Select(static item => string.IsNullOrWhiteSpace(item.Definition.Name) ? item.Definition.Id : item.Definition.Name)];
        bool skippedLegacy = activeDefs.RemoveAll(static item => item.Definition.Transport == McpTransport.Http && item.Definition.RequiresCredentialReentry) > 0;

        Task<(string Id, IReadOnlyList<AgentToolSurfaceEntry>? Entries)>[] tasks = activeDefs
            .Select(binding => ConnectSafeAsync(binding.Definition, context, ct))
            .ToArray();
        (string Id, IReadOnlyList<AgentToolSurfaceEntry>? Entries)[] results = await Task.WhenAll(tasks).ConfigureAwait(false);

        var entries = new List<AgentToolSurfaceEntry>();
        bool complete = !skippedLegacy;
        foreach ((string id, IReadOnlyList<AgentToolSurfaceEntry>? listed) in results)
        {
            if (listed is null)
            {
                complete = false;
                McpServerDefinition failed = activeDefs.First(item => string.Equals(item.Definition.Id, id, StringComparison.Ordinal)).Definition;
                unavailable.Add(string.IsNullOrWhiteSpace(failed.Name) ? failed.Id : failed.Name);
                continue;
            }

            entries.AddRange(listed);
        }

        McpAgentToolProviderLog.TotalToolsAvailable(logger, context.AgentId, entries.Count);
        return new AgentToolSurfaceContribution(entries, complete, unavailable);
    }

    public ValueTask<IReadOnlyList<AIFunction>> HydrateAsync(
        AgentToolContext context,
        IReadOnlyList<AgentToolSurfaceEntry> entries,
        CancellationToken ct)
    {
        _ = ct;
        return new ValueTask<IReadOnlyList<AIFunction>>(Hydrate(context, entries));
    }

    private IReadOnlyList<AIFunction> Hydrate(AgentToolContext context, IReadOnlyList<AgentToolSurfaceEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(entries);

        Dictionary<string, ResolvedAgentConnectorBinding> bindings = ResolveAgentConnectorBindings(context.AgentDefinition, toolExecutionPolicyService)
            .ToDictionary(static binding => binding.ConnectorId, StringComparer.Ordinal);
        PlatformConfiguration config = configProvider.GetCurrent();

        var serverTools = new List<(string ServerId, IReadOnlyList<AIFunction>? Tools)>();
        foreach (IGrouping<string, AgentToolSurfaceEntry> group in entries
            .Where(static entry => string.Equals(entry.Source, AgentToolSurfaceSources.Mcp, StringComparison.Ordinal))
            .GroupBy(static entry => entry.SourceId, StringComparer.Ordinal))
        {
            if (!bindings.TryGetValue(group.Key, out ResolvedAgentConnectorBinding? binding) || binding is null)
            {
                continue;
            }

            McpServerDefinition? server = config.McpServers.FirstOrDefault(candidate =>
                string.Equals(candidate.Id, group.Key, StringComparison.Ordinal));
            if (server is null || !IsConnectorEnabled(server, context.UserId)
                || !GatewayOwnershipAccess.CallerCanUse(context.UserId, server.Scope, server.OwnerUserId))
            {
                continue;
            }

            var tools = new List<AIFunction>();
            foreach (AgentToolSurfaceEntry entry in group)
            {
                tools.Add(CreateBoundProxy(
                    group.Key,
                    binding,
                    entry.Name,
                    entry.Description,
                    McpToolInvocationCodec.ParseToolJsonSchema(entry.JsonSchema),
                    entry.ArtifactDigest,
                    entry.ImageDigest,
                    entry.ExecutionConfigHash,
                    entry.EffectiveEndpointHash,
                    context));
            }

            serverTools.Add((group.Key, tools));
        }

        return [.. ApplyApprovalPolicy(
            serverTools,
            context.AgentDefinition.ApprovalPolicy,
            toolExecutionPolicyService)];
    }

    private McpProxyAIFunction CreateBoundProxy(
        string connectorId,
        ResolvedAgentConnectorBinding binding,
        string toolName,
        string description,
        JsonElement jsonSchema,
        string? artifactDigest,
        string? imageDigest,
        string? executionConfigHash,
        string? effectiveEndpointHash,
        AgentToolContext context)
    {
        return new McpProxyAIFunction(
            toolName,
            description,
            jsonSchema,
            async (arguments, cancellationToken) =>
            {
                PlatformConfiguration config = configProvider.GetCurrent();
                if (gatewayState is not null && !string.IsNullOrEmpty(context.UserId)
                    && !gatewayState.Users.Values.Any(user => user.IsActive && string.Equals(user.Id, context.UserId, StringComparison.Ordinal)))
                    throw new InvalidOperationException("Caller is no longer active.");
                McpServerDefinition serverDef = config.McpServers.FirstOrDefault(candidate =>
                    string.Equals(candidate.Id, connectorId, StringComparison.Ordinal))
                    ?? throw new InvalidOperationException($"MCP connector '{connectorId}' is no longer registered.");
                if (!IsConnectorEnabled(serverDef, context.UserId)
                    || !GatewayOwnershipAccess.CallerCanUse(context.UserId, serverDef.Scope, serverDef.OwnerUserId))
                    throw new InvalidOperationException($"MCP connector '{connectorId}' is not available to this caller.");

                AgentDefinition? currentAgent = config.Agents.FirstOrDefault(candidate => string.Equals(candidate.Id, context.AgentId, StringComparison.Ordinal));
                if (currentAgent is null || !currentAgent.Enabled || !GatewayOwnershipAccess.CallerCanUse(context.UserId, currentAgent.Scope, currentAgent.OwnerUserId))
                    throw new InvalidOperationException("Agent is no longer available to this caller.");
                ResolvedAgentConnectorBinding? currentBinding = ResolveAgentConnectorBindings(currentAgent, toolExecutionPolicyService)
                    .FirstOrDefault(candidate => string.Equals(candidate.ConnectorId, connectorId, StringComparison.Ordinal));
                if (currentBinding is null || currentBinding.ExecutionContext != binding.ExecutionContext)
                    throw new InvalidOperationException($"MCP connector '{connectorId}' is no longer permitted for this agent.");
                ToolApprovalDirective originalApproval = toolExecutionPolicyService.EvaluateApproval(new ToolApprovalRequest(
                    new ToolIdentity(ToolPolicyCategory.Connector, toolName, connectorId), context.AgentDefinition.ApprovalPolicy));
                ToolApprovalDirective currentApproval = toolExecutionPolicyService.EvaluateApproval(new ToolApprovalRequest(
                    new ToolIdentity(ToolPolicyCategory.Connector, toolName, connectorId), currentAgent.ApprovalPolicy));
                if (!currentApproval.Include || (currentApproval.RequiresApproval && !originalApproval.RequiresApproval))
                    throw new InvalidOperationException($"MCP tool '{toolName}' permissions changed. Refresh the tool catalogue.");

                if (serverDef.Transport == McpTransport.Http && serverDef.RequiresCredentialReentry)
                {
                    bool canManage = string.Equals(serverDef.Scope, GatewayOwnershipScopes.System, StringComparison.OrdinalIgnoreCase)
                        ? IsAdminUser(context.UserId ?? string.Empty)
                        : !string.IsNullOrEmpty(context.UserId) && string.Equals(context.UserId, serverDef.OwnerUserId, StringComparison.Ordinal);
                    throw new InvalidOperationException(canManage
                        ? $"Remote connector '{serverDef.Name}' needs its authentication reviewed in connector settings before it can be used."
                        : $"Remote connector '{serverDef.Name}' needs its authentication updated. Ask the connector owner or an administrator.");
                }

                if (string.IsNullOrWhiteSpace(executionConfigHash)
                    || !string.Equals(executionConfigHash, AgentToolingFingerprint.ConnectorExecutionHash(serverDef), StringComparison.Ordinal))
                    throw new InvalidOperationException($"Connector '{serverDef.Name}' changed since this run started. Test the connection and start a new run.");
                if (serverDef.Transport == McpTransport.Container
                    && (string.IsNullOrWhiteSpace(imageDigest)
                        || !string.Equals(imageDigest, serverDef.LocalImageDigest, StringComparison.OrdinalIgnoreCase)
                        || !string.Equals(artifactDigest, serverDef.LocalArtifactDigest, StringComparison.OrdinalIgnoreCase)))
                    throw new InvalidOperationException($"Connector '{serverDef.Name}' changed since this run started. Test the connection and start a new run.");

                AgentToolInvocationContextState? invocationContext = AgentToolInvocationContext.Current;
                McpAgentToolProviderLog.ConnectorToolInvoking(
                    logger,
                    context.AgentId,
                    context.SessionId,
                    context.UserId,
                    connectorId,
                    toolName,
                    invocationContext?.CallerSessionId,
                    context.WorkspaceBinding.ScopeType,
                    context.WorkspaceBinding.ScopeId);

                if (serverDef.Transport == McpTransport.Http)
                {
                    try
                    {
                        if (string.IsNullOrWhiteSpace(effectiveEndpointHash))
                            throw new InvalidOperationException("The connector endpoint was not pinned. Start a new run.");
                        return await InvokeHttpAsync(serverDef, toolName, arguments, context, effectiveEndpointHash, executionConfigHash!, cancellationToken).ConfigureAwait(false);
                    }
                    catch (RemoteMcpConfigurationException ex)
                    {
                        throw new InvalidOperationException(ex.Message);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        throw new InvalidOperationException(RemoteMcpFailureMessages.Describe(ex) + " If the connector's tools have changed, start a new run.");
                    }
                }

                if (serverDef.Transport == McpTransport.Container)
                {
                    return await InvokeContainerAsync(serverDef, binding, toolName, arguments, context, imageDigest!, cancellationToken).ConfigureAwait(false);
                }

                throw new NotSupportedException($"MCP transport '{serverDef.Transport}' is not supported. Only Container and Http transports are supported.");
            });
    }

    private async ValueTask<object?> InvokeHttpAsync(
        McpServerDefinition serverDef,
        string toolName,
        AIFunctionArguments arguments,
        AgentToolContext context,
        string effectiveEndpointHash,
        string executionConfigHash,
        CancellationToken cancellationToken)
    {
        McpClient client = await CreateHttpClientAsync(
            serverDef,
            context.UserId,
            context,
            cancellationToken,
            effectiveEndpointHash,
            expectedExecutionHash: executionConfigHash).ConfigureAwait(false);
        await using (client.ConfigureAwait(false))
        {
            Dictionary<string, object?> toolInput = McpToolInvocationCodec.ParseInput(
                McpToolInvocationCodec.SerializeToolArguments(arguments));
            CallToolResult result = await client.CallToolAsync(toolName, toolInput, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            IReadOnlyList<string> textBlocks = [.. result.Content.OfType<TextContentBlock>().Select(static block => block.Text)];
            return McpToolInvocationCodec.ToInvokeResult(result.StructuredContent, result.IsError, textBlocks);
        }
    }

    private async ValueTask<object?> InvokeContainerAsync(
        McpServerDefinition serverDef,
        ResolvedAgentConnectorBinding binding,
        string toolName,
        AIFunctionArguments arguments,
        AgentToolContext context,
        string expectedImageDigest,
        CancellationToken cancellationToken)
    {
        ToolPolicyDecision executionDecision = toolExecutionPolicyService.EvaluateConnectorExposure(
            context.AgentDefinition,
            binding.ExecutionContext);
        if (!executionDecision.Allowed)
        {
            throw new InvalidOperationException(
                $"Connector execution denied by policy ({executionDecision.ReasonCode}): {executionDecision.Message}");
        }

        if (binding.ExecutionContext == WorkspaceMcpExecutionContext.AgentSandbox && serverDef.RequiresIsolation(connectorOAuth?.Catalog))
            throw new InvalidOperationException(serverDef.IsolationMessage);

        Dictionary<string, string> env = await ResolveEnvAsync(serverDef, context.UserId ?? string.Empty, cancellationToken)
            .ConfigureAwait(false);
        ValidateRequiredEnv(serverDef, env);
        env = await PrepareConnectorAuthEnvironmentAsync(serverDef, context.UserId, env, exchangeAccessToken: true, cancellationToken).ConfigureAwait(false);

        bool allowInternalSystemConnector = serverDef.IsInternalSystemConnector;
        string scopedConnectorId = WorkspaceMcpConnectorIdentity.BuildScopedConnectorId(context.UserId, serverDef.Id);
        WorkspaceContextMessage? workspaceContext = ResolveWorkspaceContext(context);
        var callRequest = new WorkspaceMcpContainerCallRequest
        {
            Runtime = new ManagerRuntimeSettingsMessage(),
            ConnectorId = scopedConnectorId,
            ToolName = toolName,
            ToolInputJson = McpToolInvocationCodec.SerializeToolArguments(arguments),
            TimeoutSeconds = serverDef.ReadinessTimeoutSeconds ?? 30,
            AllowInternalSystemConnector = allowInternalSystemConnector,
            ExecutionContext = binding.ExecutionContext,
            HostArtifactDirectory = ResolveHostArtifactDirectory(context) ?? string.Empty,
            ExpectedImageDigest = expectedImageDigest,
            ExpectedSourceDigest = serverDef.LocalArtifactDigest ?? string.Empty,
            VerifyExecutionCommand = true,
            ExpectedCommand = serverDef.ContainerCommand ?? string.Empty,
            ExpectedMcpProtocolVersion = serverDef.McpProtocolVersion ?? string.Empty,
            ExpectedServerType = serverDef.ServerType ?? string.Empty,
        };
        callRequest.ExpectedArguments.AddRange(serverDef.ContainerArgs);
        if (workspaceContext is not null)
        {
            callRequest.WorkspaceContext = workspaceContext;
        }

        callRequest.Environment.AddRange(env.Select(static pair => new WorkspaceKvPair { Key = pair.Key, Value = pair.Value }));

        WorkspaceMcpContainerCallResponse callResponse = await workspaceGrpcService
            .CallMcpContainerToolAsync(callRequest, cancellationToken)
            .ConfigureAwait(false);
        if (!callResponse.Ok)
        {
            throw new InvalidOperationException($"Container MCP tool '{toolName}' failed: {callResponse.Message}");
        }

        string resultJson = string.IsNullOrWhiteSpace(callResponse.ResultJson) ? "{}" : callResponse.ResultJson;
        if (documentIngestion is not null)
        {
            resultJson = await ViewAttachmentReader.ApplyAsync(
                toolName,
                resultJson,
                ResolveHostArtifactDirectory(context),
                context.SessionId,
                documentIngestion,
                gatewayState?.SystemSettings.DocumentIngestionEnabled ?? true,
                cancellationToken,
                mailImages,
                FunctionInvokingChatClient.CurrentContext?.CallContent.CallId).ConfigureAwait(false);
        }

        try
        {
            return JsonNode.Parse(resultJson);
        }
        catch (JsonException)
        {
            return new JsonObject { ["result"] = resultJson };
        }
    }
}
