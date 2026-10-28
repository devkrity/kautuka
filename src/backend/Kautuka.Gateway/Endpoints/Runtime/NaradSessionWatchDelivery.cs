using System.Collections.Concurrent;
using System.Text.Json.Nodes;

using Kautuka.Gateway.Domain.Execution;
using Kautuka.Gateway.RuntimePlane.Projection;

using Narad.Contracts;

namespace Kautuka.Gateway.Endpoints.Runtime;

internal static class NaradSessionWatchDelivery
{
    public static async Task StreamSessionWatchAsync(
        HttpContext context,
        string sessionId,
        GatewayAgent agent,
        IAgentDefinitionStore definitionStore,
        IRuntimeEventStreamHub streamHub,
        long afterSessionSequence,
        CancellationToken cancellationToken)
    {
        NaradSseWriter.ConfigureResponse(context);
        string storageLocatorKey = definitionStore.GetStorageLocatorKey(agent.Id);

        RuntimeEventStreamSubscription subscription = streamHub.SubscribeForSession(sessionId);
        try
        {
            long boundaryRevision = definitionStore.ReadNaradSessionProjectionRevision(storageLocatorKey, sessionId);
            var liveBuffer = new ConcurrentDictionary<string, NaradPersistedEvent>(StringComparer.Ordinal);
            using CancellationTokenSource liveReaderCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            Task liveReader = PumpLiveEventsAsync(subscription, liveBuffer, liveReaderCancellation.Token);

            IReadOnlyList<NaradPersistedEvent> backfill = NaradStreamDelivery.LoadBackfillThroughBoundary(
                definitionStore,
                storageLocatorKey,
                sessionId,
                afterSessionSequence,
                boundaryRevision);
            NaradStreamDelivery.ValidateBackfill(backfill);

            NaradSseWriteContext writer = new(context, NaradProfileDiscovery.DefaultHeartbeatIntervalMs);
            try
            {
                await writer.StartAsync(cancellationToken).ConfigureAwait(false);

                var deliveredEventIds = new HashSet<string>(StringComparer.Ordinal);
                long lastDeliveredSequence = afterSessionSequence;
                foreach (NaradPersistedEvent persistedEvent in backfill)
                {
                    if (!deliveredEventIds.Add(persistedEvent.EventId))
                    {
                        continue;
                    }

                    lastDeliveredSequence = Math.Max(lastDeliveredSequence, persistedEvent.SessionSequence);
                    await WriteWatchRevisionAsync(writer, sessionId, persistedEvent, cancellationToken).ConfigureAwait(false);
                }

                await liveReaderCancellation.CancelAsync().ConfigureAwait(false);
                try
                {
                    await liveReader.ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                }

                foreach (NaradPersistedEvent buffered in liveBuffer.Values
                             .Where(item => item.SessionSequence > afterSessionSequence && item.SessionSequence <= boundaryRevision)
                             .OrderBy(item => item.SessionSequence))
                {
                    if (!deliveredEventIds.Add(buffered.EventId))
                    {
                        continue;
                    }

                    lastDeliveredSequence = Math.Max(lastDeliveredSequence, buffered.SessionSequence);
                    await WriteWatchRevisionAsync(writer, sessionId, buffered, cancellationToken).ConfigureAwait(false);
                }

                using RuntimeEventStreamSubscription liveSubscription = streamHub.SubscribeForSession(sessionId);
                await foreach (NaradPersistedEvent liveEvent in liveSubscription.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
                {
                    if (!RuntimeEventStreamHub.IsSessionWatchInvalidationBoundary(liveEvent))
                    {
                        continue;
                    }

                    if (!string.Equals(liveEvent.SessionId, sessionId, StringComparison.Ordinal))
                    {
                        continue;
                    }

                    if (liveEvent.SessionSequence <= lastDeliveredSequence && !deliveredEventIds.Add(liveEvent.EventId))
                    {
                        continue;
                    }

                    if (!deliveredEventIds.Add(liveEvent.EventId))
                    {
                        continue;
                    }

                    lastDeliveredSequence = Math.Max(lastDeliveredSequence, liveEvent.SessionSequence);
                    try
                    {
                        _ = NaradStreamObservation.ValidateEventJson(liveEvent);
                        await WriteWatchRevisionAsync(writer, sessionId, liveEvent, cancellationToken).ConfigureAwait(false);
                    }
                    catch (NaradProjectionException ex)
                    {
                        await writer.WriteStreamErrorAsync("invalid_event", ex.Message, cancellationToken).ConfigureAwait(false);
                        return;
                    }
                }
            }
            finally
            {
                await writer.DisposeAsync().ConfigureAwait(false);
            }
        }
        finally
        {
            subscription.Dispose();
        }
    }

    private static async Task WriteWatchRevisionAsync(
        NaradSseWriteContext writer,
        string sessionId,
        NaradPersistedEvent persistedEvent,
        CancellationToken cancellationToken)
    {
        string watchEventId = NaradSessionProjection.BuildSessionWatchEventId(sessionId, persistedEvent.SessionSequence);
        JsonObject payload = NaradSessionProjection.BuildSessionRevisionWatch(sessionId, persistedEvent);
        await writer.WriteWatchAsync(payload, watchEventId, cancellationToken).ConfigureAwait(false);
    }

    private static async Task PumpLiveEventsAsync(
        RuntimeEventStreamSubscription subscription,
        ConcurrentDictionary<string, NaradPersistedEvent> liveBuffer,
        CancellationToken cancellationToken)
    {
        try
        {
            await foreach (NaradPersistedEvent liveEvent in subscription.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                liveBuffer[liveEvent.EventId] = liveEvent;
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (InvalidOperationException)
        {
        }
    }
}
