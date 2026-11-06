using System.Net;
using System.Net.Sockets;
using System.Threading.Channels;

namespace Kautuka.NetworkGateway.Dns;

/// <summary>
/// Bounded fixture listener. A root-owned network boundary must bind this explicit local
/// address to the configured ingress and exclude all other ingress/local query paths.
/// The interface is trusted configuration, not metadata observed by these sockets.
/// </summary>
internal sealed class DnsResolverListener : IDisposable
{
    private sealed record Request(IPEndPoint Peer, byte[]? Datagram, Socket? Connection, long ReceivedAt);

    private readonly Socket udp;
    private readonly Socket tcp;
    private readonly DnsQueryCoordinator coordinator;
    private readonly uint ingress;
    private readonly long generation;
    private readonly TimeSpan deadline;
    private readonly int concurrency;
    private readonly SemaphoreSlim slots;
    private readonly Channel<Request> requests;
    private int started;

    public DnsResolverListener(IPEndPoint endpoint, uint ingress, long generation,
        DnsQueryCoordinator coordinator, TimeSpan deadline, int concurrency = 16)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        ArgumentNullException.ThrowIfNull(coordinator);
        ArgumentOutOfRangeException.ThrowIfZero(ingress);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(generation);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(concurrency);
        if (endpoint.Address.AddressFamily is not (AddressFamily.InterNetwork or AddressFamily.InterNetworkV6)
            || endpoint.Address.Equals(IPAddress.Any) || endpoint.Address.Equals(IPAddress.IPv6Any)
            || endpoint.Address.IsIPv4MappedToIPv6
            || (endpoint.Address.AddressFamily == AddressFamily.InterNetworkV6 && endpoint.Address.ScopeId != 0)
            || deadline <= TimeSpan.Zero || deadline > TimeSpan.FromSeconds(5))
        {
            throw new ArgumentException("Listener requires an explicit native address and bounded deadline.", nameof(endpoint));
        }
        this.coordinator = coordinator;
        this.ingress = ingress;
        this.generation = generation;
        this.deadline = deadline;
        this.concurrency = concurrency;
        slots = new SemaphoreSlim(concurrency, concurrency);
        requests = Channel.CreateBounded<Request>(concurrency);
        udp = new Socket(endpoint.AddressFamily, SocketType.Dgram, ProtocolType.Udp);
        tcp = new Socket(endpoint.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
        try
        {
            if (endpoint.AddressFamily == AddressFamily.InterNetworkV6)
            {
                udp.DualMode = false;
                tcp.DualMode = false;
            }
            udp.Bind(endpoint);
            tcp.Bind(udp.LocalEndPoint!);
            tcp.Listen(concurrency);
        }
        catch
        {
            udp.Dispose();
            tcp.Dispose();
            slots.Dispose();
            throw;
        }
    }

    public IPEndPoint LocalEndPoint => (IPEndPoint)udp.LocalEndPoint!;

