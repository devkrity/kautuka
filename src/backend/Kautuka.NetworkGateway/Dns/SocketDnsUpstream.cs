using System.Net;
using System.Net.Sockets;

namespace Kautuka.NetworkGateway.Dns;

/// <summary>One connected literal-endpoint socket per fixture request, owned by the resolver UID.</summary>
internal sealed class SocketDnsUpstream : IDnsUpstream
{
    private readonly IPAddress address;
    private readonly int port;

    public SocketDnsUpstream(IPEndPoint endpoint)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        if (endpoint.Port == 0 || endpoint.Address.AddressFamily is not (AddressFamily.InterNetwork or AddressFamily.InterNetworkV6)
            || endpoint.Address.IsIPv4MappedToIPv6
            || (endpoint.Address.AddressFamily == AddressFamily.InterNetworkV6 && endpoint.Address.ScopeId != 0)
            || endpoint.Address.Equals(IPAddress.Any) || endpoint.Address.Equals(IPAddress.IPv6Any))
        {
            throw new ArgumentException("DNS upstream must be an explicit native endpoint.", nameof(endpoint));
        }
        address = new IPAddress(endpoint.Address.GetAddressBytes());
        port = endpoint.Port;
    }

    public async Task<byte[]> ExchangeAsync(ReadOnlyMemory<byte> query, byte protocol, CancellationToken cancellationToken)
    {
        if (protocol is not (6 or 17) || query.Length is < 12 or > DnsWire.MaximumMessageBytes)
        {
            throw new ArgumentException("Unsupported DNS fixture exchange.", nameof(protocol));
        }
        using var socket = new Socket(address.AddressFamily, protocol == 6 ? SocketType.Stream : SocketType.Dgram,
            protocol == 6 ? ProtocolType.Tcp : ProtocolType.Udp);
        await socket.ConnectAsync(new IPEndPoint(address, port), cancellationToken).ConfigureAwait(false);
        if (protocol == 17)
        {
            int sent = await socket.SendAsync(query, SocketFlags.None, cancellationToken).ConfigureAwait(false);
            if (sent != query.Length)
            {
                throw new IOException("DNS datagram was not sent completely.");
            }
            byte[] buffer = new byte[DnsWire.MaximumMessageBytes + 1];
            int count = await socket.ReceiveAsync(buffer, SocketFlags.None, cancellationToken).ConfigureAwait(false);
            if (count > DnsWire.MaximumMessageBytes)
            {
                throw new InvalidDataException("DNS response exceeds fixture bounds.");
            }
            return buffer.AsSpan(0, count).ToArray();
        }
        using var stream = new NetworkStream(socket, ownsSocket: false);
        await stream.WriteAsync(DnsWire.Frame(query.Span), cancellationToken).ConfigureAwait(false);
        byte[] prefix = new byte[2];
        await stream.ReadExactlyAsync(prefix, cancellationToken).ConfigureAwait(false);
        if (!DnsWire.TryFrameLength(prefix, out int length))
        {
            throw new InvalidDataException("DNS TCP response length exceeds fixture bounds.");
        }
        byte[] response = new byte[length];
        await stream.ReadExactlyAsync(response, cancellationToken).ConfigureAwait(false);
        return response;
    }
}
