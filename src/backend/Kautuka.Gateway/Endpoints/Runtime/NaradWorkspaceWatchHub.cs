using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using System.Threading.Channels;

using Narad.Contracts;

namespace Kautuka.Gateway.Endpoints.Runtime;

internal interface INaradWorkspaceWatchHub
{
    void NotifySessionCreated(GatewaySession session);
    void NotifySessionUpdated(GatewaySession session);
    void NotifySessionArchived(GatewaySession session);
    NaradWorkspaceWatchSubscription Subscribe(string userId, long afterRevision = 0);
}

internal sealed record NaradWorkspaceWatchSubscription(
    IAsyncEnumerable<JsonObject> Events,
    Action Dispose);

internal sealed class NaradWorkspaceWatchHub : INaradWorkspaceWatchHub
{
    internal const int DefaultSubscriberCapacity = 64;

    private readonly ConcurrentDictionary<string, WorkspaceState> workspaces = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<Guid, Channel<JsonObject>>> subscribers = new(StringComparer.Ordinal);
    private readonly int subscriberCapacity;

    public NaradWorkspaceWatchHub()
        : this(DefaultSubscriberCapacity)
    {
    }

    internal NaradWorkspaceWatchHub(int subscriberCapacity)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(subscriberCapacity);
        this.subscriberCapacity = subscriberCapacity;
    }

    public void NotifySessionCreated(GatewaySession session) => Publish(session, "session.created");

    public void NotifySessionUpdated(GatewaySession session) => Publish(session, "session.updated");

    public void NotifySessionArchived(GatewaySession session) => Publish(session, "session.archived");

    public NaradWorkspaceWatchSubscription Subscribe(string userId, long afterRevision = 0)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);
        var channel = Channel.CreateBounded<JsonObject>(new BoundedChannelOptions(subscriberCapacity)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.Wait,
        });
        Guid subscriptionId = Guid.NewGuid();
        ConcurrentDictionary<Guid, Channel<JsonObject>> userSubscribers = subscribers.GetOrAdd(
            userId,
            static _ => new ConcurrentDictionary<Guid, Channel<JsonObject>>());
        userSubscribers.TryAdd(subscriptionId, channel);

        WorkspaceState workspace = workspaces.GetOrAdd(userId, static key => new WorkspaceState(key));
        foreach (JsonObject catchUpEvent in workspace.ListEventsAfter(afterRevision))
        {
            channel.Writer.TryWrite(catchUpEvent);
        }

        return new NaradWorkspaceWatchSubscription(
            channel.Reader.ReadAllAsync(),
            () =>
            {
                if (userSubscribers.TryRemove(subscriptionId, out Channel<JsonObject>? removed))
                {
                    removed.Writer.TryComplete();
                }

                if (userSubscribers.IsEmpty)
                {
                    subscribers.TryRemove(new KeyValuePair<string, ConcurrentDictionary<Guid, Channel<JsonObject>>>(userId, userSubscribers));
                }
            });
    }

    private void Publish(GatewaySession session, string eventType)
    {
        WorkspaceState workspace = workspaces.GetOrAdd(session.OwnerUserId, static key => new WorkspaceState(key));
        JsonObject payload = workspace.CreateEvent(eventType, session);
        if (!subscribers.TryGetValue(session.OwnerUserId, out ConcurrentDictionary<Guid, Channel<JsonObject>>? userSubscribers))
        {
            return;
        }

        foreach (KeyValuePair<Guid, Channel<JsonObject>> subscriber in userSubscribers.ToArray())
        {
            if (subscriber.Value.Writer.TryWrite(payload))
            {
                continue;
            }

            if (userSubscribers.TryRemove(subscriber.Key, out Channel<JsonObject>? removed))
            {
                removed.Writer.TryComplete(new InvalidOperationException(
                    "Workspace watch buffer overflow; reconnect with afterRevision cursor."));
            }
        }

        if (userSubscribers.IsEmpty)
        {
            subscribers.TryRemove(new KeyValuePair<string, ConcurrentDictionary<Guid, Channel<JsonObject>>>(session.OwnerUserId, userSubscribers));
        }
    }

    private sealed class WorkspaceState(string workspaceId)
    {
        private readonly object gate = new();
        private readonly List<JsonObject> history = [];
        private long revision;

        public JsonObject CreateEvent(string eventType, GatewaySession session)
        {
            lock (gate)
            {
                revision++;
                var payload = new JsonObject
                {
                    ["type"] = eventType,
                    ["protocolVersion"] = NaradProtocolVersions.V1,
                    ["eventId"] = $"workspace-revision:{workspaceId}:{revision}",
                    ["workspaceId"] = workspaceId,
                    ["revision"] = revision,
                    ["timestamp"] = DateTimeOffset.UtcNow.ToString("O"),
                    ["sessionId"] = session.Id,
                    ["session"] = NaradSessionMappings.ToSessionRecord(session),
                };
                history.Add(payload);
                return payload;
            }
        }

        public IEnumerable<JsonObject> ListEventsAfter(long afterRevision)
        {
            lock (gate)
            {
                foreach (JsonObject payload in history)
                {
                    if (payload["revision"]?.GetValue<long>() > afterRevision)
                    {
                        yield return payload;
                    }
                }
            }
        }
    }
}
