using System.Text.Json;
using System.Text.Json.Nodes;

using Kautuka.Gateway.Domain.Execution;
using Kautuka.Gateway.Domain.Hitl;
using Kautuka.Gateway.Domain.Workflows;

using Narad.Contracts;

namespace Kautuka.Gateway.Domain.Channels;

internal sealed partial class ChannelIngressService
{
    private async Task<ChannelIngressResult> ResolveInteractionCallbackAsync(
        GatewayChannelRegistration registration,
        string registrationId,
        GatewayChannelIdentityLink identity,
        ChannelInboundEnvelope envelope,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(envelope.CallbackInteractionId)
            || string.IsNullOrWhiteSpace(envelope.CallbackAction)
            || !state.ChannelInteractions.TryGetValue(envelope.CallbackInteractionId, out GatewayChannelInteraction? interaction))
        {
            string unknownMessage = $"Action received: {envelope.CallbackAction}. Workflow callback integration is in progress.";
            await AnswerChannelCallbackAsync(registration, envelope.CallbackQueryId, unknownMessage, cancellationToken).ConfigureAwait(false);
            return new ChannelIngressResult(true, "callback_acknowledged", unknownMessage);
        }

        if (!string.Equals(interaction.RegistrationId, registrationId, StringComparison.Ordinal))
        {
            await AnswerChannelCallbackAsync(registration, envelope.CallbackQueryId, "This interaction is not valid for this bot.", cancellationToken).ConfigureAwait(false);
            return new ChannelIngressResult(false, "unauthorized", "This interaction is not valid for the current bot registration.");
        }

        string expectedThread = string.IsNullOrWhiteSpace(interaction.MessageThreadId) ? "root" : interaction.MessageThreadId;
        string actualThread = string.IsNullOrWhiteSpace(envelope.MessageThreadId) ? "root" : envelope.MessageThreadId;
        string expectedSourceMessage = interaction.SourceTransportMessageId
            ?? interaction.SourceMessageId?.ToString(System.Globalization.CultureInfo.InvariantCulture)
            ?? string.Empty;
        string actualSourceMessage = envelope.CallbackTransportMessageId
            ?? envelope.CallbackMessageId?.ToString(System.Globalization.CultureInfo.InvariantCulture)
            ?? string.Empty;
        if (!string.Equals(interaction.ChatId, envelope.ChatId, StringComparison.Ordinal)
            || !string.Equals(expectedThread, actualThread, StringComparison.Ordinal)
            || string.IsNullOrWhiteSpace(expectedSourceMessage)
            || !string.Equals(expectedSourceMessage, actualSourceMessage, StringComparison.Ordinal))
        {
            await AnswerChannelCallbackAsync(registration, envelope.CallbackQueryId, "This interaction is not valid for this message.", cancellationToken).ConfigureAwait(false);
            return new ChannelIngressResult(false, "unauthorized", "Callback source binding did not match the issued interaction.");
        }

        if (!string.Equals(interaction.Status, "pending", StringComparison.Ordinal))
        {
            string already = $"Interaction already finalized as {interaction.Status}.";
            await AnswerChannelCallbackAsync(registration, envelope.CallbackQueryId, already, cancellationToken).ConfigureAwait(false);
            return new ChannelIngressResult(true, "callback_acknowledged", already, interaction.SessionId, interaction.TargetType, interaction.TargetId, interaction.Id, interaction.RunId);
        }

