using ModelContextProtocol.Client;

namespace Kautuka.Gateway.Infrastructure.Agents;

internal interface IMcpHttpClientFactory
{
    TimeSpan ApprovalHold => TimeSpan.Zero;

    Task<McpClient> CreateAsync(HttpClientTransportOptions transportOptions, bool allowPrivateDestination, RemoteMcpRequestContext context, CancellationToken cancellationToken);
}
