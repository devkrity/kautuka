using System.Collections.Concurrent;

using Kautuka.Gateway.Domain.Execution;

namespace Kautuka.IntegrationTests;

public sealed class ThreadCommandCoordinatorTests
{
    [Fact]
    public async Task CoordinatorEvictsIdleGatesAfterConcurrentWaitersComplete()
    {
        var store = new ThreadCommandTestSupport.ThreadCommandAgentStore();
        var coordinator = new ThreadCommandCoordinator(store);

        Task<ThreadCommandGateResult<int>>[] tasks = Enumerable.Range(0, 32)
            .Select(index => coordinator.ExecuteAsync(
                "agent-1",
                $"thread-{index % 8}",
                null,
                static (_, _) => ValueTask.FromResult(1),
                TestContext.Current.CancellationToken).AsTask())
            .ToArray();

        ThreadCommandGateResult<int>[] results = await Task.WhenAll(tasks).ConfigureAwait(true);
        Assert.All(results, result => Assert.Equal(ThreadCommandStatus.Accepted, result.Status));
        Assert.Equal(0, coordinator.ActiveGateCountForTests);
    }

    [Fact]
    public async Task CoordinatorNeverRunsMoreThanOneBodyConcurrentlyPerKeyDuringChurn()
    {
        var store = new ThreadCommandTestSupport.ThreadCommandAgentStore();
        var coordinator = new ThreadCommandCoordinator(store);
        var activeByKey = new ConcurrentDictionary<string, int>(StringComparer.Ordinal);
        var maxByKey = new ConcurrentDictionary<string, int>(StringComparer.Ordinal);
        int globalActive = 0;
        int globalMax = 0;
        var startSignal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        async ValueTask<int> TrackConcurrency(string agentId, string threadId, long _, CancellationToken cancellationToken)
        {
            await startSignal.Task.WaitAsync(cancellationToken).ConfigureAwait(false);

            string key = $"{agentId}:{threadId}";
            int activeForKey = activeByKey.AddOrUpdate(key, 1, (_, current) => current + 1);
            maxByKey.AddOrUpdate(key, activeForKey, (_, currentMax) => Math.Max(currentMax, activeForKey));

            int activeGlobal = Interlocked.Increment(ref globalActive);
            int previousGlobalMax = globalMax;
            while (activeGlobal > previousGlobalMax
                && Interlocked.CompareExchange(ref globalMax, activeGlobal, previousGlobalMax) != previousGlobalMax)
            {
                previousGlobalMax = globalMax;
            }

            await Task.Delay(15, cancellationToken).ConfigureAwait(false);

            activeByKey.AddOrUpdate(key, 0, (_, current) => current - 1);
            Interlocked.Decrement(ref globalActive);
            return 1;
        }

        const string sharedAgentId = "agent-1";
        const string sharedThreadId = "thread-shared";
        List<Task<ThreadCommandGateResult<int>>> tasks = [];
        for (int index = 0; index < 24; index++)
        {
            tasks.Add(coordinator.ExecuteAsync(
                sharedAgentId,
                sharedThreadId,
                null,
                (_, ct) => TrackConcurrency(sharedAgentId, sharedThreadId, _, ct),
                TestContext.Current.CancellationToken).AsTask());
        }

        for (int index = 0; index < 8; index++)
        {
            string threadId = $"thread-other-{index}";
            tasks.Add(coordinator.ExecuteAsync(
                sharedAgentId,
                threadId,
                null,
                (_, ct) => TrackConcurrency(sharedAgentId, threadId, _, ct),
                TestContext.Current.CancellationToken).AsTask());
        }

        startSignal.SetResult();
        ThreadCommandGateResult<int>[] results = await Task.WhenAll(tasks).ConfigureAwait(true);

        Assert.All(results, result => Assert.Equal(ThreadCommandStatus.Accepted, result.Status));
        Assert.Equal(1, maxByKey[$"{sharedAgentId}:{sharedThreadId}"]);
        Assert.True(globalMax >= 2, $"Expected concurrent execution across different keys, observed max {globalMax}.");
        Assert.Equal(0, coordinator.ActiveGateCountForTests);
    }

    [Fact]
    public async Task CoordinatorLinearizesLeaseAndEvictionWhenCallerPausesBeforeSemaphoreWait()
    {
        var store = new ThreadCommandTestSupport.ThreadCommandAgentStore();
        var coordinator = new ThreadCommandCoordinator(store);
        var gateTestState = new ThreadCommandCoordinatorGateTestState();
        coordinator.GateTestState = gateTestState;
        var activeByKey = new ConcurrentDictionary<string, int>(StringComparer.Ordinal);
        var maxByKey = new ConcurrentDictionary<string, int>(StringComparer.Ordinal);

        const string agentId = "agent-1";
        const string threadId = "thread-race";
        string key = $"{agentId}:{threadId}";

        async ValueTask<int> TrackBody(long _, CancellationToken cancellationToken)
        {
            int activeForKey = activeByKey.AddOrUpdate(key, 1, (_, current) => current + 1);
            maxByKey.AddOrUpdate(key, activeForKey, (_, currentMax) => Math.Max(currentMax, activeForKey));
            await Task.Delay(10, cancellationToken).ConfigureAwait(false);
            activeByKey.AddOrUpdate(key, 0, (_, current) => current - 1);
            return 1;
        }

        Task<ThreadCommandGateResult<int>> pausedLease = coordinator.ExecuteAsync(
            agentId,
            threadId,
            null,
            TrackBody,
            TestContext.Current.CancellationToken).AsTask();

        await gateTestState.EvictionWindowOpened.Task.WaitAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);
        Assert.Equal(1, coordinator.ActiveGateCountForTests);

        ThreadCommandGateResult<int> evictionCycle = await coordinator.ExecuteAsync(
            agentId,
            threadId,
            null,
            static (_, _) => ValueTask.FromResult(2),
            TestContext.Current.CancellationToken).ConfigureAwait(true);

        Assert.Equal(ThreadCommandStatus.Accepted, evictionCycle.Status);
        Assert.Equal(1, coordinator.ActiveGateCountForTests);

        gateTestState.ReleasePausedWaiters.TrySetResult();

        ThreadCommandGateResult<int> pausedResult = await pausedLease.ConfigureAwait(true);
        Assert.Equal(ThreadCommandStatus.Accepted, pausedResult.Status);
        Assert.Equal(1, maxByKey[key]);
        Assert.Equal(0, coordinator.ActiveGateCountForTests);
    }
}
