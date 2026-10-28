namespace Kautuka.ChannelHost.Providers.Telegram;

internal sealed partial class TelegramLongPollingWorker
{
    private readonly record struct OutboxExecutionResult(
        bool Success,
        string? Error,
        int? RetryAfterSeconds,
        string? TransportMessageId = null,
        string? ProviderStatePatchJson = null,
        bool PermanentFailure = false);

    private readonly record struct TelegramMessageResult(string? MessageId, OutboxExecutionResult Execution);
}
