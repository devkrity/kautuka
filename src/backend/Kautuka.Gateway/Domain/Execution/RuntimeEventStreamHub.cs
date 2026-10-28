using System.Collections.Concurrent;
using System.Threading.Channels;

using Microsoft.Extensions.Logging.Abstractions;

using Narad.Contracts;

namespace Kautuka.Gateway.Domain.Execution;

internal sealed class RuntimeEventStreamHub : IRuntimeEventStreamHub
{
    internal const int DefaultSubscriberCapacity = 256;
    internal const int DefaultCompletedRootRetentionLimit = 1024;

    private static readonly TimeSpan completedRootRetention = TimeSpan.FromMinutes(5);
    private static readonly Action<ILogger, Guid, string, int, Exception?> logSlowSubscriberDisconnected =
        LoggerMessage.Define<Guid, string, int>(
            LogLevel.Warning,
            new EventId(1, "RuntimeStreamBackpressure"),
            "Disconnected slow runtime stream subscriber {SubscriptionId} for root {RootRunId} after its {Capacity}-event buffer filled; client must reconnect and replay");

    internal const string AdminApprovalsSubscriptionKey = "__approvals:admin__";

    private readonly ConcurrentDictionary<string, ConcurrentDictionary<Guid, SubscriberState>> subscribers = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<Guid, SubscriberState>> sessionSubscribers = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<Guid, ApprovalSubscriberState>> approvalSubscribers = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, byte>> publishedEventIds = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, DateTimeOffset> completedRoots = new(StringComparer.Ordinal);
    private readonly ILogger<RuntimeEventStreamHub> logger;
    private readonly int subscriberCapacity;
    private readonly int completedRootRetentionLimit;
    private long slowSubscriberDisconnectCount;

    public RuntimeEventStreamHub()
        : this(NullLogger<RuntimeEventStreamHub>.Instance, DefaultSubscriberCapacity, DefaultCompletedRootRetentionLimit)
    {
    }

    public RuntimeEventStreamHub(ILogger<RuntimeEventStreamHub> logger)
        : this(logger, DefaultSubscriberCapacity, DefaultCompletedRootRetentionLimit)
    {
    }

    internal RuntimeEventStreamHub(int subscriberCapacity, int completedRootRetentionLimit = DefaultCompletedRootRetentionLimit)
        : this(NullLogger<RuntimeEventStreamHub>.Instance, subscriberCapacity, completedRootRetentionLimit)
    {
    }

