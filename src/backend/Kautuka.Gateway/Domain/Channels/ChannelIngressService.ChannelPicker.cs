namespace Kautuka.Gateway.Domain.Channels;

internal sealed partial class ChannelIngressService
{
    private static string StripTaskDirective(string message)
    {
        string[] parts = message.Trim().Split((char[]?)null, 2, StringSplitOptions.RemoveEmptyEntries);
        return parts.Length == 2 && ChannelCommandParser.IsCommand(parts[0], "/task") ? parts[1] : message;
    }

    private async Task<ChannelIngressResult?> TryHandleAgentPickerCallbackAsync(
        GatewayChannelRegistration registration,
        string registrationId,
        ChannelInboundEnvelope envelope,
        CancellationToken cancellationToken)
    {
        string callbackData = envelope.MessageText.Trim();
        if (!callbackData.StartsWith("targetpick:", StringComparison.Ordinal)) return null;
        string[] parts = callbackData.Split(':', 3);
        if (parts.Length != 3 || !int.TryParse(parts[2], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out int index)
            || !targetSelections.TrySelect(parts[1], index, registrationId, envelope.ExternalUserId, envelope.ChatId, out GatewayChannelTargetBinding? choice)
            || choice is null)
        {
            await AnswerChannelCallbackAsync(registration, envelope.CallbackQueryId, "This picker expired or was already used. Open /agents again.", cancellationToken).ConfigureAwait(false);
            HashSet<string> deletedExpiredPickerMessages = new(StringComparer.Ordinal);
            if (IsTargetPickerOwnerConversation(registration, envelope))
            {
                var expiredPickerMessageStore = new ChannelPromptAnchorStore(state.Database);
                foreach (ChannelOutboundDestination destination in ChannelOutboundDestinations.Resolve(
                    attachmentRegistry, registration, envelope.ChatId, envelope.MessageThreadId))
                {
                    string? externalUserId = destination.Registration.Id == registrationId
                        ? envelope.ExternalUserId
                        : ChannelOutboundDestinations.ResolveExternalUserId(destination.Registration, destination.ConversationId);
                    if (!string.IsNullOrWhiteSpace(externalUserId))
                        targetSelections.Invalidate(destination.Registration.Id, externalUserId, destination.ConversationId);

                    foreach (string pickerMessageId in expiredPickerMessageStore.TakeTargetPickerMessages(
                        destination.Registration, destination.ConversationId, destination.ConversationThreadId))
                    {
                        await DeleteChannelMessageAsync(destination.Registration, destination.ConversationId,
                            pickerMessageId, cancellationToken).ConfigureAwait(false);
                        deletedExpiredPickerMessages.Add($"{destination.Registration.Id}\0{pickerMessageId}");
                    }
                }
            }
            string? expiredMessageId = envelope.CallbackTransportMessageId
                ?? envelope.CallbackMessageId?.ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (!string.IsNullOrWhiteSpace(expiredMessageId) && deletedExpiredPickerMessages.Add($"{registration.Id}\0{expiredMessageId}"))
                await DeleteChannelMessageAsync(registration, envelope.ChatId, expiredMessageId, cancellationToken).ConfigureAwait(false);
            return new ChannelIngressResult(true, "picker_expired", "Picker is unavailable.");
        }

        GatewayChannelTargetBinding? selected = attachmentRegistry.GetBindings(registrationId).FirstOrDefault(item =>
            string.Equals(item.TargetType, choice.TargetType, StringComparison.OrdinalIgnoreCase)
            && string.Equals(item.TargetId, choice.TargetId, StringComparison.Ordinal));
        if (selected is null)
        {
            await AnswerChannelCallbackAsync(registration, envelope.CallbackQueryId, "That agent is no longer available.", cancellationToken).ConfigureAwait(false);
            return new ChannelIngressResult(true, "target_unavailable", "Selected target is no longer granted.");
        }

        string displayName = routingCoordinator.ResolveTargetDisplayName(selected.TargetType, selected.TargetId);
        await attachmentRegistry.SetActiveTargetForUserAsync(registrationId, envelope.ExternalUserId, selected.TargetType, selected.TargetId, cancellationToken).ConfigureAwait(false);
        ChannelOutboundDestination[] destinations = ChannelOutboundDestinations.Resolve(
            attachmentRegistry, registration, envelope.ChatId, envelope.MessageThreadId);
        var pickerMessageStore = new ChannelPromptAnchorStore(state.Database);
        HashSet<string> deletedMessages = new(StringComparer.Ordinal);
        foreach (ChannelOutboundDestination destination in destinations)
        {
            string? linkedExternalUserId = destination.Registration.Id == registrationId
                ? envelope.ExternalUserId
                : ChannelOutboundDestinations.ResolveExternalUserId(destination.Registration, destination.ConversationId);
            if (!string.IsNullOrWhiteSpace(linkedExternalUserId))
            {
                targetSelections.Invalidate(destination.Registration.Id, linkedExternalUserId, destination.ConversationId);
            }

            foreach (string pickerMessageId in pickerMessageStore.TakeTargetPickerMessages(
                destination.Registration, destination.ConversationId, destination.ConversationThreadId))
            {
                await DeleteChannelMessageAsync(destination.Registration, destination.ConversationId,
                    pickerMessageId, cancellationToken).ConfigureAwait(false);
                deletedMessages.Add($"{destination.Registration.Id}\0{pickerMessageId}");
            }

            if (destination.Registration.Id == registrationId
                || string.IsNullOrWhiteSpace(linkedExternalUserId)
                || attachmentRegistry.GetBindings(destination.Registration.Id).All(binding =>
                    !string.Equals(binding.TargetType, selected.TargetType, StringComparison.OrdinalIgnoreCase)
                    || !string.Equals(binding.TargetId, selected.TargetId, StringComparison.Ordinal))) continue;

            await attachmentRegistry.SetActiveTargetForUserAsync(destination.Registration.Id,
                linkedExternalUserId, selected.TargetType, selected.TargetId, cancellationToken).ConfigureAwait(false);
        }
        await AnswerChannelCallbackAsync(registration, envelope.CallbackQueryId, $"Switched to {displayName}", cancellationToken).ConfigureAwait(false);

        string? messageId = envelope.CallbackTransportMessageId
            ?? envelope.CallbackMessageId?.ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (!string.IsNullOrWhiteSpace(messageId) && deletedMessages.Add($"{registration.Id}\0{messageId}"))
        {
            await DeleteChannelMessageAsync(registration, envelope.ChatId, messageId, cancellationToken).ConfigureAwait(false);
        }

        ChannelConversationMapEntry map = routingCoordinator.ResolveConversationMap(registrationId, selected, envelope);
        queueViews.Close(registration.OwnerUserId, ChannelQueueViewStore.RegistrationScope(registration) + ":session",
            envelope.ChatId, ChannelThreadRouting.ProviderTopic(envelope.ChatId, envelope.MessageThreadId));
        string sessionLabel = map.SessionId is not null
            && sessionStore.TryGetSession(map.SessionId, out GatewaySession? session)
            && session is not null && session.OwnerUserId == registration.OwnerUserId
            && session.TargetType == selected.TargetType && session.TargetId == selected.TargetId
                ? SessionLabel(session)
                : "A new session will start with your next prompt";
        await outboundMessagePublisher.PublishTextMessageAsync(new ChannelOutboundTextMessage(
            registration.ChannelType, registration.Id, envelope.ChatId, null,
            $"Now talking to {displayName}\nSession: {sessionLabel}\nYour next message goes here."), cancellationToken).ConfigureAwait(false);

        return new ChannelIngressResult(true, "ok", "Default target changed.");
    }

