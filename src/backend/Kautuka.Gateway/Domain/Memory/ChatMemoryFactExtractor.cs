using Microsoft.Extensions.AI;

namespace Kautuka.Gateway.Domain.Memory;

internal sealed class ChatMemoryFactExtractor : IMemoryFactExtractor
{
    private readonly ModelMemoryFactExtractor modelExtractor;
    private readonly HeuristicMemoryFactExtractor heuristicExtractor;

    public ChatMemoryFactExtractor(
        ModelMemoryFactExtractor modelExtractor,
        HeuristicMemoryFactExtractor heuristicExtractor)
    {
        this.modelExtractor = modelExtractor;
        this.heuristicExtractor = heuristicExtractor;
    }

    public async Task<IReadOnlyList<GatewayMemoryFact>> ExtractAsync(
        IChatClient? client,
        string targetType,
        string targetId,
        string ownerUserId,
        string userMessage,
        string assistantMessage,
        DateTimeOffset timestamp,
        CancellationToken cancellationToken = default)
    {
        _ = assistantMessage;

        IReadOnlyList<GatewayMemoryFact> aiFacts = await modelExtractor.ExtractAsync(
            client,
            targetType,
            targetId,
            ownerUserId,
            userMessage,
            timestamp,
            cancellationToken).ConfigureAwait(false);
        if (aiFacts.Count > 0)
        {
            return aiFacts;
        }

        return heuristicExtractor.Extract(targetType, targetId, ownerUserId, userMessage, timestamp);
    }
}
