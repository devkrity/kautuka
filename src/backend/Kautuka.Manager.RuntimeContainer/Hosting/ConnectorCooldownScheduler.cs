using System.Collections.Concurrent;

namespace Kautuka.Manager.RuntimeContainer.Hosting;

internal sealed class ConnectorCooldownScheduler : IAsyncDisposable
{
    private readonly ConcurrentDictionary<string, Registration> registrations = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, int> activeUses = new(StringComparer.OrdinalIgnoreCase);
    private int disposed;

    internal int Count => registrations.Count;
    internal bool IsScheduled(string key) => registrations.ContainsKey(key);

    // The connector runtime lock covers this accounting and the caller's decision to arm or stop.
    internal bool IsInUse(string key) => activeUses.ContainsKey(key);

    internal void BeginUse(string key)
        => activeUses.AddOrUpdate(key, 1, static (_, count) => checked(count + 1));

    internal bool EndUse(string key)
    {
        if (!activeUses.TryGetValue(key, out int count))
            return false;
        if (count == 1)
            return activeUses.TryRemove(key, out _);

        activeUses[key] = count - 1;
        return false;
    }

    public async Task ScheduleAsync(string key, TimeSpan delay, Func<Task> callback)
    {
        ArgumentNullException.ThrowIfNull(callback);
        await ScheduleAsync(key, delay, _ => callback()).ConfigureAwait(false);
    }

    public async Task ScheduleAsync(string key, TimeSpan delay, Func<CancellationToken, Task> callback, bool preserveExisting = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(delay, TimeSpan.Zero);
        ArgumentNullException.ThrowIfNull(callback);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);

        if (!preserveExisting)
            await CancelAsync(key).ConfigureAwait(false);
        var registration = new Registration();
        if (!registrations.TryAdd(key, registration))
        {
            registration.Cancellation.Dispose();
            return;
        }
        registration.Task = RunAsync(key, delay, callback, registration);
    }

    public async Task CancelAsync(string key)
    {
        if (!registrations.TryRemove(key, out Registration? registration))
        {
            return;
        }

        await registration.Cancellation.CancelAsync().ConfigureAwait(false);
        await registration.Task.ConfigureAwait(false);
    }

    private async Task RunAsync(string key, TimeSpan delay, Func<CancellationToken, Task> callback, Registration registration)
    {
        try
        {
            await Task.Delay(delay, registration.Cancellation.Token).ConfigureAwait(false);
            await callback(registration.Cancellation.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (registration.Cancellation.IsCancellationRequested)
        {
        }
        finally
        {
            _ = registrations.TryRemove(new KeyValuePair<string, Registration>(key, registration));
            registration.Cancellation.Dispose();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
        {
            return;
        }

        string[] keys = [.. registrations.Keys];
        foreach (string key in keys)
        {
            await CancelAsync(key).ConfigureAwait(false);
        }
    }

    private sealed class Registration
    {
        public CancellationTokenSource Cancellation { get; } = new();

        public Task Task { get; set; } = Task.CompletedTask;
    }
}
