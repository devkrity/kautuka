using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;

using Kautuka.Gateway.Domain.AgentFiles;
using Kautuka.Gateway.Domain.Assets;
using Kautuka.Gateway.Domain.Execution;
using Kautuka.Gateway.Domain.Sandbox;
using Kautuka.Gateway.Domain.Stores;
using Kautuka.Protos;

using Narad.Contracts;

namespace Kautuka.Gateway.Domain.Channels;

internal interface IChannelRuntimeEventProjector
{
    ValueTask ProjectAsync(NaradPersistedEvent persistedEvent, CancellationToken cancellationToken = default);

    ValueTask ProjectBoundAsync(
        NaradPersistedEvent persistedEvent,
        string registrationId,
        string conversationId,
        string? conversationThreadId,
        string? interactionId = null,
        CancellationToken cancellationToken = default);

    ValueTask ProjectApprovalRequestedAsync(
        NaradPersistedEvent persistedEvent,
        string registrationId,
        string conversationId,
        string? conversationThreadId,
        string interactionId,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Maps journal events onto channel mirrors.
/// Ownership: one map per (registration, chat/thread). Rebind session only when the event belongs
/// to the <em>surface</em> agent (attachment default / map target). Nested AAAT children never rebind.
/// </summary>
internal sealed partial class ChannelRuntimeEventProjector(
    IChannelOutboxStore outboxStore,
    IChannelAttachmentRegistry attachments,
    IChannelConversationMapStore conversationMapStore,
    GatewayState state,
    IGatewaySessionStore sessionStore,
    IConversationAssetStore? assets = null,
    IWorkspaceRuntimeTool? workspace = null,
    IChannelOutboundAssetUploadService? assetUploads = null) : IChannelRuntimeEventProjector, IRuntimeEventPublicationObserver
{
    private readonly ConcurrentDictionary<string, byte> projectedEventIds = new(StringComparer.Ordinal);

    public ValueTask OnPublishedAsync(NaradPersistedEvent persistedEvent, CancellationToken cancellationToken = default)
        => ProjectAsync(persistedEvent, cancellationToken);

    public async ValueTask ProjectAsync(NaradPersistedEvent persistedEvent, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(persistedEvent);

        // Approval request/resolved cards are owned by HITL ProjectBound paths only.
        if (persistedEvent.EventType is NaradEventTypes.InterruptRequested
            or NaradEventTypes.InterruptResolved)
        {
            return;
        }

        bool isView = string.Equals(persistedEvent.EventType, NaradEventTypes.ViewOpened, StringComparison.Ordinal);
        string wireEventType = string.Empty;
        string payloadJson = string.Empty;
        if (!isView && !ChannelNaradProjectionPayload.TryBuild(persistedEvent, out wireEventType, out payloadJson))
        {
            return;
        }

        if (IsAutopilotResponse(persistedEvent)) return;

        // AAAT parent conversation lane (sourceEventId) is for browser journal continuity only.
        // ChannelHost already receives surface-agent journals; replaying child run.finished here
        // prematurely mirrors the nested agent final under the parent display name.
        if (!string.IsNullOrWhiteSpace(NaradPersistedEventReader.String(persistedEvent, "sourceEventId")))
        {
            return;
        }

        // Nested agent-as-tool journals are keyed with parent* lineage. Their message.user / run
        // finals must never fan out to the surface conversation (duplicate child prompt).
        if (!string.IsNullOrWhiteSpace(NaradPersistedEventReader.String(persistedEvent, "parentSessionId"))
            || !string.IsNullOrWhiteSpace(NaradPersistedEventReader.String(persistedEvent, "parentToolCallId"))
            || !string.IsNullOrWhiteSpace(NaradPersistedEventReader.String(persistedEvent, "parentRunId")))
        {
            return;
        }

        // agent_tool child sessions are never channel surfaces (approvals use ProjectBound).
        if (IsAgentToolChildSession(persistedEvent.SessionId))
        {
            return;
        }

        string? userId = ResolveOwnerUserId(persistedEvent);
        if (string.IsNullOrWhiteSpace(userId))
        {
            return;
        }

        string? eventAgentId = NaradPersistedEventReader.String(persistedEvent, "agentId");
        List<(GatewayChannelRegistration Registration, string ChatId)> destinations = [];
        foreach (GatewayChannelRegistration linked in attachments.GetActiveChatRegistrations(userId))
        {
            string? chatId = string.Equals(linked.ChannelType, "slack", StringComparison.OrdinalIgnoreCase)
                ? linked.Settings.GetStringNodeValue("slack_dm_channel_id")
                : linked.Settings.GetStringNodeValue("channel_device_id");
            if (!string.IsNullOrWhiteSpace(chatId)) destinations.Add((linked, chatId));
        }
        if (!string.IsNullOrWhiteSpace(eventAgentId)
            && attachments.TryResolveAgentBot(userId, eventAgentId, out string? agentRegistrationId, out string? agentChatId)
            && !string.IsNullOrWhiteSpace(agentRegistrationId)
            && !string.IsNullOrWhiteSpace(agentChatId)
            && attachments.TryGetRegistration(agentRegistrationId, out GatewayChannelRegistration? agentRegistration)
            && agentRegistration is { IsActive: true }
            && destinations.All(item => item.Registration.Id != agentRegistration.Id))
        {
            destinations.RemoveAll(item => string.Equals(item.Registration.ChannelType, agentRegistration.ChannelType, StringComparison.OrdinalIgnoreCase));
            destinations.Add((agentRegistration, agentChatId));
        }
        if (destinations.Count == 0) return;

        string? admittedSource = NaradPersistedEventReader.String(persistedEvent, "sourceChannel");
        string? sourceProvider = ResolveChannelMessageSource(admittedSource, persistedEvent.SessionId);

        foreach ((GatewayChannelRegistration registration, string configuredChatId) in destinations)
        {
            // The provider already displays the user's own inbound message. Mirror the
            // gateway-recorded prompt to the other linked providers without echoing it
            // back into the channel that received it.
            if (string.Equals(wireEventType, ChannelNaradProjectionPayload.WireEventTypes.UserMessageRecorded, StringComparison.Ordinal)
                && sourceProvider is not null
                && string.Equals(registration.ChannelType, sourceProvider, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            await ProjectToRegistrationAsync(persistedEvent, wireEventType, payloadJson, isView,
                eventAgentId, registration, configuredChatId, cancellationToken).ConfigureAwait(false);
        }
    }

    private async ValueTask ProjectToRegistrationAsync(
        NaradPersistedEvent persistedEvent,
        string wireEventType,
        string payloadJson,
        bool isView,
        string? eventAgentId,
        GatewayChannelRegistration registration,
        string configuredChatId,
        CancellationToken cancellationToken)
    {

        GatewayChannelTargetBinding? defaultBinding = attachments.GetDefaultBinding(registration.Id);
        ChannelConversationMapEntry? map = conversationMapStore.TryResolveBySession(
            persistedEvent.SessionId,
            registration.ChannelType);

        if (map is not null && !string.Equals(map.RegistrationId, registration.Id, StringComparison.Ordinal)) map = null;

        bool mirrorOtherOwnedAgent = false;
        if (map is null && !string.IsNullOrWhiteSpace(configuredChatId))
        {
            map = ResolveOrBindSurfaceMap(
                registration,
                defaultBinding,
                configuredChatId,
                conversationThreadId: null,
                persistedEvent.SessionId,
                eventAgentId);
            mirrorOtherOwnedAgent = map is null && IsOtherOwnedAgentSession(persistedEvent.SessionId, eventAgentId);
            if (map is null && !mirrorOtherOwnedAgent)
            {
                // Nested child (or unbindable event) — do not invent a second mirror on the DM.
                return;
            }
        }

        bool slackPrivateDm = string.Equals(registration.ChannelType, "slack", StringComparison.OrdinalIgnoreCase);
        string? conversationId = slackPrivateDm
            ? configuredChatId
            : map?.ProviderConversationId ?? (string.IsNullOrWhiteSpace(configuredChatId) ? null : configuredChatId);
        if (string.IsNullOrWhiteSpace(conversationId))
        {
            return;
        }

        string? surfaceAgentId = !string.IsNullOrWhiteSpace(map?.TargetId)
            ? map.TargetId
            : defaultBinding?.TargetId;

        // Nested AAAT child journals must not appear on the surface agent DM.
        // An agent that stays on the account bot is still delivered here.
        if (!mirrorOtherOwnedAgent
            && !string.IsNullOrWhiteSpace(surfaceAgentId)
            && !string.IsNullOrWhiteSpace(eventAgentId)
            && !string.Equals(eventAgentId, surfaceAgentId, StringComparison.Ordinal))
        {
            return;
        }

        string? outboundThreadId = slackPrivateDm
            ? null
            : map is null
            ? ChannelThreadRouting.OutboundTopic(registration, conversationId, null, "agent", eventAgentId ?? string.Empty)
            : ResolveOutboundThreadId(registration, map, conversationId);

        bool establishesLatestContext = wireEventType is ChannelNaradProjectionPayload.WireEventTypes.AssistantMessageCompleted
            or ChannelNaradProjectionPayload.WireEventTypes.RunFinished
            or ChannelNaradProjectionPayload.WireEventTypes.ToolApprovalRequested
            or ChannelNaradProjectionPayload.WireEventTypes.ToolApprovalResolved;
        if (establishesLatestContext && !string.IsNullOrWhiteSpace(eventAgentId)
            && map?.ProviderThreadId.StartsWith("task:", StringComparison.Ordinal) is not true)
        {
            await SetLatestChannelContextAsync(registration, conversationId, outboundThreadId, eventAgentId, persistedEvent.SessionId, cancellationToken).ConfigureAwait(false);
        }

        if (isView)
        {
            await EnqueueViewAsync(persistedEvent, registration, conversationId, outboundThreadId, eventAgentId, cancellationToken).ConfigureAwait(false);
            return;
        }

        Enqueue(
            persistedEvent,
            wireEventType,
            payloadJson,
            registration,
            conversationId,
            outboundThreadId,
            NaradPersistedEventReader.String(persistedEvent, "interactionId"),
            mirrorOtherOwnedAgent ? eventAgentId : surfaceAgentId ?? eventAgentId);
        return;
    }

    private async ValueTask SetLatestChannelContextAsync(
        GatewayChannelRegistration registration,
        string conversationId,
        string? conversationThreadId,
        string targetId,
        string sessionId,
        CancellationToken cancellationToken)
    {
        if (attachments.TryResolveIdentity(registration.Id, conversationId, conversationId, out GatewayChannelIdentityLink? identity)
            && identity is not null)
        {
            string selected = $"agent:{targetId}";
            string? current = registration.Settings["active_target_by_external_user"] is JsonObject activeByUser
                ? activeByUser[identity.ExternalUserId]?.ToString()
                : null;
            if (!string.Equals(current, selected, StringComparison.Ordinal))
            {
                await attachments.SetActiveTargetForUserAsync(registration.Id, identity.ExternalUserId,
                    "agent", targetId, cancellationToken).ConfigureAwait(false);
            }
        }

        ChannelConversationSessionBinding.BindSelectedSession(conversationMapStore, registration,
            conversationId, conversationThreadId, "agent", targetId, sessionId, replaceExisting: true);
    }

    public async ValueTask ProjectBoundAsync(
        NaradPersistedEvent persistedEvent,
        string registrationId,
        string conversationId,
        string? conversationThreadId,
        string? interactionId = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(persistedEvent);
        if (IsAutopilotResponse(persistedEvent)) return;
        if (string.IsNullOrWhiteSpace(registrationId)
            || string.IsNullOrWhiteSpace(conversationId)
            || !attachments.TryGetRegistration(registrationId, out GatewayChannelRegistration? registration)
            || registration is null
            || !registration.IsActive
            || !ChannelNaradProjectionPayload.TryBuild(persistedEvent, out string wireEventType, out string payloadJson))
        {
            return;
        }

        GatewayChannelTargetBinding? defaultBinding = attachments.GetDefaultBinding(registration.Id);
        if (string.Equals(registration.ChannelType, "slack", StringComparison.OrdinalIgnoreCase))
        {
            conversationId = registration.Settings.GetStringNodeValue("slack_dm_channel_id") ?? string.Empty;
            conversationThreadId = null;
            if (!conversationId.StartsWith('D')) return;
        }
        ChannelConversationMapEntry? boundMap = conversationMapStore.TryResolveBySession(persistedEvent.SessionId, registration.ChannelType);
        if (boundMap is not null && !string.Equals(boundMap.RegistrationId, registration.Id, StringComparison.Ordinal)) boundMap = null;
        string? surfaceAgentId = boundMap?.TargetId
            ?? NaradPersistedEventReader.String(persistedEvent, "agentId") ?? defaultBinding?.TargetId;
        bool slackPrivateDm = string.Equals(registration.ChannelType, "slack", StringComparison.OrdinalIgnoreCase);
        string? providerTopic = slackPrivateDm ? null : ChannelThreadRouting.ProviderTopic(conversationId, conversationThreadId)
            ?? ChannelThreadRouting.ProviderTopic(conversationId, boundMap?.ProviderThreadId);
        conversationThreadId = slackPrivateDm ? null : ChannelThreadRouting.OutboundTopic(registration, conversationId, providerTopic,
            boundMap?.TargetType ?? "agent", surfaceAgentId ?? string.Empty);
        if (!string.IsNullOrWhiteSpace(surfaceAgentId)
            && !string.IsNullOrWhiteSpace(boundMap?.TargetId)
            && string.Equals(boundMap.TargetType, "agent", StringComparison.OrdinalIgnoreCase))
        {
            await SetLatestChannelContextAsync(registration, conversationId, conversationThreadId,
                surfaceAgentId, persistedEvent.SessionId, cancellationToken).ConfigureAwait(false);
        }
        Enqueue(
            persistedEvent,
            wireEventType,
            payloadJson,
            registration,
            conversationId,
            conversationThreadId,
            interactionId ?? NaradPersistedEventReader.String(persistedEvent, "interactionId"),
            surfaceAgentId);
        return;
    }

    public ValueTask ProjectApprovalRequestedAsync(
        NaradPersistedEvent persistedEvent,
        string registrationId,
        string conversationId,
        string? conversationThreadId,
        string interactionId,
        CancellationToken cancellationToken = default)
        => ProjectBoundAsync(
            persistedEvent,
            registrationId,
            conversationId,
            conversationThreadId,
            interactionId,
            cancellationToken);

    /// <summary>
    /// Bind or rebind the chat mirror only for surface-agent traffic. Child agents never take the map.
    /// </summary>
    private ChannelConversationMapEntry? ResolveOrBindSurfaceMap(
        GatewayChannelRegistration registration,
        GatewayChannelTargetBinding? defaultBinding,
        string configuredChatId,
        string? conversationThreadId,
        string sessionId,
        string? eventAgentId)
    {
        string targetType = defaultBinding?.TargetType ?? "agent";
        string? surfaceTargetId = defaultBinding?.TargetId;
        if (string.IsNullOrWhiteSpace(surfaceTargetId))
        {
            return null;
        }

        // Require an explicit surface agent stamp. Missing agentId used to bind AAAT child
        // sessions onto the DM (and rebind parent maps), then re-echo the child user prompt.
        if (string.IsNullOrWhiteSpace(eventAgentId)
            || !string.Equals(eventAgentId, surfaceTargetId, StringComparison.Ordinal))
        {
            return null;
        }

        // Preserve the established root session during migration to target-scoped keys.
        string providerThreadId = ChannelThreadRouting.ConversationKey(registration, configuredChatId, conversationThreadId, targetType, surfaceTargetId);
        if (conversationMapStore.TryResolve(registration.ChannelType, registration.Id, configuredChatId, configuredChatId) is { } legacy
            && ChannelThreadRouting.MatchesTarget(legacy, targetType, surfaceTargetId)) providerThreadId = legacy.ProviderThreadId;
        ChannelConversationMapEntry? existingForChat = conversationMapStore.TryResolve(
            registration.ChannelType,
            registration.Id,
            configuredChatId,
            providerThreadId);

        if (existingForChat is null)
        {
            return conversationMapStore.GetOrCreatePermanentTopic(
                registration.ChannelType, registration.Id, targetType, surfaceTargetId,
                configuredChatId, providerThreadId, sessionId);
        }

        // Existing map is for a different surface agent — keep isolation.
        if (!string.IsNullOrWhiteSpace(existingForChat.TargetId)
            && !string.Equals(existingForChat.TargetId, surfaceTargetId, StringComparison.Ordinal))
        {
            return null;
        }

        // Surface activity never changes an explicitly selected conversation session.
        if (string.IsNullOrWhiteSpace(existingForChat.SessionId))
            return conversationMapStore.AttachSession(existingForChat.Id, sessionId) ?? existingForChat with { SessionId = sessionId };

        return existingForChat;
    }

    private async ValueTask EnqueueViewAsync(
        NaradPersistedEvent persistedEvent,
        GatewayChannelRegistration registration,
        string conversationId,
        string? conversationThreadId,
        string? agentId,
        CancellationToken cancellationToken)
    {
        if (!projectedEventIds.TryAdd($"{registration.Id}:{persistedEvent.EventId}", 0))
        {
            return;
        }

        using JsonDocument document = JsonDocument.Parse(persistedEvent.EventJson);
        if (!document.RootElement.TryGetProperty("model", out JsonElement model))
        {
            return;
        }

        string viewId = model.TryGetProperty("viewId", out JsonElement viewIdElement)
            ? viewIdElement.GetString() ?? string.Empty
            : string.Empty;
        string title = model.TryGetProperty("title", out JsonElement titleElement)
            ? titleElement.GetString() ?? "Update"
            : "Update";
        var lines = new List<string>();
        var buttons = new List<ChannelInlineKeyboardButton>();
        var files = new List<(string Name, string MediaType, string Path)>();
        if (model.TryGetProperty("blocks", out JsonElement blocks) && blocks.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement block in blocks.EnumerateArray())
            {
                string type = block.TryGetProperty("type", out JsonElement typeElement) ? typeElement.GetString() ?? string.Empty : string.Empty;
                if (type == "text" && block.TryGetProperty("text", out JsonElement textElement))
                {
                    string? text = textElement.GetString();
                    if (!string.IsNullOrWhiteSpace(text))
                    {
                        lines.Add(text);
                    }
                }
                else if (type == "record")
                {
                    string status = ReadBlockString(block, "status");
                    if (!string.IsNullOrWhiteSpace(status))
                    {
                        lines.Add(status);
                    }

                    if (block.TryGetProperty("fields", out JsonElement fields) && fields.ValueKind == JsonValueKind.Array)
                    {
                        foreach (JsonElement field in fields.EnumerateArray())
                        {
                            string label = ReadBlockString(field, "label");
                            string value = ReadBlockString(field, "value");
                            if (!string.IsNullOrWhiteSpace(label) && !string.IsNullOrWhiteSpace(value))
                            {
                                lines.Add($"{label}: {value}");
                            }
                        }
                    }

                    string body = ReadBlockString(block, "body");
                    if (!string.IsNullOrWhiteSpace(body))
                    {
                        lines.Add(body);
                    }
                }
                else if (type == "file" && block.TryGetProperty("name", out JsonElement nameElement))
                {
                    string fileName = nameElement.GetString() ?? "file";
                    lines.Add(fileName);
                    bool unavailable = block.TryGetProperty("unavailable", out JsonElement unavailableElement) && unavailableElement.ValueKind == JsonValueKind.True;
                    if (!unavailable
                        && block.TryGetProperty("path", out JsonElement pathElement)
                        && pathElement.GetString() is string relativePath
                        && relativePath.StartsWith("view-files/", StringComparison.Ordinal))
                    {
                        string mediaType = block.TryGetProperty("mediaType", out JsonElement mediaElement)
                            ? mediaElement.GetString() ?? "application/octet-stream"
                            : "application/octet-stream";
                        files.Add((fileName, mediaType, relativePath));
                    }
                }
                else if (type == "actions" && block.TryGetProperty("items", out JsonElement items) && items.ValueKind == JsonValueKind.Array)
                {
                    foreach (JsonElement item in items.EnumerateArray())
                    {
                        string actionName = item.TryGetProperty("name", out JsonElement actionNameElement) ? actionNameElement.GetString() ?? string.Empty : string.Empty;
                        string actionLabel = item.TryGetProperty("label", out JsonElement actionLabelElement) ? actionLabelElement.GetString() ?? actionName : actionName;
                        if (string.IsNullOrWhiteSpace(actionName) || string.IsNullOrWhiteSpace(viewId))
                        {
                            continue;
                        }

                        string callback = $"v:{viewId}:{actionName}";
                        if (callback.Length <= 64)
                        {
                            buttons.Add(new ChannelInlineKeyboardButton(actionLabel, callback));
                        }
                    }
                }
            }
        }

        string textBody = lines.Count == 0 ? title : string.Join("\n\n", lines);
        if (textBody.Length > 3500)
        {
            textBody = textBody[..3500];
        }

        string? threadId = NormalizeOutboundThreadId(conversationId, conversationThreadId);
        if (buttons.Any(static button => button.CallbackData.EndsWith(":reply", StringComparison.OrdinalIgnoreCase)))
        {
            new ChannelPromptAnchorStore(state.Database).SaveViewSession(
                registration, conversationId, threadId, viewId, persistedEvent.SessionId);
        }
        if (buttons.Count == 0)
        {
            outboxStore.Enqueue(
                registration.ChannelType,
                registration.Id,
                "send_text",
                JsonSerializer.Serialize(new ChannelTextMessageAction(conversationId, threadId, textBody), ChannelContractsJsonContext.Default.ChannelTextMessageAction),
                availableAtUtc: null,
                runId: persistedEvent.RootRunId,
                ownerUserId: registration.OwnerUserId);
        }
        else
        {
            outboxStore.Enqueue(
                registration.ChannelType,
                registration.Id,
                "send_inline_keyboard",
                JsonSerializer.Serialize(
                    new ChannelInlineKeyboardAction(conversationId, threadId, textBody, [new ChannelInlineKeyboardRow(buttons)]),
                    ChannelContractsJsonContext.Default.ChannelInlineKeyboardAction),
                availableAtUtc: null,
                runId: persistedEvent.RootRunId,
                ownerUserId: registration.OwnerUserId);
        }

        await SendViewFilesAsync(persistedEvent, registration, conversationId, threadId, agentId, files, cancellationToken).ConfigureAwait(false);
    }

    private async Task SendViewFilesAsync(
        NaradPersistedEvent persistedEvent,
        GatewayChannelRegistration registration,
        string conversationId,
        string? threadId,
        string? agentId,
        List<(string Name, string MediaType, string Path)> files,
        CancellationToken cancellationToken)
    {
        if (files.Count == 0 || assets is null || workspace is null || assetUploads is null || string.IsNullOrWhiteSpace(agentId))
        {
            return;
        }

        string root = workspace.DescribeAgentWorkspace(agentId, registration.OwnerUserId).HostWorkspacePath;
        if (string.IsNullOrWhiteSpace(root))
        {
            return;
        }

        const long maxBytes = 15L * 1024 * 1024;
        foreach ((string name, string mediaType, string relativePath) in files)
        {
            if (!WorkspaceFile.TryReadUnderRoot(root, relativePath, maxBytes, out byte[]? bytes, out _) || bytes is null)
            {
                continue;
            }

            using var stream = new MemoryStream(bytes);
            GatewayConversationAsset asset = await assets.CreateUploadAsync(
                persistedEvent.SessionId,
                registration.OwnerUserId,
                new AssetUploadCommand(name, mediaType, bytes.LongLength, "agentOutput", "attachment", stream),
                cancellationToken).ConfigureAwait(false);
            UploadOutboundAssetResponse upload = await assetUploads.UploadAsync(
                persistedEvent.SessionId,
                registration.OwnerUserId,
                asset.Id,
                registration.Id,
                conversationId,
                threadId,
                cancellationToken).ConfigureAwait(false);
            if (!upload.Accepted || string.IsNullOrWhiteSpace(upload.UploadId))
            {
                continue;
            }

            outboxStore.Enqueue(
                registration.ChannelType,
                registration.Id,
                "send_file",
                JsonSerializer.Serialize(
                    new ChannelOutboundFileAction(upload.UploadId, conversationId, threadId, name, mediaType, bytes.LongLength, null, null),
                    ChannelContractsJsonContext.Default.ChannelOutboundFileAction),
                availableAtUtc: null,
                runId: persistedEvent.RootRunId,
                ownerUserId: registration.OwnerUserId);
        }
    }

    private void Enqueue(
        NaradPersistedEvent persistedEvent,
        string wireEventType,
        string payloadJson,
        GatewayChannelRegistration registration,
        string conversationId,
        string? conversationThreadId,
        string? interactionId,
        string? agentIdOverride = null)
    {
        if (!projectedEventIds.TryAdd($"{registration.Id}:{persistedEvent.EventId}", 0))
        {
            return;
        }

        // Always accumulate delta text in Gateway so completed / run.finished can stamp content.
        // Never outbox progressive deltas: ChannelHost translates them to an empty action list
        // and still counts SendOutboundMessage against the mutation window.
        if (string.Equals(wireEventType, ChannelNaradProjectionPayload.WireEventTypes.AssistantTextDelta, StringComparison.Ordinal))
        {
            ChannelNaradProjectionPayload.AccumulateTextDelta(
                registration.Id,
                persistedEvent.RootRunId ?? persistedEvent.RunId ?? string.Empty,
                payloadJson);
            return;
        }

        if (string.Equals(wireEventType, ChannelNaradProjectionPayload.WireEventTypes.AssistantMessageCompleted, StringComparison.Ordinal))
        {
            payloadJson = ChannelNaradProjectionPayload.AttachAccumulatedContent(
                registration.Id,
                persistedEvent.RootRunId ?? persistedEvent.RunId ?? string.Empty,
                payloadJson);
            // Empty complete (thinking-only path, etc.) — no outbound noise.
            if (!ChannelNaradProjectionPayload.HasContent(payloadJson))
            {
                return;
            }
        }

        if (string.Equals(wireEventType, ChannelNaradProjectionPayload.WireEventTypes.RunFinished, StringComparison.Ordinal))
        {
            payloadJson = ChannelNaradProjectionPayload.AttachRunFinishedFallbackText(
                registration.Id,
                persistedEvent.RootRunId ?? persistedEvent.RunId ?? string.Empty,
                payloadJson);
            // Empty run.finished is a no-op for channel (no response field / no leftover buffer).
            if (!ChannelNaradProjectionPayload.HasContent(payloadJson)
                && !ChannelNaradProjectionPayload.HasRunFinishedResponse(payloadJson))
            {
                return;
            }
        }

        string? agentId = agentIdOverride ?? NaradPersistedEventReader.String(persistedEvent, "agentId");
        // Some providers use the conversation id as the map thread key; that is not a provider topic.
        string? outboundThreadId = NormalizeOutboundThreadId(conversationId, conversationThreadId);
        (string promptSessionId, string promptRootRunId) = ResolvePromptContext(persistedEvent, registration, agentId);
        _ = sessionStore.TryGetSession(promptSessionId, out GatewaySession? displaySession);
        string? promptText = wireEventType != ChannelNaradProjectionPayload.WireEventTypes.AssistantTextDelta
            && displaySession?.TargetType == "agent"
            ? state.AgentDefinitionStore.FindRunUserMessage(state.AgentDefinitionStore.GetStorageLocatorKey(displaySession.TargetId), promptSessionId, promptRootRunId)
            : null;
        var action = new ChannelRuntimeEventAction(
            persistedEvent.EventId,
            wireEventType,
            payloadJson,
            registration.OwnerUserId,
            registration.Id,
            persistedEvent.SessionId,
            persistedEvent.RootRunId ?? persistedEvent.RunId ?? string.Empty,
            persistedEvent.RunId ?? string.Empty,
            conversationId,
            outboundThreadId,
            agentId,
            interactionId,
            ResolveAgentDisplayName(agentId),
            displaySession?.Title,
            PromptSessionId: promptSessionId, PromptRootRunId: promptRootRunId, PromptText: promptText,
            AgentIcon: ResolveAgentIcon(agentId), AgentColor: ResolveAgentColor(agentId));

        outboxStore.Enqueue(
            registration.ChannelType,
            registration.Id,
            ChannelOutboundActionTypes.RuntimeEvent,
            JsonSerializer.Serialize(action, ChannelContractsJsonContext.Default.ChannelRuntimeEventAction),
            availableAtUtc: null,
            runId: persistedEvent.RootRunId ?? persistedEvent.RunId,
            ownerUserId: registration.OwnerUserId);
    }

    private static string? NormalizeOutboundThreadId(string conversationId, string? conversationThreadId)
        => ChannelThreadRouting.ProviderTopic(conversationId, conversationThreadId);

    private static string? ResolveOutboundThreadId(GatewayChannelRegistration registration, ChannelConversationMapEntry? map, string conversationId)
        => map is null ? null : ChannelThreadRouting.OutboundTopic(registration, conversationId, map.ProviderThreadId, map.TargetType, map.TargetId);

    private string? ResolveOwnerUserId(NaradPersistedEvent persistedEvent)
    {
        string? userId = NaradPersistedEventReader.String(persistedEvent, "userId");
        if (!string.IsNullOrWhiteSpace(userId))
        {
            return userId;
        }

        return sessionStore.TryGetSession(persistedEvent.SessionId, out GatewaySession? session)
            && session is not null
            && !string.IsNullOrWhiteSpace(session.OwnerUserId)
                ? session.OwnerUserId
                : null;
    }

    private static bool IsAutopilotResponse(NaradPersistedEvent persistedEvent)
        // AutopilotNotificationDispatcher owns these results, including empty/error
        // completion notices and schedule notification preferences. HITL resumes have
        // sourceChannel=hitl and retain their runtime delivery path.
        => persistedEvent.EventType is NaradEventTypes.MessageTextDelta or NaradEventTypes.MessageTextCompleted or NaradEventTypes.RunFinished
            && string.Equals(NaradPersistedEventReader.String(persistedEvent, "sourceChannel"), "autopilot", StringComparison.OrdinalIgnoreCase);

    private static bool IsChannelAdmitSource(string? sourceChannel)
    {
        if (string.IsNullOrWhiteSpace(sourceChannel))
        {
            return false;
        }

        return sourceChannel.Equals("telegram", StringComparison.OrdinalIgnoreCase)
            || sourceChannel.Equals("slack", StringComparison.OrdinalIgnoreCase)
            || sourceChannel.Equals("channel", StringComparison.OrdinalIgnoreCase);
    }

    private string? ResolveChannelMessageSource(string? sourceChannel, string sessionId)
    {
        if (string.Equals(sourceChannel, "telegram", StringComparison.OrdinalIgnoreCase)
            || string.Equals(sourceChannel, "slack", StringComparison.OrdinalIgnoreCase))
        {
            return string.Equals(sourceChannel, "slack", StringComparison.OrdinalIgnoreCase) ? "slack" : "telegram";
        }

        if ((string.IsNullOrWhiteSpace(sourceChannel) || IsChannelAdmitSource(sourceChannel))
            && sessionStore.TryGetSession(sessionId, out GatewaySession? session)
            && session is not null
            && (string.Equals(session.Originator, "telegram", StringComparison.OrdinalIgnoreCase)
                || string.Equals(session.Originator, "slack", StringComparison.OrdinalIgnoreCase)))
        {
            return string.Equals(session.Originator, "slack", StringComparison.OrdinalIgnoreCase) ? "slack" : "telegram";
        }

        return null;
    }

    private bool IsOtherOwnedAgentSession(string sessionId, string? eventAgentId)
    {
        if (string.IsNullOrWhiteSpace(eventAgentId)
            || !sessionStore.TryGetSession(sessionId, out GatewaySession? session)
            || session is null)
        {
            return false;
        }

        return string.Equals(session.TargetType, "agent", StringComparison.OrdinalIgnoreCase)
            && string.Equals(session.TargetId, eventAgentId, StringComparison.Ordinal);
    }

    private bool IsAgentToolChildSession(string sessionId)
    {
        if (!sessionStore.TryGetSession(sessionId, out GatewaySession? session) || session is null)
        {
            return false;
        }

        string? origin = session.Originator?.Trim();
        return !string.IsNullOrWhiteSpace(origin)
            && origin.Equals("agent_tool", StringComparison.OrdinalIgnoreCase);
    }

    private string? ResolveAgentDisplayName(string? agentId)
    {
        if (string.IsNullOrWhiteSpace(agentId))
        {
            return null;
        }

        return state.Agents.TryGetValue(agentId, out GatewayAgent? agent)
            && !string.IsNullOrWhiteSpace(agent?.Name)
                ? agent.Name.Trim()
                : null;
    }

    private string? ResolveAgentIcon(string? agentId)
    {
        if (string.IsNullOrWhiteSpace(agentId)
            || !state.Agents.TryGetValue(agentId, out GatewayAgent? agent)
            || agent?.Persona is not JsonObject persona)
            return null;

        return GatewayPersonaProfileMapper.NormalizeIdentityIcon(persona.GetStringNodeValue("icon"));
    }

    private string? ResolveAgentColor(string? agentId)
    {
        if (string.IsNullOrWhiteSpace(agentId)
            || !state.Agents.TryGetValue(agentId, out GatewayAgent? agent)
            || agent?.Persona is not JsonObject persona)
            return null;

        string? color = persona.GetStringNodeValue("color");
        return color is "#0F766E" or "#2563EB" or "#7C3AED" or "#DB2777" or "#EA580C" or "#16A34A"
            ? color
            : null;
    }

    private static string ReadBlockString(JsonElement element, string name)
        => element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;

}
