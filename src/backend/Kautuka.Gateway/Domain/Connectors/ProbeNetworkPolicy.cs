using System.Net;
using System.Net.Sockets;

namespace Kautuka.Gateway.Domain.Connectors;

internal static class ProbeNetworkPolicy
{
    public static bool IsConfirmedPrivate(string? url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? uri)
            || uri.Scheme is not ("http" or "https")
            || string.IsNullOrWhiteSpace(uri.Host))
        {
            return false;
        }

        if (IsBlockedName(uri.IdnHost))
        {
            return true;
        }

        if (IPAddress.TryParse(uri.IdnHost, out IPAddress? literal))
        {
            return IsPrivate(literal);
        }

        try
        {
            return Dns.GetHostAddresses(uri.IdnHost).Any(static address => IsPrivate(address));
        }
        catch (SocketException)
        {
            return false;
        }
    }

    public static bool IsAllowed(string? url, bool allowPrivate)
    {
        if (allowPrivate)
        {
            return true;
        }

        if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? uri)
            || uri.Scheme is not ("http" or "https")
            || string.IsNullOrWhiteSpace(uri.Host)
            || IsBlockedName(uri.IdnHost))
        {
            return false;
        }

        if (IPAddress.TryParse(uri.IdnHost, out IPAddress? literal))
        {
            return !IsPrivate(literal);
        }

        try
        {
            return Dns.GetHostAddresses(uri.IdnHost).All(static address => !IsPrivate(address));
        }
        catch (SocketException)
        {
            return false;
        }
    }

    public static SocketsHttpHandler CreateProbeHandler(bool allowPrivate)
        => new()
        {
            AllowAutoRedirect = false,
            ConnectCallback = (context, cancellationToken) => ConnectAsync(context, allowPrivate, cancellationToken),
        };

    public static SocketsHttpHandler CreateRuntimeHandler(bool allowPrivate)
        => new()
        {
            ConnectCallback = (context, cancellationToken) => ConnectAsync(context, allowPrivate, cancellationToken),
        };

    private static async ValueTask<Stream> ConnectAsync(
        SocketsHttpConnectionContext context,
        bool allowPrivate,
        CancellationToken cancellationToken)
    {
        IPAddress[] addresses;
        try
        {
            addresses = await Dns.GetHostAddressesAsync(context.DnsEndPoint.Host, cancellationToken).ConfigureAwait(false);
        }
        catch (SocketException ex)
        {
            throw new HttpRequestException("Connector test destination is not allowed.", ex);
        }

        IPAddress? chosen = null;
        foreach (IPAddress address in addresses)
        {
            if (allowPrivate || !IsPrivate(address))
            {
                chosen = address;
                break;
            }
        }

        if (chosen is null)
        {
            throw new HttpRequestException("Connector test destination is not allowed.");
        }

        Socket? socket = new(chosen.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
        try
        {
            await socket.ConnectAsync(new IPEndPoint(chosen, context.DnsEndPoint.Port), cancellationToken).ConfigureAwait(false);
            Stream stream = new NetworkStream(socket, ownsSocket: true);
            socket = null;
            return stream;
        }
        finally
        {
            socket?.Dispose();
        }
    }

    private static bool IsBlockedName(string host)
        => host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
            || host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase)
            || host.Equals("metadata.google.internal", StringComparison.OrdinalIgnoreCase);

    private static bool IsPrivate(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        if (IPAddress.IsLoopback(address)
            || address.Equals(IPAddress.IPv6Any)
            || address.IsIPv6LinkLocal
            || address.IsIPv6SiteLocal)
        {
            return true;
        }

        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            byte[] bytes = address.GetAddressBytes();
            return bytes[0] == 10
                || bytes[0] == 127
                || (bytes[0] == 100 && bytes[1] >= 64 && bytes[1] <= 127)
                || (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31)
                || (bytes[0] == 192 && bytes[1] == 168)
                || (bytes[0] == 169 && bytes[1] == 254)
                || bytes[0] == 0;
        }

        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            byte[] bytes = address.GetAddressBytes();
            return (bytes[0] & 0xFE) == 0xFC || (bytes[0] == 0xFE && (bytes[1] & 0xC0) == 0x80);
        }

        return false;
    }
}
