using System.Runtime.CompilerServices;
using System.Text;

using Kautuka.Gateway.Domain.Usage;

using Microsoft.Extensions.AI;

namespace Kautuka.Gateway.Infrastructure.AI.ProviderPathPolicy;

internal sealed partial class ProviderPathTelemetryChatClient : DelegatingChatClient
{
    private readonly ProviderPathKey pathKey;
    private readonly IProviderPathObservationStore store;
    private readonly IProviderPathPolicyResolver resolver;
    private readonly IGatewayUsageCaptureAccessor usageCaptureAccessor;
    private readonly ILogger<ProviderPathTelemetryChatClient> logger;
    private readonly string pathStoreKey;
    private ProviderPathEffectiveMode? cachedMode;

    public ProviderPathTelemetryChatClient(
        IChatClient innerClient,
        ProviderPathKey pathKey,
        IProviderPathObservationStore store,
        IProviderPathPolicyResolver resolver,
        IGatewayUsageCaptureAccessor usageCaptureAccessor,
        ILogger<ProviderPathTelemetryChatClient> logger)
        : base(innerClient)
    {
        this.pathKey = pathKey;
        this.store = store;
        this.resolver = resolver;
        this.usageCaptureAccessor = usageCaptureAccessor;
        this.logger = logger;
        pathStoreKey = pathKey.ToStoreKey();
    }

    public override async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> chatMessages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ProviderPathEffectiveMode mode = await GetModeAsync(cancellationToken).ConfigureAwait(false);

        bool hasTools = options?.Tools is { Count: > 0 };
        bool usePromptedJsonFallback = hasTools && mode.ToolMode == ToolCompatibilityMode.PromptedJson;

        if (usePromptedJsonFallback)
        {
            ProviderPathTelemetryChatClientLog.PromptedJsonModeActive(logger, pathStoreKey);
            return await RunPromptedJsonGetResponseAsync(chatMessages, options!, cancellationToken).ConfigureAwait(false);
        }

        usageCaptureAccessor.RecordProviderRequestStarted();

        ChatOptions? effectiveOptions = options;

        if (effectiveOptions?.ResponseFormat == ChatResponseFormat.Json
            && mode.JsonMode == JsonCompatibilityMode.Relaxed)
        {
            ChatOptions relaxedOptions = effectiveOptions.Clone();
            relaxedOptions.ResponseFormat = ChatResponseFormat.Text;
            effectiveOptions = relaxedOptions;
        }

        ChatResponse response = await base.GetResponseAsync(chatMessages, effectiveOptions, cancellationToken).ConfigureAwait(false);
        if (response.Messages.SelectMany(static message => message.Contents).Any())
        {
            usageCaptureAccessor.RecordFirstModelOutput(DateTimeOffset.UtcNow);
        }

        if (hasTools)
        {
            bool toolCalled = response.Messages
                .SelectMany(static m => m.Contents)
                .OfType<FunctionCallContent>()
                .Any();

            if (toolCalled)
                await store.RecordNativeToolSuccessAsync(pathKey, cancellationToken).ConfigureAwait(false);
            else
                await store.RecordNativeToolFailureAsync(pathKey, cancellationToken).ConfigureAwait(false);
        }

        if (effectiveOptions?.ResponseFormat == ChatResponseFormat.Json)
        {
            string text = response.Text?.Trim() ?? string.Empty;
            if (TryParseJson(text, out _))
            {
                if (mode.JsonMode == JsonCompatibilityMode.Strict)
                    await store.RecordStrictJsonSuccessAsync(pathKey, cancellationToken).ConfigureAwait(false);
                else
                    await store.RecordRelaxedJsonSuccessAsync(pathKey, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await store.RecordJsonFailureAsync(pathKey, cancellationToken).ConfigureAwait(false);
            }
        }

        bool hasReasoning = response.Messages
            .SelectMany(static m => m.Contents)
            .OfType<TextReasoningContent>()
            .Any(static r => !string.IsNullOrWhiteSpace(r.Text));

        bool hasText = response.Messages
            .SelectMany(static m => m.Contents)
            .OfType<TextContent>()
            .Any(static t => !string.IsNullOrWhiteSpace(t.Text));

        if (hasReasoning && !hasText)
        {
            await store.RecordReasoningOnlyAsync(pathKey, cancellationToken).ConfigureAwait(false);

            if (mode.ReasoningMode == ReasoningCompatibilityMode.PromoteReasoningWhenEmpty)
            {
                response = PromoteReasoningToText(response);
            }
        }

        return response;
    }

