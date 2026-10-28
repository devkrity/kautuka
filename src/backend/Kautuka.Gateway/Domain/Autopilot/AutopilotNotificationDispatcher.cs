using System.Text.Json;
using System.Text.Json.Nodes;

using Kautuka.Gateway.Domain.Channels;
using Kautuka.Gateway.Domain.Stores;

namespace Kautuka.Gateway.Domain.Autopilot;

internal sealed class AutopilotNotificationDispatcher(
    IGatewayAutopilotStore autopilotStore,
    IChannelAttachmentRegistry attachments,
    IChannelRoutingCoordinator routingCoordinator,
    IChannelOutboundMessagePublisher outboundMessagePublisher,
    IChannelConversationMapStore conversationMaps,
    IGatewaySessionStore? sessionStore = null) : IAutopilotNotificationDispatcher
{
    public async Task DispatchNotificationsAsync(
        GatewayScheduledRun run,
        string scheduleId,
        string? startedMessage,
        string? finalMessage,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken)
    {
        GatewayAutopilotSchedule? schedule = autopilotStore.GetSchedule(scheduleId);
        if (schedule is null)
        {
            return;
        }

        if (schedule.NotifyUiInbox)
        {
            if (!string.IsNullOrWhiteSpace(startedMessage))
            {
                autopilotStore.RecordNotification(new GatewayAutopilotNotification(
                    Id: $"ain-{Guid.NewGuid():N}",
                    ScheduledRunId: run.Id,
                    OwnerUserId: run.OwnerUserId,
                    ChannelType: "ui_inbox",
                    Destination: run.OwnerUserId,
                    Status: "sent",
                    PayloadJson: JsonSerializer.Serialize(
                        new JsonObject { ["message"] = startedMessage, ["scheduleId"] = scheduleId, ["runId"] = run.Id },
                        KautukaJsonContext.Default.JsonObject),
                    CreatedAt: nowUtc.ToString("O"),
                    SentAt: nowUtc.ToString("O"),
                    Error: null,
                    IsRead: false,
                    ReadAt: null));
            }

            if (!string.IsNullOrWhiteSpace(finalMessage))
            {
                autopilotStore.RecordNotification(new GatewayAutopilotNotification(
                    Id: $"ain-{Guid.NewGuid():N}",
                    ScheduledRunId: run.Id,
                    OwnerUserId: run.OwnerUserId,
                    ChannelType: "ui_inbox",
                    Destination: run.OwnerUserId,
                    Status: "sent",
                    PayloadJson: JsonSerializer.Serialize(
                        new JsonObject { ["message"] = finalMessage, ["scheduleId"] = scheduleId, ["runId"] = run.Id },
                        KautukaJsonContext.Default.JsonObject),
                    CreatedAt: nowUtc.ToString("O"),
                    SentAt: nowUtc.ToString("O"),
                    Error: null,
                    IsRead: false,
                    ReadAt: null));
            }
        }

        if (!schedule.NotifyChannels)
        {
            return;
        }

        await attachments.RefreshUserAsync(run.OwnerUserId, cancellationToken).ConfigureAwait(false);
        string? registrationId = null;
        string? externalUserId = null;
        bool routedToAgentBot = string.Equals(run.TargetType, "agent", StringComparison.OrdinalIgnoreCase)
            && attachments.TryResolveAgentBot(run.OwnerUserId, run.TargetId, out registrationId, out externalUserId);
        if (!routedToAgentBot
            && !attachments.TryResolveActiveRegistration(run.OwnerUserId, out registrationId, out externalUserId))
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(registrationId)
            || string.IsNullOrWhiteSpace(externalUserId))
        {
            return;
        }

        if (!attachments.TryGetRegistration(registrationId, out GatewayChannelRegistration? registration) || registration is null)
            return;

        if (!string.IsNullOrWhiteSpace(finalMessage))
        {
            await EnqueueChannelNotificationAsync(registration, externalUserId, finalMessage, run, scheduleId, nowUtc, cancellationToken).ConfigureAwait(false);
        }

        return;
    }

    private async Task EnqueueChannelNotificationAsync(
        GatewayChannelRegistration registration,
        string externalUserId,
        string message,
        GatewayScheduledRun run,
        string scheduleId,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken)
    {
        string registrationId = registration.Id;
        string channelType = registration.ChannelType;
        string? sessionTitle = run.SessionId is not null
            && sessionStore?.TryGetSession(run.SessionId, out GatewaySession? session) == true
                ? session?.Title
                : null;
        await outboundMessagePublisher.PublishTextMessageAsync(
            new ChannelOutboundTextMessage(
                channelType,
                registrationId,
                externalUserId,
                null,
                message,
                routingCoordinator.ResolveTargetDisplayName(run.TargetType, run.TargetId),
                sessionTitle,
                run.SessionId, run.RuntimeRunId),
            cancellationToken).ConfigureAwait(false);

        // The latest response becomes the default destination for the next plain-text prompt.
        // Session selection is scoped per target; switching this context does not move queued prompts.
        bool isWorkflowTarget = string.Equals(run.TargetType, AutopilotTargetTypes.Workflow, StringComparison.OrdinalIgnoreCase);
        if (!isWorkflowTarget)
        {
            await attachments.SetActiveTargetForUserAsync(registrationId, externalUserId,
                    run.TargetType, run.TargetId, cancellationToken).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(run.SessionId))
                ChannelConversationSessionBinding.BindSelectedSession(conversationMaps, registration,
                    externalUserId, null, run.TargetType, run.TargetId, run.SessionId, replaceExisting: true);
        }

        autopilotStore.RecordNotification(new GatewayAutopilotNotification(
            Id: $"ain-{Guid.NewGuid():N}",
            ScheduledRunId: run.Id,
            OwnerUserId: run.OwnerUserId,
            ChannelType: channelType,
            Destination: externalUserId,
            Status: "sent",
            PayloadJson: JsonSerializer.Serialize(
                new JsonObject { ["message"] = message, ["scheduleId"] = scheduleId, ["runId"] = run.Id },
                KautukaJsonContext.Default.JsonObject),
            CreatedAt: nowUtc.ToString("O"),
            SentAt: nowUtc.ToString("O"),
            Error: null,
            IsRead: false,
            ReadAt: null));
    }
}
