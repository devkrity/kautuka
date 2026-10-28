using System.Text.Json;
using System.Text.Json.Nodes;

using Kautuka.Gateway.Domain.Execution;

using Narad.Contracts;

namespace Kautuka.Gateway.Domain.Channels;

internal sealed partial class ChannelIngressService
{
    internal SessionTurnQueueStore PendingTurns => pendingTurns;

    internal bool IsSessionBusy(string targetId, string sessionId, string? exceptRootRunId = null)
        => state.AgentDefinitionStore.HasActiveRunForSession(state.AgentDefinitionStore.GetStorageLocatorKey(targetId), sessionId, exceptRootRunId);

    private async Task<ChannelIngressResult> QueueTurnAsync(ChannelMessageProcessingContext context, CancellationToken cancellationToken, bool showReceipt = true)
    {
        string sessionId = context.ConversationMap.SessionId ?? throw new InvalidOperationException("A queued turn requires a bound session.");
        var payload = new JsonObject
        {
            ["registrationId"] = context.RegistrationId,
            ["ownerUserId"] = context.Identity.UserId,
            ["externalUserId"] = context.Envelope.ExternalUserId,
            ["chatId"] = context.Envelope.ChatId,
            ["topicId"] = context.Envelope.MessageThreadId,
            ["conversationKey"] = context.ConversationMap.ProviderThreadId,
            ["queueTopic"] = ChannelThreadRouting.ProviderTopic(context.Envelope.ChatId, context.OutboundThreadId),
            ["targetType"] = context.TargetBinding.TargetType,
            ["targetId"] = context.TargetBinding.TargetId,
            ["sessionId"] = sessionId,
            ["message"] = context.Envelope.MessageText,
            ["request"] = JsonNode.Parse(context.Request.GetRawText()),
        };
        string id = SessionTurnQueueStore.BuildId(context.RegistrationId, context.Envelope.UpdateId);
        JsonObject request = JsonNode.Parse(context.Request.GetRawText())!.AsObject();
        request["session_id"] = sessionId;
        request["run_id"] = id;
        if (context.Envelope.PromptTransportMessageId is { Length: > 0 } promptId)
            new ChannelPromptAnchorStore(state.Database).Save(context.Registration, context.Envelope.ChatId,
                context.OutboundThreadId, sessionId, id, promptId);
        try { _ = pendingTurns.Admit(id, JsonSerializer.SerializeToElement(request, KautukaJsonContext.Default.JsonObject), payload); }
        catch (SessionQueueRejectedException ex)
        {
            await SendChannelMessageAsync(context.Registration, context.Envelope.ChatId, ex.Message, context.OutboundThreadId, cancellationToken).ConfigureAwait(false);
            return new ChannelIngressResult(false, "queue_rejected", ex.Message);
        }
        if (showReceipt)
        {
            QueuedSessionTurn[] waiting = pendingTurns.ListSession(context.Identity.UserId, sessionId).ToArray();
            GatewaySession session = sessionStore.TryGetSession(sessionId, out GatewaySession? currentSession) && currentSession is not null
                ? currentSession
                : throw new InvalidOperationException("The queued session is no longer available.");
            string agentName = routingCoordinator.ResolveTargetDisplayName(context.TargetBinding.TargetType, context.TargetBinding.TargetId);
            await outboundMessagePublisher.PublishTextMessageAsync(
                new ChannelOutboundTextMessage(context.Registration.ChannelType, context.RegistrationId,
                    context.Envelope.ChatId, context.OutboundThreadId,
                    $"Queued for {agentName} · {SessionLabel(session)}\nPosition {waiting.Length} · {waiting.Length} waiting in this session. Tap Queue to review or remove messages.",
                    agentName, SessionLabel(session), session.Id),
                cancellationToken).ConfigureAwait(false);
        }
        return new ChannelIngressResult(true, "queued", "Prompt durably queued for the bound session.", sessionId,
            context.TargetBinding.TargetType, context.TargetBinding.TargetId, RunId: id);
    }

