using System.Text.Json;

using Kautuka.Protos;

namespace Kautuka.ChannelHost.Hosting;

internal static class ChannelSemanticMessageOutboundExecutor
{
    public static async Task<SendOutboundMessageResponse> ExecuteAsync(
        string attachmentId,
        IChannelAdapter adapter,
        SendOutboundMessageRequest request,
        CancellationToken cancellationToken)
    {
        if (request.Content is null || request.Content.ActionType is not (ChannelOutboundActionTypes.SendSemanticMessage or ChannelOutboundActionTypes.EditSemanticMessage))
            return new SendOutboundMessageResponse { Accepted = false, DeliveryStatus = DeliveryStatus.Rejected, Detail = "invalid_semantic_action" };
        ChannelSemanticMessageAction action;
        try
        {
            action = JsonSerializer.Deserialize(request.Content.PayloadJson, ChannelContractsJsonContext.Default.ChannelSemanticMessageAction)
                ?? throw new JsonException("Semantic message is empty.");
        }
        catch (JsonException)
        {
            return new SendOutboundMessageResponse { Accepted = false, DeliveryStatus = DeliveryStatus.Rejected, Detail = "invalid_semantic_message" };
        }

        ChannelSemanticMessageDocument document = action.Document;
        bool hasSession = document.Blocks.Any(block => block.Type == ChannelSemanticBlockTypes.Context
            && block.Text?.StartsWith("Session:", StringComparison.Ordinal) == true);
        if (!hasSession && action.PromptSessionLabel is not null)
            document = ChannelMessageTemplates.WithSession(document, action.PromptSessionLabel);
        bool hasPrompt = document.Blocks.Any(block => block.Type == ChannelSemanticBlockTypes.Details
            && block.Text?.StartsWith("Prompt:", StringComparison.Ordinal) == true);
        if (!hasPrompt && action.PromptText is not null)
            document = ChannelMessageTemplates.WithPrompt(document, action.PromptText);

        if (ReferenceEquals(document, action.Document))
            return await adapter.SendOutboundMessageAsync(attachmentId, request, cancellationToken).ConfigureAwait(false);
        SendOutboundMessageRequest enrichedRequest = request.Clone();
        enrichedRequest.Content.PayloadJson = JsonSerializer.Serialize(action with { Document = document }, ChannelContractsJsonContext.Default.ChannelSemanticMessageAction);
        return await adapter.SendOutboundMessageAsync(attachmentId, enrichedRequest, cancellationToken).ConfigureAwait(false);
    }
}
