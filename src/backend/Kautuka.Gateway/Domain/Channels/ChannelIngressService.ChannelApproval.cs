using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

using Grpc.Core;

using Kautuka.Gateway.Domain.Execution;
using Kautuka.Gateway.Domain.Workflows;

using Narad.Contracts;

namespace Kautuka.Gateway.Domain.Channels;

internal sealed partial class ChannelIngressService
{
    internal async Task SyncResolvedChannelApprovalAfterDeliveryAsync(
        string interactionId,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(interactionId)
            || !state.ChannelInteractions.TryGetValue(interactionId, out GatewayChannelInteraction? interaction)
            || !string.Equals(interaction.Status, "pending", StringComparison.Ordinal)
            || string.IsNullOrWhiteSpace(interaction.ApprovalRequestId)
            || string.IsNullOrWhiteSpace(GetInteractionTransportMessageId(interaction)))
        {
            return;
        }

        GatewayToolApprovalRequest? approval = approvalStore.GetById(interaction.ApprovalRequestId);
        if (approval?.Decision is not { Length: > 0 } decision)
        {
            return;
        }

        string resolution = ApprovalActionCatalog.Resolve(
            approved: string.Equals(decision, "approved", StringComparison.OrdinalIgnoreCase),
            expired: string.Equals(decision, "expired", StringComparison.OrdinalIgnoreCase),
            reason: approval.Reason,
            stopRun: string.Equals(approval.Reason, "stopped", StringComparison.OrdinalIgnoreCase));

