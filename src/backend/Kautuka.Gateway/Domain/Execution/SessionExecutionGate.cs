namespace Kautuka.Gateway.Domain.Execution;

internal sealed class ChannelSessionBusyException : InvalidOperationException
{
    public ChannelSessionBusyException() : base("The channel session already has an active run.") { }
    public ChannelSessionBusyException(string message) : base(message) { }
    public ChannelSessionBusyException(string message, Exception innerException) : base(message, innerException) { }
}

internal static class SessionExecutionGate
{
    private static readonly object sync = new();
    private static readonly Dictionary<string, Entry> entries = new(StringComparer.Ordinal);

    public static async ValueTask<IDisposable> EnterAsync(string key, CancellationToken cancellationToken)
    {
        Entry entry;
        lock (sync)
        {
            if (!entries.TryGetValue(key, out entry!)) entries[key] = entry = new Entry();
            entry.Users++;
        }
        try { await entry.Gate.WaitAsync(cancellationToken).ConfigureAwait(false); }
        catch { ReleaseReference(key, entry); throw; }
        return new Lease(key, entry);
    }

    private static void ReleaseReference(string key, Entry entry)
    {
        lock (sync)
        {
            if (--entry.Users != 0) return;
            entries.Remove(key);
            entry.Gate.Dispose();
        }
    }
    private sealed class Entry
    {
        public SemaphoreSlim Gate { get; } = new(1, 1);
        public int Users;
    }
    private sealed class Lease(string key, Entry entry) : IDisposable
    {
        private int released;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref released, 1) != 0) return;
            entry.Gate.Release();
            ReleaseReference(key, entry);
        }
    }
}
