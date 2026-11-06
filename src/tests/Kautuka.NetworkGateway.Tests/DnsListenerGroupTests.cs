using Kautuka.NetworkGateway.Dns;

namespace Kautuka.NetworkGateway.Tests;

public sealed class DnsListenerGroupTests
{
    [Fact]
    public async Task ListenerFaultCancelsSiblingAndWaitsForItsResourceDrain()
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(5));
        CancellationToken token = deadline.Token;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var draining = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        bool drained = false;
        Task group = DnsListenerGroup.RunAsync(token,
            async stop =>
            {
                await entered.Task.WaitAsync(stop).ConfigureAwait(true);
                throw new IOException("Injected listener fault");
            },
            async stop =>
            {
                entered.SetResult();
                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, stop).ConfigureAwait(true);
                }
                finally
                {
                    draining.SetResult();
                    await release.Task.WaitAsync(token).ConfigureAwait(true);
                    drained = true;
                }
            });
        await draining.Task.WaitAsync(token).ConfigureAwait(true);
        Assert.False(group.IsCompleted);
        release.SetResult();
        await Assert.ThrowsAsync<IOException>(() => group).ConfigureAwait(true);
        Assert.True(drained);
    }

    [Fact]
    public async Task UnexpectedNormalExitStopsTheGroupAndDrainsSibling()
    {
        bool drained = false;
        await Assert.ThrowsAsync<InvalidOperationException>(() => DnsListenerGroup.RunAsync(TestContext.Current.CancellationToken,
            _ => Task.CompletedTask,
            async stop =>
            {
                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, stop).ConfigureAwait(true);
                }
                finally
                {
                    drained = true;
                }
            })).ConfigureAwait(true);
        Assert.True(drained);
    }

    [Fact]
    public async Task CallerShutdownCancelsBothListenersAndReturnsAfterDrain()
    {
        using var shutdown = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        shutdown.CancelAfter(TimeSpan.FromSeconds(5));
        CancellationToken token = shutdown.Token;
        int started = 0;
        int drained = 0;
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task Listen(CancellationToken stop)
        {
            if (Interlocked.Increment(ref started) == 2)
            {
                ready.SetResult();
            }
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, stop).ConfigureAwait(true);
            }
            finally
            {
                Interlocked.Increment(ref drained);
            }
        }
        Task group = DnsListenerGroup.RunAsync(token, Listen, Listen);
        await ready.Task.WaitAsync(token).ConfigureAwait(true);
        await shutdown.CancelAsync().ConfigureAwait(true);
        await group.ConfigureAwait(true);
        Assert.Equal(2, drained);
    }

    [Fact]
    public async Task UnrequestedCancellationIsAControlledServiceFault()
    {
        InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(() => DnsListenerGroup.RunAsync(
            TestContext.Current.CancellationToken, _ => Task.FromException(new OperationCanceledException("Unexpected cancellation")))).ConfigureAwait(true);
        Assert.IsType<OperationCanceledException>(error.InnerException);
    }
}
