using Microsoft.Extensions.AI;

namespace Kautuka.Gateway.Domain.Memory;

internal interface IMemoryFactExtractor
{
    Task<IReadOnlyList<GatewayMemoryFact>> ExtractAsync(
        IChatClient? client,
        string targetType,
        string targetId,
        string ownerUserId,
        string userMessage,
        string assistantMessage,
        DateTimeOffset timestamp,
        CancellationToken cancellationToken = default);
}
