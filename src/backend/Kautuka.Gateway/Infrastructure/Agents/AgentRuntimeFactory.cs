using System.Text.Json;
using System.Text.Json.Nodes;

using Kautuka.Gateway.Domain.Assets;
using Kautuka.Gateway.Domain.Execution;
using Kautuka.Gateway.Domain.Runtime;
using Kautuka.Gateway.Domain.Sandbox;
using Kautuka.Gateway.Domain.Skills;

using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace Kautuka.Gateway.Infrastructure.Agents;

internal interface IAgentRuntimeFactory
{
    ValueTask<AgentRuntimeResult> BuildAsync(
        string agentId,
        string sessionId,
        string? userId,
        string? stageTransitionId,
        IReadOnlyList<InjectedMemoryFact> memoryFacts,
        bool isContinuationResume,
        bool requireApprovalForAllTools,
        string? sessionStateOverrideJson,
        string? effectiveBehaviorOverrideJson,
        CancellationToken ct);

    ValueTask SaveSessionAsync(AgentRuntime runtime, CancellationToken ct);
}

internal sealed class AgentRuntimeFactory(
    IPlatformConfigurationProvider configProvider,
    KautukaAiClientFactory aiClientFactory,
    IWorkspaceRuntimeTool workspaceTool,
    IAgentToolCatalog toolCatalog,
    IInjectedMemoryContextBuilder memoryContextBuilder,
    IChatClientMemoryDecorator chatClientMemoryDecorator,
    IEffectiveSkillManifestResolver effectiveSkillManifestResolver,
    IProviderPathTelemetryChatClientDecorator providerPathTelemetryChatClientDecorator,
    IPersonaSettingsReader personaSettingsReader,
    ISamplingPolicyResolver samplingPolicyResolver,
    MafSessionStore mafSessionStore,
    IModelCapabilityService capabilityService,
    IToolInvocationSettingsResolver toolInvocationSettingsResolver,
    IServiceProvider serviceProvider,
    ILogger<AgentRuntimeFactory> logger,
    MailImageHandoff? mailImages = null) : IAgentRuntimeFactory
{
    /// <summary>
    /// Increment this when capability probe logic changes in a way that
    /// makes prior cached probes invalid.
    /// </summary>
    private const int probeSchemaVersion = 1;
    private const string providerPathClientType = "IChatClient";
    private static readonly Action<ILogger, string, string, string, Exception?> logOtelWrappersConfigured =
        LoggerMessage.Define<string, string, string>(
            LogLevel.Information,
            new EventId(6201, nameof(AgentRuntimeFactory)),
            "Agent runtime OpenTelemetry wrappers configured. AgentId={AgentId}, ProviderId={ProviderId}, Model={Model}");
    private static readonly Action<ILogger, string, bool, bool, int, Exception?> logToolInvocationPolicyApplied =
        LoggerMessage.Define<string, bool, bool, int>(
            LogLevel.Information,
            new EventId(6202, nameof(AgentRuntimeFactory)),
            "Agent runtime tool invocation policy applied. AgentId={AgentId}, AllowMultipleToolCalls={AllowMultipleToolCalls}, AllowConcurrentInvocation={AllowConcurrentInvocation}, MaxParallelTools={MaxParallelTools}");

    public async ValueTask<AgentRuntimeResult> BuildAsync(
        string agentId,
        string sessionId,
        string? userId,
        string? stageTransitionId,
        IReadOnlyList<InjectedMemoryFact> memoryFacts,
        bool isContinuationResume,
        bool requireApprovalForAllTools,
        string? sessionStateOverrideJson,
        string? effectiveBehaviorOverrideJson,
        CancellationToken ct)
    {
        PlatformConfiguration config = configProvider.GetCurrent();
        GenAiTelemetryEnvironment.ApplyCaptureMessageContent(config.System.Observability.CaptureMessageContent);

        AgentDefinition? agentDef = config.Agents.FirstOrDefault(a =>
            string.Equals(a.Id, agentId, StringComparison.Ordinal));
        if (agentDef is null)
            return AgentRuntimeResult.Failure(AgentRuntimeError.AgentNotFound,
                $"Agent '{agentId}' not found in configuration.");

        EffectiveRunBehavior? resumedBehavior = DeserializeEffectiveBehavior(effectiveBehaviorOverrideJson);

        string resolvedModelId = resumedBehavior?.ModelId ?? agentDef.ModelId;
        ModelDefinition? modelDef = config.Models.FirstOrDefault(m =>
            string.Equals(m.Id, resolvedModelId, StringComparison.OrdinalIgnoreCase));
        if (modelDef is null)
            return AgentRuntimeResult.Failure(AgentRuntimeError.ModelNotFound,
                $"Model '{resolvedModelId}' for agent '{agentId}' not found.");

        string resolvedProviderId = resumedBehavior?.ProviderId ?? modelDef.ProviderId;
        ProviderDefinition? providerDef = config.Providers.FirstOrDefault(p =>
            string.Equals(p.Id, resolvedProviderId, StringComparison.OrdinalIgnoreCase));
        if (providerDef is null)
            return AgentRuntimeResult.Failure(AgentRuntimeError.ProviderNotFound,
                $"Provider '{resolvedProviderId}' for model '{resolvedModelId}' not found.");

        IChatClient? rawChatClient = await aiClientFactory.TryCreateAsync(providerDef, modelDef.ModelName, ct).ConfigureAwait(false);
        if (rawChatClient is null)
            return AgentRuntimeResult.Failure(AgentRuntimeError.ClientCreationFailed,
                $"Could not create chat client for provider '{providerDef.Id}'. Check API key and provider settings.");

        rawChatClient = rawChatClient.AsBuilder()
            .UseOpenTelemetry(sourceName: "Experimental.Microsoft.Extensions.AI")
            .Build();

        if (logger.IsEnabled(LogLevel.Information))
        {
            logOtelWrappersConfigured(logger, agentId, providerDef.Id, modelDef.ModelName, null);
        }

        var capKey = new ModelCapabilityKey(
            ProviderId: providerDef.Id,
            BaseUrl: KautukaAiClientFactory.GetBaseUrl(providerDef),
            ModelId: modelDef.ModelName,
            ModelDigest: null,
            ProbeSchemaVersion: probeSchemaVersion);

        ModelCapabilityProfile capabilityProfile =
            await capabilityService.GetOrProbeAsync(capKey, rawChatClient, ct).ConfigureAwait(false);
        // ── End capability pipeline ──────────────────────────────────────────

        // ── Compatibility telemetry middleware ───────────────────────────────
        var pathKey = new ProviderPathKey(
            ProviderType: providerDef.Type.ToUpperInvariant(),
            EndpointKey: KautukaAiClientFactory.GetBaseUrl(providerDef).AbsoluteUri,
            ClientType: providerPathClientType,
            ModelKey: modelDef.ModelName);

        IChatClient chatClient = providerPathTelemetryChatClientDecorator.Decorate(rawChatClient, pathKey);
        try
        {
            // ── End compatibility middleware ──────────────────────────────────────
            IReadOnlyList<EffectiveSkillManifestEntry> effectiveSkillManifest = effectiveSkillManifestResolver.Resolve(config, agentDef, userId);
            JsonObject personaProfile = personaSettingsReader.ResolveProfile(agentId);
            EffectivePersonaProfile persona = BuildPersonaProfile(personaProfile);
            EffectiveRuntimeGrounding runtimeGrounding = resumedBehavior?.RuntimeGrounding
                ?? ChatExecutionHelpers.BuildRuntimeGrounding(config.System.Locale, config.System.TimeZone);
            string serializedModelVisibleContext = resumedBehavior?.SerializedModelVisibleContext
                ?? ChatExecutionHelpers.SerializeModelVisibleContext(new EffectiveModelVisibleContext(persona, runtimeGrounding));
            string memoryContext = resumedBehavior?.MemoryContext ?? memoryContextBuilder.Build(memoryFacts);
            string skillAdvertisingContext = resumedBehavior?.SkillAdvertisingContext ?? ChatExecutionHelpers.BuildSkillAdvertisingContext(effectiveSkillManifest);
            string scopeContractContext = resumedBehavior?.ScopeContractContext ?? ChatExecutionHelpers.BuildScopeContractContext(ReadPersonaCategory(personaProfile), agentDef.Name);
            ResolvedToolInvocationSettings invocation = resumedBehavior?.Tooling is { } persistedTooling
                ? ToolInvocationSettings.FromSnapshot(
                    persistedTooling.AllowMultipleToolCalls,
                    persistedTooling.AllowConcurrentInvocation,
                    persistedTooling.MaxParallelTools)
                : toolInvocationSettingsResolver.Resolve(agentDef.Runtime);
            string toolCallGuidance = GeminiParallelToolCallGuidance.Build(
                providerDef.Type,
                invocation.AllowMultipleToolCalls);
            string combinedContext = resumedBehavior?.CombinedSystemContext
                ?? GeminiParallelToolCallGuidance.Append(
                    ChatExecutionHelpers.CombineSystemContext(
                        agentDef.SystemPrompt,
                        memoryContext,
                        serializedModelVisibleContext,
                        skillAdvertisingContext,
                        scopeContractContext),
                    toolCallGuidance);
            EffectiveSamplingPolicy samplingPolicy = resumedBehavior?.SamplingPolicy ?? samplingPolicyResolver.Resolve(ReadPersonaTemperature(personaProfile));
            if (!string.IsNullOrEmpty(combinedContext))
                chatClient = chatClientMemoryDecorator.Decorate(chatClient, combinedContext);

            AgentToolContext toolContext = WorkspaceTooling.BuildAgentToolContext(
                workspaceTool, agentDef, agentId, sessionId, userId, stageTransitionId, effectiveSkillManifest: effectiveSkillManifest);
            IReadOnlyList<AIFunction> tools = await toolCatalog.BuildToolsAsync(toolContext, ct).ConfigureAwait(false);
            tools = ApplyPersistedToolSurface(tools, resumedBehavior?.Tooling);
            if (requireApprovalForAllTools)
            {
                tools = tools.Select(tool => tool is ApprovalRequiredAIFunction
                    ? tool
                    : new ApprovalRequiredAIFunction(tool)).ToArray();
            }
            tools = ConcurrencyLimitedAIFunction.Apply(tools, invocation.ConcurrencyLimit);
            if (logger.IsEnabled(LogLevel.Information))
            {
                logToolInvocationPolicyApplied(
                    logger,
                    agentId,
                    invocation.AllowMultipleToolCalls,
                    invocation.AllowConcurrentInvocation,
                    invocation.MaxParallelTools,
                    null);
            }

            var chatOptions = new ChatOptions
            {
                Temperature = samplingPolicy.Temperature,
                TopP = samplingPolicy.TopP,
            };
            if (tools.Count > 0)
            {
                chatOptions.Tools = [.. tools];
                chatOptions.ToolMode = ChatToolMode.Auto;
                chatOptions.AllowMultipleToolCalls = invocation.AllowMultipleToolCalls;
            }

            EffectiveRunBehavior effectiveBehavior = resumedBehavior ?? BuildEffectiveBehavior(
                agentDef,
                providerDef,
                modelDef,
                persona,
                runtimeGrounding,
                samplingPolicy,
                agentDef.SystemPrompt,
                skillAdvertisingContext,
                serializedModelVisibleContext,
                scopeContractContext,
                memoryContext,
                combinedContext,
                memoryFacts.Count,
                tools,
                invocation,
                toolCallGuidance);

            var agentOptions = new ChatClientAgentOptions
            {
                Id = agentId,
                Name = agentDef.Name,
                // Same-session conversational context is always retained.
                ChatHistoryProvider = new InMemoryChatHistoryProvider(new InMemoryChatHistoryProviderOptions()),
                ChatOptions = chatOptions,
                AllowConcurrentInvocation = invocation.AllowConcurrentInvocation,
                // FICC is all-or-nothing: if any tool in a response is ApprovalRequiredAIFunction,
                // Disable…=true converts ALL FunctionCallContent to ToolApprovalRequestContent.
                // Bypass (false) lets unwrapped MCP/connectors run before HITL cards exist, so
                // interrupt.requested waits on the fetch. Keep the pack on the interrupt path.
                DisableApprovalNotRequiredFunctionBypassing = true,
            };

#pragma warning disable CA2000
            if (mailImages is not null)
            {
                chatClient = new MailImageChatClient(chatClient, mailImages);
            }

            chatClient = new ModelTurnProgressChatClient(chatClient);
#pragma warning restore CA2000

            AIAgent agent = chatClient
                .AsAIAgent(agentOptions)
                .AsBuilder()
                .UseOpenTelemetry(sourceName: "Experimental.Microsoft.Agents.AI")
                .Build(serviceProvider);

            AgentSession agentSession;
            if (!string.IsNullOrEmpty(sessionId))
            {
                if (!string.IsNullOrWhiteSpace(sessionStateOverrideJson))
                {
                    JsonElement overrideState = ParseSessionStateOverride(sessionStateOverrideJson, isContinuationResume);
                    agentSession = await agent.DeserializeSessionAsync(overrideState, cancellationToken: ct).ConfigureAwait(false);
                }
                else
                {
                    // If this is a new user message (not a continuation resume), start fresh to avoid
                    // carrying over orphaned tool calls from previous failed executions.
                    // When resuming interrupted runs, we must reuse the session with its full history.
                    if (isContinuationResume)
                    {
                        // This fallback only applies when we do not have an explicit checkpoint
                        // snapshot for the approval boundary. In that case we still need to strip
                        // stale approval content from the persisted maf session before deserializing.
                        JsonElement? stored = await mafSessionStore.LoadCleanedForApprovalResumeAsync(sessionId, ct).ConfigureAwait(false);
                        agentSession = stored.HasValue
                            ? await agent.DeserializeSessionAsync(stored.Value, cancellationToken: ct).ConfigureAwait(false)
                            : await agent.CreateSessionAsync(ct).ConfigureAwait(false);
                    }
                    else
                    {
                        JsonElement? stored = await mafSessionStore.LoadAsync(sessionId, ct).ConfigureAwait(false);
                        agentSession = stored.HasValue
                            ? await agent.DeserializeSessionAsync(stored.Value, cancellationToken: ct).ConfigureAwait(false)
                            : await agent.CreateSessionAsync(ct).ConfigureAwait(false);
                    }
                }
            }
            else
            {
                agentSession = await agent.CreateSessionAsync(ct).ConfigureAwait(false);
            }

            KautukaAgentSession.BindWorkspace(
                agentSession,
                toolContext.WorkspaceBinding.WorkspaceId,
                toolContext.WorkspaceBinding.ScopeType,
                toolContext.WorkspaceBinding.ScopeId,
                toolContext.WorkspaceBinding.UserId,
                toolContext.WorkspaceBinding.StageTransitionId);

            return AgentRuntimeResult.Success(
                new AgentRuntime(
                    agent,
                    agentSession,
                    sessionId,
                    agentId,
                    agentDef.Name,
                    capabilityProfile,
                    pathKey,
                    chatClient,
                    agentDef.SuperExecMode,
                    agentDef.Sandbox.Enabled,
                    effectiveBehavior,
                    requireApprovalForAllTools));
        }
        catch
        {
            if (chatClient is IDisposable decoratedChatClient)
            {
                decoratedChatClient.Dispose();
            }

            throw;
        }
    }

    public async ValueTask SaveSessionAsync(AgentRuntime runtime, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(runtime);

        if (string.IsNullOrEmpty(runtime.SessionId)) return;
        MafSessionJson.StripAgentRequestMessageAttributions(runtime.Agent, runtime.Session);
        JsonElement serial = await runtime.Agent.SerializeSessionAsync(runtime.Session, cancellationToken: ct).ConfigureAwait(false);
        await mafSessionStore.SaveAsync(runtime.SessionId, runtime.AgentId, "agent", serial, ct).ConfigureAwait(false);
    }

    internal static JsonElement ParseSessionStateOverride(string sessionStateOverrideJson, bool isContinuationResume)
    {
        JsonElement overrideState = JsonDocument.Parse(sessionStateOverrideJson).RootElement.Clone();
        return isContinuationResume
            ? MafSessionStore.StripApprovalContentItems(overrideState)
            : overrideState;
    }

    private static EffectiveRunBehavior? DeserializeEffectiveBehavior(string? effectiveBehaviorOverrideJson)
    {
        if (string.IsNullOrWhiteSpace(effectiveBehaviorOverrideJson))
        {
            return null;
        }

        return JsonSerializer.Deserialize(effectiveBehaviorOverrideJson, KautukaJsonContext.Default.EffectiveRunBehavior);
    }

    internal static IReadOnlyList<AIFunction> ApplyPersistedToolSurface(
        IReadOnlyList<AIFunction> tools,
        EffectiveToolingSnapshot? persistedTooling)
    {
        if (persistedTooling is null)
        {
            return tools;
        }

        var allowedToolNames = new HashSet<string>(persistedTooling.ToolNames, StringComparer.OrdinalIgnoreCase);
        return [.. tools.Where(tool => allowedToolNames.Contains(tool.Name))];
    }

    private static EffectiveRunBehavior BuildEffectiveBehavior(
        AgentDefinition agentDef,
        ProviderDefinition providerDef,
        ModelDefinition modelDef,
        EffectivePersonaProfile persona,
        EffectiveRuntimeGrounding runtimeGrounding,
        EffectiveSamplingPolicy samplingPolicy,
        string? systemPrompt,
        string skillAdvertisingContext,
        string serializedModelVisibleContext,
        string scopeContractContext,
        string memoryContext,
        string combinedContext,
        int memoryFactCount,
        IReadOnlyList<AIFunction> tools,
        ResolvedToolInvocationSettings invocation,
        string toolCallGuidance)
    {
        string[] toolNames = [.. tools.Select(static tool => tool.Name).OrderBy(static name => name, StringComparer.OrdinalIgnoreCase)];

        return new EffectiveRunBehavior(
            agentDef.Id,
            agentDef.Name,
            providerDef.Id,
            providerDef.Type,
            KautukaAiClientFactory.GetBaseUrl(providerDef).AbsoluteUri,
            modelDef.Id,
            modelDef.ModelName,
            persona,
            runtimeGrounding,
            samplingPolicy,
            [
                new EffectiveSystemContextSection("systemPrompt", systemPrompt ?? string.Empty),
                new EffectiveSystemContextSection("skillAdvertising", skillAdvertisingContext),
                new EffectiveSystemContextSection("modelVisibleContext", serializedModelVisibleContext),
                new EffectiveSystemContextSection("scopeContract", scopeContractContext),
                new EffectiveSystemContextSection("memory", memoryContext),
                new EffectiveSystemContextSection("toolCallGuidance", toolCallGuidance),
            ],
            combinedContext,
            serializedModelVisibleContext,
            skillAdvertisingContext,
            scopeContractContext,
            memoryContext,
            memoryFactCount,
            new EffectiveToolingSnapshot(
                toolNames,
                toolNames.Length > 0 ? "Auto" : "None",
                invocation.AllowMultipleToolCalls,
                invocation.AllowConcurrentInvocation,
                invocation.MaxParallelTools));
    }

    private static EffectivePersonaProfile BuildPersonaProfile(JsonObject personaProfile)
        => new(
            ReadPersonaString(personaProfile, "style", "professional"),
            ReadPersonaString(personaProfile, "tone", "neutral"),
            ReadPersonaString(personaProfile, "verbosity", "concise"));

    private static string ReadPersonaString(JsonObject personaProfile, string propertyName, string fallback)
    {
        if (personaProfile[propertyName] is JsonValue value)
        {
            try
            {
                string? parsed = value.GetValue<string>();
                if (!string.IsNullOrWhiteSpace(parsed))
                {
                    return parsed;
                }
            }
            catch (InvalidOperationException)
            {
            }
            catch (FormatException)
            {
            }
        }

        return fallback;
    }

    private static float? ReadPersonaTemperature(JsonObject personaProfile)
    {
        if (personaProfile["temperature"] is JsonValue value)
        {
            try
            {
                return value.GetValue<float>();
            }
            catch (InvalidOperationException)
            {
            }
            catch (FormatException)
            {
            }
        }

        return ReadPersonaCategory(personaProfile)?.Trim().ToUpperInvariant() switch
        {
            "PROCESS_AGENT" => 0.3f,
            "OFFICE_WORKER" => 0.5f,
            "GENERAL" => 0.7f,
            _ => null,
        };
    }

    private static string ReadPersonaCategory(JsonObject personaProfile)
    {
        string? category = personaProfile["category"]?.GetValue<string>();
        return string.IsNullOrWhiteSpace(category) ? "general" : category;
    }
}
