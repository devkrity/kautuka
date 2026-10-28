using Kautuka.Gateway.Domain.Runtime;

namespace Kautuka.IntegrationTests;

public sealed class ActiveRunBudgetTests
{
    [Fact]
    public async Task PauseDoesNotConsumeBudgetAndResumeContinuesRemaining()
    {
        // The paused wait has to exceed the budget for this to prove anything, while the
        // post-resume wait has to stay well inside it. Thread-pool starvation under the
        // parallel suite can stretch a short Task.Delay far past its nominal duration.
        using ActiveRunBudget budget = ActiveRunBudget.Start(TimeSpan.FromSeconds(5), TimeSpan.Zero, CancellationToken.None);

        budget.Pause();
        await Task.Delay(TimeSpan.FromSeconds(6), TestContext.Current.CancellationToken).ConfigureAwait(true);
        Assert.False(budget.Token.IsCancellationRequested);

        budget.Resume();
        Assert.False(budget.Token.IsCancellationRequested);
        await Task.Delay(30, TestContext.Current.CancellationToken).ConfigureAwait(true);
        Assert.False(budget.Token.IsCancellationRequested);
    }

    [Fact]
    public async Task ActiveBudgetCancelsWhenNotPaused()
    {
        using ActiveRunBudget budget = ActiveRunBudget.Start(TimeSpan.FromMilliseconds(40), TimeSpan.Zero, CancellationToken.None);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        try
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, budget.Token).WaitAsync(timeout.Token).ConfigureAwait(true);
        }
        catch (OperationCanceledException) when (budget.Token.IsCancellationRequested)
        {
        }

        Assert.True(budget.Token.IsCancellationRequested);
    }

    [Fact]
    public void StartWithAlreadyElapsedEqualToLimitIsTimedOut()
    {
        using ActiveRunBudget budget = ActiveRunBudget.Start(
            TimeSpan.FromMilliseconds(80),
            TimeSpan.FromMilliseconds(80),
            CancellationToken.None);

        Assert.True(budget.IsTimedOut);
        Assert.True(budget.Token.IsCancellationRequested);
    }

    [Fact]
    public async Task StartWithAlreadyElapsedCancelsOnRemainingBudgetOnly()
    {
        using ActiveRunBudget budget = ActiveRunBudget.Start(
            TimeSpan.FromMilliseconds(1200),
            TimeSpan.FromMilliseconds(600),
            CancellationToken.None);

        Assert.False(budget.Token.IsCancellationRequested);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            Task.Delay(Timeout.InfiniteTimeSpan, budget.Token)
                .WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
        Assert.True(budget.Token.IsCancellationRequested);
    }
}
