using System.Text.Json;

using Kautuka.Gateway.Domain.Stores;

using Microsoft.Extensions.AI;

namespace Kautuka.Gateway.Domain.ModelCapability;

internal sealed partial class ModelProbeService : IModelProbeService
{
    private const int attempts = 3;
    private static readonly TimeSpan attemptDelay = TimeSpan.FromMilliseconds(500);

    private readonly IGatewayCatalogStore catalog;
    private readonly KautukaAiClientFactory aiClientFactory;
    private readonly ILogger<ModelProbeService> logger;

    public ModelProbeService(
        IGatewayCatalogStore catalog,
        KautukaAiClientFactory aiClientFactory,
        ILogger<ModelProbeService> logger)
    {
        this.catalog = catalog;
        this.aiClientFactory = aiClientFactory;
        this.logger = logger;
    }

    public async Task<GatewayModelCapabilities?> ProbeModelAsync(string modelId)
    {
        if (!catalog.TryGetModel(modelId, out GatewayModel? model) || model is null)
            return null;

        if (!catalog.TryGetProvider(model.ProviderId, out GatewayProvider? provider) || provider is null)
        {
            GatewayModelCapabilities unknown = CreateUnknownCapabilities("Provider not found");
            catalog.UpdateModelCapabilities(modelId, unknown);
            return unknown;
        }

        IChatClient? client = await aiClientFactory.TryCreateAsync(
            provider.Id, provider.Type, provider.Settings, model.ModelName).ConfigureAwait(false);
        if (client is null)
        {
            GatewayModelCapabilities unknown = CreateUnknownCapabilities("Unable to create chat client for probe");
            catalog.UpdateModelCapabilities(modelId, unknown);
            return unknown;
        }

        GatewayModelCapabilities capabilities = await ProbeCapabilitiesAsync(client).ConfigureAwait(false);
        catalog.UpdateModelCapabilities(modelId, capabilities);
        return capabilities;
    }

    public async Task<GatewayModelCapabilities> ProbeModelCandidateAsync(string providerId, string modelName)
    {
        if (!catalog.TryGetProvider(providerId, out GatewayProvider? provider) || provider is null)
        {
            return CreateUnknownCapabilities("Provider not found");
        }

        IChatClient? client = await aiClientFactory.TryCreateAsync(
            provider.Id, provider.Type, provider.Settings, modelName).ConfigureAwait(false);
        return client is null
            ? CreateUnknownCapabilities("Unable to create chat client for probe")
            : await ProbeCapabilitiesAsync(client).ConfigureAwait(false);
    }

    internal async Task<GatewayModelCapabilities> ProbeCapabilitiesAsync(IChatClient client)
    {
        int toolSuccesses = 0;
        int toolFailures = 0;
        int streamSuccesses = 0;
        int streamFailures = 0;
        int reasoningShown = 0;
        int reasoningOnlyCount = 0;
        int structuredSuccesses = 0;
        int structuredFailures = 0;
        string? probeError = null;

        for (int i = 0; i < attempts; i++)
        {
            if (i > 0)
                await Task.Delay(attemptDelay).ConfigureAwait(false);

            // Probes run in parallel within each round.
            var streamTask = ProbeStreamingAsync(client);
            var toolTask = ProbeToolCallingAsync(client);
            var jsonTask = ProbeStructuredJsonAsync(client);

            await Task.WhenAll(streamTask, toolTask, jsonTask).ConfigureAwait(false);

            var (streaming, reasoningOnly, streamError) = await streamTask.ConfigureAwait(false);
            probeError ??= streamError;
            if (streaming.HasValue)
            {
                if (streaming.Value)
                    streamSuccesses++;
                else
                    streamFailures++;
            }
            if (reasoningOnly.HasValue)
            {
                reasoningShown++;
                if (reasoningOnly.Value) reasoningOnlyCount++;
            }
            var (toolCalling, toolError) = await toolTask.ConfigureAwait(false);
            probeError ??= toolError;
            if (toolCalling.HasValue)
            {
                if (toolCalling.Value)
                    toolSuccesses++;
                else
                    toolFailures++;
            }

            var (structuredJson, structuredError) = await jsonTask.ConfigureAwait(false);
            probeError ??= structuredError;
            if (structuredJson.HasValue)
            {
                if (structuredJson.Value)
                    structuredSuccesses++;
                else
                    structuredFailures++;
            }
        }

        var capabilities = new GatewayModelCapabilities(
            ToolCalling: Vote(toolSuccesses, toolFailures),
            Streaming: Vote(streamSuccesses, streamFailures),
            ReasoningOnlyOutput: reasoningShown == 0 ? null : Vote(reasoningOnlyCount, reasoningShown - reasoningOnlyCount),
            StructuredJson: Vote(structuredSuccesses, structuredFailures),
            AgentConversations: Vote(streamSuccesses, streamFailures),
            ProbeError: probeError,
            ProbedAt: Timestamp());
        return ClearProbeErrorWhenResolved(capabilities);
    }

