using System.Text.Json;

using Kautuka.Gateway.Infrastructure.Observability;
using Kautuka.Protos;

namespace Kautuka.Gateway.Domain.Channels;

internal sealed class ChannelOutboundMessagePublisher(
    IChannelOutboxStore outboxStore,
    IChannelAttachmentRegistry attachments,
    IChannelOutboundAssetUploadService? assetUploads = null) : IChannelOutboundMessagePublisher
{
    private string RequireOwner(string registrationId)
    {
        if (!attachments.TryGetRegistration(registrationId, out GatewayChannelRegistration? registration)
            || registration is null
            || string.IsNullOrWhiteSpace(registration.OwnerUserId))
            throw new InvalidOperationException("Channel registration owner is unavailable.");
        return registration.OwnerUserId;
    }

    private GatewayChannelRegistration RequireRegistration(string registrationId)
    {
        if (!attachments.TryGetRegistration(registrationId, out GatewayChannelRegistration? registration)
            || registration is null
            || string.IsNullOrWhiteSpace(registration.OwnerUserId))
            throw new InvalidOperationException("Channel registration owner is unavailable.");
        return registration;
    }

    public Task PublishTextMessageAsync(ChannelOutboundTextMessage message, CancellationToken cancellationToken)
    {
        _ = cancellationToken;

        string text = string.IsNullOrWhiteSpace(message.Text)
            ? "(empty response)"
            : message.Text;

        AgentRunHostCallTracer.AgentRunTraceContext? traceContext = AgentRunHostCallTracer.CaptureCurrentRunTraceContext();

        GatewayChannelRegistration source = RequireRegistration(message.RegistrationId);
        foreach (ChannelOutboundDestination destination in ChannelOutboundDestinations.Resolve(
            attachments, source, message.ConversationId, message.ConversationThreadId))
        {
            outboxStore.Enqueue(
                destination.Registration.ChannelType,
                destination.Registration.Id,
                "send_semantic_message",
                JsonSerializer.Serialize(
                    new ChannelSemanticMessageAction(destination.ConversationId, destination.ConversationThreadId, ChannelMessageTemplates.WithSession(
                        ChannelMessageTemplates.AgentResponse(text, message.SenderDisplayName), message.SessionDisplayName),
                        PromptSessionId: message.SessionId, PromptRootRunId: message.RootRunId),
                    ChannelContractsJsonContext.Default.ChannelSemanticMessageAction),
                null,
                traceContext?.RunId,
                traceContext?.TraceParent,
                traceContext?.TraceState,
                source.OwnerUserId);
        }

        return Task.CompletedTask;
    }

    public Task PublishTargetPickerAsync(ChannelOutboundTargetPickerMessage message, CancellationToken cancellationToken)
    {
        _ = cancellationToken;
        var actions = message.Options.Select((option, index) => new ChannelSemanticAction(
            $"target-{index}", ChannelSemanticActionTypes.Button, option.Label,
            option.CallbackToken, option.Selected ? ChannelSemanticStyles.Primary : ChannelSemanticStyles.Default));
        var document = new ChannelSemanticMessageDocument(
            ChannelSemanticContractVersions.MessageDocument,
            "target_picker",
            null,
            message.Prompt,
            [
                new(ChannelSemanticBlockTypes.Header, Text: "Choose an agent"),
                new(ChannelSemanticBlockTypes.Section, Text: message.Prompt),
                new(ChannelSemanticBlockTypes.Actions, Actions: [.. actions]),
            ]);
        outboxStore.Enqueue(
            message.ChannelType,
            message.RegistrationId,
            "send_semantic_message",
            JsonSerializer.Serialize(
                new ChannelSemanticMessageAction(message.ConversationId, message.ConversationThreadId, document),
                ChannelContractsJsonContext.Default.ChannelSemanticMessageAction),
            null, null, null, null, RequireOwner(message.RegistrationId));
        return Task.CompletedTask;
    }

    public Task PublishStreamMessageAsync(ChannelOutboundStreamMessage message, CancellationToken cancellationToken)
    {
        _ = cancellationToken;
        AgentRunHostCallTracer.AgentRunTraceContext? traceContext = AgentRunHostCallTracer.CaptureCurrentRunTraceContext();
        GatewayChannelRegistration source = RequireRegistration(message.RegistrationId);
        foreach (ChannelOutboundDestination destination in ChannelOutboundDestinations.Resolve(
            attachments, source, message.ConversationId, message.ConversationThreadId))
        {
            outboxStore.Enqueue(destination.Registration.ChannelType, destination.Registration.Id, "stream_message",
                JsonSerializer.Serialize(new ChannelStreamMessageAction(
                    message.StreamId, message.Sequence, destination.ConversationId, destination.ConversationThreadId,
                    null, message.AccumulatedText, message.SenderDisplayName, message.IsFinal),
                    ChannelContractsJsonContext.Default.ChannelStreamMessageAction),
                null, traceContext?.RunId, traceContext?.TraceParent, traceContext?.TraceState, source.OwnerUserId);
        }
        return Task.CompletedTask;
    }

    public Task<long?> PublishPromptMessageAsync(ChannelOutboundPromptMessage message, CancellationToken cancellationToken)
    {
        _ = cancellationToken;

        string prompt = string.IsNullOrWhiteSpace(message.Prompt)
            ? "(empty response)"
            : message.Prompt;

        AgentRunHostCallTracer.AgentRunTraceContext? traceContext = AgentRunHostCallTracer.CaptureCurrentRunTraceContext();
        outboxStore.Enqueue(
            message.ChannelType,
            message.RegistrationId,
            "send_semantic_message",
            JsonSerializer.Serialize(
                new ChannelSemanticMessageAction(
                    message.ConversationId,
                    message.ConversationThreadId,
                    new ChannelSemanticMessageDocument(
                        ChannelSemanticContractVersions.MessageDocument,
                        "approval_request",
                        message.SenderDisplayName,
                        prompt,
                        [
                            new(ChannelSemanticBlockTypes.Header, Text: "Approval required"),
                            new(ChannelSemanticBlockTypes.Section, Text: prompt),
                            new(ChannelSemanticBlockTypes.Actions, Actions: [.. message.Options.Select((option, index) => new ChannelSemanticAction($"decision-{index}", ChannelSemanticActionTypes.Button, option.Label, $"hitl:{message.InteractionId}:{option.Value}", option.Style, Required: true))]),
                        ]),
                    message.InteractionId),
                ChannelContractsJsonContext.Default.ChannelSemanticMessageAction),
            null,
            traceContext?.RunId,
            traceContext?.TraceParent,
            traceContext?.TraceState,
            RequireOwner(message.RegistrationId));

        return Task.FromResult(-1L as long?);
    }

    public Task<bool> PublishMessageUpdateAsync(ChannelOutboundMessageUpdate message, CancellationToken cancellationToken)
    {
        _ = cancellationToken;
        AgentRunHostCallTracer.AgentRunTraceContext? traceContext = AgentRunHostCallTracer.CaptureCurrentRunTraceContext();

        outboxStore.Enqueue(
            message.ChannelType,
            message.RegistrationId,
            "edit_message_text",
            JsonSerializer.Serialize(
                new ChannelMessageEditAction(
                    message.ConversationId,
                    message.MessageId,
                    message.Text,
                    message.ClearActions,
                    message.UseRichText ? "HTML" : null,
                    message.SenderDisplayName),
                ChannelContractsJsonContext.Default.ChannelMessageEditAction),
            null,
            traceContext?.RunId,
            traceContext?.TraceParent,
            traceContext?.TraceState,
            RequireOwner(message.RegistrationId));

        return Task.FromResult(true);
    }

    public async Task PublishFileAsync(ChannelOutboundFileMessage message, CancellationToken cancellationToken)
    {
        if (assetUploads is null) throw new InvalidOperationException("Channel outbound asset upload service is unavailable.");
        GatewayChannelRegistration source = RequireRegistration(message.RegistrationId);
        AgentRunHostCallTracer.AgentRunTraceContext? traceContext = AgentRunHostCallTracer.CaptureCurrentRunTraceContext();
        foreach (ChannelOutboundDestination destination in ChannelOutboundDestinations.Resolve(
            attachments, source, message.ConversationId, message.ConversationThreadId))
        {
            UploadOutboundAssetResponse upload = await assetUploads.UploadAsync(message.SessionId, message.OwnerUserId,
                message.AssetId, destination.Registration.Id, destination.ConversationId, destination.ConversationThreadId,
                cancellationToken).ConfigureAwait(false);
            if (!upload.Accepted || string.IsNullOrWhiteSpace(upload.UploadId))
                throw new InvalidOperationException($"Channel outbound asset upload failed: {upload.Detail}");
            outboxStore.Enqueue(destination.Registration.ChannelType, destination.Registration.Id, "send_file",
                JsonSerializer.Serialize(new ChannelOutboundFileAction(upload.UploadId, destination.ConversationId,
                    destination.ConversationThreadId, message.SafeFileName, message.ContentType, message.SizeBytes,
                    message.Caption, message.SenderDisplayName), ChannelContractsJsonContext.Default.ChannelOutboundFileAction),
                null, traceContext?.RunId, traceContext?.TraceParent, traceContext?.TraceState, message.OwnerUserId);
        }
    }
}