    private static bool IsTargetPickerOwnerConversation(GatewayChannelRegistration registration, ChannelInboundEnvelope envelope)
        => string.Equals(registration.ChannelType, "slack", StringComparison.OrdinalIgnoreCase)
            ? string.Equals(envelope.ExternalUserId, registration.Settings.GetStringNodeValue("slack_owner_user_id"), StringComparison.Ordinal)
                && string.Equals(envelope.ChatId, registration.Settings.GetStringNodeValue("slack_dm_channel_id"), StringComparison.Ordinal)
            : string.Equals(registration.ChannelType, "telegram", StringComparison.OrdinalIgnoreCase)
                && string.Equals(envelope.ChatId, registration.Settings.GetStringNodeValue("channel_device_id"), StringComparison.Ordinal);

    private async Task PublishTargetPickerAsync(
        GatewayChannelRegistration registration,
        ChannelInboundEnvelope envelope,
        CancellationToken cancellationToken)
    {
        bool fromRetiredTopic = ChannelThreadRouting.IsManagedTopic(registration, envelope.MessageThreadId)
            || !string.IsNullOrWhiteSpace(envelope.MessageThreadId);
        string prompt = fromRetiredTopic
            ? "Choose the agent for your next prompt, then return to the main chat to send it."
            : "Who would you like to chat with? Your choice stays selected until you switch agents.";
        GatewayChannelRegistration[] destinations =
        [.. ChannelOutboundDestinations.Resolve(attachmentRegistry, registration, envelope.ChatId, envelope.MessageThreadId)
            .Select(static destination => destination.Registration)];
        foreach (GatewayChannelRegistration destination in destinations)
        {
            ChannelOutboundDestination address = ChannelOutboundDestinations.Resolve(
                attachmentRegistry, registration, envelope.ChatId, envelope.MessageThreadId)
                .First(item => item.Registration.Id == destination.Id);
            string? externalUserId = destination.Id == registration.Id
                ? envelope.ExternalUserId
                : ChannelOutboundDestinations.ResolveExternalUserId(destination, address.ConversationId);
            if (string.IsNullOrWhiteSpace(externalUserId)) continue;

            GatewayChannelTargetBinding[] bindings = attachmentRegistry.GetBindings(destination.Id);
            if (bindings.Length == 0)
            {
                if (destination.Id == registration.Id)
                {
                    await outboundMessagePublisher.PublishTextMessageAsync(
                        new ChannelOutboundTextMessage(registration.ChannelType, registration.Id, envelope.ChatId,
                            envelope.MessageThreadId, "No agents are available on this bot yet. An admin must grant at least one agent."),
                        cancellationToken).ConfigureAwait(false);
                }
                continue;
            }

            (string selectedType, string selectedId) = routingCoordinator.ResolveCurrentTargetSelection(destination, externalUserId);
            GatewayChannelTargetBinding[] sorted = [.. bindings.OrderBy(item => routingCoordinator.ResolveTargetDisplayName(item.TargetType, item.TargetId), StringComparer.OrdinalIgnoreCase)];
            foreach (GatewayChannelTargetBinding[] page in sorted.Chunk(25))
            {
                string pickerId = targetSelections.Create(destination.Id, externalUserId, address.ConversationId, page);
                ChannelOutboundTargetPickerOption[] options = [.. page.Select((item, index) =>
                {
                    bool selected = string.Equals(item.TargetType, selectedType, StringComparison.OrdinalIgnoreCase) && string.Equals(item.TargetId, selectedId, StringComparison.Ordinal);
                    string label = routingCoordinator.BuildPickerLabel(item.TargetType, selected, routingCoordinator.ResolveTargetDisplayName(item.TargetType, item.TargetId));
                    if (label.Length > 64) label = label[..63] + "…";
                    return new ChannelOutboundTargetPickerOption(label, $"targetpick:{pickerId}:{index}", selected);
                })];
                await outboundMessagePublisher.PublishTargetPickerAsync(
                    new ChannelOutboundTargetPickerMessage(destination.ChannelType, destination.Id, address.ConversationId,
                        address.ConversationThreadId, prompt, options), cancellationToken).ConfigureAwait(false);
            }
        }
    }

}