        if (DateTimeOffset.TryParse(interaction.ExpiresAt, out DateTimeOffset expiresAt) && expiresAt <= DateTimeOffset.UtcNow)
        {
            string expiredAt = Timestamp();
            GatewayChannelInteraction[] linkedInteractions = LinkedInteractions(interaction);
            foreach (GatewayChannelInteraction linked in linkedInteractions)
            {
                state.UpsertChannelInteraction(linked with { Status = "expired", ResolvedAt = expiredAt });
            }
            sessionStore.UpdateInteractionRun(interaction.InteractionGroupId ?? interaction.Id,
                "expired", BuildInteractionOutput(interaction, null, null));

            foreach (GatewayChannelInteraction linked in linkedInteractions)
            {
                if (attachmentRegistry.TryGetRegistration(linked.RegistrationId, out GatewayChannelRegistration? linkedRegistration)
                    && linkedRegistration is not null)
                    await ProjectInteractionResolutionAsync(linkedRegistration, linked with
                    {
                        Status = "expired",
                        ResolvedAt = expiredAt,
                    }, "expired", cancellationToken).ConfigureAwait(false);
            }

            string? expiredTransportMessageId = GetInteractionTransportMessageId(interaction);
            if (!string.IsNullOrWhiteSpace(expiredTransportMessageId))
            {
                await DeleteChannelMessageAsync(registration, envelope.ChatId, expiredTransportMessageId, cancellationToken).ConfigureAwait(false);
            }

            await outboundMessagePublisher.PublishTextMessageAsync(
                new ChannelOutboundTextMessage(registration.ChannelType, registration.Id, envelope.ChatId, envelope.MessageThreadId, "⏰ This request expired. Please try again."),
                cancellationToken).ConfigureAwait(false);

            await AnswerChannelCallbackAsync(registration, envelope.CallbackQueryId, "This request expired.", cancellationToken).ConfigureAwait(false);

            return new ChannelIngressResult(
                true,
                "callback_acknowledged",
                "Interaction expired.",
                interaction.SessionId,
                interaction.TargetType,
                interaction.TargetId,
                interaction.Id,
                interaction.RunId);
        }

        if (!string.IsNullOrWhiteSpace(interaction.UserId)
            && !string.Equals(interaction.UserId, identity.UserId, StringComparison.Ordinal))
        {
            await AnswerChannelCallbackAsync(registration, envelope.CallbackQueryId, "You are not authorized for this interaction.", cancellationToken).ConfigureAwait(false);
            return new ChannelIngressResult(false, "unauthorized", "You are not authorized to respond to this interaction.");
        }

        GatewayChannelInteractionOption? selected = interaction.Options.FirstOrDefault(option =>
            string.Equals(option.Value, envelope.CallbackAction, StringComparison.OrdinalIgnoreCase));
        if (selected is null)
        {
            await AnswerChannelCallbackAsync(registration, envelope.CallbackQueryId, "Unknown interaction option selected.", cancellationToken).ConfigureAwait(false);
            return new ChannelIngressResult(true, "callback_acknowledged", "Unknown interaction option selected.");
        }

        await AnswerChannelCallbackAsync(registration, envelope.CallbackQueryId, selected.Label, cancellationToken).ConfigureAwait(false);

