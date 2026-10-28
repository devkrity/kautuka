namespace Kautuka.ChannelHost.Providers.Telegram;

internal sealed partial class TelegramLongPollingWorker
{
    private async Task<OutboxExecutionResult> ExecuteRuntimeEventAsync(
        PollingRegistration registration,
        string payloadJson,
        CancellationToken cancellationToken)
    {
        ChannelRuntimeEventAction? action = ChannelRuntimeEventOutboundExecutor.TryDeserialize(payloadJson);
        if (action is null)
            return new OutboxExecutionResult(false, "invalid_payload", null);

        IReadOnlyList<ChannelTranslatedOutboundAction> translated = ChannelRuntimeEventTranslator.Shared.Translate(
            registration.Settings,
            action, richContext: true);

        // Same policy as Slack gRPC executor: never progressive-stream from runtime_event.
        translated = [.. translated.Where(static item =>
            !string.Equals(item.ActionType, ChannelOutboundActionTypes.StreamMessage, StringComparison.Ordinal))];

        OutboxExecutionResult last = new(true, null, null);
        foreach (ChannelTranslatedOutboundAction item in translated)
        {
            last = await ExecuteOutboxActionAsync(registration, item.ActionType, item.PayloadJson, cancellationToken).ConfigureAwait(false);
            if (!last.Success)
                return last;
        }

        return last;
    }
}
