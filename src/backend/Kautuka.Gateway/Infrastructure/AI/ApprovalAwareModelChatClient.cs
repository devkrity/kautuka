using System.Runtime.CompilerServices;

using Kautuka.Gateway.Infrastructure.Egress;

using Microsoft.Extensions.AI;

namespace Kautuka.Gateway.Infrastructure.AI;

internal sealed class ApprovalAwareModelChatClient(IChatClient inner, GatewayOutboundApproval approval, Uri destination)
    : DelegatingChatClient(inner)
{
    public override async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> chatMessages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        await approval.WaitAsync(destination, cancellationToken).ConfigureAwait(false);
        using IDisposable? providerScope = approval.BeginProviderRequest();
        return await base.GetResponseAsync(chatMessages, options, cancellationToken).ConfigureAwait(false);
    }

    public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> chatMessages, ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await approval.WaitAsync(destination, cancellationToken).ConfigureAwait(false);
        using IDisposable? providerScope = approval.BeginProviderRequest();
        await foreach (ChatResponseUpdate update in base.GetStreamingResponseAsync(chatMessages, options, cancellationToken)
            .ConfigureAwait(false))
        {
            yield return update;
        }
    }
}
