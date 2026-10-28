using Kautuka.Gateway.Domain.Execution;

namespace Kautuka.Gateway.Domain.Channels;

internal sealed partial class ChannelIngressService
{
    private Task PublishSessionsAsync(GatewayChannelRegistration registration, ChannelInboundEnvelope envelope, int offset, CancellationToken cancellationToken, bool bringToFront = true)
    {
        cancellationToken.ThrowIfCancellationRequested();
        GatewayChannelTargetBinding? target = routingCoordinator.ResolveTargetBinding(registration, envelope.ExternalUserId, envelope.MessageThreadId);
        if (target is null) return Task.CompletedTask;
        ChannelConversationMapEntry map = routingCoordinator.ResolveConversationMap(registration.Id, target, envelope);
        string? topic = ChannelThreadRouting.ProviderTopic(envelope.ChatId, ResolveOutboundThreadId(registration, target, envelope.ChatId, envelope.MessageThreadId));
        return factPublisher.PublishSessionPickerFactsAsync(registration, target, envelope.ChatId, topic, map.SessionId, offset,
            onlyIfChanged: false, bringToFront: bringToFront, cancellationToken);
    }

    internal async Task RefreshSessionViewsAsync(CancellationToken cancellationToken)
    {
        var refreshed = new HashSet<string>(StringComparer.Ordinal);
        foreach (ChannelConversationMapEntry map in conversationMapStore.ListAll().OrderByDescending(map => map.UpdatedAt, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!attachmentRegistry.TryGetRegistration(map.RegistrationId, out GatewayChannelRegistration? registration)
                || registration is null || !registration.IsActive) continue;
            GatewayChannelTargetBinding? target = attachmentRegistry.GetBindings(registration.Id).FirstOrDefault(binding =>
                binding.TargetId == map.TargetId && binding.TargetType == map.TargetType);
            if (target is null || AccessibleSession(registration.OwnerUserId, map.SessionId, target) is not { } session) continue;
            string? topic = ChannelThreadRouting.ProviderTopic(map.ProviderConversationId,
                ResolveOutboundThreadId(registration, target, map.ProviderConversationId, map.ProviderThreadId));
            string canonicalKey = ChannelThreadRouting.ConversationKey(registration, map.ProviderConversationId, topic, target.TargetType, target.TargetId);
            ChannelConversationMapEntry? canonicalMap = conversationMapStore.TryResolve(map.ChannelType, map.RegistrationId, map.ProviderConversationId, canonicalKey);
            if (canonicalMap is not null && canonicalMap.Id != map.Id) continue;
            if (!refreshed.Add(string.Join('\0', registration.Id, map.ProviderConversationId, topic))) continue;
            using IDisposable lease = await SessionExecutionGate.EnterAsync(string.Join('\0', "channel-admission", registration.Id, map.ProviderConversationId), cancellationToken).ConfigureAwait(false);
            // Reload after the gate: a notification or explicit selection may have rebound this map.
            ChannelConversationMapEntry? current = conversationMapStore.TryResolve(map.ChannelType, map.RegistrationId, map.ProviderConversationId, map.ProviderThreadId);
            if (current?.SessionId != session.Id) continue;
            ChannelQueueView? currentView = queueViews.Read(registration.OwnerUserId,
                ChannelQueueViewStore.RegistrationScope(registration) + ":session", map.ProviderConversationId, topic);
            if (currentView?.View == ChannelPanelView.Sessions)
            {
                await factPublisher.PublishSessionPickerFactsAsync(registration, target, map.ProviderConversationId, topic,
                    session.Id, currentView.PageOffset, onlyIfChanged: true, bringToFront: false,
                    cancellationToken).ConfigureAwait(false);
            }
            else if (currentView?.View == ChannelPanelView.Queue)
            {
                await PublishBotQueueAsync(registration, registration.OwnerUserId, map.ProviderConversationId, topic,
                    currentView.PageOffset, onlyIfChanged: true, bringToFront: false,
                    cancellationToken).ConfigureAwait(false);
            }
        }
    }
}

internal sealed partial class ChannelSessionUiWorker(ChannelIngressService ingress, ILogger<ChannelSessionUiWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(3));
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
        {
            try { await ingress.RefreshSessionViewsAsync(stoppingToken).ConfigureAwait(false); }
            catch (Exception ex) when (ex is not OperationCanceledException) { RefreshFailed(logger, ex.GetType().Name); }
        }
    }
    [LoggerMessage(1, LogLevel.Warning, "Channel session view refresh failed: {FailureType}")]
    private static partial void RefreshFailed(ILogger logger, string failureType);
}
