using Kautuka.Gateway.Domain.Stores;

using Mediator;

namespace Kautuka.Gateway.Domain.Sessions;

internal interface ISessionLifecycleService
{
    ValueTask<GatewaySession> CreateSessionAsync(string targetType, string targetId, string ownerUserId, CancellationToken cancellationToken, string? originator = null);

    ValueTask<GatewaySession> EnsureSessionAsync(string targetType, string targetId, string sessionId, string ownerUserId, CancellationToken cancellationToken, string? originator = null);
}

internal sealed class SessionLifecycleService(
    IGatewaySessionStore sessionStore,
    IPlatformConfigurationProvider configProvider,
    IMediator mediator) : ISessionLifecycleService
{
    public async ValueTask<GatewaySession> CreateSessionAsync(string targetType, string targetId, string ownerUserId, CancellationToken cancellationToken, string? originator = null)
    {
        GatewaySession session = sessionStore.CreateSession(targetType, targetId, ownerUserId, originator);
        await PublishRetentionRequestedAsync(targetType, targetId, ownerUserId, session.Id).ConfigureAwait(false);
        return session;
    }

    public async ValueTask<GatewaySession> EnsureSessionAsync(string targetType, string targetId, string sessionId, string ownerUserId, CancellationToken cancellationToken, string? originator = null)
    {
        if (TryReuseSession(targetType, targetId, sessionId, ownerUserId, out GatewaySession? existing) && existing is not null)
        {
            return existing;
        }

        return await CreateSessionAsync(targetType, targetId, ownerUserId, cancellationToken, originator).ConfigureAwait(false);
    }

    private bool TryReuseSession(string targetType, string targetId, string sessionId, string ownerUserId, out GatewaySession? session)
    {
        session = null;
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return false;
        }

        if (!sessionStore.TryGetSession(sessionId, out GatewaySession? existing) || existing is null)
        {
            return false;
        }

        bool sameTargetType = string.Equals(existing.TargetType, targetType, StringComparison.OrdinalIgnoreCase);
        bool sameTargetId = string.Equals(existing.TargetId, targetId, StringComparison.OrdinalIgnoreCase);
        bool sameOwnerUserId = string.Equals(existing.OwnerUserId, ownerUserId, StringComparison.Ordinal);
        if (!sameTargetType || !sameTargetId || !sameOwnerUserId)
        {
            return false;
        }

        session = existing;
        return true;
    }

    private async ValueTask PublishRetentionRequestedAsync(string targetType, string targetId, string ownerUserId, string sessionId)
    {
        PlatformConfiguration config = configProvider.GetCurrent();
        int? retainCount = ResolveRetainCount(config, targetType, targetId);
        if (!retainCount.HasValue)
        {
            return;
        }

        await mediator.Publish(new SessionRetentionRequested(targetType, targetId, ownerUserId, sessionId, retainCount.Value), CancellationToken.None).ConfigureAwait(false);
    }

    private static int? ResolveRetainCount(PlatformConfiguration config, string targetType, string targetId)
    {
        if (string.Equals(targetType, "agent", StringComparison.OrdinalIgnoreCase))
        {
            AgentDefinition? agent = config.Agents.FirstOrDefault(item => string.Equals(item.Id, targetId, StringComparison.Ordinal));
            return agent is null ? null : Math.Max(1, agent.Session.MaxHistoryRuns);
        }

        return null;
    }
}
