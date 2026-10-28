namespace Kautuka.Manager.Hosting;

/// <summary>
/// A singleton gate that gRPC callers await before executing any operation that requires
/// a live container runtime. <see cref="WorkspaceContainerSupervisorWorker"/> signals it
/// once that runtime can run containers. Agent sandbox startup does not close it.
/// The gate is reset only when the runtime becomes unavailable.
/// </summary>
public sealed class WorkspaceReadinessGate
{
    private volatile TaskCompletionSource tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>
    /// Returns <c>true</c> when the machine is currently in the ready state.
    /// </summary>
    public bool IsReady => tcs.Task.IsCompletedSuccessfully;

    /// <summary>
    /// Signals readiness. Safe to call multiple times in a row — subsequent calls are no-ops
    /// until <see cref="Reset"/> is called.
    /// </summary>
    public void SignalReady() => tcs.TrySetResult();

    /// <summary>
    /// Resets the gate back to "not ready", causing subsequent <see cref="WaitAsync(CancellationToken)"/> callers
    /// to block. Call this before starting a runtime reinitialisation sequence.
    /// </summary>
    public void Reset()
    {
        // Only replace the TCS if it has already completed (i.e. was ready).
        TaskCompletionSource current = tcs;
        if (!current.Task.IsCompleted)
            return;

        // Swap in a fresh, incomplete TCS. Use Interlocked to handle concurrent resets.
        Interlocked.CompareExchange(
            ref tcs,
            new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously),
            current);
    }

    /// <summary>
    /// Awaits runtime readiness until the caller cancels. A gRPC deadline is that cancellation.
    /// This wait has no shorter timeout of its own.
    /// </summary>
    public async Task WaitAsync(CancellationToken cancellationToken)
    {
        TaskCompletionSource current = tcs;
        if (current.Task.IsCompletedSuccessfully)
        {
            return;
        }

        await current.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task WaitAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(timeout, TimeSpan.Zero);
        TaskCompletionSource current = tcs;
        if (current.Task.IsCompletedSuccessfully)
            return;

        // WaitAsync applies cancellation and timeout to this caller's proxy task only. The
        // shared readiness generation remains available to every other current/future waiter.
        await current.Task.WaitAsync(timeout, cancellationToken).ConfigureAwait(false);
    }
}
