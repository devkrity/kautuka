using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;

using Kautuka.NetworkGateway.Dns;

namespace Kautuka.NetworkGateway.Tests;

public sealed class SocketDnsUpstreamTests
{
    private static readonly DnsQuestion question = new(0x1234, "allowed.ktng.test", 1, 1);

    [Theory]
    [InlineData(6, false)]
    [InlineData(6, true)]
    [InlineData(17, false)]
    [InlineData(17, true)]
    public async Task ConnectedTransportReturnsACompleteBoundedReply(byte protocol, bool ipv6)
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        cancellation.CancelAfter(TimeSpan.FromSeconds(5));
        CancellationToken token = cancellation.Token;
        using var server = Bind(protocol, ipv6);
        byte[] reply = DnsWire.Response(question, new DnsAnswer(IPAddress.Parse("192.0.2.7"), 30));
        var upstream = new SocketDnsUpstream((IPEndPoint)server.LocalEndPoint!);
        Task<byte[]> receiving = upstream.ExchangeAsync(DnsWire.Query(question), protocol, token);
        await Task.WhenAll(protocol == 6 ? TcpReply(server, DnsWire.Frame(reply), token) : UdpReply(server, reply, token), receiving).ConfigureAwait(true);
        Assert.Equal(reply, await receiving.ConfigureAwait(true));
    }

    [Fact]
    public async Task ConnectedUdpRejectsAValidLookingReplyFromAnotherSourcePort()
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        cancellation.CancelAfter(TimeSpan.FromSeconds(5));
        CancellationToken token = cancellation.Token;
        using var server = Bind(17);
        using var other = Bind(17);
        byte[] approved = DnsWire.Response(question, new DnsAnswer(IPAddress.Parse("192.0.2.7"), 30));
        byte[] forged = DnsWire.Response(question, new DnsAnswer(IPAddress.Parse("192.0.2.99"), 30));
        var upstream = new SocketDnsUpstream((IPEndPoint)server.LocalEndPoint!);
        Task<byte[]> pending = upstream.ExchangeAsync(DnsWire.Query(question), 17, token);
        await Task.WhenAll(SpoofThenReply(server, other, forged, approved, token), pending).ConfigureAwait(true);
        Assert.Equal(approved, await pending.ConfigureAwait(true));
    }

    [Fact]
    public async Task UdpReceiveHonorsCancellationAndRejectsOversizedMessages()
    {
        using var server = Bind(17);
        var upstream = new SocketDnsUpstream((IPEndPoint)server.LocalEndPoint!);
        using (var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken))
        {
            cancellation.CancelAfter(TimeSpan.FromMilliseconds(100));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => upstream.ExchangeAsync(DnsWire.Query(question), 17, cancellation.Token)).ConfigureAwait(true);
        }
        using var oversized = Bind(17);
        using var bounded = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        bounded.CancelAfter(TimeSpan.FromSeconds(5));
        var receiver = new SocketDnsUpstream((IPEndPoint)oversized.LocalEndPoint!);
        Task<InvalidDataException> rejected = Assert.ThrowsAsync<InvalidDataException>(() => receiver.ExchangeAsync(DnsWire.Query(question), 17, bounded.Token));
        await Task.WhenAll(UdpReply(oversized, new byte[DnsWire.MaximumMessageBytes + 1], bounded.Token), rejected).ConfigureAwait(true);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task TcpRejectsOversizedAndIncompleteFrames(bool oversized)
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        cancellation.CancelAfter(TimeSpan.FromSeconds(5));
        CancellationToken token = cancellation.Token;
        using var server = Bind(6);
        byte[] reply = oversized ? [255, 255] : [0, 20, 0, 0, 0];
        var upstream = new SocketDnsUpstream((IPEndPoint)server.LocalEndPoint!);
        Task rejected = oversized
            ? Assert.ThrowsAsync<InvalidDataException>(() => upstream.ExchangeAsync(DnsWire.Query(question), 6, token))
            : Assert.ThrowsAsync<EndOfStreamException>(() => upstream.ExchangeAsync(DnsWire.Query(question), 6, token));
        await Task.WhenAll(TcpReply(server, reply, token), rejected).ConfigureAwait(true);
    }

    private static Socket Bind(byte protocol, bool ipv6 = false)
    {
        var socket = new Socket(ipv6 ? AddressFamily.InterNetworkV6 : AddressFamily.InterNetwork, protocol == 6 ? SocketType.Stream : SocketType.Dgram,
            protocol == 6 ? ProtocolType.Tcp : ProtocolType.Udp);
        socket.Bind(new IPEndPoint(ipv6 ? IPAddress.IPv6Loopback : IPAddress.Loopback, 0));
        if (protocol == 6)
        {
            socket.Listen(1);
        }
        return socket;
    }

    private static async Task TcpReply(Socket server, byte[] reply, CancellationToken token)
    {
        using Socket connection = await server.AcceptAsync(token).ConfigureAwait(true);
        using var stream = new NetworkStream(connection, ownsSocket: false);
        byte[] prefix = new byte[2];
        await stream.ReadExactlyAsync(prefix, token).ConfigureAwait(true);
        int length = BinaryPrimitives.ReadUInt16BigEndian(prefix);
        Assert.InRange(length, 12, DnsWire.MaximumMessageBytes);
        byte[] query = new byte[length];
        await stream.ReadExactlyAsync(query, token).ConfigureAwait(true);
        Assert.True(DnsWire.TryParseQuery(query, out _));
        await stream.WriteAsync(reply.AsMemory(0, 1), token).ConfigureAwait(true);
        await Task.Delay(TimeSpan.FromMilliseconds(10), token).ConfigureAwait(true);
        await stream.WriteAsync(reply.AsMemory(1), token).ConfigureAwait(true);
    }

    private static async Task SpoofThenReply(Socket server, Socket other, byte[] forged, byte[] approved, CancellationToken token)
    {
        byte[] buffer = new byte[DnsWire.MaximumMessageBytes];
        SocketReceiveFromResult query = await server.ReceiveFromAsync(buffer, SocketFlags.None,
            new IPEndPoint(server.AddressFamily == AddressFamily.InterNetworkV6 ? IPAddress.IPv6Any : IPAddress.Any, 0), token).ConfigureAwait(true);
        Assert.True(DnsWire.TryParseQuery(buffer.AsSpan(0, query.ReceivedBytes), out _));
        await other.SendToAsync(forged, SocketFlags.None, query.RemoteEndPoint, token).ConfigureAwait(true);
        await Task.Delay(TimeSpan.FromMilliseconds(10), token).ConfigureAwait(true);
        await server.SendToAsync(approved, SocketFlags.None, query.RemoteEndPoint, token).ConfigureAwait(true);
    }

    private static async Task UdpReply(Socket server, byte[] reply, CancellationToken token)
    {
        byte[] buffer = new byte[DnsWire.MaximumMessageBytes];
        SocketReceiveFromResult query = await server.ReceiveFromAsync(buffer, SocketFlags.None,
            new IPEndPoint(server.AddressFamily == AddressFamily.InterNetworkV6 ? IPAddress.IPv6Any : IPAddress.Any, 0), token).ConfigureAwait(true);
        Assert.True(DnsWire.TryParseQuery(buffer.AsSpan(0, query.ReceivedBytes), out _));
        await server.SendToAsync(reply, SocketFlags.None, query.RemoteEndPoint, token).ConfigureAwait(true);
    }
}