    public async ValueTask<ISessionTurnSink?> PrepareAsync(QueuedSessionTurn job, CancellationToken cancellationToken)
    {
        JsonObject payload = JsonNode.Parse(job.PayloadJson)?.AsObject() ?? throw new InvalidOperationException("Queued channel payload is missing.");
        if (payload["registrationId"] is null) return null;
        string Required(string key) => payload[key]?.GetValue<string>() ?? throw new InvalidOperationException("Queued channel field is missing: " + key);
        string registrationId = Required("registrationId");
        string ownerUserId = Required("ownerUserId");
        await attachmentRegistry.RefreshAttachmentAsync(registrationId, ownerUserId, cancellationToken).ConfigureAwait(false);
        if (!attachmentRegistry.TryGetRegistration(registrationId, out GatewayChannelRegistration? registration)
            || registration is null || !registration.IsActive || registration.OwnerUserId != ownerUserId)
            throw new InvalidOperationException("Queued channel registration is no longer available.");
        string chatId = Required("chatId");
        string? topicId = payload["topicId"]?.GetValue<string>();
        var envelope = new ChannelInboundEnvelope(job.Id, Required("externalUserId"), chatId,
            Required("conversationKey"), topicId, Required("message"), false, null, null, null, null, []);
        GatewayChannelIdentityLink? identity = ResolveChannelIdentity(registrationId, envelope);
        GatewayChannelTargetBinding? target = attachmentRegistry.GetBindings(registrationId).FirstOrDefault(binding =>
            binding.TargetId == job.TargetId && string.Equals(binding.TargetType, Required("targetType"), StringComparison.OrdinalIgnoreCase));
        if (identity is null || identity.UserId != ownerUserId || target is null)
            throw new InvalidOperationException("Queued channel permission was revoked.");
        ChannelConversationMapEntry? map = conversationMapStore.TryResolve(registration.ChannelType, registrationId, chatId, envelope.ProviderThreadId);
        if (map is null || !ChannelThreadRouting.MatchesTarget(map, target.TargetType, target.TargetId))
            throw new InvalidOperationException("Queued channel conversation is no longer available.");
        JsonObject request = payload["request"]?.AsObject() ?? throw new InvalidOperationException("Queued request is missing.");
        request["session_id"] = job.SessionId;
        request["run_id"] = job.Id;
        var context = new ChannelMessageProcessingContext(registration, registrationId, identity, envelope, target,
            map with { SessionId = job.SessionId }, JsonSerializer.SerializeToElement(request, KautukaJsonContext.Default.JsonObject),
            ResolveOutboundThreadId(registration, target, chatId, topicId));
        return new QueuedChannelSink(this, context);
    }

    private sealed class QueuedChannelSink(ChannelIngressService ingress, ChannelMessageProcessingContext context) : ISessionTurnSink
    {
        private readonly System.Text.StringBuilder response = new();
        public async ValueTask ObserveAsync(NaradPersistedEvent runtimeEvent, CancellationToken cancellationToken)
            => _ = await ingress.HandleChannelChatStreamEventAsync(context, runtimeEvent, response, context.ConversationMap.SessionId!, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask QueueChangedAsync(QueuedSessionTurn turn, CancellationToken cancellationToken)
    {
        string? owner = JsonNode.Parse(turn.PayloadJson)?["ownerUserId"]?.GetValue<string>();
        if (owner is null) return;
        foreach (ChannelConversationMapEntry map in conversationMapStore.ListBySession(turn.SessionId))
        {
            if (!attachmentRegistry.TryGetRegistration(map.RegistrationId, out GatewayChannelRegistration? registration)
                || registration is null || !registration.IsActive || registration.OwnerUserId != owner) continue;
            GatewayChannelTargetBinding? target = attachmentRegistry.GetBindings(map.RegistrationId).FirstOrDefault(binding =>
                binding.TargetId == turn.TargetId && ChannelThreadRouting.MatchesTarget(map, binding.TargetType, binding.TargetId));
            if (target is null) continue;
            string? topic = ResolveOutboundThreadId(registration, target, map.ProviderConversationId, map.ProviderThreadId);
            topic = ChannelThreadRouting.ProviderTopic(map.ProviderConversationId, topic);
            using IDisposable lease = await SessionExecutionGate.EnterAsync(string.Join('\0', "channel-admission", registration.Id, map.ProviderConversationId), cancellationToken).ConfigureAwait(false);
            string viewScope = ChannelQueueViewStore.RegistrationScope(registration) + ":session";
            ChannelQueueView? currentView = queueViews.Read(owner, viewScope, map.ProviderConversationId, topic);
            // Queue cards are on demand; keep one current only while the Queue view is open.
            if (currentView?.View != ChannelPanelView.Queue) continue;
            await PublishBotQueueAsync(registration, owner, map.ProviderConversationId, topic,
                currentView.PageOffset,
                onlyIfChanged: true,
                bringToFront: false,
                cancellationToken).ConfigureAwait(false);
        }
    }
    public async ValueTask FailedAsync(QueuedSessionTurn turn, CancellationToken cancellationToken)
        => await NotifyQueuedTurnFailureAsync(turn, cancellationToken).ConfigureAwait(false);

    internal async Task NotifyQueuedTurnFailureAsync(QueuedSessionTurn job, CancellationToken cancellationToken)
    {
        JsonObject payload = JsonNode.Parse(job.PayloadJson)!.AsObject();
        string? registrationId = payload["registrationId"]?.GetValue<string>();
        string? owner = payload["ownerUserId"]?.GetValue<string>();
        string? chat = payload["chatId"]?.GetValue<string>();
        if (registrationId is null || chat is null || !attachmentRegistry.TryGetRegistration(registrationId, out GatewayChannelRegistration? registration)
            || registration is null || registration.OwnerUserId != owner || !registration.IsActive) return;
        await SendChannelMessageAsync(registration, chat,
            "A queued message could not be completed. It was removed from the waiting queue. Please check the session and send it again when ready.",
            payload["queueTopic"]?.GetValue<string>(), cancellationToken).ConfigureAwait(false);
    }
}
