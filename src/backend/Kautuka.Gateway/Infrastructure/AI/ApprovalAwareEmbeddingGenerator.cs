using Kautuka.Gateway.Infrastructure.Egress;

using Microsoft.Extensions.AI;

namespace Kautuka.Gateway.Infrastructure.AI;

internal sealed class ApprovalAwareEmbeddingGenerator(
    IEmbeddingGenerator<string, Embedding<float>> inner, GatewayOutboundApproval approval, Uri destination)
    : DelegatingEmbeddingGenerator<string, Embedding<float>>(inner)
{
    public override async Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
        IEnumerable<string> values, EmbeddingGenerationOptions? options = null, CancellationToken cancellationToken = default)
    {
        await approval.WaitAsync(destination, cancellationToken).ConfigureAwait(false);
        using IDisposable? providerScope = approval.BeginProviderRequest();
        return await base.GenerateAsync(values, options, cancellationToken).ConfigureAwait(false);
    }
}
