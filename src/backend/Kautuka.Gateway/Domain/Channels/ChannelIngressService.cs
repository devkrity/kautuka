using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;

using Grpc.Core;

using Kautuka.Gateway.Domain.Assets;
using Kautuka.Gateway.Domain.Execution;
using Kautuka.Gateway.Domain.Hitl;
using Kautuka.Gateway.Domain.Sessions;
using Kautuka.Gateway.Domain.Stores;
using Kautuka.Gateway.Infrastructure.Channels;

using Mediator;

using Narad.Contracts;

namespace Kautuka.Gateway.Domain.Channels;

internal sealed class ChannelIngressInfrastructure(
    IHttpClientFactory httpClientFactory,
    IChannelOutboxStore outboxStore,
    IChannelOutboundMessagePublisher outboundMessagePublisher,
    IConversationAssetStore assetStore,
    IChannelHostGrpcService channelHost,
    IGatewayApprovalStore approvalStore,
    IApprovalResolutionService approvalResolutionService,
    IThreadCommandService threadCommands,
    IMediator mediator,
    IChannelRuntimeEventProjector runtimeEventProjector,
    IChannelHostOwnerTargetSyncNotifier ownerTargetSyncNotifier)
{
    public IHttpClientFactory HttpClientFactory { get; } = httpClientFactory;
    public IChannelOutboxStore OutboxStore { get; } = outboxStore;
    public IChannelOutboundMessagePublisher OutboundMessagePublisher { get; } = outboundMessagePublisher;
    public IConversationAssetStore AssetStore { get; } = assetStore;
    public IChannelHostGrpcService ChannelHost { get; } = channelHost;
    public IGatewayApprovalStore ApprovalStore { get; } = approvalStore;
    public IApprovalResolutionService ApprovalResolutionService { get; } = approvalResolutionService;
    public IThreadCommandService ThreadCommands { get; } = threadCommands;
    public IMediator Mediator { get; } = mediator;
    public IChannelRuntimeEventProjector RuntimeEventProjector { get; } = runtimeEventProjector;
    public IChannelHostOwnerTargetSyncNotifier OwnerTargetSyncNotifier { get; } = ownerTargetSyncNotifier;
}

internal sealed class ChannelIngressRoutingInfrastructure(
    IGatewaySessionStore sessionStore,
    ISessionCompactionService sessionCompactionService,
    ISessionLifecycleService sessionLifecycleService,
    IChannelConversationMapStore conversationMapStore,
    IChannelAttachmentRegistry attachmentRegistry,
    IChannelRoutingCoordinator routingCoordinator,
    IGatewayWorkflowStore workflowStore)
{
    public IGatewaySessionStore SessionStore { get; } = sessionStore;
    public ISessionCompactionService SessionCompactionService { get; } = sessionCompactionService;
    public ISessionLifecycleService SessionLifecycleService { get; } = sessionLifecycleService;
    public IChannelConversationMapStore ConversationMapStore { get; } = conversationMapStore;
    public IChannelAttachmentRegistry AttachmentRegistry { get; } = attachmentRegistry;
    public IChannelRoutingCoordinator RoutingCoordinator { get; } = routingCoordinator;
    public IGatewayWorkflowStore WorkflowStore { get; } = workflowStore;
}

internal sealed partial class ChannelIngressService : IChannelIngressService, ISessionTurnProjection
{
    private static readonly TimeSpan transientThreadTtl = TimeSpan.FromHours(8);
    private static readonly TimeSpan processedInboundRetention = TimeSpan.FromHours(1);
    private static readonly TimeSpan processedInboundCleanupInterval = TimeSpan.FromMinutes(10);

    private readonly GatewayState state;
    private readonly IChatGatewayService chatGatewayService;
    private readonly IGatewaySessionStore sessionStore;
    private readonly ISessionCompactionService sessionCompactionService;
    private readonly ISessionLifecycleService sessionLifecycleService;
    private readonly IChannelConversationMapStore conversationMapStore;
    private readonly IChannelAttachmentRegistry attachmentRegistry;
    private readonly IChannelRoutingCoordinator routingCoordinator;
    private readonly IGatewayWorkflowStore workflowStore;
    internal IGatewayWorkflowStore WorkflowStore => workflowStore;
    private readonly IChannelOutboxStore outboxStore;
    private readonly IChannelOutboundMessagePublisher outboundMessagePublisher;
    private readonly IConversationAssetStore assetStore;
    private readonly IChannelHostGrpcService channelHost;
    private readonly IGatewayApprovalStore approvalStore;
    private readonly IThreadCommandService threadCommands;
    private readonly IMediator mediator;
    private readonly IChannelRuntimeEventProjector runtimeEventProjector;
    private readonly IChannelHostOwnerTargetSyncNotifier ownerTargetSyncNotifier;
    private readonly ILogger<ChannelIngressService> logger;
    private readonly SessionTurnQueueStore pendingTurns;
    private readonly ChannelQueueViewStore queueViews;
    private readonly ChannelFactPublisher factPublisher;
    private readonly ConcurrentDictionary<string, DateTimeOffset> processedInboundUpdates = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim[] approvalResolutionLocks = Enumerable.Range(0, 64)
        .Select(static _ => new SemaphoreSlim(1, 1))
        .ToArray();
    private readonly ChannelTargetSelectionState targetSelections = new();
    private DateTimeOffset lastInboundCleanupAt = DateTimeOffset.MinValue;

