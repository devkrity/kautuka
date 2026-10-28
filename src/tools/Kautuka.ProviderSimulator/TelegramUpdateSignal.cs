internal sealed class TelegramUpdateSignal
{
    private readonly object gate = new();
    private TaskCompletionSource next = CreateSource();

    public Task Capture()
    {
        lock (gate) return next.Task;
    }

    public void Pulse()
    {
        TaskCompletionSource previous;
        lock (gate)
        {
            previous = next;
            next = CreateSource();
        }

        previous.TrySetResult();
    }

    private static TaskCompletionSource CreateSource()
        => new(TaskCreationOptions.RunContinuationsAsynchronously);
}