        string interactionStatus = "resolved";
        string interactionResolution = selected.Value;
        string? resolvedByExternalUserId = envelope.ExternalUserId;
        string? alreadyResolvedMessage = null;
        if (!string.IsNullOrWhiteSpace(interaction.ApprovalRequestId))
        {
            GatewayToolApprovalRequest? storedApproval = approvalStore.GetById(interaction.ApprovalRequestId);
            if (storedApproval is null)
            {
                return new ChannelIngressResult(
                    false,
                    "approval_resolve_failed",
                    "Approval request could not be resolved.",
                    interaction.SessionId,
                    interaction.TargetType,
                    interaction.TargetId,
                    interaction.Id,
                    interaction.RunId);
            }

            bool resolvedHere = false;
            bool selectedApproved = string.Equals(selected.Value, "approve", StringComparison.OrdinalIgnoreCase);
            bool selectedStopRun = string.Equals(selected.Value, "stop_run", StringComparison.OrdinalIgnoreCase)
                || string.Equals(selected.Value, "cancel", StringComparison.OrdinalIgnoreCase);
            string? selectedReason = selectedStopRun
                    ? "stopped"
                    : string.Equals(selected.Value, "deny", StringComparison.OrdinalIgnoreCase)
                        ? "denied"
                    : string.Equals(selected.Value, "request_changes", StringComparison.OrdinalIgnoreCase)
                        ? "request_changes"
                    : null;
            if (string.IsNullOrWhiteSpace(storedApproval.Decision))
            {
                if (!ApprovalActionCatalog.TryMapDecision(selected.Value, out _, out _, out string decision))
                {
                    await AnswerChannelCallbackAsync(registration, envelope.CallbackQueryId, "Unknown interaction option selected.", cancellationToken).ConfigureAwait(false);
                    return new ChannelIngressResult(false, "unknown_option", "Unknown interaction option selected.", interaction.SessionId, interaction.TargetType, interaction.TargetId, interaction.Id, interaction.RunId);
                }

                ThreadCommandResult commandResult = await threadCommands.ResolveInterruptAsync(
                    new ResolveThreadInterruptCommand(
                        storedApproval.Id,
                        decision,
                        selectedReason,
                        interaction.UserId ?? identity.UserId ?? string.Empty,
                        ResolveApprovalActorLabel(identity.UserId),
                        ExpectedRevision: null,
                        HitlResolutionSource.ChannelCallback,
                        AdminFallback: false,
                        OriginatingChannelType: registration.ChannelType),
                    CancellationToken.None).ConfigureAwait(false);
                resolvedHere = commandResult.Status is ThreadCommandStatus.Accepted;
                storedApproval = approvalStore.GetById(storedApproval.Id);
            }

            if (string.IsNullOrWhiteSpace(storedApproval?.Decision))
            {
                return new ChannelIngressResult(
                    false,
                    "approval_resolve_failed",
                    "Approval request could not be resolved.",
                    interaction.SessionId,
                    interaction.TargetType,
                    interaction.TargetId,
                    interaction.Id,
                    interaction.RunId);
            }

            interactionResolution = resolvedHere
                ? ApprovalActionCatalog.Resolve(
                    selectedApproved,
                    expired: false,
                    selectedReason,
                    selectedStopRun)
                : ApprovalActionCatalog.Resolve(
                    string.Equals(storedApproval.Decision, "approved", StringComparison.Ordinal),
                    string.Equals(storedApproval.Decision, "expired", StringComparison.Ordinal),
                    storedApproval.Reason,
                    string.Equals(storedApproval.Reason, "stopped", StringComparison.OrdinalIgnoreCase));
            interactionStatus = string.Equals(storedApproval.Decision, "expired", StringComparison.Ordinal)
                ? "expired"
                : "resolved";
            resolvedByExternalUserId = storedApproval.DecidedBy ?? envelope.ExternalUserId;

            if (!resolvedHere)
            {
                alreadyResolvedMessage = storedApproval.Decision switch
                {
                    "approved" => "Approval was already approved in another channel.",
                    "expired" => "Approval was expired in another channel.",
                    _ => "Approval was already denied in another channel.",
                };
            }
        }

        string resolvedAt = Timestamp();
        GatewayChannelInteraction[] linkedInteractionGroup = LinkedInteractions(interaction);
        GatewayChannelInteraction resolvedInteraction = interaction with
        {
            Status = interactionStatus,
            ResolvedAt = resolvedAt,
            ResolvedByExternalUserId = resolvedByExternalUserId,
            Resolution = interactionResolution,
        };
        foreach (GatewayChannelInteraction linked in linkedInteractionGroup)
        {
            state.UpsertChannelInteraction(linked with
            {
                Status = interactionStatus,
                ResolvedAt = resolvedAt,
                ResolvedByExternalUserId = resolvedByExternalUserId,
                Resolution = interactionResolution,
            });
        }
        sessionStore.UpdateInteractionRun(
            interaction.InteractionGroupId ?? interaction.Id,
            interactionStatus,
            BuildInteractionOutput(interaction, interactionResolution, resolvedByExternalUserId));

