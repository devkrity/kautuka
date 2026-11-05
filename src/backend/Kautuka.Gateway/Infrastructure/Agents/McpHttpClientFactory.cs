using Kautuka.Gateway.Domain.Connectors;

using ModelContextProtocol.Client;

namespace Kautuka.Gateway.Infrastructure.Agents;

internal sealed class McpHttpClientFactory : IMcpHttpClientFactory
{
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Reliability", "CA2000:Dispose objects before losing scope", Justification = "The MCP client owns the transport on success; failure disposes it below.")]
    public async Task<McpClient> CreateAsync(HttpClientTransportOptions transportOptions, bool allowPrivateDestination, RemoteMcpRequestContext context, CancellationToken cancellationToken)
    {
        HttpClientTransport transport = CreateTransport(transportOptions, allowPrivateDestination, context);
        try
        {
            return await McpClient.CreateAsync(transport, new McpClientOptions { ProtocolVersion = context.ProtocolVersion }, cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await transport.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private static HttpClientTransport CreateTransport(HttpClientTransportOptions options, bool allowPrivateDestination, RemoteMcpRequestContext context)
    {
        SocketsHttpHandler? socket = ProbeNetworkPolicy.CreateRuntimeHandler(allowPrivateDestination, context.PreferIpv4);
        RemoteMcpRequestHandler? handler = null;
        HttpClient? client = null;
        try
        {
            handler = new RemoteMcpRequestHandler(context, socket);
            socket = null;
            client = new HttpClient(handler, disposeHandler: true);
            handler = null;
            var transport = new HttpClientTransport(options, client, loggerFactory: null, ownsHttpClient: true);
            client = null;
            return transport;
        }
        finally
        {
            client?.Dispose();
            handler?.Dispose();
            socket?.Dispose();
        }
    }

}