    public Task<IReadOnlyList<DiscoveredModelDefinition>> DiscoverModelsAsync(string providerId)
    {
        if (!catalog.TryGetProvider(providerId, out GatewayProvider? provider) || provider is null)
            return Task.FromResult<IReadOnlyList<DiscoveredModelDefinition>>([]);

        return aiClientFactory.DiscoverModelsAsync(providerId, provider.Type, provider.Settings);
    }

    /// <summary>
    /// Streams a minimal message to probe both streaming support and whether the model
    /// emits reasoning-only output (thinking tokens without any text content).
    /// </summary>
    private async Task<(bool? Streaming, bool? ReasoningOnly, string? ErrorDetail)> ProbeStreamingAsync(IChatClient client)
    {
        try
        {
            var messages = new List<ChatMessage> { new(ChatRole.User, "Reply with exactly: ok") };
            bool gotUpdate = false;
            bool gotReasoning = false;
            bool gotText = false;

            await foreach (ChatResponseUpdate update in client.GetStreamingResponseAsync(
                messages,
                new ChatOptions { MaxOutputTokens = 256, ResponseFormat = ChatResponseFormat.Text }).ConfigureAwait(false))
            {
                gotUpdate = true;
                foreach (AIContent content in update.Contents)
                {
                    if (content is TextReasoningContent)
                        gotReasoning = true;
                    else if (content is TextContent tc && !string.IsNullOrEmpty(tc.Text))
                        gotText = true;
                }
            }

            // null = no reasoning tokens seen (can't determine reasoning-only behaviour)
            bool? reasoningOnly = gotReasoning ? !gotText : null;
            return (gotUpdate, reasoningOnly, null);
        }
        catch (HttpRequestException ex)
        {
            ModelProbeServiceLog.StreamingProbeFailed(logger, ex);
            return (null, null, SummarizeProbeFailure(ex));
        }
        catch (InvalidOperationException ex)
        {
            ModelProbeServiceLog.StreamingProbeFailed(logger, ex);
            return (null, null, SummarizeProbeFailure(ex));
        }
        catch (JsonException ex)
        {
            ModelProbeServiceLog.StreamingProbeFailed(logger, ex);
            return (null, null, SummarizeProbeFailure(ex));
        }
        catch (IOException ex)
        {
            ModelProbeServiceLog.StreamingProbeFailed(logger, ex);
            return (null, null, SummarizeProbeFailure(ex));
        }
        catch (System.ClientModel.ClientResultException ex)
        {
            ModelProbeServiceLog.StreamingProbeFailed(logger, ex);
            return (null, null, SummarizeProbeFailure(ex));
        }
        catch (ArgumentOutOfRangeException ex) when (ModelProbeFailureClassifier.IsUnknownFinishReason(ex))
        {
            ModelProbeServiceLog.StreamingProbeFailed(logger, ex);
            return (null, null, SummarizeProbeFailure(ex));
        }
    }

