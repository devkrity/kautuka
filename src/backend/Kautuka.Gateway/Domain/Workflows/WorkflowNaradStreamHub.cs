using System.Collections.Concurrent;
using System.Threading.Channels;

using Kautuka.Gateway.Domain.Execution;

using Narad.Contracts;

namespace Kautuka.Gateway.Domain.Workflows;

internal interface IWorkflowNaradStreamHub
{
    void Publish(NaradPersistedEvent persistedEvent);

    RuntimeEventStreamSubscription Subscribe(string instanceId);
}

internal sealed class WorkflowNaradStreamHub : IWorkflowNaradStreamHub
{
    private const int subscriberCapacity = 256;
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<Guid, Channel<NaradPersistedEvent>>> subscribers = new(StringComparer.Ordinal);

    public void Publish(NaradPersistedEvent persistedEvent)
    {
        ArgumentNullException.ThrowIfNull(persistedEvent);
        if (!subscribers.TryGetValue(persistedEvent.SessionId, out ConcurrentDictionary<Guid, Channel<NaradPersistedEvent>>? instanceSubscribers))
        {
            return;
        }

        foreach (KeyValuePair<Guid, Channel<NaradPersistedEvent>> pair in instanceSubscribers)
        {
            _ = pair.Value.Writer.TryWrite(persistedEvent);
        }
    }

    public RuntimeEventStreamSubscription Subscribe(string instanceId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(instanceId);
        Guid subscriptionId = Guid.NewGuid();
        Channel<NaradPersistedEvent> channel = Channel.CreateBounded<NaradPersistedEvent>(new BoundedChannelOptions(subscriberCapacity)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false,
        });
        ConcurrentDictionary<Guid, Channel<NaradPersistedEvent>> instanceSubscribers = subscribers.GetOrAdd(
            instanceId,
            static _ => new ConcurrentDictionary<Guid, Channel<NaradPersistedEvent>>());
        instanceSubscribers[subscriptionId] = channel;
        return new RuntimeEventStreamSubscription(channel.Reader, () =>
        {
            if (subscribers.TryGetValue(instanceId, out ConcurrentDictionary<Guid, Channel<NaradPersistedEvent>>? current)
                && current.TryRemove(subscriptionId, out Channel<NaradPersistedEvent>? removed))
            {
                removed.Writer.TryComplete();
            }
        });
    }
}
