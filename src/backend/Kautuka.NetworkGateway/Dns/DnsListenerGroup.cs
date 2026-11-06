namespace Kautuka.NetworkGateway.Dns;

/// <summary>One service lifetime: a listener failure cancels and drains every sibling.</summary>
internal static class DnsListenerGroup
{
    public static async Task RunAsync(CancellationToken cancellationToken, params Func<CancellationToken, Task>[] listeners)
    {
        ArgumentNullException.ThrowIfNull(listeners);
        if (listeners.Length == 0 || listeners.Any(listener => listener is null))
        {
            throw new ArgumentException("At least one non-null listener is required.", nameof(listeners));
        }
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        try
        {
            await Task.WhenAll(listeners.Select(listener => RunOne(listener, stop))).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // All listeners and their request resources have drained before this returns.
        }
        catch (OperationCanceledException error)
        {
            throw new InvalidOperationException("DNS listener cancelled without a service shutdown request.", error);
        }
    }

    private static async Task RunOne(Func<CancellationToken, Task> listener, CancellationTokenSource stop)
    {
        try
        {
            await listener(stop.Token).ConfigureAwait(false);
            if (!stop.IsCancellationRequested)
            {
                throw new InvalidOperationException("DNS listener stopped without a service shutdown request.");
            }
        }
        finally
        {
            await stop.CancelAsync().ConfigureAwait(false);
        }
    }
}
