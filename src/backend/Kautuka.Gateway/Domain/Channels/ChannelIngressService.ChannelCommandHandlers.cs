namespace Kautuka.Gateway.Domain.Channels;

internal sealed partial class ChannelIngressService
{
    private async Task<ChannelIngressResult?> TryHandleChannelCommandAsync(
        string registrationId,
        GatewayChannelRegistration registration,
        ChannelInboundEnvelope envelope,
        CancellationToken cancellationToken)
    {
        string text = envelope.MessageText.Trim() switch
        {
            "👥 Agents" => "/agents",
            "🗂 Sessions" => "/sessions",
            "➕ New session" => "/new",
            "ℹ️ Current context" => "/context",
            "📥 Queue" => "/queue",
            "🕒 Queue" => "/queue",
            _ => envelope.MessageText.Trim(),
        };
        if (!text.StartsWith('/'))
        {
            return null;
        }

        string[] parts = text.Split((char[]?)null, 2, StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        string commandToken = parts[0];
        string command = commandToken.Split('@', 2)[0].ToUpperInvariant();

        // Retired /to must be rejected explicitly so it cannot fall through and run as
        // prompt text against the currently selected agent.
        if (command == "/TO")
        {
            const string removedMessage = "The /to command has been removed. Choose an agent with Agents or /agents, then send your prompt.";
            await outboundMessagePublisher.PublishTextMessageAsync(
                new ChannelOutboundTextMessage(registration.ChannelType, registration.Id, envelope.ChatId, envelope.MessageThreadId, removedMessage),
                cancellationToken).ConfigureAwait(false);
            return new ChannelIngressResult(false, "unsupported_command", "The /to command has been removed.");
        }

        if (command is "/SESSION" or "/SESSIONS")
        {
            await PublishSessionsAsync(registration, envelope, 0, cancellationToken).ConfigureAwait(false);
            return new ChannelIngressResult(true, "sessions_shown", "Session picker shown.");
        }

        if (command == "/QUEUE")
        {
            string? topic = ChannelThreadRouting.ProviderTopic(envelope.ChatId, envelope.MessageThreadId);
            int queued = await PublishBotQueueAsync(registration, registration.OwnerUserId, envelope.ChatId, topic,
                0,
                onlyIfChanged: false,
                bringToFront: true,
                cancellationToken).ConfigureAwait(false);
            if (queued <= 1)
                await outboundMessagePublisher.PublishTextMessageAsync(
                    new ChannelOutboundTextMessage(registration.ChannelType, registration.Id, envelope.ChatId,
                        envelope.MessageThreadId, queued == 0 ? "No waiting messages." : "One message is waiting. Queue controls appear when any session has more than one waiting message."),
                    cancellationToken).ConfigureAwait(false);
            return new ChannelIngressResult(true, "queue_shown", queued <= 1 ? "No queue card shown." : "Queue shown.");
        }

        if (command == "/START")
        {
            // Starting a bot chat signals that a deleted or newly opened
            // conversation can be provisioned now instead of waiting for the
            // periodic owner-target reconciliation pass.
            _ = ownerTargetSyncNotifier.TryNotify(registration.OwnerUserId, "channel-start-command");
            const string startMessage = "Kautuka is ready. Use the keyboard below to choose an agent, switch sessions, start a new session, or check where your next prompt will go. Use /help for commands.";
            await outboundMessagePublisher.PublishTextMessageAsync(
                new ChannelOutboundTextMessage(registration.ChannelType, registration.Id, envelope.ChatId, envelope.MessageThreadId, startMessage),
                cancellationToken).ConfigureAwait(false);
            bool sent = true;
            return sent
                ? new ChannelIngressResult(true, "ok", "Start command handled.")
                : new ChannelIngressResult(false, "delivery_failed", "Failed to deliver start response.");
        }

        if (command == "/HELP")
        {
            const string helpMessage = "Choose Agents, Sessions, New session, or Current context from the keyboard below.\n\nCommands:\n/agents — choose an agent\n/sessions — view and switch sessions\n/new — start a new session\n/context — show your current agent and session\n/queue — view queued messages\n/compact — compact the current session\n/task <message> — start a focused transient thread\n/help — show this help";
            await outboundMessagePublisher.PublishTextMessageAsync(
                new ChannelOutboundTextMessage(registration.ChannelType, registration.Id, envelope.ChatId, envelope.MessageThreadId, helpMessage),
                cancellationToken).ConfigureAwait(false);
            bool sent = true;
            return sent
                ? new ChannelIngressResult(true, "ok", "Help command handled.")
                : new ChannelIngressResult(false, "delivery_failed", "Failed to deliver help response.");
        }

        if (command is "/WHOAMI" or "/CONTEXT")
        {
            GatewayChannelTargetBinding? effectiveBinding = routingCoordinator.ResolveTargetBinding(registration, envelope.ExternalUserId, envelope.MessageThreadId);
            string targetType = effectiveBinding?.TargetType ?? "agent";
            string targetId = effectiveBinding?.TargetId ?? string.Empty;
            string displayName = routingCoordinator.ResolveTargetDisplayName(targetType, targetId);
            string sessionLabel = "No session selected";
            if (effectiveBinding is not null)
            {
                string conversationKey = ChannelThreadRouting.ConversationKey(registration, envelope.ChatId,
                    envelope.ProviderThreadId, effectiveBinding.TargetType, effectiveBinding.TargetId);
                ChannelConversationMapEntry? currentMap = conversationMapStore.TryResolve(
                    registration.ChannelType, registration.Id, envelope.ChatId, conversationKey);
                if (!string.IsNullOrWhiteSpace(currentMap?.SessionId)
                    && sessionStore.TryGetSession(currentMap.SessionId, out GatewaySession? currentSession)
                    && currentSession is not null)
                {
                    sessionLabel = currentSession.Title;
                }
            }
            await outboundMessagePublisher.PublishTextMessageAsync(
                new ChannelOutboundTextMessage(
                    registration.ChannelType,
                    registration.Id,
                    envelope.ChatId,
                    envelope.MessageThreadId,
                    $"Sending to {displayName}\nSession: {sessionLabel}\nUse Sessions to switch sessions or Agents to change agent."),
                cancellationToken).ConfigureAwait(false);
            bool sent = true;

            return sent
                ? new ChannelIngressResult(true, "ok", "Whoami command handled.")
                : new ChannelIngressResult(false, "delivery_failed", "Failed to deliver /context response.");
        }

        if (command is "/TARGET" or "/TARGETS" or "/AGENTS")
        {
            await PublishTargetPickerAsync(registration, envelope, cancellationToken).ConfigureAwait(false);
            return new ChannelIngressResult(true, "ok", "Persistent target picker shown.");
        }

        if (command == "/TASK")
        {
            bool hasTaskMessage = parts.Length > 1 && !string.IsNullOrWhiteSpace(parts[1]);
            if (hasTaskMessage)
            {
                return null;
            }

            const string usage = "Usage: /task <message>";
            await outboundMessagePublisher.PublishTextMessageAsync(
                new ChannelOutboundTextMessage(registration.ChannelType, registration.Id, envelope.ChatId, envelope.MessageThreadId, usage),
                cancellationToken).ConfigureAwait(false);
            bool sent = true;
            return sent
                ? new ChannelIngressResult(true, "ok", "Task command usage sent.")
                : new ChannelIngressResult(false, "delivery_failed", "Failed to deliver /task usage.");
        }

        if (command == "/NEW")
        {
            GatewayChannelTargetBinding? newBinding = routingCoordinator.ResolveTargetBinding(registration, envelope.ExternalUserId, envelope.MessageThreadId);
            if (newBinding is null)
            {
                await outboundMessagePublisher.PublishTextMessageAsync(
                    new ChannelOutboundTextMessage(registration.ChannelType, registration.Id, envelope.ChatId, envelope.MessageThreadId, "No agent is configured yet. Configure a default agent in Settings or use /agents."),
                    cancellationToken).ConfigureAwait(false);
                bool noTargetSent = true;
                return noTargetSent
                    ? new ChannelIngressResult(true, "ok", "No-target new-session response sent.")
                    : new ChannelIngressResult(false, "delivery_failed", "Failed to deliver no-target response.");
            }

            GatewaySession newSession = await sessionLifecycleService.CreateSessionAsync(newBinding.TargetType, newBinding.TargetId, registration.OwnerUserId, cancellationToken, registration.ChannelType).ConfigureAwait(false);
            BindChannelConversationToSession(
                registrationId,
                newBinding.TargetType,
                newBinding.TargetId,
                envelope,
                newSession.Id);
            BindSelectedSessionAcrossLinkedChannels(registration, envelope,
                newBinding.TargetType, newBinding.TargetId, newSession.Id);
            string? messageId = envelope.CallbackTransportMessageId
                ?? envelope.CallbackMessageId?.ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (!string.IsNullOrWhiteSpace(messageId))
                await DeleteChannelMessageAsync(registration, envelope.ChatId, messageId, cancellationToken).ConfigureAwait(false);
            queueViews.Close(registration.OwnerUserId, ChannelQueueViewStore.RegistrationScope(registration) + ":session",
                envelope.ChatId, ChannelThreadRouting.ProviderTopic(envelope.ChatId, envelope.MessageThreadId));
            string? topic = ChannelThreadRouting.ProviderTopic(envelope.ChatId,
                ResolveOutboundThreadId(registration, newBinding, envelope.ChatId, envelope.MessageThreadId));
            string agentName = routingCoordinator.ResolveTargetDisplayName(newBinding.TargetType, newBinding.TargetId);
            string sessionName = SessionLabel(newSession);
            string shortId = newSession.Id.Length > 6 ? newSession.Id[^6..] : newSession.Id;
            await outboundMessagePublisher.PublishTextMessageAsync(new ChannelOutboundTextMessage(
                registration.ChannelType,
                registration.Id,
                envelope.ChatId,
                topic,
                $"New session started\nAgent: {agentName}\nSession: {sessionName} · {shortId}\nYour next prompt goes here."),
                cancellationToken).ConfigureAwait(false);
            return new ChannelIngressResult(true, "ok", "New session created.", newSession.Id, newBinding.TargetType, newBinding.TargetId);
        }

        if (command == "/COMPACT")
        {
            GatewayChannelTargetBinding? compactTarget = routingCoordinator.ResolveTargetBinding(registration, envelope.ExternalUserId, envelope.MessageThreadId);
            ChannelConversationMapEntry? compactMap = compactTarget is null ? null : routingCoordinator.ResolveConversationMap(registrationId, compactTarget, envelope);
            if (compactMap is null || string.IsNullOrWhiteSpace(compactMap.SessionId))
            {
                await outboundMessagePublisher.PublishTextMessageAsync(
                    new ChannelOutboundTextMessage(registration.ChannelType, registration.Id, envelope.ChatId, envelope.MessageThreadId, "No active session to compact."),
                    cancellationToken).ConfigureAwait(false);
                bool noSessionSent = true;
                return noSessionSent
                    ? new ChannelIngressResult(true, "ok", "No-session compact response sent.")
                    : new ChannelIngressResult(false, "delivery_failed", "Failed to deliver no-session response.");
            }

            _ = await sessionCompactionService.CompactAsync(compactMap.SessionId, "manual", cancellationToken).ConfigureAwait(false);
            await outboundMessagePublisher.PublishTextMessageAsync(
                new ChannelOutboundTextMessage(registration.ChannelType, registration.Id, envelope.ChatId, envelope.MessageThreadId, "ðŸ“¦ Session compacted. Context will be trimmed in future runs."),
                cancellationToken).ConfigureAwait(false);
            bool compactSent = true;
            return compactSent
                ? new ChannelIngressResult(true, "ok", "Session compacted.", compactMap.SessionId)
                : new ChannelIngressResult(false, "delivery_failed", "Session compacted but failed to deliver confirmation.");
        }

        return null;
    }

}
