using Kautuka.Manager.Hosting;

namespace Kautuka.IntegrationTests;

public sealed class WorkspaceReadinessGateTests
{
    [Fact]
    public async Task CancellingOneWaiterDoesNotCancelSharedReadinessGeneration()
    {
        var gate = new WorkspaceReadinessGate();
        using var cancelledCaller = new CancellationTokenSource();
        Task cancelledWait = gate.WaitAsync(TimeSpan.FromSeconds(5), cancelledCaller.Token);
        Task survivingWait = gate.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        await cancelledCaller.CancelAsync().ConfigureAwait(true);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelledWait).ConfigureAwait(true);
        Assert.False(survivingWait.IsCompleted);

        gate.SignalReady();
        await survivingWait.ConfigureAwait(true);
        Assert.True(gate.IsReady);
    }

    [Fact]
    public async Task WaitTimeoutDoesNotChangeSharedGateState()
    {
        var gate = new WorkspaceReadinessGate();

        await Assert.ThrowsAsync<TimeoutException>(() =>
            gate.WaitAsync(TimeSpan.FromMilliseconds(10), TestContext.Current.CancellationToken)).ConfigureAwait(true);

        Assert.False(gate.IsReady);
        gate.SignalReady();
        await gate.WaitAsync(TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken).ConfigureAwait(true);
    }
}
