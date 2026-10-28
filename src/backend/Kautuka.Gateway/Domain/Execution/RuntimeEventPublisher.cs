using System.Collections.Concurrent;
using System.Text.Json;

using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

using Narad.Contracts;

namespace Kautuka.Gateway.Domain.Execution;

internal sealed class RuntimeEventPublisher : IRuntimeEventPublisher, IRuntimeEventLifecycle, IDisposable
{
    private static readonly TimeSpan outboxLeaseDuration = TimeSpan.FromSeconds(30);
    private const int maxDispatchPerPass = 128;
    private static readonly Action<ILogger, Exception?> logOutboxDiscoveryFailed = LoggerMessage.Define(
        LogLevel.Warning,
        new EventId(1, "RuntimeOutboxDiscoveryFailed"),
        "Runtime outbox storage discovery failed; recovery will retry");
    private static readonly Action<ILogger, string, string, Exception?> logOutboxDeliveryFailed =
        LoggerMessage.Define<string, string>(
            LogLevel.Warning,
            new EventId(2, "RuntimeOutboxDeliveryFailed"),
            "Runtime outbox delivery failed for event {EventId} in root {RootRunId}; durable retry scheduled");

    private readonly IAgentDefinitionStore agentDefinitionStore;
    private readonly IRuntimeEventStreamHub streamHub;
    private readonly ILogger<RuntimeEventPublisher> logger;
    private readonly IRuntimeEventPublicationObserver[] publicationObservers;
    private readonly string leaseOwner = $"gateway-{Environment.ProcessId}-{Guid.NewGuid():N}";
    private readonly ConcurrentDictionary<string, string> rootStorageLocators = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> dispatchGates = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, byte> quiescedStorageLocators = new(StringComparer.Ordinal);
    private readonly Lock discoverySync = new();
    private readonly CancellationTokenSource retryCancellation = new();
    private readonly Task retryTask;
    private string[] discoveredOutboxStorageLocators = [];
    private DateTimeOffset nextOutboxDiscoveryAt = DateTimeOffset.MinValue;
    private int disposed;

    public RuntimeEventPublisher(IAgentDefinitionStore agentDefinitionStore, IRuntimeEventStreamHub streamHub)
        : this(agentDefinitionStore, streamHub, NullLogger<RuntimeEventPublisher>.Instance)
    {
    }

    public RuntimeEventPublisher(
        IAgentDefinitionStore agentDefinitionStore,
        IRuntimeEventStreamHub streamHub,
        ILogger<RuntimeEventPublisher> logger)
        : this(agentDefinitionStore, streamHub, logger, publicationObservers: null)
    {
    }

    public RuntimeEventPublisher(
        IAgentDefinitionStore agentDefinitionStore,
        IRuntimeEventStreamHub streamHub,
        ILogger<RuntimeEventPublisher> logger,
        IEnumerable<IRuntimeEventPublicationObserver>? publicationObservers)
    {
        this.agentDefinitionStore = agentDefinitionStore;
        this.streamHub = streamHub;
        this.logger = logger;
        this.publicationObservers = publicationObservers is null
            ? []
            : [.. publicationObservers];
        retryTask = Task.Run(RetryPendingOutboxAsync);
    }

    public async ValueTask<NaradPersistedEvent> PublishAsync(
        NaradAuthorityEventDraft draft,
        NaradEventContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(draft);
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();

        string sourceStorageLocatorKey = agentDefinitionStore.GetStorageLocatorKey(context.AgentId);
        string rootStorageLocatorKey = ResolveRootStorageLocatorKey(context, sourceStorageLocatorKey);
        ThrowIfQuiesced(sourceStorageLocatorKey);
        ThrowIfQuiesced(rootStorageLocatorKey);

        NaradPersistedEvent persistedEvent = agentDefinitionStore.AppendNaradAuthorityEvent(
            rootStorageLocatorKey,
            sourceStorageLocatorKey,
            draft);
        await DispatchOutboxAsync(rootStorageLocatorKey, cancellationToken).ConfigureAwait(false);

        if (!string.IsNullOrWhiteSpace(context.ParentSessionId)
            && !string.Equals(context.ParentSessionId, persistedEvent.SessionId, StringComparison.Ordinal)
            && NaradEventTypes.ShouldProjectToParentConversation(persistedEvent.EventType))
        {
            _ = agentDefinitionStore.AppendNaradParentSessionProjection(
                rootStorageLocatorKey,
                persistedEvent,
                context.ParentSessionId);
            await DispatchOutboxAsync(rootStorageLocatorKey, cancellationToken).ConfigureAwait(false);
        }

        if (IsRootTerminalEvent(persistedEvent))
        {
            rootStorageLocators.TryRemove(context.RootRunId, out _);
        }

        return persistedEvent;
    }

