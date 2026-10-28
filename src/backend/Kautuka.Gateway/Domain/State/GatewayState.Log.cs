namespace Kautuka.Gateway.Domain.State;

internal static partial class GatewayStateLog
{
    [LoggerMessage(EventId = 1, Level = LogLevel.Information, Message = "First admin account created via setup flow. Username={Username}")]
    public static partial void FirstAdminCreated(ILogger logger, string username);

    [LoggerMessage(EventId = 4, Level = LogLevel.Information, Message = "External sign-in link cleared. Actor={ActorId} Target={TargetId} Provider={Provider}")]
    public static partial void ExternalLoginCleared(ILogger logger, string actorId, string targetId, string provider);
}
