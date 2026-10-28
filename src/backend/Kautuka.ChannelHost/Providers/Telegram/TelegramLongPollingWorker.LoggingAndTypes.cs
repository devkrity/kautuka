using System.Text.Json;

namespace Kautuka.ChannelHost.Providers.Telegram;

internal sealed partial class TelegramLongPollingWorker
{
    private sealed record PollingRegistration(
        string RegistrationId,
        string OwnerUserId,
        string BotToken,
        int PollTimeoutSeconds,
        JsonElement Settings,
        string? VerifiedBotId = null)
    {
        public string BotIdentity => VerifiedBotId ?? TelegramBotIdentity.FromToken(BotToken);
        public string CursorScope => "telegram-bot:" + BotIdentity;
    }

    private readonly record struct ForwardUpdateOutcome(long UpdateId, bool ShouldAdvanceOffset, bool ShouldStopPolling, bool CursorPersisted = false);

    [LoggerMessage(EventId = 1, Level = LogLevel.Warning, Message = "Telegram polling failed for registration {RegistrationId}")]
    private static partial void LogPollingFailed(ILogger logger, Exception exception, string registrationId);

    [LoggerMessage(EventId = 2, Level = LogLevel.Error, Message = "Telegram polling worker iteration failed")]
    private static partial void LogWorkerIterationFailed(ILogger logger, Exception exception);

    [LoggerMessage(EventId = 5, Level = LogLevel.Warning, Message = "Telegram getUpdates failed. registration={RegistrationId} status={StatusCode}")]
    private static partial void LogTelegramGetUpdatesFailed(ILogger logger, string registrationId, System.Net.HttpStatusCode statusCode);

    [LoggerMessage(EventId = 6, Level = LogLevel.Warning, Message = "Failed to forward telegram update to ingress queue. registration={RegistrationId} status={Status} detail={Detail}")]
    private static partial void LogForwardTelegramUpdateFailed(ILogger logger, string registrationId, string status, string detail);

    [LoggerMessage(EventId = 11, Level = LogLevel.Information, Message = "Outbox intent started. actionId={ActionId} actionType={ActionType} leaseId={LeaseId}")]
    private static partial void LogOutboxIntentStarted(ILogger logger, string actionId, string actionType, string leaseId);

    [LoggerMessage(EventId = 12, Level = LogLevel.Information, Message = "Outbox intent processed. actionId={ActionId} actionType={ActionType} success={Success} error={Error}")]
    private static partial void LogOutboxIntentProcessed(ILogger logger, string actionId, string actionType, bool success, string error);

    [LoggerMessage(EventId = 16, Level = LogLevel.Debug, Message = "Provisioning step skipped. registration={RegistrationId} step={Step} reason={Reason}")]
    private static partial void LogProvisioningStepSkipped(ILogger logger, string registrationId, string step, string reason);

    [LoggerMessage(EventId = 17, Level = LogLevel.Information, Message = "Telegram poll started. registration={RegistrationId} offset={Offset} timeoutSeconds={TimeoutSeconds}")]
    private static partial void LogPollRequestStarted(ILogger logger, string registrationId, long offset, int timeoutSeconds);

    [LoggerMessage(EventId = 18, Level = LogLevel.Information, Message = "Telegram poll response. registration={RegistrationId} httpStatus={HttpStatus} elapsedMs={ElapsedMs}")]
    private static partial void LogPollRequestCompleted(ILogger logger, string registrationId, int httpStatus, double elapsedMs);

    [LoggerMessage(EventId = 19, Level = LogLevel.Information, Message = "Telegram callback received. registration={RegistrationId} eventId={EventId}")]
    private static partial void LogCallbackReceived(ILogger logger, string registrationId, string eventId);

    [LoggerMessage(EventId = 20, Level = LogLevel.Information, Message = "Telegram callback acknowledgement result. registration={RegistrationId} httpStatus={HttpStatus} accepted={Accepted} elapsedMs={ElapsedMs} outcome={Outcome}")]
    private static partial void LogCallbackAcknowledgement(ILogger logger, string registrationId, int httpStatus, bool accepted, double elapsedMs, string outcome);

    [LoggerMessage(EventId = 21, Level = LogLevel.Information, Message = "Telegram callback acknowledgement started. registration={RegistrationId}")]
    private static partial void LogCallbackAcknowledgementStarted(ILogger logger, string registrationId);

    [LoggerMessage(EventId = 22, Level = LogLevel.Information, Message = "Telegram bot poller {State}. registration={RegistrationId} bot={BotIdentity}")]
    private static partial void LogBotPollerState(ILogger logger, string state, string registrationId, string botIdentity);

    [LoggerMessage(EventId = 23, Level = LogLevel.Warning, Message = "Telegram bot poller backing off. registration={RegistrationId} reason={Reason} delayMs={DelayMs}")]
    private static partial void LogBotPollerBackoff(ILogger logger, string registrationId, string reason, double delayMs);

    [LoggerMessage(EventId = 24, Level = LogLevel.Warning, Message = "Telegram bot has conflicting active attachments; polling is suspended. bot={BotIdentity} attachmentCount={AttachmentCount}")]
    private static partial void LogBotOwnershipConflict(ILogger logger, string botIdentity, int attachmentCount);

    [LoggerMessage(EventId = 25, Level = LogLevel.Warning, Message = "Telegram polling roster refresh failed; existing bot loops retained. failureType={FailureType}")]
    private static partial void LogPollingRosterFailure(ILogger logger, string failureType);
}
