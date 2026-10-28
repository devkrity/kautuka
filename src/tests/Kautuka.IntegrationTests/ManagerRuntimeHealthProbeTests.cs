using Kautuka.Manager.RuntimeContainer.Configuration;
using Kautuka.Manager.RuntimeContainer.Hosting;

using Microsoft.Extensions.Logging.Abstractions;

using ManagerSandboxRuntimeDefinition = Kautuka.Manager.RuntimeContainer.Configuration.SandboxRuntimeDefinition;
using ManagerWorkspaceDefinition = Kautuka.Manager.RuntimeContainer.Configuration.WorkspaceDefinition;

namespace Kautuka.IntegrationTests;

public sealed class ManagerRuntimeHealthProbeTests
{
    [Fact]
    public async Task ConfirmsATimedOutProbeBeforeReportingRuntimeUnavailable()
    {
        var command = new SequenceHealthCommand(
            TimedOut(),
            new PodmanCommandResult(0, "{}", string.Empty, ProcessTerminationReason.Completed));
        var probe = new ManagerRuntimeHealthProbe(command);

        bool available = await probe.IsAvailableAsync(TestContext.Current.CancellationToken);

        Assert.True(available);
        Assert.Equal(2, command.CallCount);
    }

    [Fact]
    public async Task ReportsAConfirmedTimeoutAsUnavailable()
    {
        var command = new SequenceHealthCommand(TimedOut(), TimedOut());
        var probe = new ManagerRuntimeHealthProbe(command);

        bool available = await probe.IsAvailableAsync(TestContext.Current.CancellationToken);

        Assert.False(available);
        Assert.Equal(2, command.CallCount);
    }

    [Fact]
    public async Task ReportsAConcreteRuntimeFailureWithoutDelayOrRetry()
    {
        var command = new SequenceHealthCommand(
            new PodmanCommandResult(125, string.Empty, "cannot connect", ProcessTerminationReason.NonZeroExit));
        var probe = new ManagerRuntimeHealthProbe(command);

        bool available = await probe.IsAvailableAsync(TestContext.Current.CancellationToken);

        Assert.False(available);
        Assert.Equal(1, command.CallCount);
    }

    [Fact]
    public async Task ConcurrentStatusRequestsShareOneBoundedRuntimeProbe()
    {
        var command = new BlockingHealthCommand();
        var probe = new ManagerRuntimeHealthProbe(command);

        Task<bool> first = probe.IsAvailableAsync(TestContext.Current.CancellationToken);
        await command.Started.Task.ConfigureAwait(true);
        Task<bool> second = probe.IsAvailableAsync(TestContext.Current.CancellationToken);

        Assert.Equal(1, command.CallCount);
        command.Release.TrySetResult();

        Assert.All(await Task.WhenAll(first, second).ConfigureAwait(true), Assert.True);
        Assert.Equal(1, command.CallCount);
    }

    [Fact]
    public async Task CompletedSampleIsReusedWithoutAnotherPodmanCall()
    {
        var command = new SequenceHealthCommand(
            new PodmanCommandResult(0, "{}", string.Empty, ProcessTerminationReason.Completed));
        var probe = new ManagerRuntimeHealthProbe(command);

        Assert.True(await probe.IsAvailableAsync(TestContext.Current.CancellationToken).ConfigureAwait(true));
        Assert.True(await probe.IsAvailableAsync(TestContext.Current.CancellationToken).ConfigureAwait(true));
        Assert.Equal(1, command.CallCount);
    }

    [Fact]
    public async Task CancelledWaiterStillStoresTheCompletedSample()
    {
        var command = new BlockingHealthCommand();
        var probe = new ManagerRuntimeHealthProbe(command);
        using var cancelled = new CancellationTokenSource();

        Task<bool> abandoned = probe.IsAvailableAsync(cancelled.Token);
        await command.Started.Task.ConfigureAwait(true);
        await cancelled.CancelAsync().ConfigureAwait(true);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => abandoned).ConfigureAwait(true);

        command.Release.TrySetResult();
        await command.Completed.Task.ConfigureAwait(true);