    public ChannelIngressService(
        GatewayState state,
        IChatGatewayService chatGatewayService,
        ChannelIngressInfrastructure infrastructure,
        ChannelIngressRoutingInfrastructure routingInfrastructure,
        ILogger<ChannelIngressService> logger)
    {
        this.state = state;
        pendingTurns = new SessionTurnQueueStore(state.Database);
        queueViews = new ChannelQueueViewStore(state.Database);
        this.chatGatewayService = chatGatewayService;
        sessionStore = routingInfrastructure.SessionStore;
        sessionCompactionService = routingInfrastructure.SessionCompactionService;
        sessionLifecycleService = routingInfrastructure.SessionLifecycleService;
        conversationMapStore = routingInfrastructure.ConversationMapStore;
        attachmentRegistry = routingInfrastructure.AttachmentRegistry;
        routingCoordinator = routingInfrastructure.RoutingCoordinator;
        workflowStore = routingInfrastructure.WorkflowStore;
        outboxStore = infrastructure.OutboxStore;
        outboundMessagePublisher = infrastructure.OutboundMessagePublisher;
        assetStore = infrastructure.AssetStore;
        channelHost = infrastructure.ChannelHost;
        approvalStore = infrastructure.ApprovalStore;
        threadCommands = infrastructure.ThreadCommands;
        mediator = infrastructure.Mediator;
        runtimeEventProjector = infrastructure.RuntimeEventProjector;
        ownerTargetSyncNotifier = infrastructure.OwnerTargetSyncNotifier;
        factPublisher = new ChannelFactPublisher(
            outboxStore,
            sessionStore,
            approvalStore,
            routingCoordinator,
            queueViews,
            pendingTurns,
            state,
            attachmentRegistry);
        this.logger = logger;
    }

    public Task<ChannelIngressResult> ProcessInboundEventAsync(ChannelInboundEvent evt, CancellationToken cancellationToken)
        => ProcessInboundEventCoreAsync(evt, false, cancellationToken);

    public async Task<ChannelIngressResult> AdmitInboundEventAsync(ChannelInboundEvent evt, CancellationToken cancellationToken)
    {
        // HTTP admission can race transport admission before a chat has its
        // first session. Serialize only acceptance, never the model execution.
        // Use the bot/chat scope so competing entry points cannot create competing sessions.
        string key = string.Join('\0', "channel-admission", evt.RegistrationId, evt.ConversationId);
        using IDisposable admissionLease = await SessionExecutionGate.EnterAsync(key, cancellationToken).ConfigureAwait(false);
        return await ProcessInboundEventCoreAsync(evt, true, cancellationToken).ConfigureAwait(false);
    }

    private async Task<ChannelIngressResult> ProcessInboundEventCoreAsync(ChannelInboundEvent evt, bool admissionOnly, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(evt.OwnerUserId))
        {
            return new ChannelIngressResult(false, "registration_owner_missing", "The admitted Channel event did not include its registration owner.");
        }
        await attachmentRegistry.RefreshAttachmentAsync(evt.RegistrationId, evt.OwnerUserId, cancellationToken).ConfigureAwait(false);
        if (!attachmentRegistry.TryGetRegistration(evt.RegistrationId, out GatewayChannelRegistration? registration))
        {
            return new ChannelIngressResult(false, "registration_not_found", "Active Channel registration was not found.");
        }

        if (registration is null
            || !registration.IsActive
            || !string.Equals(registration.ChannelType, evt.ChannelType, StringComparison.OrdinalIgnoreCase))
        {
            return new ChannelIngressResult(false, "registration_not_found", "Active Channel registration was not found.");
        }

        ChannelInboundEnvelope? envelope = CreateChannelEnvelope(evt);
        if (envelope is null)
        {
            return new ChannelIngressResult(false, "unsupported_event", "Inbound event payload was not recognized.");
        }

