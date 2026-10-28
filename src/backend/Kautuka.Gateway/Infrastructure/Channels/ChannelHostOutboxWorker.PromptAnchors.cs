using System.Text.Json;

using Kautuka.Gateway.Domain.Channels;
using Kautuka.Protos;

namespace Kautuka.Gateway.Infrastructure.Channels;

internal sealed partial class ChannelHostOutboxWorker
{
    private ChannelPromptAnchorStore? promptAnchors;

    internal async Task<SendOutboundMessageResponse> DeliverRuntimeEventAsync(ChannelOutboxEntry entry,
        GatewayChannelRegistration registration, SendOutboundMessageRequest request, CancellationToken cancellationToken)
    {
        if (entry.ActionType != ChannelOutboundActionTypes.RuntimeEvent)
        {
            if (entry.ActionType == ChannelOutboundActionTypes.SendSemanticMessage)
            {
                ChannelSemanticMessageAction? semantic = JsonSerializer.Deserialize(entry.PayloadJson, ChannelContractsJsonContext.Default.ChannelSemanticMessageAction);
                if (semantic?.PromptSessionId is not null)
                {
                    string? semanticPromptText = semantic.PromptText ?? (semantic.PromptRootRunId is null ? null : ResolvePromptText(semantic.PromptSessionId, semantic.PromptRootRunId));
                    string? semanticSessionLabel = semantic.PromptSessionLabel ?? ResolveSessionLabel(semantic.PromptSessionId);
                    ChannelSemanticMessageAction enriched = semantic with { PromptText = semanticPromptText, PromptSessionLabel = semanticSessionLabel };
                    request.Content.PayloadJson = JsonSerializer.Serialize(enriched, ChannelContractsJsonContext.Default.ChannelSemanticMessageAction);
                }
            }

            SendOutboundMessageResponse delivered = await channelHost.SendOutboundMessageAsync(request, cancellationToken).ConfigureAwait(false);
            if (entry.ActionType == ChannelOutboundActionTypes.SendSemanticMessage && delivered.Accepted
                && !string.IsNullOrWhiteSpace(delivered.TransportMessageId))
            {
                ChannelSemanticMessageAction? semantic = JsonSerializer.Deserialize(entry.PayloadJson, ChannelContractsJsonContext.Default.ChannelSemanticMessageAction);
                if (semantic?.Document.Template == "target_picker")
                {
                    promptAnchors ??= new ChannelPromptAnchorStore(state.Database);
                    promptAnchors.SaveTargetPickerMessage(registration, semantic.ConversationId,
                        semantic.ConversationThreadId, delivered.TransportMessageId);
                }
                if (semantic?.PromptSessionId is not null)
                {
                    promptAnchors ??= new ChannelPromptAnchorStore(state.Database);
                    promptAnchors.SaveMessageSession(registration, semantic.ConversationId, semantic.ConversationThreadId,
                        semantic.PromptSessionId, delivered.TransportMessageId);
                }
            }
            return delivered;
        }

        ChannelRuntimeEventAction action = JsonSerializer.Deserialize(entry.PayloadJson, ChannelContractsJsonContext.Default.ChannelRuntimeEventAction)
            ?? throw new InvalidOperationException("Runtime event is missing.");
        if (action.ConversationId is null)
            return await channelHost.SendOutboundMessageAsync(request, cancellationToken).ConfigureAwait(false);
        promptAnchors ??= new ChannelPromptAnchorStore(state.Database);
        bool prompt = action.EventType == "user.message.recorded";
        string anchorSession = action.PromptSessionId ?? action.SessionId;
        string anchorRoot = action.PromptRootRunId ?? action.RootRunId;
        if (prompt) promptAnchors.Save(registration, action.ConversationId, action.ConversationThreadId, anchorSession, anchorRoot, null);
        string? promptText = action.PromptText ?? (action.EventType == "assistant.text.delta" ? null : ResolvePromptText(anchorSession, anchorRoot));
        string? sessionLabel = action.SessionDisplayName ?? ResolveSessionLabel(anchorSession);
        request.Content.PayloadJson = JsonSerializer.Serialize(action with { PromptText = promptText, SessionDisplayName = sessionLabel },
            ChannelContractsJsonContext.Default.ChannelRuntimeEventAction);
        SendOutboundMessageResponse result = await channelHost.SendOutboundMessageAsync(request, cancellationToken).ConfigureAwait(false);
        if (prompt && result.Accepted && !string.IsNullOrWhiteSpace(result.TransportMessageId))
            promptAnchors.Save(registration, action.ConversationId, action.ConversationThreadId,
                anchorSession, anchorRoot, result.TransportMessageId, sessionLabel);
        if (result.Accepted && !string.IsNullOrWhiteSpace(result.TransportMessageId))
            promptAnchors.SaveMessageSession(registration, action.ConversationId, action.ConversationThreadId, anchorSession, result.TransportMessageId);
        return result;
    }

    private string? ResolveSessionLabel(string sessionId)
        => state.Sessions.TryGetValue(sessionId, out GatewaySession? session) ? session.Title : null;

    private string? ResolvePromptText(string sessionId, string rootRunId)
    {
        if (!state.Sessions.TryGetValue(sessionId, out GatewaySession? session) || session.TargetType != "agent") return null;
        return state.AgentDefinitionStore.FindRunUserMessage(state.AgentDefinitionStore.GetStorageLocatorKey(session.TargetId), sessionId, rootRunId);
    }
}