        Assert.True(await probe.IsAvailableAsync(TestContext.Current.CancellationToken).ConfigureAwait(true));
        Assert.Equal(1, command.CallCount);
    }

    [Fact]
    public async Task RefreshAsyncRunsANewProbeAfterACompletedSample()
    {
        var command = new SequenceHealthCommand(
            new PodmanCommandResult(0, "{}", string.Empty, ProcessTerminationReason.Completed),
            new PodmanCommandResult(125, string.Empty, "cannot connect", ProcessTerminationReason.NonZeroExit),
            new PodmanCommandResult(125, string.Empty, "cannot connect", ProcessTerminationReason.NonZeroExit));
        var probe = new ManagerRuntimeHealthProbe(command);

        Assert.True(await probe.IsAvailableAsync(TestContext.Current.CancellationToken).ConfigureAwait(true));
        Assert.False(await probe.RefreshAsync(TestContext.Current.CancellationToken).ConfigureAwait(true));
        // Failures are not sticky — the next status check re-probes.
        Assert.False(await probe.IsAvailableAsync(TestContext.Current.CancellationToken).ConfigureAwait(true));
        Assert.Equal(3, command.CallCount);
    }

    [Fact]
    public async Task FailedSampleIsNotStickyAndReprobes()
    {
        var command = new SequenceHealthCommand(
            new PodmanCommandResult(125, string.Empty, "cannot connect", ProcessTerminationReason.NonZeroExit),
            new PodmanCommandResult(0, "{}", string.Empty, ProcessTerminationReason.Completed));
        var probe = new ManagerRuntimeHealthProbe(command);

        Assert.False(await probe.IsAvailableAsync(TestContext.Current.CancellationToken).ConfigureAwait(true));
        Assert.True(await probe.IsAvailableAsync(TestContext.Current.CancellationToken).ConfigureAwait(true));
        Assert.Equal(2, command.CallCount);
    }

    [Fact]
    public async Task IsAvailableAsyncDoesNotWaitForAnInFlightRefreshWhenASampleExists()
    {
        var command = new SequenceThenBlockingHealthCommand(
            new PodmanCommandResult(0, "{}", string.Empty, ProcessTerminationReason.Completed));
        var probe = new ManagerRuntimeHealthProbe(command);

        Assert.True(await probe.IsAvailableAsync(TestContext.Current.CancellationToken).ConfigureAwait(true));

        Task<bool> refresh = probe.RefreshAsync(TestContext.Current.CancellationToken);
        await command.BlockingStarted.Task.ConfigureAwait(true);

        Assert.True(await probe.IsAvailableAsync(TestContext.Current.CancellationToken).ConfigureAwait(true));
        command.Release.TrySetResult();
        Assert.False(await refresh.ConfigureAwait(true));
        Assert.Equal(2, command.CallCount);
    }

    private static PodmanCommandResult TimedOut()
        => new(124, string.Empty, string.Empty, ProcessTerminationReason.TimedOut);

    private sealed class SequenceHealthCommand(params PodmanCommandResult[] results)
        : IManagerRuntimeHealthCommand
    {
        private readonly Queue<PodmanCommandResult> results = new(results);

        public int CallCount { get; private set; }

        public Task<PodmanCommandResult> ExecuteAsync(CancellationToken cancellationToken)
        {
            CallCount++;
            return Task.FromResult(results.Dequeue());
        }
    }

    private sealed class BlockingHealthCommand : IManagerRuntimeHealthCommand
    {
        public TaskCompletionSource Started { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Release { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Completed { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int CallCount { get; private set; }

        public async Task<PodmanCommandResult> ExecuteAsync(CancellationToken cancellationToken)
        {
            CallCount++;
            Started.TrySetResult();
            await Release.Task.ConfigureAwait(false);
            Completed.TrySetResult();
            return new PodmanCommandResult(0, "{}", string.Empty, ProcessTerminationReason.Completed);
        }
    }

    private sealed class SequenceThenBlockingHealthCommand(PodmanCommandResult first)
        : IManagerRuntimeHealthCommand
    {
        public TaskCompletionSource BlockingStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Release { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int CallCount { get; private set; }

        public async Task<PodmanCommandResult> ExecuteAsync(CancellationToken cancellationToken)
        {
            CallCount++;
            if (CallCount == 1)
            {
                return first;
            }

            BlockingStarted.TrySetResult();
            await Release.Task.ConfigureAwait(false);
            return new PodmanCommandResult(125, string.Empty, "cannot connect", ProcessTerminationReason.NonZeroExit);
        }
    }
}

public sealed class ManagerRuntimeAdapterEnsureReadyTests
{
    [Fact]
    public async Task EnsureReadyAsyncReturnsFalseWhenTheHealthProbeReportsUnavailable()
    {
        var adapter = new ManagerRuntimeAdapter(
            new PodmanCommandRunner(),
            new FixedHealthProbe(false),
            NullLogger<ManagerRuntimeAdapter>.Instance);

        bool ready = await adapter.EnsureReadyAsync(
            CreateRuntimeSettings(Path.Combine(Path.GetTempPath(), "kautuka-ensure-ready-unavailable")),
            TestContext.Current.CancellationToken);

        Assert.False(ready);
    }

    [Fact]
    public async Task EnsureReadyAsyncDoesNotCallPodmanInfoWhenTheHealthProbeIsReady()
    {
        string root = Path.Combine(Path.GetTempPath(), "kautuka-ensure-ready-" + Guid.NewGuid().ToString("N"));
        var adapter = new ManagerRuntimeAdapter(
            new PodmanCommandRunner(),
            new FixedHealthProbe(true),
            NullLogger<ManagerRuntimeAdapter>.Instance);

        bool ready = await adapter.EnsureReadyAsync(CreateRuntimeSettings(root), TestContext.Current.CancellationToken);

        Assert.True(ready);
        Assert.True(Directory.Exists(root));
    }

    private static ManagerRuntimeSettings CreateRuntimeSettings(string root)
        => new(
            new ManagerWorkspaceDefinition { BaseRoot = root },
            new ManagerSandboxRuntimeDefinition(),
            new WorkspaceTelemetryDefinition());

    private sealed class FixedHealthProbe(bool available) : IManagerRuntimeHealthProbe
    {
        public Task<bool> IsAvailableAsync(CancellationToken cancellationToken)
            => Task.FromResult(available);

        public Task<bool> RefreshAsync(CancellationToken cancellationToken)
            => Task.FromResult(available);
    }
}