    private async Task DispatchOutboxAsync(string rootStorageLocatorKey, CancellationToken cancellationToken)
    {
        if (quiescedStorageLocators.ContainsKey(rootStorageLocatorKey))
        {
            return;
        }

        SemaphoreSlim gate = dispatchGates.GetOrAdd(rootStorageLocatorKey, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (quiescedStorageLocators.ContainsKey(rootStorageLocatorKey))
            {
                return;
            }

            for (int dispatched = 0; dispatched < maxDispatchPerPass; dispatched++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                NaradRuntimeEventOutboxEntry? entry = agentDefinitionStore
                    .ClaimNaradEventOutbox(rootStorageLocatorKey, leaseOwner, outboxLeaseDuration, 1)
                    .SingleOrDefault();
                if (entry is null)
                {
                    return;
                }

                if (!entry.IsMirrored)
                {
                    try
                    {
                        agentDefinitionStore.MirrorNaradAuthorityEvent(entry.SourceStorageLocatorKey, entry.Event);
                        agentDefinitionStore.MarkNaradEventMirrored(rootStorageLocatorKey, entry.Event.EventId, leaseOwner);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        RecordDeliveryFailure(rootStorageLocatorKey, entry.Event, ex);
                        return;
                    }
                }

                if (!entry.IsPublished)
                {
                    try
                    {
                        streamHub.Publish(entry.Event);
                        await NotifyPublicationObserversAsync(entry.Event, cancellationToken).ConfigureAwait(false);
                        agentDefinitionStore.MarkNaradEventPublished(rootStorageLocatorKey, entry.Event.EventId, leaseOwner);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        RecordDeliveryFailure(rootStorageLocatorKey, entry.Event, ex);
                        return;
                    }
                }
            }
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task NotifyPublicationObserversAsync(NaradPersistedEvent persistedEvent, CancellationToken cancellationToken)
    {
        foreach (IRuntimeEventPublicationObserver observer in publicationObservers)
        {
            await observer.OnPublishedAsync(persistedEvent, cancellationToken).ConfigureAwait(false);
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
        {
            return;
        }

        retryCancellation.Cancel();
        try
        {
            retryTask.GetAwaiter().GetResult();
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            retryCancellation.Dispose();
            foreach (SemaphoreSlim gate in dispatchGates.Values)
            {
                gate.Dispose();
            }

            dispatchGates.Clear();
        }
    }

    public async ValueTask QuiesceAgentAsync(string agentId, CancellationToken cancellationToken = default)
    {
        string storageLocatorKey = agentDefinitionStore.GetStorageLocatorKey(agentId);
        quiescedStorageLocators[storageLocatorKey] = 0;
        lock (discoverySync)
        {
        }

        if (!dispatchGates.TryGetValue(storageLocatorKey, out SemaphoreSlim? gate))
        {
            return;
        }

        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        gate.Release();
    }

    public void ResumeAgent(string agentId)
    {
        string storageLocatorKey = agentDefinitionStore.GetStorageLocatorKey(agentId);
        quiescedStorageLocators.TryRemove(storageLocatorKey, out _);
    }

    public void ForgetAgent(string agentId)
    {
        string storageLocatorKey = agentDefinitionStore.GetStorageLocatorKey(agentId);
        quiescedStorageLocators[storageLocatorKey] = 0;

        foreach (KeyValuePair<string, string> rootStorageLocator in rootStorageLocators)
        {
            if (string.Equals(rootStorageLocator.Value, storageLocatorKey, StringComparison.Ordinal))
            {
                rootStorageLocators.TryRemove(rootStorageLocator);
            }
        }
    }

    private async Task RetryPendingOutboxAsync()
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        try
        {
            do
            {
                IReadOnlyList<string> knownRootStorageLocators = DiscoverOutboxStorageLocators();

                foreach (string rootStorageLocatorKey in knownRootStorageLocators)
                {
                    try
                    {
                        await DispatchOutboxAsync(rootStorageLocatorKey, retryCancellation.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (retryCancellation.IsCancellationRequested)
                    {
                        return;
                    }
                    catch (SqliteException)
                    {
                    }
                    catch (IOException)
                    {
                    }
                    catch (InvalidOperationException)
                    {
                    }
                    catch (JsonException)
                    {
                    }
                }
            }
            while (await timer.WaitForNextTickAsync(retryCancellation.Token).ConfigureAwait(false));
        }
        catch (OperationCanceledException) when (retryCancellation.IsCancellationRequested)
        {
        }
    }

    private string[] DiscoverOutboxStorageLocators()
    {
        if (DateTimeOffset.UtcNow >= nextOutboxDiscoveryAt)
        {
            lock (discoverySync)
            {
                try
                {
                    HashSet<string> excludedStorageLocatorKeys = [.. quiescedStorageLocators.Keys];
                    discoveredOutboxStorageLocators = agentDefinitionStore
                        .ListNaradEventOutboxStorageLocatorKeys(excludedStorageLocatorKeys)
                        .ToArray();
                    nextOutboxDiscoveryAt = DateTimeOffset.UtcNow.AddSeconds(30);
                }
                catch (Exception ex) when (ex is SqliteException or IOException or InvalidOperationException)
                {
                    logOutboxDiscoveryFailed(logger, ex);
                    nextOutboxDiscoveryAt = DateTimeOffset.UtcNow.AddSeconds(1);
                }
            }
        }

        return discoveredOutboxStorageLocators
            .Concat(rootStorageLocators.Values)
            .Where(storageLocatorKey => !quiescedStorageLocators.ContainsKey(storageLocatorKey))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
    }

    private void RecordDeliveryFailure(string rootStorageLocatorKey, NaradPersistedEvent persistedEvent, Exception exception)
    {
        string safeError = exception.GetType().Name;
        agentDefinitionStore.RecordNaradEventOutboxFailure(
            rootStorageLocatorKey,
            persistedEvent.EventId,
            leaseOwner,
            safeError);
        logOutboxDeliveryFailed(
            logger,
            persistedEvent.EventId,
            persistedEvent.RootRunId ?? string.Empty,
            exception);
    }

    private static bool IsRootTerminalEvent(NaradPersistedEvent persistedEvent)
        => string.Equals(persistedEvent.RunId, persistedEvent.RootRunId, StringComparison.Ordinal)
            && !string.IsNullOrWhiteSpace(persistedEvent.RootRunId)
            && NaradEventTypes.IsRootRunTerminalEventType(persistedEvent.EventType);

    private string ResolveRootStorageLocatorKey(NaradEventContext context, string sourceStorageLocatorKey)
    {
        if (!string.IsNullOrWhiteSpace(context.RootAgentId))
        {
            string rootStorageLocatorKey = agentDefinitionStore.GetStorageLocatorKey(context.RootAgentId);
            rootStorageLocators[context.RootRunId] = rootStorageLocatorKey;
            return rootStorageLocatorKey;
        }

        if (string.Equals(context.RunId, context.RootRunId, StringComparison.Ordinal))
        {
            rootStorageLocators[context.RootRunId] = sourceStorageLocatorKey;
            return sourceStorageLocatorKey;
        }

        if (rootStorageLocators.TryGetValue(context.RootRunId, out string? knownRootStorageLocatorKey))
        {
            return knownRootStorageLocatorKey;
        }

        string? discoveredRootStorageLocatorKey = agentDefinitionStore.FindNaradRootStorageLocatorKey(context.RootRunId);
        if (!string.IsNullOrWhiteSpace(discoveredRootStorageLocatorKey))
        {
            rootStorageLocators[context.RootRunId] = discoveredRootStorageLocatorKey;
            return discoveredRootStorageLocatorKey;
        }

        throw new InvalidOperationException(
            $"Root storage for child run '{context.RunId}' was not registered by root run '{context.RootRunId}'.");
    }

    private void ThrowIfQuiesced(string storageLocatorKey)
    {
        if (quiescedStorageLocators.ContainsKey(storageLocatorKey))
        {
            throw new InvalidOperationException($"Runtime events for agent store '{storageLocatorKey}' are quiesced.");
        }
    }
}