        await SyncChannelApprovalResolutionAsync(
            new ChannelApprovalResolution(
                interaction.ApprovalRequestId,
                resolution,
                approval.DecidedBy ?? interaction.UserId),
            cancellationToken).ConfigureAwait(false);
    }

    public async Task SyncChannelApprovalResolutionAsync(
        ChannelApprovalResolution resolution,
        CancellationToken cancellationToken)
    {
        string requestId = resolution.RequestId;
        if (string.IsNullOrWhiteSpace(requestId))
        {
            return;
        }

        SemaphoreSlim resolutionLock = approvalResolutionLocks[
            (StringComparer.Ordinal.GetHashCode(requestId) & int.MaxValue) % approvalResolutionLocks.Length];
        await resolutionLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            GatewayChannelInteraction[] interactions =
            [
                .. state.ChannelInteractions.Values.Where(interaction =>
                    string.Equals(interaction.ApprovalRequestId, requestId, StringComparison.Ordinal)
                    && string.Equals(interaction.Status, "pending", StringComparison.Ordinal)
                    && !string.IsNullOrWhiteSpace(GetInteractionTransportMessageId(interaction)))
            ];

            if (interactions.Length == 0)
            {
                return;
            }

            string resolutionValue = resolution.Action;
            string decidedByLabel = ResolveApprovalActorLabel(resolution.DecidedByUserId);
            foreach (GatewayChannelInteraction candidate in interactions)
            {
                if (!state.ChannelInteractions.TryGetValue(candidate.Id, out GatewayChannelInteraction? interaction)
                    || !IsPendingDeliveredApproval(interaction, requestId))
                {
                    continue;
                }

                if (!attachmentRegistry.TryGetRegistration(interaction.RegistrationId, out GatewayChannelRegistration? registration)
                    || registration is null)
                {
                    CloseUnprojectedChannelApproval(interaction, resolutionValue);
                    continue;
                }
                await attachmentRegistry.RefreshAttachmentAsync(interaction.RegistrationId, registration.OwnerUserId, cancellationToken).ConfigureAwait(false);
                if (!state.ChannelInteractions.TryGetValue(candidate.Id, out interaction)
                    || !IsPendingDeliveredApproval(interaction, requestId))
                {
                    continue;
                }

                if (!attachmentRegistry.TryGetRegistration(interaction.RegistrationId, out registration)
                    || registration is null
                    || !registration.IsActive)
                {
                    CloseUnprojectedChannelApproval(interaction, resolutionValue);
                    continue;
                }
                if (string.IsNullOrWhiteSpace(GetInteractionTransportMessageId(interaction))) continue;

                string transportMessageId = GetInteractionTransportMessageId(interaction)!;
                string eventId = Guid.NewGuid().ToString("N");
                string runId = string.IsNullOrWhiteSpace(interaction.RunId) ? eventId : interaction.RunId;
                string sessionId = interaction.SessionId ?? string.Empty;
                var payload = new JsonObject
                {
                    ["approvalRequestId"] = requestId,
                    ["decision"] = resolutionValue,
                    ["resolution"] = resolutionValue,
                    ["toolName"] = approvalStore.GetById(requestId)?.ToolName ?? "tool",
                    ["prompt"] = interaction.Prompt,
                    ["transportMessageId"] = transportMessageId,
                    ["decidedByUserId"] = resolution.DecidedByUserId,
                    ["agentId"] = interaction.TargetId,
                };
                GatewayToolApprovalRequest? approvalDetails = approvalStore.GetById(requestId);
                bool workflowReview = string.Equals(approvalDetails?.Kind, "workflow_review", StringComparison.Ordinal)
                    || string.Equals(approvalDetails?.ToolType, "workflow_review", StringComparison.Ordinal);
                if (workflowReview)
                {
                    payload["kind"] = "workflow_review";
                    GatewayWorkflowInstance? workflowInstance = workflowStore.GetInstance(approvalDetails?.InstanceId ?? string.Empty);
                    payload["title"] = WorkflowReviewTitle(workflowInstance, approvalDetails?.NodeId);
                }
                string? argumentsJson = ChannelApprovalArguments.Sanitize(approvalDetails?.ArgumentsJson ?? interaction.ArgumentsJson);
                if (!workflowReview && !string.IsNullOrWhiteSpace(argumentsJson))
                    payload["argumentsJson"] = argumentsJson;
                if (!string.IsNullOrWhiteSpace(decidedByLabel))
                {
                    payload["reason"] = $"by {decidedByLabel}";
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

                state.UpsertChannelInteraction(interaction with
                {
                    Status = string.Equals(resolutionValue, ApprovalActionCatalog.Expired, StringComparison.Ordinal)
                        ? "expired"
                        : "resolved",
                    ResolvedAt = Timestamp(),
                    Resolution = resolutionValue,
                });
            }
        }
        finally
        {
            resolutionLock.Release();
        }
    }

    private static bool IsPendingDeliveredApproval(GatewayChannelInteraction interaction, string requestId)
        => string.Equals(interaction.ApprovalRequestId, requestId, StringComparison.Ordinal)
            && string.Equals(interaction.Status, "pending", StringComparison.Ordinal)
            && !string.IsNullOrWhiteSpace(GetInteractionTransportMessageId(interaction));

    private void CloseUnprojectedChannelApproval(GatewayChannelInteraction interaction, string resolution)
    {
        ChannelIngressServiceLog.ChannelApprovalResolutionSkipped(logger, interaction.ApprovalRequestId ?? string.Empty, interaction.Id);
        state.UpsertChannelInteraction(interaction with
        {
            Status = string.Equals(resolution, ApprovalActionCatalog.Expired, StringComparison.Ordinal) ? "expired" : "resolved",
            ResolvedAt = Timestamp(),
            Resolution = resolution,
        });
    }

    public async Task NotifyChannelApprovalAsync(
        string userId,
        string sessionId,
        string? targetType,
        string? targetId,
        GatewayChatApprovalRequest approvalReq,
        CancellationToken cancellationToken)
    {
        ChannelIngressServiceLog.ChannelApprovalNotifyEntered(logger, approvalReq.RequestId, userId, sessionId);

        ApprovalRoutingContext? routingContext = await ResolveApprovalRoutingContextAsync(
            userId,
            sessionId,
            targetType,
            targetId,
            approvalReq.ToolType,
            ReadWorkflowId(approvalReq),
            cancellationToken).ConfigureAwait(false);
        if (routingContext is null)
        {
            ChannelIngressServiceLog.ChannelApprovalRoutingFailed(logger, approvalReq.RequestId, userId, sessionId);
            return;
        }

        GatewayToolApprovalRequest? storedApproval = approvalStore.GetById(approvalReq.RequestId);
        bool workflowReview = string.Equals(approvalReq.ToolType, "workflow_review", StringComparison.Ordinal)
            || string.Equals(storedApproval?.Kind, "workflow_review", StringComparison.Ordinal)
            || string.Equals(storedApproval?.ToolType, "workflow_review", StringComparison.Ordinal);
        GatewayWorkflowInstance? workflowInstance = workflowReview
            ? workflowStore.GetInstance(storedApproval?.InstanceId ?? string.Empty)
            : null;
        int finishedReviewAttempts = workflowInstance?.Steps.Count(step =>
            string.Equals(step.NodeId, storedApproval?.NodeId, StringComparison.Ordinal)
            && string.Equals(step.Status, WorkflowStepAttemptStatuses.Finished, StringComparison.Ordinal)) ?? 0;
        var actions = ApprovalResponse.ComputeActions(approvalReq.ToolType)
            .Where(action => !workflowReview
                || action.Value != ApprovalActionCatalog.RequestChanges
                || WorkflowRetryPolicy.CanRequestChanges(finishedReviewAttempts));
        var options = actions
            .Select(a => new GatewayChannelInteractionOption($"opt-{Guid.NewGuid():N}", a.Label, a.Value, a.Style))
            .ToList();
        string? userMessage = workflowReview
            ? null
            : RunUserMessageResolver.Resolve(state.AgentDefinitionStore, sessionStore,
                approvalReq.AgentId ?? routingContext.TargetId, sessionId, approvalReq.RunId, approvalReq.ToolCallId);
        string? channelArgumentsJson = ChannelApprovalArguments.Sanitize(approvalReq.ArgumentsJson);
        string prompt = workflowReview
            ? "This step finished. Check its result before the next step runs."
            : BuildChannelApprovalPrompt(
                approvalReq.ToolName,
                channelArgumentsJson,
                approvalReq.OuterAgentId,
                approvalReq.OuterSessionId,
                userMessage,
                fullDetails: false);

        // Fan out the canonical approval request; ChannelHost filters/translates. Interaction id
        // is required so callback tokens round-trip to GatewayChannelInteraction.
        string eventId = Guid.NewGuid().ToString("N");
        string runId = string.IsNullOrWhiteSpace(approvalReq.RunId) ? eventId : approvalReq.RunId;
        JsonObject approvalPayload = JsonSerializer.SerializeToNode(
            approvalReq,
            KautukaJsonContext.Default.GatewayChatApprovalRequest)?.AsObject()
            ?? [];
        approvalPayload["prompt"] = prompt;
        approvalPayload["kind"] = workflowReview ? "workflow_review" : approvalReq.ToolType;
        // ChannelHost owns presentation, but only receives bounded, redacted arguments.
        if (workflowReview)
            approvalPayload.Remove("argumentsJson");
        else
            approvalPayload["argumentsJson"] = channelArgumentsJson;
        approvalPayload["title"] = "Permission required";
        approvalPayload["summary"] = prompt;
        approvalPayload["fields"] = new JsonArray();
        approvalPayload["actions"] = BuildChannelApprovalActions(
            approvalReq.ToolType,
            hideRequestChanges: workflowReview && !WorkflowRetryPolicy.CanRequestChanges(finishedReviewAttempts));
        if (workflowReview)
        {
            GatewayWorkflowInstance? instance = workflowInstance;
            GatewayWorkflowNode? node = instance?.Snapshot.Nodes.FirstOrDefault(candidate =>
                string.Equals(candidate.NodeId, storedApproval?.NodeId, StringComparison.Ordinal));
            string title = WorkflowReviewTitle(instance, node?.NodeId);
            string agentName = node is not null
                && state.Agents.TryGetValue(node.AgentId, out GatewayAgent? reviewAgent)
                ? reviewAgent.Name
                : "Agent";
            JsonArray fields = BuildWorkflowReviewFields(approvalReq.ArgumentsJson);
            fields.Insert(0, new JsonObject { ["label"] = "Agent", ["value"] = agentName });
            if (fields.Count > 10) fields.RemoveAt(fields.Count - 1);
            approvalPayload["title"] = title;
            approvalPayload["summary"] = prompt;
            approvalPayload["fields"] = fields;
            approvalPayload["prompt"] = prompt;
            approvalPayload.Remove("userMessage");
        }
        if (!workflowReview && !string.IsNullOrWhiteSpace(userMessage))
        {
            approvalPayload["userMessage"] = userMessage;
        }

        // Attribute approval requests to the surface/default agent, never the nested AAAT child,
        // so the channel identifies the agent the user was interacting with.
        var runtimeEvent = NaradStreamObservation.CreateSynthetic(
            eventId,
            NaradEventTypes.InterruptRequested,
            sessionId,
            runId,
            runId,
            JsonSerializer.SerializeToElement(approvalPayload, KautukaJsonContext.Default.JsonObject));

        List<ApprovalRoutingContext> destinations = [routingContext];
        foreach (GatewayChannelRegistration linked in attachmentRegistry.GetActiveChatRegistrations(userId))
        {
            if (linked.Id == routingContext.Registration.Id
                || string.Equals(linked.ChannelType, routingContext.Registration.ChannelType, StringComparison.OrdinalIgnoreCase)) continue;
            string? chatId = linked.ChannelType.Equals("slack", StringComparison.OrdinalIgnoreCase)
                ? linked.Settings.GetStringNodeValue("slack_dm_channel_id")
                : linked.Settings.GetStringNodeValue("channel_device_id");
            if (string.IsNullOrWhiteSpace(chatId)) continue;
            string linkedTargetType = routingContext.TargetType;
            string linkedTargetId = routingContext.TargetId;
            destinations.Add(new ApprovalRoutingContext(linked, chatId, linkedTargetType, linkedTargetId,
                routingCoordinator.ResolveTargetThreadId(linked, linkedTargetType, linkedTargetId),
                routingCoordinator.ResolveTargetDisplayName(linkedTargetType, linkedTargetId)));
        }

        foreach (ApprovalRoutingContext destination in destinations)
        {
            GatewayChannelInteraction? existingPending = state.ChannelInteractions.Values.FirstOrDefault(interaction =>
                interaction.ApprovalRequestId == approvalReq.RequestId
                && interaction.RegistrationId == destination.Registration.Id
                && interaction.Status == "pending");
            if (existingPending is not null && HasDeliveredChannelApproval(existingPending))
            {
                ChannelIngressServiceLog.ChannelApprovalAlreadyPending(logger, approvalReq.RequestId);
                continue;
            }
            string interactionId = existingPending?.Id ?? $"hitl-{Guid.NewGuid():N}";
            string interactionGroupId = existingPending?.InteractionGroupId ?? $"approval:{approvalReq.RequestId}";
            var interaction = new GatewayChannelInteraction(interactionId, destination.Registration.Id,
                UserId: userId, TargetType: destination.TargetType, TargetId: destination.TargetId,
                SessionId: sessionId, RunId: approvalReq.RunId, ChatId: destination.ChatId,
                MessageThreadId: destination.MessageThreadId, Prompt: prompt,
                SenderDisplayName: destination.TargetDisplayName, Options: options, Status: "pending",
                CreatedAt: Timestamp(), ExpiresAt: approvalReq.ExpiresAt ?? DateTimeOffset.MaxValue.ToString("O"),
                ResolvedAt: null, ResolvedByExternalUserId: null, Resolution: null,
                ApprovalRequestId: approvalReq.RequestId, ArgumentsJson: channelArgumentsJson,
                InteractionGroupId: interactionGroupId);
            state.UpsertChannelInteraction(interaction);
            ChannelIngressServiceLog.ChannelApprovalInteractionCreated(logger, approvalReq.RequestId, interactionId);
            await runtimeEventProjector.ProjectApprovalRequestedAsync(runtimeEvent, destination.Registration.Id,
                interaction.ChatId, interaction.MessageThreadId, interactionId, cancellationToken).ConfigureAwait(false);
            ChannelIngressServiceLog.ChannelApprovalDelivered(logger, approvalReq.RequestId, interactionId, messageId: null);
        }
    }

    private static string WorkflowReviewTitle(GatewayWorkflowInstance? instance, string? nodeId)
    {
        string workflowName = instance?.Snapshot.Name ?? "Workflow";
        if (instance is null || string.IsNullOrWhiteSpace(nodeId))
            return $"Workflow review: {workflowName}";

        int stepIndex = Array.FindIndex(instance.Snapshot.Nodes, candidate =>
            string.Equals(candidate.NodeId, nodeId, StringComparison.Ordinal));
        return stepIndex >= 0
            ? $"Workflow review: {workflowName} — step {stepIndex + 1} of {instance.Snapshot.Nodes.Length}"
            : $"Workflow review: {workflowName}";
    }

    /// <summary>
    /// No-op: channel finals are delivered by ChannelHost from projected
    /// <c>runtime_event</c> envelopes (<c>assistant.message.completed</c> / <c>run.finished</c>).
    /// Kept on <see cref="IChannelIngressService"/> for call-site compatibility.
    /// </summary>
    public Task NotifyChannelContinuationAsync(string requestId, string continuationText, CancellationToken cancellationToken)
    {
        _ = requestId;
        _ = continuationText;
        _ = cancellationToken;
        return Task.CompletedTask;
    }

    private static DateTimeOffset ParseUtcOrMin(string? value)
        => DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out DateTimeOffset parsed)
            ? parsed
            : DateTimeOffset.MinValue;

    private static string? GetInteractionTransportMessageId(GatewayChannelInteraction interaction)
        => !string.IsNullOrWhiteSpace(interaction.SourceTransportMessageId)
            ? interaction.SourceTransportMessageId
            : interaction.SourceMessageId?.ToString(CultureInfo.InvariantCulture);

    private static JsonArray BuildChannelApprovalActions(string toolType, bool hideRequestChanges = false)
        => new([.. ApprovalActionCatalog.ForToolType(toolType)
            .Where(action => !hideRequestChanges || action.Value != ApprovalActionCatalog.RequestChanges)
            .Select(action => (JsonNode?)new JsonObject
        {
            ["value"] = action.Value,
            ["label"] = action.Label,
            ["style"] = action.Style,
        })]);

    private static JsonArray BuildWorkflowReviewFields(string? argumentsJson)
    {
        if (string.IsNullOrWhiteSpace(argumentsJson)) return [];
        try
        {
            using JsonDocument document = JsonDocument.Parse(argumentsJson);
            JsonElement root = document.RootElement;
            if (!root.TryGetProperty("envelope", out JsonElement envelopeElement)
                || envelopeElement.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("proposed", out JsonElement proposedElement)
                || proposedElement.ValueKind != JsonValueKind.Object)
            {
                return [];
            }

            JsonObject envelope = JsonNode.Parse(envelopeElement.GetRawText())!.AsObject();
            JsonObject proposed = JsonNode.Parse(proposedElement.GetRawText())!.AsObject();
            JsonArray fields = [];
            foreach ((string key, JsonNode? value) in proposed)
            {
                if (ChannelApprovalArguments.IsSensitiveProperty(key)
                    || (envelope.TryGetPropertyValue(key, out JsonNode? previous)
                        && JsonNode.DeepEquals(previous, value)))
                {
                    continue;
                }

                string safeValue = FormatWorkflowReviewValue(value);
                fields.Add((JsonNode?)new JsonObject
                {
                    ["label"] = key,
                    ["value"] = safeValue.Length > 300 ? safeValue[..299] + "…" : safeValue,
                });
                if (fields.Count == 9) break; // The remaining tenth slot is reserved for the Agent field.
            }
            return fields;
        }
        catch (JsonException)
        {
            return [];
        }
        catch (InvalidOperationException)
        {
            return [];
        }
    }

    private static string FormatWorkflowReviewValue(JsonNode? value)
    {
        JsonNode? safeValue = ChannelApprovalArguments.SanitizeForDisplay(value);
        if (safeValue is null) return "null";
        if (safeValue is JsonValue scalar)
        {
            using JsonDocument document = JsonDocument.Parse(scalar.ToJsonString());
            JsonElement element = document.RootElement;
            return element.ValueKind == JsonValueKind.String
                ? element.GetString() ?? string.Empty
                : element.ToString();
        }

        return safeValue.ToJsonString();
    }

    internal static bool HasDeliveredChannelApproval(GatewayChannelInteraction interaction)
        => !string.IsNullOrWhiteSpace(GetInteractionTransportMessageId(interaction));

    private static string? ReadWorkflowId(GatewayChatApprovalRequest approval)
    {
        if (!string.Equals(approval.ToolType, "workflow_review", StringComparison.Ordinal)
            || string.IsNullOrWhiteSpace(approval.ArgumentsJson))
        {
            return null;
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(approval.ArgumentsJson);
            return document.RootElement.TryGetProperty("workflowId", out JsonElement workflowId)
                ? workflowId.GetString()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private async Task<ApprovalRoutingContext?> ResolveApprovalRoutingContextAsync(
        string userId,
        string sessionId,
        string? targetType,
        string? targetId,
        string? toolType,
        string? workflowId,
        CancellationToken cancellationToken)
    {
        try
        {
            await attachmentRegistry.RefreshUserAsync(userId, cancellationToken).ConfigureAwait(false);
        }
        catch (RpcException ex)
        {
            ChannelIngressServiceLog.ChannelApprovalRefreshFailed(logger, ex, userId);
        }

        if (string.Equals(toolType, "workflow_review", StringComparison.Ordinal))
        {
            GatewayChannelRegistration? reviewRegistration = null;
            bool routed = attachmentRegistry.TryResolveWorkflowReviewBot(userId, workflowId, out string? reviewRegistrationId, out string? reviewChatId)
                && !string.IsNullOrWhiteSpace(reviewRegistrationId)
                && !string.IsNullOrWhiteSpace(reviewChatId)
                && attachmentRegistry.TryGetRegistration(reviewRegistrationId, out reviewRegistration)
                && reviewRegistration is not null;
            if (!routed)
            {
                reviewRegistration = null;
                routed = attachmentRegistry.TryResolveActiveRegistration(userId, out reviewRegistrationId, out reviewChatId)
                    && !string.IsNullOrWhiteSpace(reviewRegistrationId)
                    && !string.IsNullOrWhiteSpace(reviewChatId)
                    && attachmentRegistry.TryGetRegistration(reviewRegistrationId, out reviewRegistration)
                    && reviewRegistration is not null;
            }

            if (!routed || reviewRegistration is null || string.IsNullOrWhiteSpace(reviewChatId))
            {
                ChannelIngressServiceLog.ChannelApprovalNoActiveRegistration(logger, userId);
                return null;
            }

            string reviewTargetType = string.IsNullOrWhiteSpace(targetType) ? "workflow" : targetType;
            string reviewTargetId = string.IsNullOrWhiteSpace(targetId) ? workflowId ?? "workflows" : targetId;
            ChannelIngressServiceLog.ChannelApprovalRoutingResolved(logger, reviewRegistration.Id, reviewChatId, reviewTargetType, reviewTargetId);
            return new ApprovalRoutingContext(
                reviewRegistration,
                reviewChatId,
                reviewTargetType,
                reviewTargetId,
                routingCoordinator.ResolveTargetThreadId(reviewRegistration, reviewTargetType, reviewTargetId),
                routingCoordinator.ResolveTargetDisplayName(reviewTargetType, reviewTargetId));
        }

        string? registrationId = null;
        string? configuredChatId = null;
        bool routedToAgentBot = string.Equals(targetType, "agent", StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrWhiteSpace(targetId)
            && attachmentRegistry.TryResolveAgentBot(userId, targetId, out registrationId, out configuredChatId);
        if (!routedToAgentBot
            && !attachmentRegistry.TryResolveActiveRegistration(userId, out registrationId, out configuredChatId))
        {
            ChannelIngressServiceLog.ChannelApprovalNoActiveRegistration(logger, userId);
            return null;
        }

        if (string.IsNullOrWhiteSpace(registrationId)
            || !attachmentRegistry.TryGetRegistration(registrationId, out GatewayChannelRegistration? registration)
            || registration is null
            || string.IsNullOrWhiteSpace(configuredChatId))
        {
            ChannelIngressServiceLog.ChannelApprovalNoActiveRegistration(logger, userId);
            return null;
        }

        GatewaySession? currentSession = null;
        if (!string.IsNullOrWhiteSpace(sessionId))
        {
            _ = sessionStore.TryGetSession(sessionId, out currentSession);
        }

        string? resolvedTargetType = string.IsNullOrWhiteSpace(targetType) ? currentSession?.TargetType : targetType;
        string? resolvedTargetId = string.IsNullOrWhiteSpace(targetId) ? currentSession?.TargetId : targetId;
        if (string.IsNullOrWhiteSpace(resolvedTargetType) || string.IsNullOrWhiteSpace(resolvedTargetId))
        {
            GatewayChannelTargetBinding? fallbackBinding = attachmentRegistry.GetBindings(registration.Id)
                .OrderByDescending(b => b.IsDefault)
                .ThenBy(b => b.TargetType, StringComparer.Ordinal)
                .ThenBy(b => b.TargetId, StringComparer.Ordinal)
                .FirstOrDefault();

            if (fallbackBinding is null)
            {
                ChannelIngressServiceLog.ChannelApprovalNoFallbackBinding(logger, registrationId, userId, sessionId);
                return null;
            }

            resolvedTargetType = fallbackBinding.TargetType;
            resolvedTargetId = fallbackBinding.TargetId;
        }

        if (string.IsNullOrWhiteSpace(resolvedTargetType) || string.IsNullOrWhiteSpace(resolvedTargetId))
        {
            return null;
        }

        ChannelIngressServiceLog.ChannelApprovalRoutingResolved(logger, registration.Id, configuredChatId, resolvedTargetType, resolvedTargetId);
        return new ApprovalRoutingContext(
            registration,
            configuredChatId,
            resolvedTargetType,
            resolvedTargetId,
            routingCoordinator.ResolveTargetThreadId(registration, resolvedTargetType, resolvedTargetId),
            routingCoordinator.ResolveTargetDisplayName(resolvedTargetType, resolvedTargetId));
    }

    private sealed record ApprovalRoutingContext(
        GatewayChannelRegistration Registration,
        string ChatId,
        string TargetType,
        string TargetId,
        string? MessageThreadId,
        string TargetDisplayName);

}