    public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> chatMessages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ProviderPathEffectiveMode mode = await GetModeAsync(cancellationToken).ConfigureAwait(false);

        bool hasTools = options?.Tools is { Count: > 0 };
        bool usePromptedJsonFallback = hasTools && mode.ToolMode == ToolCompatibilityMode.PromptedJson;

        if (usePromptedJsonFallback)
        {
            ProviderPathTelemetryChatClientLog.PromptedJsonStreamingModeActive(logger, pathStoreKey);

            ChatResponse completed = await RunPromptedJsonGetResponseAsync(chatMessages, options!, cancellationToken).ConfigureAwait(false);
            foreach (ChatMessage msg in completed.Messages)
            {
                yield return new ChatResponseUpdate
                {
                    Contents = [.. msg.Contents],
                    Role = msg.Role,
                };
            }
            yield break;
        }

        ChatOptions? effectiveOptions = options;
        if (effectiveOptions?.ResponseFormat == ChatResponseFormat.Json
            && mode.JsonMode == JsonCompatibilityMode.Relaxed)
        {
            ChatOptions relaxedOptions = effectiveOptions.Clone();
            relaxedOptions.ResponseFormat = ChatResponseFormat.Text;
            effectiveOptions = relaxedOptions;
        }

        var reasoningBuffer = new StringBuilder();
        var textBuffer = new StringBuilder();
        bool gotText = false;
        bool gotToolCall = false;
        bool firstOutputRecorded = false;

        usageCaptureAccessor.RecordProviderRequestStarted();

        await foreach (ChatResponseUpdate update in base.GetStreamingResponseAsync(
            chatMessages, effectiveOptions, cancellationToken).ConfigureAwait(false))
        {
            if (!firstOutputRecorded && update.Contents.Count > 0)
            {
                firstOutputRecorded = true;
                usageCaptureAccessor.RecordFirstModelOutput(DateTimeOffset.UtcNow);
            }

            foreach (AIContent content in update.Contents)
            {
                if (content is TextReasoningContent reasoning && !string.IsNullOrWhiteSpace(reasoning.Text))
                {
                    if (reasoningBuffer.Length > 0) reasoningBuffer.Append('\n');
                    reasoningBuffer.Append(reasoning.Text);
                    continue;
                }

                if (content is FunctionCallContent)
                    gotToolCall = true;

                if (content is TextContent tc && !string.IsNullOrWhiteSpace(tc.Text))
                {
                    textBuffer.Append(tc.Text);
                    gotText = true;
                }
            }

            yield return update;
        }

        if (hasTools)
        {
            if (gotToolCall)
                await store.RecordNativeToolSuccessAsync(pathKey, cancellationToken).ConfigureAwait(false);
            else
                await store.RecordNativeToolFailureAsync(pathKey, cancellationToken).ConfigureAwait(false);
        }

        bool hadReasoning = reasoningBuffer.Length > 0;

        if (!hadReasoning && gotText && textBuffer.Length > 0 && IsAllTaggedReasoning(textBuffer.ToString()))
        {
            hadReasoning = true;
            gotText = false;
        }

        if (hadReasoning && !gotText)
        {
            await store.RecordReasoningOnlyAsync(pathKey, cancellationToken).ConfigureAwait(false);

            if (mode.ReasoningMode == ReasoningCompatibilityMode.PromoteReasoningWhenEmpty)
            {
                string promoted = reasoningBuffer.ToString();
                ProviderPathTelemetryChatClientLog.PromotingReasoningOnlyOutput(logger, promoted.Length, pathStoreKey);

                yield return new ChatResponseUpdate
                {
                    Contents = [new TextContent(promoted)],
                };
            }
        }
    }

    private async ValueTask<ProviderPathEffectiveMode> GetModeAsync(CancellationToken ct)
    {
        cachedMode ??= await resolver.ResolveAsync(pathKey, ct).ConfigureAwait(false);
        return cachedMode;
    }
}
