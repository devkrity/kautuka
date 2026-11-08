using System.Net;
using System.Net.Sockets;

using Kautuka.Gateway.Domain.Connectors;
using Kautuka.Manager.Protos;

using ModelContextProtocol.Client;

namespace Kautuka.Gateway.Infrastructure.Agents;

internal sealed class McpHttpClientFactory : IMcpHttpClientFactory
{
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Reliability", "CA2000:Dispose objects before losing scope", Justification = "The MCP client owns the transport on success; failure disposes it below.")]
    public async Task<McpClient> CreateAsync(HttpClientTransportOptions transportOptions, bool allowPrivateDestination, RemoteMcpRequestContext context, CancellationToken cancellationToken)
    {
        context = await PrepareNetworkContextAsync(context, allowPrivateDestination, cancellationToken).ConfigureAwait(false);
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

    private static async Task<RemoteMcpRequestContext> PrepareNetworkContextAsync(
        RemoteMcpRequestContext context,
        bool allowPrivateDestination,
        CancellationToken cancellationToken)
    {
        RemoteMcpConfigurationValidator.ValidateEndpoint(context.Endpoint);
        if (context.RequiresCredentialReentry)
            throw new RemoteMcpConfigurationException("This remote connector used the previous credential setup. Enter its key again in connector settings before using it.");
        if (context.CheckNetwork is null)
            throw new RemoteMcpConfigurationException("Remote MCP network authorization is unavailable. No request was sent.");

        string? pin = null;
        if (IPAddress.TryParse(context.Endpoint.IdnHost, out IPAddress? literal))
        {
            if (literal.AddressFamily == AddressFamily.InterNetwork) pin = literal.ToString();
        }
        else if (context.Endpoint.Scheme == Uri.UriSchemeHttp)
        {
            // A no-pin call distinguishes the host-development mode from NAC mode.
            // In disabled mode retain the previous address-selection behavior; in
            // enabled mode obtain a single IPv4 candidate for the exact route check.
            RemoteConnectorNetworkResponse mode = await context.CheckNetwork(context.Endpoint, null, cancellationToken).ConfigureAwait(false);
            if (mode.Enabled)
            {
                if (!string.Equals(mode.Decision, "pin_required", StringComparison.Ordinal))
                    throw DecisionException(mode);
                IPAddress[] addresses;
                try
                {
                    addresses = await Dns.GetHostAddressesAsync(context.Endpoint.IdnHost, cancellationToken).ConfigureAwait(false);
                }
                catch (SocketException exception)
                {
                    throw new RemoteMcpConfigurationException("The remote MCP address could not be found.", exception);
                }
                IPAddress[] ipv4 = [.. addresses.Where(static address => address.AddressFamily == AddressFamily.InterNetwork)];
                IPAddress? selected = ProbeNetworkPolicy.SelectAddress(ipv4, allowPrivate: false, preferIpv4: true)
                    ?? ProbeNetworkPolicy.SelectAddress(ipv4, allowPrivate: true, preferIpv4: true);
                if (selected is null)
                    throw new RemoteMcpConfigurationException("The remote MCP address has no IPv4 route that can be authorized.");
                pin = selected.ToString();
            }
            else if (!string.Equals(mode.Decision, "allowed", StringComparison.Ordinal))
            {
                throw DecisionException(mode);
            }
        }

        var routeState = new RemoteMcpNetworkRouteState(allowPrivateDestination);
        return context with
        {
            PinnedIpv4 = pin,
            LegacyAllowPrivateDestination = allowPrivateDestination,
            NetworkRouteState = routeState,
        };
    }

    private static RemoteMcpConfigurationException DecisionException(RemoteConnectorNetworkResponse response)
        => new(string.IsNullOrWhiteSpace(response.Message)
            ? "Remote MCP network access is not authorized. No request was sent."
            : response.Message);

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Reliability", "CA2000:Dispose objects before losing scope", Justification = "The handler is transferred into RemoteMcpRequestHandler, and the finally block disposes it if construction fails.")]
    private static HttpClientTransport CreateTransport(HttpClientTransportOptions options, bool allowPrivateDestination, RemoteMcpRequestContext context)
    {
        IPAddress? pin = IPAddress.TryParse(context.PinnedIpv4, out IPAddress? parsedPin) ? parsedPin : null;
        RemoteMcpNetworkRouteState routeState = context.NetworkRouteState ?? new RemoteMcpNetworkRouteState(allowPrivateDestination);
        SocketsHttpHandler? socket = ProbeNetworkPolicy.CreateRuntimeHandler(allowPrivateDestination, context.PreferIpv4,
            pin is null ? null : context.Endpoint, pin, () => routeState.AllowPrivateDestination);
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
