using System.Globalization;
using System.Text.Json;

namespace Kautuka.Gateway.Domain.Channels;

internal sealed partial class ChannelIngressService
{
    private const string invalidPayloadStatus = "invalid_payload";

    public async Task<ChannelInteractionDispatchResult> DispatchChannelInteractionAsync(string registrationId, JsonElement payload, CancellationToken cancellationToken)
    {
        if (!attachmentRegistry.TryGetRegistration(registrationId, out GatewayChannelRegistration? registration)
            || registration is null)
        {
            return new ChannelInteractionDispatchResult(false, "registration_not_found", "Active Channel registration was not found.");
        }
        await attachmentRegistry.RefreshAttachmentAsync(registrationId, registration.OwnerUserId, cancellationToken).ConfigureAwait(false);
        if (!attachmentRegistry.TryGetRegistration(registrationId, out registration))
        {
            return new ChannelInteractionDispatchResult(false, "registration_not_found", "Active Channel registration was not found.");
        }

        if (registration is null
            || !registration.IsActive)
        {
            return new ChannelInteractionDispatchResult(false, "registration_not_found", "Active Channel registration was not found.");
        }

        string prompt = payload.GetStringProperty("prompt", "").Trim();
        if (string.IsNullOrWhiteSpace(prompt))
        {
            return new ChannelInteractionDispatchResult(false, invalidPayloadStatus, "prompt is required.");
        }

        string chatId = payload.GetStringProperty("chat_id", payload.GetStringProperty("chatId", "")).Trim();
        if (string.IsNullOrWhiteSpace(chatId))
        {
            return new ChannelInteractionDispatchResult(false, invalidPayloadStatus, "chat_id is required.");
        }

        string? threadId = payload.GetNullableStringProperty("message_thread_id")
            ?? payload.GetNullableStringProperty("messageThreadId");
        string? userId = payload.GetNullableStringProperty("user_id")
            ?? payload.GetNullableStringProperty("userId");
        string? targetType = (payload.GetNullableStringProperty("target_type")
            ?? payload.GetNullableStringProperty("targetType"))?.ToUpperInvariant();
        string? targetId = payload.GetNullableStringProperty("target_id")
            ?? payload.GetNullableStringProperty("targetId");
        string? sessionId = payload.GetNullableStringProperty("session_id")
            ?? payload.GetNullableStringProperty("sessionId");
        int timeoutSeconds = Math.Clamp(
            payload.GetIntProperty("timeout_seconds", payload.GetIntProperty("timeoutSeconds", 900)),
            30,
            86400);

        if (!TryResolveInteractionContext(sessionId, registration.OwnerUserId, ref targetType, ref targetId, out string? resolvedContextError))
        {
            return new ChannelInteractionDispatchResult(false, invalidPayloadStatus, resolvedContextError ?? "Invalid interaction context.");
        }

        if (string.IsNullOrWhiteSpace(threadId)
            && !string.IsNullOrWhiteSpace(targetType)
            && !string.IsNullOrWhiteSpace(targetId))
        {
            threadId = routingCoordinator.ResolveTargetThreadId(registration, targetType, targetId);
        }

        List<GatewayChannelInteractionOption> options = ParseOptions(payload);
        if (options.Count == 0)
        {
            return new ChannelInteractionDispatchResult(false, invalidPayloadStatus, "At least one interaction option is required.");
        }

        string interactionId = $"hitl-{Guid.NewGuid():N}";
        GatewayRun? run = null;
        if (!string.IsNullOrWhiteSpace(targetType) && !string.IsNullOrWhiteSpace(targetId))
        {
            run = sessionStore.CreateInteractionRun(
                targetType,
                targetId,
                string.IsNullOrWhiteSpace(userId) ? registration.OwnerUserId : userId,
                sessionId,
                interactionId,
                prompt,
                options.Select(static item => item.Value).ToArray());
        }

        string? senderDisplayName = string.IsNullOrWhiteSpace(targetType) || string.IsNullOrWhiteSpace(targetId)
            ? null
            : routingCoordinator.ResolveTargetDisplayName(targetType, targetId);

        ChannelOutboundDestination[] destinations = ChannelOutboundDestinations.Resolve(
            attachmentRegistry, registration, chatId, threadId);
        string expiresAt = DateTimeOffset.UtcNow.AddSeconds(timeoutSeconds).ToString("O");
        GatewayChannelInteraction? primaryInteraction = null;
        foreach (ChannelOutboundDestination destination in destinations)
        {
            string destinationInteractionId = destination.Registration.Id == registration.Id
                ? interactionId
                : $"hitl-{Guid.NewGuid():N}";
            GatewayChannelInteraction interaction = new(
                destinationInteractionId,
                destination.Registration.Id,
                userId,
                targetType,
                targetId,
                sessionId,
                run?.Id,
                destination.ConversationId,
                destination.ConversationThreadId,
                prompt,
                options,
                "pending",
                Timestamp(),
                expiresAt,
                null,
                null,
                null,
                SenderDisplayName: senderDisplayName,
                InteractionGroupId: interactionId);
            state.UpsertChannelInteraction(interaction);
            if (destination.Registration.Id == registration.Id) primaryInteraction = interaction;

            long? messageId = await outboundMessagePublisher.PublishPromptMessageAsync(
                new ChannelOutboundPromptMessage(
                    destination.Registration.ChannelType,
                    destination.Registration.Id,
                    interaction.Id,
                    interaction.ChatId,
                    interaction.MessageThreadId,
                    interaction.Prompt,
                    [.. interaction.Options.Select(option => new ChannelOutboundPromptOption(option.Label, option.Value))],
                    interaction.SenderDisplayName),
                cancellationToken).ConfigureAwait(false);
            if (messageId is null)
            {
                GatewayChannelInteraction failedInteraction = interaction with { Status = "delivery_failed" };
                state.UpsertChannelInteraction(failedInteraction);
                if (interaction.Id == interactionId) primaryInteraction = failedInteraction;
                if (run is not null && interaction.Id == interactionId)
                {
                    sessionStore.UpdateInteractionRun(interactionId, "delivery_failed",
                        BuildInteractionOutput(interaction, null, null), "Channel delivery failed.");
                }
                continue;
            }

            if (messageId > 0)
            {
                state.UpsertChannelInteraction(interaction with
                {
                    SourceMessageId = messageId,
                    SourceTransportMessageId = messageId.Value.ToString(CultureInfo.InvariantCulture),
                });
            }
        }

        if (primaryInteraction is null || primaryInteraction.Status == "delivery_failed")
        {
            return new ChannelInteractionDispatchResult(false, "delivery_failed", "Interaction persisted but Channel delivery failed.", interactionId, run?.Id);
        }

        return new ChannelInteractionDispatchResult(true, "ok", "Interaction delivered.", interactionId, run?.Id);
    }
}
