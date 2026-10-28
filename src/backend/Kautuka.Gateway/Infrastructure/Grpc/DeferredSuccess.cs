namespace Kautuka.Gateway.Infrastructure.Grpc;

/// <summary>
/// Runs <typeparamref name="T"/>'s factory on demand and keeps the result only after it returns.
/// A throw leaves the slot empty, so the next access runs the factory again.
/// </summary>
internal sealed class DeferredSuccess<T> where T : class
{
    private readonly Func<T> factory;
    private readonly object gate = new();
    private T? value;

    public DeferredSuccess(Func<T> factory)
    {
        ArgumentNullException.ThrowIfNull(factory);
        this.factory = factory;
    }

    public T Value
    {
        get
        {
            T? ready = Volatile.Read(ref value);
            if (ready is not null)
            {
                return ready;
            }

            lock (gate)
            {
                if (value is not null)
                {
                    return value;
                }

                T created = factory()
                    ?? throw new InvalidOperationException("Deferred initialization returned no value.");
                Volatile.Write(ref value, created);
                return created;
            }
        }
    }
}
