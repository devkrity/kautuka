using System.Collections.Concurrent;

namespace Kautuka.Gateway.Domain.Hitl;

/// <summary>
/// In-memory registry of runs that have been hard-stopped by the user.
/// When a user chooses "Stop Run", the run ID is registered here.
/// Narad intent handlers and the continuation dispatcher check this to stop active execution.
/// Cleared automatically when the run ends or on server restart.
/// </summary>
internal interface IRunStopRegistry
{
    /// <summary>Registers a stop for the given run.</summary>
    void RegisterStop(string runId);

    /// <summary>Gets a token that is canceled when the given run is stopped.</summary>
    CancellationToken GetStopToken(string runId);

    /// <summary>Returns true if a stop has been requested for <paramref name="runId"/>.</summary>
    bool IsStopped(string runId);

    /// <summary>Removes the stop entry for the given run.</summary>
    void Clear(string runId);
}

internal sealed class RunStopRegistry : IRunStopRegistry
{
    private readonly ConcurrentDictionary<string, CancellationTokenSource> stopped = new(StringComparer.Ordinal);

    public void RegisterStop(string runId)
    {
        CancellationTokenSource cts = stopped.GetOrAdd(runId, static _ => new CancellationTokenSource());
        try
        {
            cts.Cancel();
        }
        catch (ObjectDisposedException)
        {
            stopped.TryRemove(runId, out _);
            stopped.GetOrAdd(runId, static _ => new CancellationTokenSource()).Cancel();
        }
    }

    public CancellationToken GetStopToken(string runId)
        => stopped.GetOrAdd(runId, static _ => new CancellationTokenSource()).Token;

    public bool IsStopped(string runId)
        => stopped.TryGetValue(runId, out CancellationTokenSource? cts) && cts.IsCancellationRequested;

    public void Clear(string runId)
    {
        if (stopped.TryRemove(runId, out CancellationTokenSource? cts))
        {
            cts.Dispose();
        }
    }
}
