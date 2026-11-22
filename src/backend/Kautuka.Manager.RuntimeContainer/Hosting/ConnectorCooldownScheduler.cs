using System.Collections.Concurrent;

namespace Kautuka.Manager.RuntimeContainer.Hosting;

internal sealed class ConnectorCooldownScheduler : IAsyncDisposable
{
    private readonly ConcurrentDictionary<string, Registration> registrations = new(StringComparer.OrdinalIgnoreCase);
    private int disposed;

    internal int Count => registrations.Count;
    internal bool IsScheduled(string key) => registrations.ContainsKey(key);

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
