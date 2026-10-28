using System.Reflection;

using Kautuka.Manager.RuntimeContainer.Hosting;
using Kautuka.Manager.RuntimeContainer.Sandbox;

using Microsoft.Extensions.Logging.Abstractions;

namespace Kautuka.IntegrationTests;

public sealed class ConnectorCooldownSchedulerTests
{
    [Fact]
    public async Task CancellationReleasesExpiredCallbackWaitingForConnectorLock()
    {
        var runner = new PodmanCommandRunner();
        await using var service = new WorkspaceContainerConnectorExecutionService(
            new WorkspaceDesiredStateStore(),
            runner,
            new McpbImageMaterializer(runner, NullLogger<McpbImageMaterializer>.Instance),
            new PodmanContainerTool(runner),
            NullLogger<WorkspaceContainerConnectorExecutionService>.Instance);
        var locks = (AsyncKeyedLockPool)typeof(WorkspaceContainerConnectorExecutionService)
            .GetField("connectorLocks", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(service)!;
        MethodInfo stop = typeof(WorkspaceContainerConnectorExecutionService)
            .GetMethod("StopConnectorContainerAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
        await using var scheduler = new ConnectorCooldownScheduler();
        await using IAsyncDisposable lease = await locks.AcquireAsync("connector", TestContext.Current.CancellationToken);
        var callbackEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await scheduler.ScheduleAsync("connector", TimeSpan.FromMilliseconds(10), cancellationToken =>
        {
            Task stopping = (Task)stop.Invoke(service, ["connector", "connector", "unused-container", "cooldown", cancellationToken])!;
            callbackEntered.SetResult();
            return stopping;
        });
        await callbackEntered.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        // Startup holds this same lock when cancelling the cooldown. Cancellation
        // must finish before the lease is released, without issuing Podman commands.
        await scheduler.CancelAsync("connector").WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        Assert.Equal(0, scheduler.Count);
        Assert.Empty(service.TrackedContainerNames);
    }

    [Fact]
    public async Task ResetCancelsOldGenerationAndOnlyLatestCallbackRuns()
    {
        var scheduler = new ConnectorCooldownScheduler();
        int first = 0;
        int second = 0;
        var secondCompleted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        await scheduler.ScheduleAsync("connector", TimeSpan.FromSeconds(5), () =>
        {
            Interlocked.Increment(ref first);
            return Task.CompletedTask;
        }).ConfigureAwait(true);
        await scheduler.ScheduleAsync("connector", TimeSpan.FromMilliseconds(20), cancellationToken =>
        {
            Assert.True(cancellationToken.CanBeCanceled);
            Assert.False(cancellationToken.IsCancellationRequested);
            Interlocked.Increment(ref second);
            secondCompleted.TrySetResult();
            return Task.CompletedTask;
        }).ConfigureAwait(true);

        await secondCompleted.Task
            .WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken)
            .ConfigureAwait(true);
        Assert.Equal(0, first);
        Assert.Equal(1, second);

        // The scheduler drops the registration after the callback returns, so the count
        // only settles once that continuation has run.
        DateTimeOffset deadline = DateTimeOffset.UtcNow.AddSeconds(5);
        while (scheduler.Count != 0 && DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(10, TestContext.Current.CancellationToken).ConfigureAwait(true);
        }

        Assert.Equal(0, scheduler.Count);
        await scheduler.DisposeAsync().ConfigureAwait(true);
    }

    [Fact]
    public async Task DisposalCancelsAndAwaitsAllRegistrations()
    {
        var scheduler = new ConnectorCooldownScheduler();
        await scheduler.ScheduleAsync("a", TimeSpan.FromSeconds(5), static () => Task.CompletedTask).ConfigureAwait(true);
        await scheduler.ScheduleAsync("b", TimeSpan.FromSeconds(5), static () => Task.CompletedTask).ConfigureAwait(true);

        await scheduler.DisposeAsync().ConfigureAwait(true);

        Assert.Equal(0, scheduler.Count);
    }

    [Fact]
    public async Task ConcurrentConnectorChurnThenShutdownLeavesNoDelayedWork()
    {
        var scheduler = new ConnectorCooldownScheduler();
        int callbacks = 0;
        Task[] schedules = Enumerable.Range(0, 1_000)
            .Select(index => scheduler.ScheduleAsync(
                $"connector-{index % 100}",
                TimeSpan.FromMinutes(5),
                () =>
                {
                    Interlocked.Increment(ref callbacks);
                    return Task.CompletedTask;
                }))
            .ToArray();

        await Task.WhenAll(schedules).ConfigureAwait(true);
        await scheduler.DisposeAsync().ConfigureAwait(true);

        Assert.Equal(0, callbacks);
        Assert.Equal(0, scheduler.Count);
    }
}
