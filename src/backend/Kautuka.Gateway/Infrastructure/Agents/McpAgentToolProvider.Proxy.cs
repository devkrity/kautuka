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
            .Where(static pair => pair.Definition is not null && (pair.Definition.Enabled || pair.Definition.IsInternalSystemConnector))
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

        Task<(string Id, IReadOnlyList<AgentToolSurfaceEntry>? Entries)>[] tasks = activeDefs
            .Select(binding => ConnectSafeAsync(binding.Definition, binding.Binding, context, ct))
            .ToArray();
        (string Id, IReadOnlyList<AgentToolSurfaceEntry>? Entries)[] results = await Task.WhenAll(tasks).ConfigureAwait(false);

        var entries = new List<AgentToolSurfaceEntry>();
        bool complete = true;
        foreach ((_, IReadOnlyList<AgentToolSurfaceEntry>? listed) in results)
        {
            if (listed is null)
            {
                complete = false;
                continue;
            }

            entries.AddRange(listed);
        }

        McpAgentToolProviderLog.TotalToolsAvailable(logger, context.AgentId, entries.Count);
        return new AgentToolSurfaceContribution(entries, complete);
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
            if (server is null
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
        AgentToolContext context)
    {
        return new McpProxyAIFunction(
            toolName,
            description,
            jsonSchema,
            async (arguments, cancellationToken) =>
            {
                PlatformConfiguration config = configProvider.GetCurrent();
                McpServerDefinition serverDef = config.McpServers.FirstOrDefault(candidate =>
                    string.Equals(candidate.Id, connectorId, StringComparison.Ordinal))
                    ?? throw new InvalidOperationException($"MCP connector '{connectorId}' is no longer registered.");
                if (!GatewayOwnershipAccess.CallerCanUse(context.UserId, serverDef.Scope, serverDef.OwnerUserId))
                {
                    throw new InvalidOperationException($"MCP connector '{connectorId}' is not available to this caller.");
                }

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
                    return await InvokeHttpAsync(serverDef, toolName, arguments, context, cancellationToken).ConfigureAwait(false);
                }

                if (serverDef.Transport == McpTransport.Container)
                {
                    return await InvokeContainerAsync(serverDef, binding, toolName, arguments, context, cancellationToken).ConfigureAwait(false);
                }

                throw new NotSupportedException($"MCP transport '{serverDef.Transport}' is not supported. Only Container and Http transports are supported.");
            });
    }

    private async ValueTask<object?> InvokeHttpAsync(
        McpServerDefinition serverDef,
        string toolName,
        AIFunctionArguments arguments,
        AgentToolContext context,
        CancellationToken cancellationToken)
    {
        McpServerDefinition storedDefinition = serverDef;
        serverDef = await ResolveHttpUrlOverrideAsync(serverDef, context.UserId, cancellationToken).ConfigureAwait(false);
        Dictionary<string, string> env = await ResolveEnvAsync(
            serverDef,
            context.UserId ?? string.Empty,
            includeSystemScope: IncludeSystemCredentials(
                storedDefinition,
                IsPersonalHttpDestination(storedDefinition, serverDef)),
            cancellationToken).ConfigureAwait(false);
        ValidateRequiredEnv(serverDef, env);
        env = await PrepareConnectorAuthEnvironmentAsync(serverDef, context.UserId, env, exchangeAccessToken: true, cancellationToken).ConfigureAwait(false);
        McpClient client = await CreateHttpClientAsync(
            serverDef,
            env,
            context.UserId,
            IsPersonalHttpDestination(storedDefinition, serverDef),
            cancellationToken).ConfigureAwait(false);
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

        Dictionary<string, string> env = await ResolveEnvAsync(serverDef, context.UserId ?? string.Empty, cancellationToken)
            .ConfigureAwait(false);
        ValidateRequiredEnv(serverDef, env);
        env = await PrepareConnectorAuthEnvironmentAsync(serverDef, context.UserId, env, exchangeAccessToken: true, cancellationToken).ConfigureAwait(false);

        bool allowInternalSystemConnector = serverDef.IsInternalSystemConnector;
        string scopedConnectorId = WorkspaceMcpConnectorIdentity.BuildScopedConnectorId(context.UserId, serverDef.Id);
        WorkspaceContextMessage? workspaceContext = ResolveWorkspaceContext(binding, context);
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
        };
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