        if (string.Equals(registration.ChannelType, "slack", StringComparison.OrdinalIgnoreCase))
        {
            string? ownerSlackUserId = registration.Settings.GetStringNodeValue("slack_owner_user_id");
            string? ownerDmChannelId = registration.Settings.GetStringNodeValue("slack_dm_channel_id");
            if (!string.IsNullOrWhiteSpace(ownerSlackUserId)
                && (!string.Equals(envelope.ChatId, ownerDmChannelId, StringComparison.Ordinal)
                    || !string.Equals(envelope.ExternalUserId, ownerSlackUserId, StringComparison.Ordinal)))
            {
                if (envelope.IsCallback)
                    await AnswerChannelCallbackAsync(registration, envelope.CallbackQueryId,
                        "This Kautuka assistant is private.", cancellationToken).ConfigureAwait(false);
                return new ChannelIngressResult(false, "slack_not_owner_dm", "Slack is restricted to the linked owner's direct message.");
            }
        }

        try
        {
            return await ProcessChannelEnvelopeAsync(evt.RegistrationId, registration, envelope, admissionOnly, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            processedInboundUpdates.TryRemove($"{evt.RegistrationId}:{envelope.UpdateId}", out _);
            throw;
        }
    }

    private async Task<ChannelIngressResult> ProcessChannelEnvelopeAsync(
        string registrationId,
        GatewayChannelRegistration registration,
        ChannelInboundEnvelope envelope,
        bool admissionOnly,
        CancellationToken cancellationToken)
    {
        ChannelIngressResult? duplicateResult = await TryHandleDuplicateChannelUpdateAsync(registrationId, registration, envelope, cancellationToken).ConfigureAwait(false);
        if (duplicateResult is not null)
        {
            return duplicateResult;
        }

        if (string.Equals(registration.ChannelType, "slack", StringComparison.OrdinalIgnoreCase)
            && envelope.ChatId.StartsWith('D')
            && await attachmentRegistry.CompleteSlackPairingAsync(
                registrationId,
                registration.OwnerUserId,
                envelope.ExternalUserId,
                envelope.ChatId,
                envelope.MessageText.Trim(),
                cancellationToken).ConfigureAwait(false))
        {
            string providerDisplayName = registration.ChannelType.ToUpperInvariant() switch
            {
                "SLACK" => "Slack",
                "TELEGRAM" => "Telegram",
                _ => registration.ChannelType,
            };
            await outboundMessagePublisher.PublishTextMessageAsync(
                new ChannelOutboundTextMessage(registration.ChannelType, registrationId, envelope.ChatId, envelope.MessageThreadId, $"Your {providerDisplayName} account is linked to Kautuka. You can send messages here now."),
                cancellationToken).ConfigureAwait(false);
            return new ChannelIngressResult(true, $"{registration.ChannelType}_account_paired", $"{providerDisplayName} account pairing completed.");
        }

        GatewayChannelIdentityLink? identity = ResolveChannelIdentity(registrationId, envelope);

        if (identity is null)
        {
            return await BuildChannelIdentityUnmappedResultAsync(registration, envelope, cancellationToken).ConfigureAwait(false);
        }

        return envelope.IsCallback
            ? await ProcessChannelCallbackAsync(registration, registrationId, identity, envelope, cancellationToken).ConfigureAwait(false)
            : await ProcessChannelMessageAsync(registration, registrationId, identity, envelope, cancellationToken, admissionOnly).ConfigureAwait(false);
    }

    private async Task<ChannelIngressResult?> TryHandleDuplicateChannelUpdateAsync(
        string registrationId,
        GatewayChannelRegistration registration,
        ChannelInboundEnvelope envelope,
        CancellationToken cancellationToken)
    {
        if (!IsDuplicateInboundUpdate(registrationId, envelope.UpdateId))
        {
            return null;
        }

        if (envelope.IsCallback)
        {
            await AnswerChannelCallbackAsync(registration, envelope.CallbackQueryId, "This action was already processed.", cancellationToken).ConfigureAwait(false);
        }

        return new ChannelIngressResult(true, "ignored_duplicate", "Duplicate Channel update ignored.");
    }

    private static bool TryParseViewAction(string? callbackAction, out string viewId, out string action)
    {
        viewId = string.Empty;
        action = string.Empty;
        if (string.IsNullOrWhiteSpace(callbackAction))
        {
            return false;
        }

        string[] parts = callbackAction.Split(':', 3, StringSplitOptions.TrimEntries);
        if (parts.Length != 3
            || !string.Equals(parts[0], "v", StringComparison.Ordinal)
            || string.IsNullOrWhiteSpace(parts[1])
            || string.IsNullOrWhiteSpace(parts[2]))
        {
            return false;
        }

        viewId = parts[1];
        action = parts[2];
        return true;
    }

