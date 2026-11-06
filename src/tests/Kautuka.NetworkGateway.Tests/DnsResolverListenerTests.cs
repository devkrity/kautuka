using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;

using Kautuka.NetworkGateway.Admission;
using Kautuka.NetworkGateway.Dns;

namespace Kautuka.NetworkGateway.Tests;

public sealed class DnsResolverListenerTests
{
    private static readonly DnsQuestion question = new(0x1234, "allowed.ktng.test", 1, 1);
    private static readonly IPAddress answer = IPAddress.Parse("192.0.2.7");

    [Theory]
    [InlineData(6, false)]
    [InlineData(6, true)]
    [InlineData(17, false)]
    [InlineData(17, true)]
    public async Task SocketExchangePreservesClientIdAndAuditsTrustedContext(byte protocol, bool ipv6)
    {
        var fixture = new Fixture(ipv6);
        await using var lifetime = fixture.ConfigureAwait(true);
        var client = new SocketDnsUpstream(fixture.Endpoint);
        byte[] reply = await client.ExchangeAsync(DnsWire.Query(question), protocol, fixture.Token).ConfigureAwait(true);
        Assert.True(DnsWire.TryParseAnswer(reply, question, out DnsAnswer? received));
        Assert.Equal(answer, received!.Address);
        Assert.Equal(1, fixture.Upstream.Calls);
        DnsDecision[] rows = fixture.Ledger.Rows.ToArray();
        Assert.Equal(2, rows.Length);
        Assert.Equal(DnsDecisionKind.AuthorizedAttempt, rows[0].Kind);
        Assert.Equal(DnsDecisionKind.Resolved, rows[1].Kind);
        Assert.All(rows, row =>
        {
            Assert.Equal("core", row.Subject);
            Assert.Equal(protocol, row.Client.Protocol);
            Assert.Equal(11u, row.Client.IngressInterfaceIndex);
            Assert.Equal(9, row.Client.NamespaceGeneration);
            Assert.Equal(ipv6 ? IPAddress.IPv6Loopback : IPAddress.Loopback, row.Client.Address);
            Assert.NotEqual(0, row.Client.Port);
        });
    }

    [Theory]
    [InlineData(6)]
    [InlineData(17)]
    public async Task DeniedQuestionIsRefusedWithoutUpstream(byte protocol)
    {
        var fixture = new Fixture();
        await using var lifetime = fixture.ConfigureAwait(true);
        DnsQuestion denied = question with { Name = "blocked.ktng.test" };
        byte[] reply = await new SocketDnsUpstream(fixture.Endpoint).ExchangeAsync(DnsWire.Query(denied), protocol, fixture.Token).ConfigureAwait(true);
        Assert.Equal(5, reply[3] & 15);
        Assert.Equal(0, fixture.Upstream.Calls);
        DnsDecision row = Assert.Single(fixture.Ledger.Rows);
        Assert.Equal(denied, row.Question);
        Assert.Null(row.UpstreamId);
    }

