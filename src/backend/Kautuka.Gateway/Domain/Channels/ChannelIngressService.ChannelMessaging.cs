using System.Text.Json;
using System.Text.Json.Nodes;

using Kautuka.Gateway.Infrastructure.Observability;

namespace Kautuka.Gateway.Domain.Channels;

internal sealed partial class ChannelIngressService
{
    private async Task<bool> SendChannelMessageAsync(
        GatewayChannelRegistration registration,
        string chatId,
        string text,
        string? messageThreadId,
        CancellationToken cancellationToken,
        string? parseMode = null)
    {
        _ = cancellationToken;
        AgentRunHostCallTracer.AgentRunTraceContext? traceContext = AgentRunHostCallTracer.CaptureCurrentRunTraceContext();
        foreach (ChannelOutboundDestination destination in ChannelOutboundDestinations.Resolve(
            attachmentRegistry, registration, chatId, messageThreadId))
        {
            outboxStore.Enqueue(
                destination.Registration.ChannelType,
                destination.Registration.Id,
                "send_text",
                JsonSerializer.Serialize(
                    new ChannelTextMessageAction(destination.ConversationId, destination.ConversationThreadId,
                        string.IsNullOrWhiteSpace(text) ? "(empty response)" : text,
                        string.Equals(destination.Registration.Id, registration.Id, StringComparison.Ordinal) ? parseMode : null),
                    ChannelContractsJsonContext.Default.ChannelTextMessageAction),
                null,
                traceContext?.RunId,
                traceContext?.TraceParent,
                traceContext?.TraceState,
                registration.OwnerUserId);
        }
        return await Task.FromResult(true).ConfigureAwait(false);
    }

    private async Task RunChannelTypingLoopAsync(
        GatewayChannelRegistration registration,
        string chatId,
        string? messageThreadId,
        CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            await SendChannelTypingAsync(registration, chatId, messageThreadId, cancellationToken).ConfigureAwait(false);
            await Task.Delay(TimeSpan.FromSeconds(4), cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task SendChannelTypingAsync(
        GatewayChannelRegistration registration,
        string chatId,
        string? messageThreadId,
        CancellationToken cancellationToken)
    {
        _ = cancellationToken;
        AgentRunHostCallTracer.AgentRunTraceContext? traceContext = AgentRunHostCallTracer.CaptureCurrentRunTraceContext();
        outboxStore.Enqueue(
            registration.ChannelType,
            registration.Id,
            "send_chat_action",
            JsonSerializer.Serialize(new ChannelChatAction(chatId, messageThreadId, "typing"), ChannelContractsJsonContext.Default.ChannelChatAction),
            null,
            traceContext?.RunId,
            traceContext?.TraceParent,
            traceContext?.TraceState,
            registration.OwnerUserId);
        await Task.CompletedTask.ConfigureAwait(false);
    }

    private async Task<long?> SendChannelInteractiveMessageAsync(
        GatewayChannelRegistration registration,
        GatewayChannelInteraction interaction,
        string? parseMode,
        CancellationToken cancellationToken)
    {
        _ = cancellationToken;
        _ = parseMode;
        AgentRunHostCallTracer.AgentRunTraceContext? traceContext = AgentRunHostCallTracer.CaptureCurrentRunTraceContext();
        outboxStore.Enqueue(
            registration.ChannelType,
            registration.Id,
            "send_semantic_message",
            JsonSerializer.Serialize(
                new ChannelSemanticMessageAction(
                    interaction.ChatId,
                    interaction.MessageThreadId,
                    new ChannelSemanticMessageDocument(
                        ChannelSemanticContractVersions.MessageDocument,
                        "approval_request",
                        null,
                        interaction.Prompt,
                        [
                            new(ChannelSemanticBlockTypes.Header, Text: "Approval required"),
                            new(ChannelSemanticBlockTypes.Section, Text: interaction.Prompt),
                            new(ChannelSemanticBlockTypes.Actions, Actions: [.. interaction.Options.Select((option, index) => new ChannelSemanticAction($"decision-{index}", ChannelSemanticActionTypes.Button, option.Label, $"hitl:{interaction.Id}:{option.Value}", option.Style, Required: true))]),
                        ]),
                    interaction.Id),
                ChannelContractsJsonContext.Default.ChannelSemanticMessageAction),
            null,
            traceContext?.RunId,
            traceContext?.TraceParent,
            traceContext?.TraceState,
            registration.OwnerUserId);
        return await Task.FromResult(-1L as long?).ConfigureAwait(false);
    }

    private async Task<bool> SendChannelInlineKeyboardMessageAsync(
        GatewayChannelRegistration registration,
        string chatId,
        string text,
        JsonObject[][] keyboard,
        string? messageThreadId,
        CancellationToken cancellationToken)
    {
        _ = cancellationToken;
        AgentRunHostCallTracer.AgentRunTraceContext? traceContext = AgentRunHostCallTracer.CaptureCurrentRunTraceContext();
        ChannelInlineKeyboardRow[] rows = [.. keyboard.Select(row => new ChannelInlineKeyboardRow([..
            row.Select(button => new ChannelInlineKeyboardButton(
                button["text"]?.ToString() ?? string.Empty,
                button["callback_data"]?.ToString() ?? string.Empty))]))];

        outboxStore.Enqueue(
            registration.ChannelType,
            registration.Id,
            "send_inline_keyboard",
            JsonSerializer.Serialize(new ChannelInlineKeyboardAction(chatId, messageThreadId, text, rows), ChannelContractsJsonContext.Default.ChannelInlineKeyboardAction),
            null,
            traceContext?.RunId,
            traceContext?.TraceParent,
            traceContext?.TraceState,
            registration.OwnerUserId);
        return await Task.FromResult(true).ConfigureAwait(false);
    }
}