    private RuntimeEventStreamHub(
        ILogger<RuntimeEventStreamHub> logger,
        int subscriberCapacity,
        int completedRootRetentionLimit)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(subscriberCapacity);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(completedRootRetentionLimit);
        this.logger = logger;
        this.subscriberCapacity = subscriberCapacity;
        this.completedRootRetentionLimit = completedRootRetentionLimit;
    }

    internal int TrackedRootCount => publishedEventIds.Count;

    internal int SubscriberCount => subscribers.Values.Sum(static root => root.Count);

    internal int SessionSubscriberCount => sessionSubscribers.Values.Sum(static session => session.Count);

    internal long SlowSubscriberDisconnectCount => Interlocked.Read(ref slowSubscriberDisconnectCount);

    public void Publish(NaradPersistedEvent persistedEvent)
    {
        ArgumentNullException.ThrowIfNull(persistedEvent);
        string rootRunId = persistedEvent.RootRunId ?? persistedEvent.RunId ?? persistedEvent.EventId;
        ConcurrentDictionary<string, byte> rootPublishedEventIds = publishedEventIds.GetOrAdd(
            rootRunId,
            static _ => new ConcurrentDictionary<string, byte>(StringComparer.Ordinal));
        if (!rootPublishedEventIds.TryAdd(persistedEvent.EventId, 0))
        {
            return;
        }

        if (subscribers.TryGetValue(rootRunId, out ConcurrentDictionary<Guid, SubscriberState>? rootSubscribers))
        {
            PublishToSubscribers(persistedEvent, rootRunId, rootSubscribers, subscribers);
        }

        if (!string.IsNullOrWhiteSpace(persistedEvent.SessionId))
        {
            PublishToSessionSubscribers(persistedEvent, persistedEvent.SessionId);
        }

        if (IsApprovalInvalidationBoundary(persistedEvent))
        {
            ApprovalInvalidation ping = ToApprovalInvalidation(persistedEvent);
            string? userId = NaradPersistedEventReader.String(persistedEvent, "userId");
            if (!string.IsNullOrWhiteSpace(userId))
            {
                PublishToApprovalSubscribers(ping, userId);
            }

            PublishToApprovalSubscribers(ping, AdminApprovalsSubscriptionKey);
        }

        if (IsTerminal(persistedEvent))
        {
            completedRoots[rootRunId] = DateTimeOffset.UtcNow;
            EvictCompletedRootState();
        }
    }

    public RuntimeEventStreamSubscription SubscribeForRoot(string rootRunId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootRunId);
        var channel = Channel.CreateBounded<NaradPersistedEvent>(new BoundedChannelOptions(subscriberCapacity)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.Wait,
            AllowSynchronousContinuations = false,
        });
        Guid subscriptionId = Guid.NewGuid();
        var state = new SubscriberState(channel);
        ConcurrentDictionary<Guid, SubscriberState> rootSubscribers = subscribers.GetOrAdd(
            rootRunId,
            static _ => new ConcurrentDictionary<Guid, SubscriberState>());
        rootSubscribers.TryAdd(subscriptionId, state);

        return new RuntimeEventStreamSubscription(channel.Reader, () =>
        {
            if (rootSubscribers.TryRemove(subscriptionId, out SubscriberState? removed))
            {
                removed.Channel.Writer.TryComplete();
            }

            if (rootSubscribers.IsEmpty)
            {
                subscribers.TryRemove(new KeyValuePair<string, ConcurrentDictionary<Guid, SubscriberState>>(rootRunId, rootSubscribers));
            }
        });
    }

    public RuntimeEventStreamSubscription SubscribeForSession(string sessionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        return Subscribe(sessionId, sessionSubscribers);
    }

    public ApprovalInvalidationSubscription SubscribeForUserApprovals(string userId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);
        return SubscribeApprovals(userId);
    }

    public ApprovalInvalidationSubscription SubscribeForAdminApprovals()
        => SubscribeApprovals(AdminApprovalsSubscriptionKey);

    public void PublishApprovalInvalidation(
        string userId,
        string? agentId,
        string requestId,
        string eventId,
        string eventType,
        string? sessionId = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);
        ArgumentException.ThrowIfNullOrWhiteSpace(requestId);
        ArgumentException.ThrowIfNullOrWhiteSpace(eventId);
        ArgumentException.ThrowIfNullOrWhiteSpace(eventType);
        _ = agentId;
        var doorbell = new ApprovalInvalidation(
            eventId,
            string.IsNullOrWhiteSpace(sessionId) ? null : sessionId,
            RootRunId: requestId,
            eventType);
        PublishToApprovalSubscribers(doorbell, userId);
        PublishToApprovalSubscribers(doorbell, AdminApprovalsSubscriptionKey);
    }

    public async IAsyncEnumerable<NaradPersistedEvent> Subscribe(
        string rootRunId,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using RuntimeEventStreamSubscription subscription = SubscribeForRoot(rootRunId);
        await foreach (NaradPersistedEvent persistedEvent in subscription.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            yield return persistedEvent;
        }
    }

    private void EvictCompletedRootState()
    {
        DateTimeOffset expiryThreshold = DateTimeOffset.UtcNow.Subtract(completedRootRetention);
        foreach (KeyValuePair<string, DateTimeOffset> completedRoot in completedRoots)
        {
            if (completedRoot.Value <= expiryThreshold)
            {
                RemoveCompletedRoot(completedRoot.Key, completedRoot.Value);
            }
        }

        int overflow = completedRoots.Count - completedRootRetentionLimit;
        if (overflow <= 0)
        {
            return;
        }

        foreach (KeyValuePair<string, DateTimeOffset> completedRoot in completedRoots
            .OrderBy(static entry => entry.Value)
            .Take(overflow))
        {
            RemoveCompletedRoot(completedRoot.Key, completedRoot.Value);
        }
    }

    private void RemoveCompletedRoot(string rootRunId, DateTimeOffset completedAt)
    {
        if (completedRoots.TryRemove(new KeyValuePair<string, DateTimeOffset>(rootRunId, completedAt)))
        {
            publishedEventIds.TryRemove(rootRunId, out _);
        }
    }

    private static bool IsTerminal(NaradPersistedEvent persistedEvent)
        => string.Equals(persistedEvent.RunId, persistedEvent.RootRunId, StringComparison.Ordinal)
            && NaradEventTypes.IsRootRunTerminalEventType(persistedEvent.EventType);

    internal static bool IsThreadChangeSnapshotBoundary(NaradPersistedEvent persistedEvent)
        => string.Equals(persistedEvent.RunId, persistedEvent.RootRunId, StringComparison.Ordinal)
            && persistedEvent.EventType is NaradEventTypes.RunPaused
                or NaradEventTypes.RunFinished
                or NaradEventTypes.RunError
                or NaradEventTypes.RunCancelled;

    private static bool IsLiveThreadChangeBoundary(NaradPersistedEvent persistedEvent)
        => persistedEvent.EventType is NaradEventTypes.InterruptRequested or NaradEventTypes.InterruptResolved
            || persistedEvent.EventType == NaradEventTypes.RunPaused
            || string.Equals(persistedEvent.RunId, persistedEvent.RootRunId, StringComparison.Ordinal)
                && persistedEvent.EventType is NaradEventTypes.RunFinished
                    or NaradEventTypes.RunError
                    or NaradEventTypes.RunCancelled;

    private static bool IsApprovalInvalidationBoundary(NaradPersistedEvent persistedEvent)
        => persistedEvent.EventType is NaradEventTypes.InterruptRequested
            or NaradEventTypes.InterruptResolved
            or NaradEventTypes.RunPaused
            or NaradEventTypes.WorkflowPaused;

    internal static bool IsSessionWatchInvalidationBoundary(NaradPersistedEvent persistedEvent)
        => IsLiveThreadChangeBoundary(persistedEvent)
            || persistedEvent.EventType is NaradEventTypes.MessageUserRecorded
                or NaradEventTypes.MessageTextCompleted
                or NaradEventTypes.RunStarted
                or NaradEventTypes.RunResumed;

    private void PublishToSessionSubscribers(NaradPersistedEvent persistedEvent, string sessionId)
    {
        if (sessionSubscribers.TryGetValue(sessionId, out ConcurrentDictionary<Guid, SubscriberState>? threadSubscribers))
        {
            PublishToSubscribers(persistedEvent, sessionId, threadSubscribers, sessionSubscribers);
        }
    }

    private void PublishToApprovalSubscribers(ApprovalInvalidation ping, string subscriptionKey)
    {
        if (!approvalSubscribers.TryGetValue(subscriptionKey, out ConcurrentDictionary<Guid, ApprovalSubscriberState>? keyedSubscribers))
        {
            return;
        }

        foreach (KeyValuePair<Guid, ApprovalSubscriberState> subscriber in keyedSubscribers)
        {
            if (subscriber.Value.Channel.Writer.TryWrite(ping))
            {
                continue;
            }

            if (keyedSubscribers.TryRemove(subscriber.Key, out ApprovalSubscriberState? removed))
            {
                Interlocked.Increment(ref slowSubscriberDisconnectCount);
                removed.Channel.Writer.TryComplete(new InvalidOperationException(
                    $"Runtime stream for '{subscriptionKey}' exceeded its {subscriberCapacity}-event live buffer; reconnect to replay durable history."));
                logSlowSubscriberDisconnected(
                    logger,
                    subscriber.Key,
                    subscriptionKey,
                    subscriberCapacity,
                    null);
            }
        }

        if (keyedSubscribers.IsEmpty)
        {
            approvalSubscribers.TryRemove(
                new KeyValuePair<string, ConcurrentDictionary<Guid, ApprovalSubscriberState>>(subscriptionKey, keyedSubscribers));
        }
    }

    private ApprovalInvalidationSubscription SubscribeApprovals(string key)
    {
        var channel = Channel.CreateBounded<ApprovalInvalidation>(new BoundedChannelOptions(subscriberCapacity)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.Wait,
            AllowSynchronousContinuations = false,
        });
        Guid subscriptionId = Guid.NewGuid();
        var state = new ApprovalSubscriberState(channel);
        ConcurrentDictionary<Guid, ApprovalSubscriberState> keyedSubscribers = approvalSubscribers.GetOrAdd(
            key,
            static _ => new ConcurrentDictionary<Guid, ApprovalSubscriberState>());
        keyedSubscribers.TryAdd(subscriptionId, state);

        return new ApprovalInvalidationSubscription(channel.Reader, () =>
        {
            if (keyedSubscribers.TryRemove(subscriptionId, out ApprovalSubscriberState? removed))
            {
                removed.Channel.Writer.TryComplete();
            }

            if (keyedSubscribers.IsEmpty)
            {
                approvalSubscribers.TryRemove(
                    new KeyValuePair<string, ConcurrentDictionary<Guid, ApprovalSubscriberState>>(key, keyedSubscribers));
            }
        });
    }

    private static ApprovalInvalidation ToApprovalInvalidation(NaradPersistedEvent persistedEvent)
        => new(persistedEvent.EventId, persistedEvent.SessionId, persistedEvent.RootRunId, persistedEvent.EventType);

    private RuntimeEventStreamSubscription Subscribe(
        string key,
        ConcurrentDictionary<string, ConcurrentDictionary<Guid, SubscriberState>> registry)
    {
        var channel = Channel.CreateBounded<NaradPersistedEvent>(new BoundedChannelOptions(subscriberCapacity)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.Wait,
            AllowSynchronousContinuations = false,
        });
        Guid subscriptionId = Guid.NewGuid();
        var state = new SubscriberState(channel);
        ConcurrentDictionary<Guid, SubscriberState> keyedSubscribers = registry.GetOrAdd(
            key,
            static _ => new ConcurrentDictionary<Guid, SubscriberState>());
        keyedSubscribers.TryAdd(subscriptionId, state);

        return new RuntimeEventStreamSubscription(channel.Reader, () =>
        {
            if (keyedSubscribers.TryRemove(subscriptionId, out SubscriberState? removed))
            {
                removed.Channel.Writer.TryComplete();
            }

            if (keyedSubscribers.IsEmpty)
            {
                registry.TryRemove(new KeyValuePair<string, ConcurrentDictionary<Guid, SubscriberState>>(key, keyedSubscribers));
            }
        });
    }

    private void PublishToSubscribers(
        NaradPersistedEvent persistedEvent,
        string subscriptionKey,
        ConcurrentDictionary<Guid, SubscriberState> keyedSubscribers,
        ConcurrentDictionary<string, ConcurrentDictionary<Guid, SubscriberState>> registry)
    {
        foreach (KeyValuePair<Guid, SubscriberState> subscriber in keyedSubscribers)
        {
            if (subscriber.Value.Channel.Writer.TryWrite(persistedEvent))
            {
                continue;
            }

            if (keyedSubscribers.TryRemove(subscriber.Key, out SubscriberState? removed))
            {
                Interlocked.Increment(ref slowSubscriberDisconnectCount);
                removed.Channel.Writer.TryComplete(new InvalidOperationException(
                    $"Runtime stream for '{subscriptionKey}' exceeded its {subscriberCapacity}-event live buffer; reconnect to replay durable history."));
                logSlowSubscriberDisconnected(
                    logger,
                    subscriber.Key,
                    subscriptionKey,
                    subscriberCapacity,
                    null);
            }
        }

        if (keyedSubscribers.IsEmpty)
        {
            registry.TryRemove(new KeyValuePair<string, ConcurrentDictionary<Guid, SubscriberState>>(subscriptionKey, keyedSubscribers));
        }
    }

    private sealed record SubscriberState(Channel<NaradPersistedEvent> Channel);

    private sealed record ApprovalSubscriberState(Channel<ApprovalInvalidation> Channel);
}