    private GatewayChannelIdentityLink? ResolveChannelIdentity(string registrationId, ChannelInboundEnvelope envelope)
    {
        return attachmentRegistry.TryResolveIdentity(registrationId, envelope.ExternalUserId, envelope.ChatId, out GatewayChannelIdentityLink? identity)
            ? identity
            : null;
    }

    private async Task<ChannelIngressResult> BuildChannelIdentityUnmappedResultAsync(
        GatewayChannelRegistration registration,
        ChannelInboundEnvelope envelope,
        CancellationToken cancellationToken)
    {
        if (envelope.IsCallback)
        {
            await AnswerChannelCallbackAsync(registration, envelope.CallbackQueryId, "Channel account is not linked for this bot.", cancellationToken).ConfigureAwait(false);
        }

        return new ChannelIngressResult(false, "identity_unmapped", "Channel user is not linked to a Kautuka user for this registration.");
    }

    private async Task<ChannelIngressResult> ProcessChannelCallbackAsync(
        GatewayChannelRegistration registration,
        string registrationId,
        GatewayChannelIdentityLink identity,
        ChannelInboundEnvelope envelope,
        CancellationToken cancellationToken)
    {
        if (ChannelBotCommandCatalogue.TryGetQuickActionCommand(envelope.MessageText, out string quickCommand))
        {
            await AnswerChannelCallbackAsync(registration, envelope.CallbackQueryId, "Opening Kautuka action.", cancellationToken).ConfigureAwait(false);
            ChannelInboundEnvelope commandEnvelope = envelope with
            {
                IsCallback = false,
                MessageText = quickCommand,
                CallbackMessageId = null,
                CallbackTransportMessageId = null,
            };
            return await TryHandleChannelCommandAsync(registrationId, registration, commandEnvelope, cancellationToken).ConfigureAwait(false)
                ?? new ChannelIngressResult(false, "unsupported_command", "This quick action is not supported.");
        }

        if (envelope.MessageText.StartsWith("sessions:", StringComparison.Ordinal))
        {
            string callbackNotice = envelope.MessageText switch
            {
                "sessions:close" => "Session list closed.",
                "sessions:new" => "Starting a new session.",
                _ => "Sessions",
            };
            await AnswerChannelCallbackAsync(registration, envelope.CallbackQueryId, callbackNotice, cancellationToken).ConfigureAwait(false);
            if (envelope.MessageText == "sessions:close")
            {
                string? messageId = envelope.CallbackTransportMessageId
                    ?? envelope.CallbackMessageId?.ToString(System.Globalization.CultureInfo.InvariantCulture);
                if (!string.IsNullOrWhiteSpace(messageId))
                    await DeleteChannelMessageAsync(registration, envelope.ChatId, messageId, cancellationToken).ConfigureAwait(false);
                queueViews.Close(identity.UserId, ChannelQueueViewStore.RegistrationScope(registration) + ":session",
                    envelope.ChatId, ChannelThreadRouting.ProviderTopic(envelope.ChatId, envelope.MessageThreadId));
                return new ChannelIngressResult(true, "sessions_closed", "Session picker closed.");
            }
            if (envelope.MessageText == "sessions:new")
            {
                queueViews.Close(identity.UserId, ChannelQueueViewStore.RegistrationScope(registration) + ":session",
                    envelope.ChatId, ChannelThreadRouting.ProviderTopic(envelope.ChatId, envelope.MessageThreadId));
                return await TryHandleChannelCommandAsync(registrationId, registration, envelope with { MessageText = "/new" }, cancellationToken).ConfigureAwait(false)
                    ?? new ChannelIngressResult(false, "session_unavailable", "Session unavailable.");
            }
            if (int.TryParse(envelope.MessageText[9..], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out int page))
                await PublishSessionsAsync(registration, envelope, page, cancellationToken, bringToFront: false).ConfigureAwait(false);
            return new ChannelIngressResult(true, "sessions_shown", "Session picker shown.");
        }
        ChannelIngressResult? queueAction = await TryHandleQueueCallbackAsync(registration, identity, envelope, cancellationToken).ConfigureAwait(false);
        if (queueAction is not null) return queueAction;
        ChannelIngressResult? agentPickerCallbackResult = await TryHandleAgentPickerCallbackAsync(registration, registrationId, envelope, cancellationToken).ConfigureAwait(false);
        if (agentPickerCallbackResult is not null)
        {
            return agentPickerCallbackResult;
        }

        if (TryParseViewAction(envelope.CallbackAction, out string viewId, out string viewAction))
        {
            GatewayChannelTargetBinding? originBinding = null;
            string? originSessionId = null;
            if (string.Equals(viewAction, "reply", StringComparison.OrdinalIgnoreCase))
            {
                originSessionId = new ChannelPromptAnchorStore(state.Database).ResolveViewSession(
                    registration, envelope.ChatId, envelope.MessageThreadId, viewId);
                GatewaySession? originSession = originSessionId is not null
                    && sessionStore.TryGetSession(originSessionId, out GatewaySession? foundSession)
                    ? foundSession
                    : null;
                if (originSession is not null && originSession.OwnerUserId == identity.UserId && originSession.ArchivedAt is null)
                {
                    originBinding = attachmentRegistry.GetBindings(registration.Id).FirstOrDefault(binding =>
                        string.Equals(binding.TargetType, originSession.TargetType, StringComparison.OrdinalIgnoreCase)
                        && string.Equals(binding.TargetId, originSession.TargetId, StringComparison.Ordinal));
                }

                if (originSessionId is null)
                {
                    await AnswerChannelCallbackAsync(registration, envelope.CallbackQueryId,
                        "This email card can't be answered from this channel. No reply was sent.", cancellationToken).ConfigureAwait(false);
                    return new ChannelIngressResult(false, "view_session_unavailable", "This email card can't be answered from this channel. No reply was sent.");
                }
                if (originSession is null || originSession.OwnerUserId != identity.UserId || originSession.ArchivedAt is not null)
                {
                    await AnswerChannelCallbackAsync(registration, envelope.CallbackQueryId,
                        "The agent session for this email card is no longer available. No reply was sent.", cancellationToken).ConfigureAwait(false);
                    return new ChannelIngressResult(false, "view_session_unavailable", "The originating email session could not be verified.");
                }
                if (originBinding is null)
                {
                    await AnswerChannelCallbackAsync(registration, envelope.CallbackQueryId,
                        "This email card can't be answered from this channel. No reply was sent.", cancellationToken).ConfigureAwait(false);
                    return new ChannelIngressResult(false, "view_session_unavailable", "This email card can't be answered from this channel. No reply was sent.");
                }
            }

            await AnswerChannelCallbackAsync(registration, envelope.CallbackQueryId, viewAction, cancellationToken).ConfigureAwait(false);
            ChannelInboundEnvelope message = envelope with
            {
                IsCallback = false,
                MessageText = $"View action {viewAction} on {viewId}.",
            };
            return await ProcessChannelMessageAsync(registration, registrationId, identity, message, cancellationToken,
                admissionOnly: true, fixedTargetBinding: originBinding, fixedSessionId: originSessionId).ConfigureAwait(false);
        }

        return await ResolveInteractionCallbackAsync(registration, registrationId, identity, envelope, cancellationToken).ConfigureAwait(false);
    }