    private async Task<(bool? Supported, string? ErrorDetail)> ProbeToolCallingAsync(IChatClient client)
    {
        try
        {
            AIFunction tool = AIFunctionFactory.Create(
                (string value) => $"pong:{value}",
                "ping_probe",
                "Returns a pong response for the provided value.",
                serializerOptions: null);

            ChatResponse response = await client.GetResponseAsync(
                "Call the ping_probe tool with value set to ok. Do not answer normally.",
                new ChatOptions
                {
                    MaxOutputTokens = 128,
                    Tools = [tool],
                    ToolMode = ChatToolMode.RequireAny,
                    AllowMultipleToolCalls = false,
                }).ConfigureAwait(false);

            return (response.Messages
                .SelectMany(static m => m.Contents)
                .OfType<FunctionCallContent>()
                .Any(static c => string.Equals(c.Name, "ping_probe", StringComparison.Ordinal)), null);
        }
        catch (HttpRequestException ex)
        {
            ModelProbeServiceLog.ToolCallProbeFailed(logger, ex);
            return (null, SummarizeProbeFailure(ex));
        }
        catch (InvalidOperationException ex)
        {
            ModelProbeServiceLog.ToolCallProbeFailed(logger, ex);
            return (null, SummarizeProbeFailure(ex));
        }
        catch (JsonException ex)
        {
            ModelProbeServiceLog.ToolCallProbeFailed(logger, ex);
            return (null, SummarizeProbeFailure(ex));
        }
        catch (IOException ex)
        {
            ModelProbeServiceLog.ToolCallProbeFailed(logger, ex);
            return (null, SummarizeProbeFailure(ex));
        }
        catch (System.ClientModel.ClientResultException ex)
        {
            ModelProbeServiceLog.ToolCallProbeFailed(logger, ex);
            return (null, SummarizeProbeFailure(ex));
        }
        catch (ArgumentOutOfRangeException ex) when (ModelProbeFailureClassifier.IsUnknownFinishReason(ex))
        {
            ModelProbeServiceLog.ToolCallProbeFailed(logger, ex);
            return (null, SummarizeProbeFailure(ex));
        }
    }

    private async Task<(bool? Supported, string? ErrorDetail)> ProbeStructuredJsonAsync(IChatClient client)
    {
        try
        {
            ChatResponse response = await client.GetResponseAsync(
                "Return a JSON object with exactly one property named status set to ok.",
                new ChatOptions { MaxOutputTokens = 128, ResponseFormat = ChatResponseFormat.Json }).ConfigureAwait(false);

            string text = response.Text?.Trim() ?? string.Empty;
            ModelProbeServiceLog.StructuredJsonProbeRaw(logger, text);

            if (!TryParseJson(text, out JsonDocument? payload))
                return (false, null);

            if (payload is null)
                return (false, null);

            using (payload)
            {
                return (payload.RootElement.ValueKind == JsonValueKind.Object
                    && payload.RootElement.TryGetProperty("status", out JsonElement status)
                    && string.Equals(status.GetString(), "ok", StringComparison.OrdinalIgnoreCase), null);
            }
        }
        catch (HttpRequestException ex)
        {
            ModelProbeServiceLog.StructuredJsonProbeFailed(logger, ex);
            return (null, SummarizeProbeFailure(ex));
        }
        catch (InvalidOperationException ex)
        {
            ModelProbeServiceLog.StructuredJsonProbeFailed(logger, ex);
            return (null, SummarizeProbeFailure(ex));
        }
        catch (JsonException ex)
        {
            ModelProbeServiceLog.StructuredJsonProbeFailed(logger, ex);
            return (null, SummarizeProbeFailure(ex));
        }
        catch (IOException ex)
        {
            ModelProbeServiceLog.StructuredJsonProbeFailed(logger, ex);
            return (null, SummarizeProbeFailure(ex));
        }
        catch (System.ClientModel.ClientResultException ex)
        {
            ModelProbeServiceLog.StructuredJsonProbeFailed(logger, ex);
            return (null, SummarizeProbeFailure(ex));
        }
        catch (ArgumentOutOfRangeException ex) when (ModelProbeFailureClassifier.IsUnknownFinishReason(ex))
        {
            ModelProbeServiceLog.StructuredJsonProbeFailed(logger, ex);
            return (null, SummarizeProbeFailure(ex));
        }
    }

}
