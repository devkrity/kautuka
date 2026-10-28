namespace Kautuka.Gateway.Domain.Execution;

internal interface IThreadCommandCoordinator
{
    ValueTask<ThreadCommandGateResult<T>> ExecuteAsync<T>(
        string agentId,
        string threadId,
        long? expectedRevision,
        Func<long, CancellationToken, ValueTask<T>> action,
        CancellationToken cancellationToken,
        Func<long>? readCurrentRevision = null);
}

/// <summary>
/// Serializes thread-scoped commands and validates projection revision against the
/// authoritative agent runtime journal immediately before invoking durable work.
/// Cross-database atomicity (gateway approvals DB vs agent runtime DB) is not available;
/// revision is re-read under this per-thread lock so validation and mutation cannot race
/// another command on the same thread within the gateway process.
/// </summary>
internal sealed class ThreadCommandCoordinator(IAgentDefinitionStore agentDefinitionStore) : IThreadCommandCoordinator
{
    private readonly object mapLock = new();
    private readonly Dictionary<string, GateEntry> gates = new(StringComparer.Ordinal);

    internal ThreadCommandCoordinatorGateTestState? GateTestState { get; set; }

    public async ValueTask<ThreadCommandGateResult<T>> ExecuteAsync<T>(
        string agentId,
        string threadId,
        long? expectedRevision,
        Func<long, CancellationToken, ValueTask<T>> action,
        CancellationToken cancellationToken,
        Func<long>? readCurrentRevision = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agentId);
        ArgumentException.ThrowIfNullOrWhiteSpace(threadId);

        string lockKey = LockKey(agentId, threadId);
        GateEntry entry = AcquireLease(lockKey);

        if (GateTestState is not null)
        {
            await GateTestState.PauseBeforeSemaphoreWaitAsync(cancellationToken).ConfigureAwait(false);
        }

        await entry.Semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            long currentRevision = readCurrentRevision is not null
                ? readCurrentRevision()
                : agentDefinitionStore.ReadNaradSessionProjectionRevision(
                    agentDefinitionStore.GetStorageLocatorKey(agentId),
                    threadId);
            if (expectedRevision is long expected && expected != currentRevision)
            {
                return new ThreadCommandGateResult<T>(
                    ThreadCommandStatus.Conflict,
                    Detail: expected > currentRevision
                        ? "Thread projection revision is ahead of the client."
                        : "Thread projection revision is stale.");
            }

            T value = await action(currentRevision, cancellationToken).ConfigureAwait(false);
            return new ThreadCommandGateResult<T>(ThreadCommandStatus.Accepted, value, currentRevision);
        }
        finally
        {
            entry.Semaphore.Release();
            ReleaseLease(lockKey, entry);
        }
    }

    private GateEntry AcquireLease(string lockKey)
    {
        lock (mapLock)
        {
            if (!gates.TryGetValue(lockKey, out GateEntry? entry))
            {
                entry = new GateEntry();
                gates[lockKey] = entry;
            }

            entry.RefCount++;
            return entry;
        }
    }

    private void ReleaseLease(string lockKey, GateEntry entry)
    {
        lock (mapLock)
        {
            entry.RefCount--;
            if (entry.RefCount == 0)
            {
                gates.Remove(lockKey);
            }
        }
    }

    private static string LockKey(string agentId, string threadId) => $"{agentId}:{threadId}";

    internal int ActiveGateCountForTests
    {
        get
        {
            lock (mapLock)
            {
                return gates.Count;
            }
        }
    }

    private sealed class GateEntry
    {
        public SemaphoreSlim Semaphore { get; } = new(1, 1);
        public int RefCount;
    }
}

internal sealed class ThreadCommandCoordinatorGateTestState
{
    private int pausedBeforeSemaphoreWait;

    public TaskCompletionSource EvictionWindowOpened { get; } =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public TaskCompletionSource ReleasePausedWaiters { get; } =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public async ValueTask PauseBeforeSemaphoreWaitAsync(CancellationToken cancellationToken)
    {
        if (Interlocked.Increment(ref pausedBeforeSemaphoreWait) != 1)
        {
            return;
        }

        EvictionWindowOpened.TrySetResult();
        await ReleasePausedWaiters.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
    }
}

internal sealed record ThreadCommandGateResult<T>(
    ThreadCommandStatus Status,
    T? Value = default,
    long CurrentRevision = 0,
    string? Detail = null);