    private async Task<ChannelIngressResult> ProcessChannelMessageAsync(
        GatewayChannelRegistration registration,
        string registrationId,
        GatewayChannelIdentityLink identity,
        ChannelInboundEnvelope envelope,
        CancellationToken cancellationToken,
        bool admissionOnly = false,
        GatewayChannelTargetBinding? fixedTargetBinding = null,
        string? fixedSessionId = null)
    {
        if (string.IsNullOrWhiteSpace(envelope.MessageText) && envelope.InboundMedia.Count == 0)
        {
            return new ChannelIngressResult(false, "ignored", "No message text was provided.");
        }

        if (ChannelAttachmentIds.TryParse(registration.Id, out ChannelAttachmentIdentity attachmentIdentity)
            && attachmentIdentity.Role is ChannelAttachmentRole.Workflows or ChannelAttachmentRole.Workflow)
        {
            const string notice = "This bot delivers workflow reviews. Use the card to approve, request changes, or stop.";
            bool delivered = await SendChannelMessageAsync(
                registration,
                envelope.ChatId,
                notice,
                envelope.MessageThreadId,
                cancellationToken).ConfigureAwait(false);
            return delivered
                ? new ChannelIngressResult(true, "ok", "Workflow bot does not start chat.")
                : new ChannelIngressResult(false, "delivery_failed", "Workflow bot notice failed to send.");
        }

        ChannelIngressResult? commandResult = await TryHandleChannelCommandAsync(registrationId, registration, envelope, cancellationToken).ConfigureAwait(false);
        if (commandResult is not null)
        {
            return commandResult;
        }

        GatewayChannelTargetBinding? targetBinding = fixedTargetBinding is null
            ? routingCoordinator.ResolveTargetBinding(registration, envelope.ExternalUserId, envelope.MessageThreadId)
            : attachmentRegistry.GetBindings(registration.Id).FirstOrDefault(binding => binding.Id == fixedTargetBinding.Id);
        if (targetBinding is null)
        {
            if (HasExplicitThreadContext(envelope.MessageThreadId))
            {
                _ = ownerTargetSyncNotifier.TryNotify(registration.OwnerUserId, "unmapped-channel-thread");
                await SendChannelMessageAsync(registration, envelope.ChatId,
                    "I couldn't safely route this prompt because this conversation thread isn't linked to an active agent. No run was started. Please retry from the agent's current conversation.",
                    envelope.MessageThreadId, cancellationToken).ConfigureAwait(false);
                return new ChannelIngressResult(false, "target_missing", "No active target is bound for this thread/topic; target reconciliation was requested.");
            }

            bool delivered = await SendChannelMessageAsync(
                registration,
                envelope.ChatId,
                "No target is currently bound for this bot. Ask an admin to bind an agent or use the target picker command first.",
                envelope.MessageThreadId,
                cancellationToken).ConfigureAwait(false);

            return delivered
                ? new ChannelIngressResult(true, "ok", "Target missing notice sent.")
                : new ChannelIngressResult(false, "delivery_failed", "Target missing notice failed to send.");
        }

        string? outboundThreadId = ResolveOutboundThreadId(registration, targetBinding, envelope.ChatId, envelope.MessageThreadId);
        conversationMapStore.CleanupExpiredTransientThreads(DateTimeOffset.UtcNow);
        ChannelConversationMapEntry conversationMap = routingCoordinator.ResolveConversationMap(registrationId, targetBinding, envelope);
        if (fixedSessionId is not null)
        {
            GatewaySession? fixedSession = AccessibleSession(identity.UserId, fixedSessionId, targetBinding);
            if (fixedSession is null)
            {
                return new ChannelIngressResult(false, "view_session_unavailable", "The originating email session could not be verified.");
            }
            // Replying to a view continues its source session without changing the channel's selected session.
            conversationMap = conversationMap with { SessionId = fixedSession.Id };
        }
        if (envelope.ReplySourceMessageId is not null)
        {
            string? replySessionId = new ChannelPromptAnchorStore(state.Database).ResolveMessageSession(registration,
                envelope.ChatId, outboundThreadId, envelope.ReplySourceMessageId);
            GatewaySession? replySession = AccessibleSession(identity.UserId, replySessionId, targetBinding);
            if (replySession is null)
            {
                await SendChannelMessageAsync(registration, envelope.ChatId,
                    "I couldn't identify an available session for that reply. Your prompt has not run. Use /sessions to choose a session, then resend without quoting that message.",
                    outboundThreadId, cancellationToken).ConfigureAwait(false);
                return new ChannelIngressResult(false, "reply_session_unavailable", "Explicit reply destination could not be verified.");
            }
            // This prompt uses the quoted session; it does not change the selected session.
            conversationMap = conversationMap with { SessionId = replySession.Id };
        }
        ChannelConversationMapEntry resolvedConversationMap = conversationMap;
        IReadOnlyList<string> assetIds = [];

        if (envelope.InboundMedia.Count > 0)
        {
            GatewaySession session = await sessionLifecycleService.EnsureSessionAsync(
                targetBinding.TargetType,
                targetBinding.TargetId,
                conversationMap.SessionId ?? string.Empty,
                identity.UserId,
                cancellationToken,
                registration.ChannelType).ConfigureAwait(false);

            ChannelConversationMapEntry? attachedMap = envelope.ReplySourceMessageId is not null
                ? conversationMap with { SessionId = session.Id } : conversationMapStore.AttachSession(
                conversationMap.Id,
                session.Id,
                string.Equals(conversationMap.ConversationKind, "transient_thread", StringComparison.Ordinal)
                    ? DateTimeOffset.UtcNow.Add(transientThreadTtl)
                    : null);
            resolvedConversationMap = attachedMap ?? conversationMap with { SessionId = session.Id };

            try
            {
                assetIds = await CreateChannelInboundAssetsAsync(registrationId, identity.UserId, resolvedConversationMap.SessionId!, envelope, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is InvalidOperationException or IOException or RpcException)
            {
                bool delivered = await SendChannelMessageAsync(
                    registration,
                    envelope.ChatId,
                    "One or more attachments could not be ingested. Please try again.",
                    outboundThreadId,
                    cancellationToken).ConfigureAwait(false);

                if (string.IsNullOrWhiteSpace(envelope.MessageText))
                {
                    return delivered
                        ? new ChannelIngressResult(true, "attachment_ingest_failed", "Attachment ingest failure notice sent.")
                        : new ChannelIngressResult(false, "attachment_ingest_failed", "Attachment ingest failure notice failed to send.");
                }
            }
        }

        if (string.IsNullOrWhiteSpace(envelope.MessageText) && assetIds.Count == 0)
        {
            return new ChannelIngressResult(false, "ignored", "No supported message content remained after attachment handling.");
        }

        JsonElement request = CreateChatRequest(targetBinding, resolvedConversationMap, envelope.MessageText, identity.UserId, registration.ChannelType, assetIds);
        var context = new ChannelMessageProcessingContext(
            registration,
            registrationId,
            identity,
            envelope,
            targetBinding,
            resolvedConversationMap,
            request,
            outboundThreadId);

        if (admissionOnly && string.IsNullOrWhiteSpace(resolvedConversationMap.SessionId))
        {
            GatewaySession session = await sessionLifecycleService.EnsureSessionAsync(targetBinding.TargetType, targetBinding.TargetId,
                string.Empty, identity.UserId, cancellationToken, registration.ChannelType).ConfigureAwait(false);
            ChannelConversationMapEntry map = conversationMapStore.AttachSession(resolvedConversationMap.Id, session.Id)
                ?? resolvedConversationMap with { SessionId = session.Id };
            context = context with { ConversationMap = map };
        }
        bool waiting = !string.IsNullOrWhiteSpace(context.ConversationMap.SessionId)
            && (pendingTurns.HasQueued(context.ConversationMap.SessionId) || IsSessionBusy(targetBinding.TargetId, context.ConversationMap.SessionId));
        if (admissionOnly || waiting)
            return await QueueTurnAsync(context, cancellationToken, waiting).ConfigureAwait(false);
        try { return await StreamChannelChatResponseAsync(context, cancellationToken).ConfigureAwait(false); }
        catch (ChannelSessionBusyException) { return await QueueTurnAsync(context, cancellationToken).ConfigureAwait(false); }
    }

    private static bool HasExplicitThreadContext(string? messageThreadId)
        => !string.IsNullOrWhiteSpace(messageThreadId)
            && !string.Equals(messageThreadId, "root", StringComparison.OrdinalIgnoreCase);

    private static string? ResolveOutboundThreadId(
        GatewayChannelRegistration registration,
        GatewayChannelTargetBinding targetBinding,
        string chatId,
        string? messageThreadId)
    {
        return ChannelThreadRouting.OutboundTopic(registration, chatId,
            messageThreadId, targetBinding.TargetType, targetBinding.TargetId);
    }

    private static JsonElement CreateChatRequest(
        GatewayChannelTargetBinding targetBinding,
        ChannelConversationMapEntry conversationMap,
        string messageText,
        string userId,
        string channelType,
        IReadOnlyList<string>? assetIds = null)
    {
        string message = StripTaskDirective(messageText);
        return JsonSerializer.SerializeToElement(
            new ChannelIngressChatRequest(targetBinding.TargetType, targetBinding.TargetId, message, conversationMap.SessionId, channelType, userId, assetIds is { Count: > 0 } ? [.. assetIds] : null),
            KautukaJsonContext.Default.ChannelIngressChatRequest);
    }

    private async Task<ChannelIngressResult> StreamChannelChatResponseAsync(
        ChannelMessageProcessingContext context,
        CancellationToken cancellationToken)
    {
        var responseText = new StringBuilder();
        string resolvedSessionId = context.ConversationMap.SessionId ?? string.Empty;

        using var typingCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        Task typingTask = RunChannelTypingLoopAsync(context.Registration, context.Envelope.ChatId, context.OutboundThreadId, typingCts.Token);

        try
        {
            await foreach (NaradPersistedEvent evt in chatGatewayService.StreamChatAsync(context.Request, cancellationToken: cancellationToken).ConfigureAwait(false))
            {
                ChannelIngressResult? streamResult = await HandleChannelChatStreamEventAsync(
                    context,
                    evt,
                    responseText,
                    resolvedSessionId,
                    cancellationToken).ConfigureAwait(false);

                if (string.Equals(evt.EventType, NaradEventTypes.RunStarted, StringComparison.Ordinal))
                {
                    resolvedSessionId = evt.SessionId;
                }

                if (streamResult is not null)
                {
                    return streamResult;
                }
            }
        }
        finally
        {
            await typingCts.CancelAsync().ConfigureAwait(false);
            try
            {
                await typingTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Expected when processing completes.
            }
        }

        return await FinalizeChannelMessageResponseAsync(
            context,
            responseText,
            resolvedSessionId,
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<ChannelIngressResult?> HandleChannelChatStreamEventAsync(
        ChannelMessageProcessingContext context,
        NaradPersistedEvent evt,
        StringBuilder responseText,
        string resolvedSessionId,
        CancellationToken cancellationToken)
    {
        switch (evt.EventType)
        {
            case NaradEventTypes.RunStarted:
                if (context.Envelope.PromptTransportMessageId is { Length: > 0 } sourceId)
                    new ChannelPromptAnchorStore(state.Database).SaveMessageSession(context.Registration, context.Envelope.ChatId, context.OutboundThreadId, evt.SessionId, sourceId);
                ChannelConversationMapEntry? selectedMap = conversationMapStore.TryResolve(context.Registration.ChannelType,
                    context.RegistrationId, context.Envelope.ChatId, context.ConversationMap.ProviderThreadId);
                // A completion from an older session must not steal a conversation that the
                // user has since selected for another session. This rule is channel-neutral.
                if (selectedMap is null || string.IsNullOrWhiteSpace(selectedMap.SessionId)
                    || string.Equals(selectedMap.SessionId, evt.SessionId, StringComparison.Ordinal))
                    _ = conversationMapStore.AttachSession(
                    context.ConversationMap.Id,
                    evt.SessionId,
                    string.Equals(context.ConversationMap.ConversationKind, "transient_thread", StringComparison.Ordinal)
                        ? DateTimeOffset.UtcNow.Add(transientThreadTtl)
                        : null);
                return null;

            case NaradEventTypes.MessageTextDelta:
                GatewayChatTokenDelta delta = NaradStreamObservation.ReadBodyPayload(evt, KautukaJsonContext.Default.GatewayChatTokenDelta);
                responseText.Append(delta.Delta);
                await ProjectChannelBoundRuntimeEventAsync(context, evt, cancellationToken).ConfigureAwait(false);
                return null;

            case NaradEventTypes.MessageTextCompleted:
            case NaradEventTypes.RunFinished:
                await ProjectChannelBoundRuntimeEventAsync(context, evt, cancellationToken).ConfigureAwait(false);
                return null;

            case NaradEventTypes.InterruptRequested:
                // Keep consuming through run.paused: ChatGatewayService persists the
                // user turn and pending tool state before yielding that terminal event.
                return null;

            case NaradEventTypes.RunPaused:
                return new ChannelIngressResult(true, "awaiting_hitl", "Tool approval requested via Channel.", resolvedSessionId, context.TargetBinding.TargetType, context.TargetBinding.TargetId);

            default:
                return null;
        }
    }

    private ValueTask ProjectChannelBoundRuntimeEventAsync(
        ChannelMessageProcessingContext context,
        NaradPersistedEvent evt,
        CancellationToken cancellationToken)
        => runtimeEventProjector.ProjectBoundAsync(
            evt,
            context.RegistrationId,
            context.Envelope.ChatId,
            context.OutboundThreadId,
            interactionId: NaradStreamObservation.ReadString(evt, "interactionId"),
            cancellationToken);

    private static Task<ChannelIngressResult> FinalizeChannelMessageResponseAsync(
        ChannelMessageProcessingContext context,
        StringBuilder responseText,
        string resolvedSessionId,
        CancellationToken cancellationToken)
    {
        _ = cancellationToken;
        // Finals are delivered by ChannelHost from projected runtime_event envelopes
        // (assistant.message.completed / run.finished) enqueued during stream handling.
        if (responseText.Length == 0)
        {
            return Task.FromResult(new ChannelIngressResult(true, "ok", "No response generated.", resolvedSessionId, context.TargetBinding.TargetType, context.TargetBinding.TargetId));
        }

        return Task.FromResult(new ChannelIngressResult(true, "ok", "Routed Channel message successfully.", resolvedSessionId, context.TargetBinding.TargetType, context.TargetBinding.TargetId));
    }

    private sealed record ChannelMessageProcessingContext(
        GatewayChannelRegistration Registration,
        string RegistrationId,
        GatewayChannelIdentityLink Identity,
        ChannelInboundEnvelope Envelope,
        GatewayChannelTargetBinding TargetBinding,
        ChannelConversationMapEntry ConversationMap,
        JsonElement Request,
        string? OutboundThreadId);
}
