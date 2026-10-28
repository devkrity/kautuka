using Kautuka.Manager.RuntimeContainer.Hosting;

namespace Kautuka.IntegrationTests;

public sealed class AsyncKeyedLockPoolTests
{
    [Fact]
    public async Task HighCardinalityChurnEvictsEveryIdleKey()
    {
        var pool = new AsyncKeyedLockPool();

        for (int index = 0; index < 2_000; index++)
        {
            await using var lease = (await pool
                .AcquireAsync($"workspace-{index}", TestContext.Current.CancellationToken)
                .ConfigureAwait(true)).ConfigureAwait(true);
        }

        Assert.Equal(0, pool.Count);
        pool.Dispose();
    }

    [Fact]
    public async Task CancelledWaiterDoesNotEvictOrReleaseActiveLease()
    {
        var pool = new AsyncKeyedLockPool();
        IAsyncDisposable active = await pool.AcquireAsync("workspace", TestContext.Current.CancellationToken).ConfigureAwait(true);
        using var cancelled = new CancellationTokenSource();
        ValueTask<IAsyncDisposable> pending = pool.AcquireAsync("workspace", cancelled.Token);

        await cancelled.CancelAsync().ConfigureAwait(true);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await pending.ConfigureAwait(true)).ConfigureAwait(true);
        Assert.Equal(1, pool.Count);

        await active.DisposeAsync().ConfigureAwait(true);
        Assert.Equal(0, pool.Count);
        pool.Dispose();
    }

    [Fact]
    public async Task ConcurrentReconcileStyleContentionDrainsEveryKeyBeforeShutdown()
    {
        var pool = new AsyncKeyedLockPool();
        int criticalSections = 0;
        Task[] contenders = Enumerable.Range(0, 2_000)
            .Select(async index =>
            {
                await using IAsyncDisposable lease = await pool
                    .AcquireAsync($"workspace-{index % 50}", TestContext.Current.CancellationToken)
                    .ConfigureAwait(true);
                Interlocked.Increment(ref criticalSections);
            })
            .ToArray();

        await Task.WhenAll(contenders).ConfigureAwait(true);

        Assert.Equal(2_000, criticalSections);
        Assert.Equal(0, pool.Count);
        pool.Dispose();
    }
}
