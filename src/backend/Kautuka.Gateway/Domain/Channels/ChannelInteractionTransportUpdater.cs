namespace Kautuka.Gateway.Domain.Channels;

internal interface IChannelInteractionTransportUpdater
{
    bool TryUpdate(string interactionId, string? sourceMessageId);
}

internal sealed class ChannelInteractionTransportUpdater(GatewayState state) : IChannelInteractionTransportUpdater
{
    public bool TryUpdate(string interactionId, string? sourceMessageId)
    {
        if (string.IsNullOrWhiteSpace(interactionId)
            || !state.ChannelInteractions.TryGetValue(interactionId, out GatewayChannelInteraction? interaction))
        {
            return false;
        }

        long? numericSourceMessageId = long.TryParse(sourceMessageId, out long parsedMessageId)
            ? parsedMessageId
            : null;
        state.UpsertChannelInteraction(interaction with
        {
            SourceMessageId = numericSourceMessageId,
            SourceTransportMessageId = sourceMessageId,
        });
        return true;
    }
}