    [Fact]
    public async Task OversizedUdpIsDroppedAndNextValidDatagramStillWorks()
    {
        var fixture = new Fixture();
        await using var lifetime = fixture.ConfigureAwait(true);
        using var client = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        await client.ConnectAsync(fixture.Endpoint, fixture.Token).ConfigureAwait(true);
        byte[] oversized = new byte[DnsWire.MaximumMessageBytes + 500];
        DnsWire.Query(question).CopyTo(oversized, 0);
        await client.SendAsync(oversized, SocketFlags.None, fixture.Token).ConfigureAwait(true);
        using (var quiet = CancellationTokenSource.CreateLinkedTokenSource(fixture.Token))
        {
            quiet.CancelAfter(TimeSpan.FromMilliseconds(150));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
                await client.ReceiveAsync(new byte[1500], SocketFlags.None, quiet.Token).ConfigureAwait(true)).ConfigureAwait(true);
        }
        Assert.Empty(fixture.Ledger.Rows);
        byte[] reply = await new SocketDnsUpstream(fixture.Endpoint).ExchangeAsync(DnsWire.Query(question), 17, fixture.Token).ConfigureAwait(true);
        Assert.True(DnsWire.TryParseAnswer(reply, question, out _));
    }

    [Fact]
    public async Task SlowTcpPrefixExpiresAndWorkerServesNextRequest()
    {
        var fixture = new Fixture(deadline: TimeSpan.FromMilliseconds(200), concurrency: 1);
        await using var lifetime = fixture.ConfigureAwait(true);
        using var client = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        await client.ConnectAsync(fixture.Endpoint, fixture.Token).ConfigureAwait(true);
        await client.SendAsync(new byte[] { 0 }, SocketFlags.None, fixture.Token).ConfigureAwait(true);
        Assert.Equal(0, await client.ReceiveAsync(new byte[1], SocketFlags.None, fixture.Token).ConfigureAwait(true));
        Assert.Empty(fixture.Ledger.Rows);
        byte[] reply = await new SocketDnsUpstream(fixture.Endpoint).ExchangeAsync(DnsWire.Query(question), 6, fixture.Token).ConfigureAwait(true);
        Assert.True(DnsWire.TryParseAnswer(reply, question, out _));
    }

    [Fact]
    public async Task SharedCapacityDropsUdpAndClosesTcpWhileResolutionIsBusyAndShutdownDrainsIt()
    {
        var fixture = new Fixture(concurrency: 1);
        await using var lifetime = fixture.ConfigureAwait(true);
        fixture.Upstream.Block = true;
        using var first = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        await first.SendToAsync(DnsWire.Query(question), SocketFlags.None, fixture.Endpoint, fixture.Token).ConfigureAwait(true);
        await fixture.Upstream.Entered.Task.WaitAsync(fixture.Token).ConfigureAwait(true);
        using var overflow = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        await overflow.ConnectAsync(fixture.Endpoint, fixture.Token).ConfigureAwait(true);
        Assert.Equal(0, await overflow.ReceiveAsync(new byte[1], SocketFlags.None, fixture.Token).ConfigureAwait(true));
        using var other = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        await other.SendToAsync(DnsWire.Query(question), SocketFlags.None, fixture.Endpoint, fixture.Token).ConfigureAwait(true);
        using (var quiet = CancellationTokenSource.CreateLinkedTokenSource(fixture.Token))
        {
            quiet.CancelAfter(TimeSpan.FromMilliseconds(100));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
                await other.ReceiveFromAsync(new byte[1500], SocketFlags.None, new IPEndPoint(IPAddress.Any, 0), quiet.Token).ConfigureAwait(true)).ConfigureAwait(true);
        }
        Assert.Equal(1, fixture.Upstream.Calls);
        await fixture.Stop().ConfigureAwait(true);
        Assert.True(fixture.Upstream.Exited);
        Assert.Equal(DnsDecisionKind.AuthorizedAttempt, Assert.Single(fixture.Ledger.Rows).Kind);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(65535)]
    public async Task InvalidTcpFrameClosesWithoutAuditOrUpstream(ushort length)
    {
        var fixture = new Fixture();
        await using var lifetime = fixture.ConfigureAwait(true);
        using var client = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        await client.ConnectAsync(fixture.Endpoint, fixture.Token).ConfigureAwait(true);
        byte[] prefix = new byte[2];
        BinaryPrimitives.WriteUInt16BigEndian(prefix, length);
        await client.SendAsync(prefix, SocketFlags.None, fixture.Token).ConfigureAwait(true);
        Assert.Equal(0, await client.ReceiveAsync(new byte[1], SocketFlags.None, fixture.Token).ConfigureAwait(true));
        Assert.Empty(fixture.Ledger.Rows);
        Assert.Equal(0, fixture.Upstream.Calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DisconnectDuringPrefixOrBodyReleasesWorker(bool body)
    {
        var fixture = new Fixture(concurrency: 1);
        await using var lifetime = fixture.ConfigureAwait(true);
        using var client = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        await client.ConnectAsync(fixture.Endpoint, fixture.Token).ConfigureAwait(true);
        byte[] partial = body ? DnsWire.Frame(DnsWire.Query(question))[..12] : [0];
        await client.SendAsync(partial, SocketFlags.None, fixture.Token).ConfigureAwait(true);
        client.Shutdown(SocketShutdown.Send);
        Assert.Equal(0, await client.ReceiveAsync(new byte[1], SocketFlags.None, fixture.Token).ConfigureAwait(true));
        byte[] reply = await new SocketDnsUpstream(fixture.Endpoint).ExchangeAsync(DnsWire.Query(question), 6, fixture.Token).ConfigureAwait(true);
        Assert.True(DnsWire.TryParseAnswer(reply, question, out _));
        Assert.Equal(1, fixture.Upstream.Calls);
    }

    [Fact]
    public async Task UnexpectedResolutionFaultStopsAndDrainsListener()
    {
        var fixture = new Fixture();
        await using var lifetime = fixture.ConfigureAwait(true);
        fixture.Upstream.Fail = true;
        fixture.ExpectedFault = true;
        using var idle = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        await idle.ConnectAsync(fixture.Endpoint, fixture.Token).ConfigureAwait(true);
        using var client = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        await client.SendToAsync(DnsWire.Query(question), SocketFlags.None, fixture.Endpoint, fixture.Token).ConfigureAwait(true);
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Completion.WaitAsync(fixture.Token)).ConfigureAwait(true);
        Assert.Equal(0, await idle.ReceiveAsync(new byte[1], SocketFlags.None, fixture.Token).ConfigureAwait(true));
        Assert.True(fixture.Upstream.Exited);
        Assert.Equal(DnsDecisionKind.AuthorizedAttempt, Assert.Single(fixture.Ledger.Rows).Kind);
    }

    private sealed class Ledger : IDnsDecisionLedger
    {
        public ConcurrentQueue<DnsDecision> Rows { get; } = new();
        public void Append(DnsDecision decision) => Rows.Enqueue(decision);
    }

    private sealed class Upstream : IDnsUpstream
    {
        private int calls;
        public int Calls => Volatile.Read(ref calls);
        public bool Block { get; set; }
        public bool Fail { get; set; }
        public bool Exited { get; private set; }
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<byte[]> ExchangeAsync(ReadOnlyMemory<byte> query, byte protocol, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref calls);
            Entered.TrySetResult();
            try
            {
                if (Fail)
                {
                    throw new InvalidOperationException("Injected unexpected resolver failure");
                }
                if (Block)
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(true);
                }
                Assert.True(DnsWire.TryParseQuery(query.Span, out DnsQuestion? parsed));
                return DnsWire.Response(parsed!, new DnsAnswer(answer, 30));
            }
            finally
            {
                Exited = true;
            }
        }
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly CancellationTokenSource cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        private readonly DnsQueryCoordinator coordinator;
        private readonly DnsResolverListener listener;
        private readonly Task running;
        public Ledger Ledger { get; } = new();
        public Upstream Upstream { get; } = new();
        public CancellationToken Token => cancellation.Token;
        public bool ExpectedFault { get; set; }
        public Task Completion => running;
        public IPEndPoint Endpoint => listener.LocalEndPoint;

        public Fixture(bool ipv6 = false, TimeSpan? deadline = null, int concurrency = 16)
        {
            cancellation.CancelAfter(TimeSpan.FromSeconds(10));
            IPAddress address = ipv6 ? IPAddress.IPv6Loopback : IPAddress.Loopback;
            var policy = new DnsQueryPolicy(1, [new SourceRegistration(address, "core", 1, 11, 9)],
                [new DnsNameRule("core", question.Name, answer, IPAddress.Parse("2001:db8::7"))]);
            coordinator = new DnsQueryCoordinator(policy, Ledger, Upstream, TimeProvider.System, TimeSpan.FromSeconds(2));
            listener = new DnsResolverListener(new IPEndPoint(address, 0), 11, 9, coordinator, deadline ?? TimeSpan.FromSeconds(2), concurrency);
            running = listener.RunAsync(Token);
        }

        public async Task Stop()
        {
            await cancellation.CancelAsync().ConfigureAwait(true);
            try
            {
                await running.ConfigureAwait(true);
            }
            catch (InvalidOperationException) when (ExpectedFault)
            {
                // The faulting test independently observes the terminal exception.
            }
        }

        public async ValueTask DisposeAsync()
        {
            await Stop().ConfigureAwait(true);
            listener.Dispose();
            coordinator.Dispose();
            cancellation.Dispose();
        }
    }
}
