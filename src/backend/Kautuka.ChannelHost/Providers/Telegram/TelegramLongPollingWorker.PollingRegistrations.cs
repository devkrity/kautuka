namespace Kautuka.ChannelHost.Providers.Telegram;

internal sealed partial class TelegramLongPollingWorker
{
    private IReadOnlyList<PollingRegistration> GetCachedRegistrations()
        => [.. attachmentStore.ListActiveTelegramRegistrations().Select(static item => new PollingRegistration(
            item.AttachmentId,
            item.OwnerUserId,
            item.BotToken,
            item.PollTimeoutSeconds,
            item.Settings))];
}
