namespace Kautuka.Manager.RuntimeContainer.Hosting;

internal sealed class AsyncKeyedLockPool
{
    private readonly object sync = new();
    private readonly Dictionary<string, Entry> entries = new(StringComparer.OrdinalIgnoreCase);
    private bool disposed;

    internal int Count
    {
        get
        {
            lock (sync)
            {
                return entries.Count;
            }
        }
    }

    public async ValueTask<IAsyncDisposable> AcquireAsync(string key, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        Entry entry;
        lock (sync)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (!entries.TryGetValue(key, out entry!))
            {
                entry = new Entry();
                entries.Add(key, entry);
            }

            entry.ReferenceCount++;
        }

        try
        {
            await entry.Semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
            return new Lease(this, key, entry);
        }
        catch
        {
            ReleaseReference(key, entry, releaseSemaphore: false);
            throw;
        }
    }

    private void ReleaseReference(string key, Entry entry, bool releaseSemaphore)
    {
        if (releaseSemaphore)
        {
            entry.Semaphore.Release();
        }

        lock (sync)
        {
            entry.ReferenceCount--;
            if (entry.ReferenceCount == 0 && entries.TryGetValue(key, out Entry? current) && ReferenceEquals(current, entry))
            {
                entries.Remove(key);
                entry.Semaphore.Dispose();
            }
        }
    }

    public void Dispose()
    {
        lock (sync)
        {
            disposed = true;
            if (entries.Count != 0)
            {
                throw new InvalidOperationException("Cannot dispose a keyed lock pool while leases are active.");
            }
        }
    }

    private sealed class Entry
    {
        public SemaphoreSlim Semaphore { get; } = new(1, 1);

        public int ReferenceCount { get; set; }
    }

    private sealed class Lease(AsyncKeyedLockPool owner, string key, Entry entry) : IAsyncDisposable
    {
        private AsyncKeyedLockPool? owner = owner;

        public ValueTask DisposeAsync()
        {
            AsyncKeyedLockPool? current = Interlocked.Exchange(ref owner, null);
            current?.ReleaseReference(key, entry, releaseSemaphore: true);
            return ValueTask.CompletedTask;
        }
    }
}
