using System.Text.Json;

namespace Kautuka.Gateway.Infrastructure.Channels;

internal sealed partial class ChannelHostOutboxWorker
{
    private Kautuka.Gateway.Domain.Channels.ChannelQueueViewStore? queueViewStore;

    internal void RecordSessionActivity(Kautuka.Gateway.Domain.Channels.ChannelOutboxEntry entry, GatewayChannelRegistration registration, string owner)
    {
        try
        {
            string? chat = null;
            string? topic = null;
            if (entry.ActionType == ChannelOutboundActionTypes.RuntimeEvent)
            {
                ChannelRuntimeEventAction? action = JsonSerializer.Deserialize(entry.PayloadJson, ChannelContractsJsonContext.Default.ChannelRuntimeEventAction);
                if (action?.EventType == "assistant.text.delta") return;
                chat = action?.ConversationId;
                topic = action?.ConversationThreadId;
            }
            else if (entry.ActionType == ChannelOutboundActionTypes.SendSemanticMessage)
            {
                ChannelSemanticMessageAction? action = JsonSerializer.Deserialize(entry.PayloadJson, ChannelContractsJsonContext.Default.ChannelSemanticMessageAction);
                if (action?.PromptSessionId is null) return;
                chat = action.ConversationId;
                topic = action.ConversationThreadId;
            }
            if (chat is null) return;
            queueViewStore ??= new Kautuka.Gateway.Domain.Channels.ChannelQueueViewStore(state.Database);
            queueViewStore.RecordActivity(owner, Kautuka.Gateway.Domain.Channels.ChannelQueueViewStore.RegistrationScope(registration) + ":session", chat, topic, entry.Id);
        }
        catch (Exception ex) when (ex is Microsoft.Data.Sqlite.SqliteException or JsonException or IOException or InvalidOperationException)
        {
            // A derived-view refresh signal must never retry an already delivered runtime event.
            LogSessionActivityFailed(logger, ex.GetType().Name);
        }
    }

    [LoggerMessage(12, LogLevel.Warning, "Could not record channel session view activity: {FailureType}")]
    private static partial void LogSessionActivityFailed(ILogger logger, string failureType);
}
