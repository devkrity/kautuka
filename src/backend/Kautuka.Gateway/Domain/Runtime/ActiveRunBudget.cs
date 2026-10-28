using System.Diagnostics;

namespace Kautuka.Gateway.Domain.Runtime;

/// <summary>
/// Bounds <strong>active</strong> run time. Pause during HITL so waiting does not consume the budget.
/// <see cref="TimeSpan.Zero"/> / null limit means infinite.
/// </summary>
internal sealed class ActiveRunBudget : IDisposable
{
    private readonly TimeSpan? remainingLimit;
    private readonly CancellationToken caller;
    private CancellationTokenSource cts;
    private TimeSpan remaining;
    private long activeStartTimestamp;
    private bool paused = true;
    private bool disposed;
    private bool timedOut;

    private ActiveRunBudget(TimeSpan? remainingLimit, TimeSpan remaining, CancellationToken caller)
    {
        this.remainingLimit = remainingLimit;
        this.remaining = remaining;
        this.caller = caller;
        cts = CancellationTokenSource.CreateLinkedTokenSource(caller);
        if (remainingLimit is not null && remaining <= TimeSpan.Zero)
        {
            timedOut = true;
            cts.Cancel();
        }
    }

    public CancellationToken Token => cts.Token;

    public bool IsTimedOut => timedOut || (remainingLimit is not null && remaining <= TimeSpan.Zero);

    public static ActiveRunBudget Start(TimeSpan? maxRunDuration, TimeSpan alreadyElapsed, CancellationToken caller)
    {
        if (maxRunDuration is null || maxRunDuration.Value <= TimeSpan.Zero)
        {
            var infinite = new ActiveRunBudget(null, TimeSpan.Zero, caller);
            infinite.Resume();
            return infinite;
        }

        TimeSpan remaining = maxRunDuration.Value - alreadyElapsed;
        var budget = new ActiveRunBudget(maxRunDuration, remaining, caller);
        budget.Resume();
        return budget;
    }

    public void Pause()
    {
        if (paused)
        {
            return;
        }

        paused = true;
        if (remainingLimit is null)
        {
            return;
        }

        remaining -= Stopwatch.GetElapsedTime(activeStartTimestamp);
        if (remaining < TimeSpan.Zero)
        {
            remaining = TimeSpan.Zero;
        }

        if (cts.IsCancellationRequested && !caller.IsCancellationRequested)
        {
            timedOut = true;
            remaining = TimeSpan.Zero;
            return;
        }

        cts.CancelAfter(Timeout.InfiniteTimeSpan);
    }

    public void Resume()
    {
        if (!paused)
        {
            return;
        }

        paused = false;
        if (remainingLimit is null)
        {
            return;
        }

        if (remaining <= TimeSpan.Zero)
        {
            timedOut = true;
            if (!cts.IsCancellationRequested)
            {
                cts.Cancel();
            }

            return;
        }

        if (cts.IsCancellationRequested && !caller.IsCancellationRequested)
        {
            cts.Dispose();
            cts = CancellationTokenSource.CreateLinkedTokenSource(caller);
        }

        activeStartTimestamp = Stopwatch.GetTimestamp();
        cts.CancelAfter(remaining);
    }

    public TimeSpan CaptureElapsed(TimeSpan previousElapsed)
    {
        TimeSpan extra = paused || remainingLimit is null
            ? TimeSpan.Zero
            : Stopwatch.GetElapsedTime(activeStartTimestamp);
        return previousElapsed + extra;
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        cts.Dispose();
    }
}
