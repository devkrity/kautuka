using System.Text.Json;

using Kautuka.Gateway.Infrastructure.Observability;

namespace Kautuka.Gateway.Domain.Channels;

internal sealed partial class ChannelIngressService
{
    private async Task DeleteChannelMessageAsync(
        GatewayChannelRegistration registration,
        string chatId,
        string messageId,
        CancellationToken cancellationToken)
    {
        _ = cancellationToken;
        AgentRunHostCallTracer.AgentRunTraceContext? traceContext = AgentRunHostCallTracer.CaptureCurrentRunTraceContext();
        outboxStore.Enqueue(
            registration.ChannelType,
            registration.Id,
            "delete_message",
            JsonSerializer.Serialize(new ChannelMessageDeleteAction(chatId, messageId), ChannelContractsJsonContext.Default.ChannelMessageDeleteAction),
            null,
            traceContext?.RunId,
            traceContext?.TraceParent,
            traceContext?.TraceState,
            registration.OwnerUserId);
        await Task.CompletedTask.ConfigureAwait(false);
    }

    private async Task AnswerChannelCallbackAsync(
        GatewayChannelRegistration registration,
        string? callbackQueryId,
        string text,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(callbackQueryId))
        {
            return;
        }

        _ = cancellationToken;
        AgentRunHostCallTracer.AgentRunTraceContext? traceContext = AgentRunHostCallTracer.CaptureCurrentRunTraceContext();

        outboxStore.Enqueue(
            registration.ChannelType,
            registration.Id,
            "acknowledge_callback",
            JsonSerializer.Serialize(new ChannelCallbackAckAction(callbackQueryId, text), ChannelContractsJsonContext.Default.ChannelCallbackAckAction),
            null,
            traceContext?.RunId,
            traceContext?.TraceParent,
            traceContext?.TraceState,
            registration.OwnerUserId);
        await Task.CompletedTask.ConfigureAwait(false);
    }

}
