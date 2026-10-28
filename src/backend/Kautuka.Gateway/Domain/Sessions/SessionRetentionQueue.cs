using System.Threading.Channels;

namespace Kautuka.Gateway.Domain.Sessions;

internal interface ISessionRetentionQueue
{
    bool TryEnqueue(SessionRetentionWork work);

    IAsyncEnumerable<SessionRetentionWork> DequeueAllAsync(CancellationToken cancellationToken);
}

internal sealed class SessionRetentionQueue : ISessionRetentionQueue
{
    private readonly Channel<SessionRetentionWork> channel = Channel.CreateBounded<SessionRetentionWork>(
        new BoundedChannelOptions(256)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false,
        });

    public bool TryEnqueue(SessionRetentionWork work) => channel.Writer.TryWrite(work);

    public IAsyncEnumerable<SessionRetentionWork> DequeueAllAsync(CancellationToken cancellationToken)
        => channel.Reader.ReadAllAsync(cancellationToken);
}
