using ModelContextProtocol.Client;

namespace Kautuka.Gateway.Infrastructure.Agents;

internal interface IMcpHttpClientFactory
{
    Task<McpClient> CreateAsync(
        HttpClientTransportOptions transportOptions,
        bool allowPrivateDestination,
        CancellationToken cancellationToken);
}