        // HITL card edits are owned by SyncChannelApprovalResolutionAsync (HitlApprovalResolved).
        // Already-resolved races never re-publish Hitl, so project here. Non-approval callbacks too.
        if (!string.IsNullOrWhiteSpace(alreadyResolvedMessage))
        {
            await ProjectInteractionResolutionAsync(
                registration,
                resolvedInteraction,
                interactionResolution,
                cancellationToken).ConfigureAwait(false);
        }
        else if (string.IsNullOrWhiteSpace(interaction.ApprovalRequestId))
        {
            foreach (GatewayChannelInteraction linked in linkedInteractionGroup)
            {
                if (attachmentRegistry.TryGetRegistration(linked.RegistrationId, out GatewayChannelRegistration? linkedRegistration)
                    && linkedRegistration is not null)
                    await ProjectInteractionResolutionAsync(linkedRegistration, linked with
                    {
                        Status = interactionStatus,
                        ResolvedAt = resolvedAt,
                        ResolvedByExternalUserId = resolvedByExternalUserId,
                        Resolution = interactionResolution,
                    }, interactionResolution, cancellationToken).ConfigureAwait(false);
            }
        }

        return new ChannelIngressResult(
            true,
            "callback_acknowledged",
            alreadyResolvedMessage ?? $"Recorded action: {selected.Label}. Continuing in background.",
            resolvedInteraction.SessionId,
            resolvedInteraction.TargetType,
            resolvedInteraction.TargetId,
            resolvedInteraction.Id,
            resolvedInteraction.RunId);
    }

    private GatewayChannelInteraction[] LinkedInteractions(GatewayChannelInteraction interaction)
    {
        if (string.IsNullOrWhiteSpace(interaction.InteractionGroupId)) return [interaction];
        GatewayChannelInteraction[] linked = [.. state.ChannelInteractions.Values.Where(candidate =>
            string.Equals(candidate.InteractionGroupId, interaction.InteractionGroupId, StringComparison.Ordinal)
            && string.Equals(candidate.Status, "pending", StringComparison.Ordinal))];
        return linked.Length == 0 ? [interaction] : linked;
    }

    private async Task ProjectInteractionResolutionAsync(
        GatewayChannelRegistration registration,
        GatewayChannelInteraction interaction,
        string resolutionValue,
        CancellationToken cancellationToken)
    {
        string? transportMessageId = GetInteractionTransportMessageId(interaction);
        if (string.IsNullOrWhiteSpace(transportMessageId))
        {
            return;
        }

        string eventId = Guid.NewGuid().ToString("N");
        string runId = string.IsNullOrWhiteSpace(interaction.RunId) ? eventId : interaction.RunId;
        string sessionId = interaction.SessionId ?? string.Empty;
        GatewayToolApprovalRequest? approval = interaction.ApprovalRequestId is { } approvalId
            ? approvalStore.GetById(approvalId)
            : null;
        var payload = new JsonObject
        {
            ["decision"] = resolutionValue,
            ["resolution"] = resolutionValue,
            ["toolName"] = approval?.ToolName ?? "tool",
            ["prompt"] = interaction.Prompt,
            ["transportMessageId"] = transportMessageId,
            ["agentId"] = interaction.TargetId,
        };
        bool workflowReview = string.Equals(approval?.Kind, "workflow_review", StringComparison.Ordinal)
            || string.Equals(approval?.ToolType, "workflow_review", StringComparison.Ordinal);
        if (workflowReview)
        {
            payload["kind"] = "workflow_review";
            GatewayWorkflowInstance? instance = workflowStore.GetInstance(approval?.InstanceId ?? string.Empty);
            payload["title"] = WorkflowReviewTitle(instance, approval?.NodeId);
        }

        NaradPersistedEvent runtimeEvent = NaradStreamObservation.CreateSynthetic(
            eventId,
            NaradEventTypes.InterruptResolved,
            sessionId,
            runId,
            runId,
            JsonSerializer.SerializeToElement(payload, KautukaJsonContext.Default.JsonObject));

        await runtimeEventProjector.ProjectBoundAsync(
            runtimeEvent,
            registration.Id,
            interaction.ChatId,
            interaction.MessageThreadId,
            interaction.Id,
            cancellationToken).ConfigureAwait(false);
    }

}