    /// <summary>Run once; cancellation drains all request resources before returning.</summary>
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        if (Interlocked.Exchange(ref started, 1) != 0)
        {
            throw new InvalidOperationException("DNS listener can run only once.");
        }
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        try
        {
            await Task.WhenAll(Enumerable.Range(0, concurrency + 2).Select(index => Guard(
                () => index == 0 ? ReceiveUdp(stop.Token) : index == 1 ? AcceptTcp(stop.Token) : ProcessRequests(stop.Token), stop))).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The caller owns shutdown. All producers/workers have completed at this point.
        }
        finally
        {
            requests.Writer.TryComplete();
            while (requests.Reader.TryRead(out Request? request))
            {
                request.Connection?.Dispose();
                slots.Release();
            }
        }
    }

    /// <summary>Await RunAsync before disposing this listener and then its coordinator/ledger.</summary>
    public void Dispose()
    {
        udp.Dispose();
        tcp.Dispose();
        slots.Dispose();
    }

    private static async Task Guard(Func<Task> action, CancellationTokenSource stop)
    {
        try
        {
            await action().ConfigureAwait(false);
        }
        finally
        {
            await stop.CancelAsync().ConfigureAwait(false);
        }
    }

    private async Task ReceiveUdp(CancellationToken token)
    {
        byte[] buffer = new byte[DnsWire.MaximumMessageBytes + 1];
        EndPoint any = new IPEndPoint(udp.AddressFamily == AddressFamily.InterNetwork ? IPAddress.Any : IPAddress.IPv6Any, 0);
        while (true)
        {
            SocketReceiveFromResult result;
            try
            {
                result = await udp.ReceiveFromAsync(buffer, SocketFlags.None, any, token).ConfigureAwait(false);
            }
            catch (SocketException error) when (error.SocketErrorCode == SocketError.MessageSize)
            {
                continue;
            }
            if (result.ReceivedBytes > DnsWire.MaximumMessageBytes || !slots.Wait(0, token))
            {
                continue;
            }
            var peer = (IPEndPoint)result.RemoteEndPoint;
            var request = new Request(new IPEndPoint(peer.Address, peer.Port), buffer.AsSpan(0, result.ReceivedBytes).ToArray(), null, TimeProvider.System.GetTimestamp());
            if (!requests.Writer.TryWrite(request))
            {
                slots.Release();
            }
        }
    }

    private async Task AcceptTcp(CancellationToken token)
    {
        while (true)
        {
            Socket connection = await tcp.AcceptAsync(token).ConfigureAwait(false);
            long receivedAt = TimeProvider.System.GetTimestamp();
            bool held = false;
            bool transferred = false;
            try
            {
                held = slots.Wait(0, CancellationToken.None);
                if (held)
                {
                    transferred = requests.Writer.TryWrite(new Request((IPEndPoint)connection.RemoteEndPoint!, null, connection, receivedAt));
                }
            }
            finally
            {
                if (!transferred)
                {
                    connection.Dispose();
                    if (held)
                    {
                        slots.Release();
                    }
                }
            }
        }
    }

    private async Task ProcessRequests(CancellationToken token)
    {
        await foreach (Request request in requests.Reader.ReadAllAsync(token).ConfigureAwait(false))
        {
            await ProcessRequest(request, token).ConfigureAwait(false);
        }
    }

    private async Task ProcessRequest(Request request, CancellationToken token)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        try
        {
            TimeSpan remaining = deadline - TimeProvider.System.GetElapsedTime(request.ReceivedAt);
            if (remaining <= TimeSpan.Zero)
            {
                return;
            }
            timeout.CancelAfter(remaining);
            byte protocol = request.Connection is null ? (byte)17 : (byte)6;
            var client = new DnsClientContext(request.Peer.Address, checked((ushort)request.Peer.Port), protocol, ingress, generation);
            if (request.Connection is null)
            {
                byte[] reply = await coordinator.ResolveAsync(request.Datagram!, client, timeout.Token).ConfigureAwait(false);
                await udp.SendToAsync(reply, SocketFlags.None, request.Peer, timeout.Token).ConfigureAwait(false);
            }
            else
            {
                using var stream = new NetworkStream(request.Connection, ownsSocket: false);
                byte[] prefix = new byte[2];
                await stream.ReadExactlyAsync(prefix, timeout.Token).ConfigureAwait(false);
                if (!DnsWire.TryFrameLength(prefix, out int length))
                {
                    return;
                }
                byte[] query = new byte[length];
                await stream.ReadExactlyAsync(query, timeout.Token).ConfigureAwait(false);
                byte[] reply = await coordinator.ResolveAsync(query, client, timeout.Token).ConfigureAwait(false);
                await stream.WriteAsync(DnsWire.Frame(reply), timeout.Token).ConfigureAwait(false);
                // Fixture subset: one framed request per connection, then close.
            }
        }
        catch (Exception error) when (error is OperationCanceledException or IOException or SocketException)
        {
            // A client disconnect, bounded timeout or send error cannot retain a worker.
        }
        finally
        {
            request.Connection?.Dispose();
            slots.Release();
        }
    }
}
