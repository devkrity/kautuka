using System.Collections.Concurrent;
using System.Text.Json.Nodes;

using Kautuka.Gateway.Domain.Execution;
using Kautuka.Gateway.RuntimePlane.Projection;

using Narad.Contracts;

namespace Kautuka.Gateway.Endpoints.Runtime;

internal static class NaradStreamDelivery
{
    internal const int MaxHistoryWindow = NaradProfileDiscovery.MaxHistoryLimit;

    public static async Task StreamSessionAsync(
        HttpContext context,
        GatewaySession session,
        GatewayAgent agent,
        IAgentDefinitionStore definitionStore,
        IRuntimeEventStreamHub streamHub,
        IRootRunAdmissionGate admissionGate,
        long afterSessionSequence,
        bool includeHydrate,
        CancellationToken cancellationToken)
    {
        NaradSseWriter.ConfigureResponse(context);
        string storageLocatorKey = definitionStore.GetStorageLocatorKey(agent.Id);

        RuntimeEventStreamSubscription subscription = streamHub.SubscribeForSession(session.Id);
        try
        {
            long boundaryRevision = definitionStore.ReadNaradSessionProjectionRevision(storageLocatorKey, session.Id);

            var liveBuffer = new ConcurrentDictionary<string, NaradPersistedEvent>(StringComparer.Ordinal);
            using CancellationTokenSource liveReaderCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            Task liveReader = PumpLiveEventsAsync(subscription, liveBuffer, liveReaderCancellation.Token);

            // Fold the full session through the attach boundary so graph hydrate has a coherent
            // revision rail. Authority backfill below still starts at afterSessionSequence —
            // the client has already (or will) load prior authority via history pages.
            // Folding only (after..boundary) rebuilt graphs mid-run and emitted graph.delta
            // with baseRevision chains the client never saw → "Graph revision gap requires resync."
            IReadOnlyList<NaradPersistedEvent> projectionHistory = LoadBackfillThroughBoundary(
                definitionStore,
                storageLocatorKey,
                session.Id,
                afterSessionSequence: 0,
                boundaryRevision);

            ValidateBackfill(projectionHistory);

            IReadOnlyList<NaradPersistedEvent> authorityBackfill = FilterAfterSessionSequence(
                projectionHistory,
                afterSessionSequence);

            NaradSessionProjection projection = NaradSessionProjection.Fold(
                session.Id,
                projectionHistory,
                rootRunId => admissionGate.IsAdmissionClosed(agent.Id, rootRunId));

            NaradSseWriteContext writer = new(context, NaradProfileDiscovery.DefaultHeartbeatIntervalMs);
            try
            {
                await writer.StartAsync(cancellationToken).ConfigureAwait(false);

                long currentRevision = definitionStore.ReadNaradSessionProjectionRevision(storageLocatorKey, session.Id);
                await writer.WriteControlAsync(
                    NaradProfileDiscovery.BuildCapabilities(currentRevision, ResolveActiveRootRunId(definitionStore, storageLocatorKey, session.Id)),
                    cancellationToken).ConfigureAwait(false);

                await writer.WriteCommentAsync("subscribed", cancellationToken).ConfigureAwait(false);

                if (includeHydrate)
                {
                    await WriteHydrateAttachAsync(writer, session.Id, projectionHistory, projection, cancellationToken).ConfigureAwait(false);
                }

                var deliveredEventIds = new HashSet<string>(StringComparer.Ordinal);
                foreach (NaradPersistedEvent persistedEvent in authorityBackfill)
                {
                    if (!deliveredEventIds.Add(persistedEvent.EventId))
                    {
                        continue;
                    }

                    await WriteAuthorityEventAsync(writer, persistedEvent, cancellationToken).ConfigureAwait(false);
                }

                await liveReaderCancellation.CancelAsync().ConfigureAwait(false);
                try
                {
                    await liveReader.ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                }

                long lastDeliveredSequence = authorityBackfill.Count > 0
                    ? authorityBackfill[^1].SessionSequence
                    : afterSessionSequence;
                foreach (NaradPersistedEvent buffered in liveBuffer.Values
                             .Where(item => BelongsToSessionStream(item, session.Id)
                                 && item.SessionSequence > afterSessionSequence
                                 && item.SessionSequence <= boundaryRevision)
                             .OrderBy(item => item.SessionSequence))
                {
                    if (!deliveredEventIds.Add(buffered.EventId))
                    {
                        continue;
                    }

                    lastDeliveredSequence = Math.Max(lastDeliveredSequence, buffered.SessionSequence);
                    // Already folded into projectionHistory; deliver wire authority + skip
                    // re-apply (would double-advance graph revisions past the attach snapshot).
                    await WriteAuthorityEventAsync(writer, buffered, cancellationToken).ConfigureAwait(false);
                }

                using RuntimeEventStreamSubscription liveSubscription = streamHub.SubscribeForSession(session.Id);
                await foreach (NaradPersistedEvent liveEvent in liveSubscription.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
                {
                    if (!BelongsToSessionStream(liveEvent, session.Id))
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
                    await WriteLiveEventAsync(
                        writer,
                        session.Id,
                        projection,
                        liveEvent,
                        rootRunId => admissionGate.IsAdmissionClosed(agent.Id, rootRunId),
                        includeHydrate,
                        cancellationToken).ConfigureAwait(false);
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

    private static async Task WriteLiveEventAsync(
        NaradSseWriteContext writer,
        string sessionId,
        NaradSessionProjection projection,
        NaradPersistedEvent persistedEvent,
        Func<string, bool> isRootAdmissionClosed,
        bool includeHydrate,
        CancellationToken cancellationToken)
    {
        NaradGraphApplyResult? graphResult;
        try
        {
            graphResult = projection.ApplyEvent(sessionId, persistedEvent, isRootAdmissionClosed);
        }
        catch (NaradProjectionException ex)
        {
            await writer.WriteStreamErrorAsync("projection_failed", ex.Message, cancellationToken).ConfigureAwait(false);
            throw;
        }

        if (!NaradPublicAuthorityFilter.TryBuildPublicPayload(persistedEvent, out JsonObject authorityPayload))
        {
            // Parent AAAT projections may still advance graphs even when they are not public
            // authority — hydrate must follow or client graph revisions lag and gap.
            if (includeHydrate && graphResult is not null)
            {
                await WriteGraphUpdateAsync(
                    writer,
                    sessionId,
                    persistedEvent,
                    projection,
                    graphResult,
                    graphResult.RootRunId,
                    cancellationToken).ConfigureAwait(false);
            }

            return;
        }

        await writer.WriteAuthorityAsync(authorityPayload, persistedEvent.EventId, cancellationToken).ConfigureAwait(false);

        if (includeHydrate && graphResult is not null)
        {
            await WriteGraphUpdateAsync(
                writer,
                sessionId,
                persistedEvent,
                projection,
                graphResult,
                graphResult.RootRunId,
                cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task WriteGraphUpdateAsync(
        NaradSseWriteContext writer,
        string sessionId,
        NaradPersistedEvent persistedEvent,
        NaradSessionProjection projection,
        NaradGraphApplyResult graphResult,
        string graphRootRunId,
        CancellationToken cancellationToken)
    {
        NaradGraphProjectionState? graph = projection.GetGraph(graphRootRunId);
        if (graph is null)
        {
            return;
        }

        DateTimeOffset timestamp = DateTimeOffset.TryParse(persistedEvent.OccurredAt, out DateTimeOffset sourceTs)
            ? sourceTs
            : DateTimeOffset.UtcNow;

        if (graphResult.RequiresInitialSnapshot)
        {
            JsonObject snapshot = graph.BuildGraphSnapshot(sessionId, timestamp);
            await writer.WriteHydrateAsync(snapshot, cancellationToken).ConfigureAwait(false);
            return;
        }

        JsonObject? delta = NaradGraphProjectionState.BuildGraphDelta(
            sessionId,
            timestamp,
            graphRootRunId,
            graphResult,
            graph.LastSourceSequence);
        if (delta is not null)
        {
            await writer.WriteHydrateAsync(delta, cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task WriteHydrateAttachAsync(
        NaradSseWriteContext writer,
        string sessionId,
        IReadOnlyList<NaradPersistedEvent> sessionHistory,
        NaradSessionProjection projection,
        CancellationToken cancellationToken)
    {
        JsonObject messagesSnapshot = NaradSessionProjection.BuildMessagesSnapshot(sessionId, sessionHistory, DateTimeOffset.UtcNow);
        await writer.WriteHydrateAsync(messagesSnapshot, cancellationToken).ConfigureAwait(false);

        // Emit every folded graph — including roots that only appear via AAAT projection
        // rows (journal RootRunId null, lineage in event_json).
        foreach (NaradGraphProjectionState graph in projection.EnumerateGraphs())
        {
            if (graph.Revision == 0)
            {
                continue;
            }

            JsonObject snapshot = graph.BuildGraphSnapshot(sessionId, DateTimeOffset.UtcNow);
            await writer.WriteHydrateAsync(snapshot, cancellationToken).ConfigureAwait(false);
        }
    }

    private static IReadOnlyList<NaradPersistedEvent> FilterAfterSessionSequence(
        IReadOnlyList<NaradPersistedEvent> history,
        long afterSessionSequence)
    {
        if (afterSessionSequence <= 0 || history.Count == 0)
        {
            return history;
        }

        var filtered = new List<NaradPersistedEvent>(history.Count);
        foreach (NaradPersistedEvent item in history)
        {
            if (item.SessionSequence > afterSessionSequence)
            {
                filtered.Add(item);
            }
        }

        return filtered;
    }

    private static async Task WriteAuthorityEventAsync(
        NaradSseWriteContext writer,
        NaradPersistedEvent persistedEvent,
        CancellationToken cancellationToken)
    {
        if (!NaradPublicAuthorityFilter.TryBuildPublicPayload(persistedEvent, out JsonObject payload))
        {
            return;
        }

        await writer.WriteAuthorityAsync(payload, persistedEvent.EventId, cancellationToken).ConfigureAwait(false);
    }

    internal static IReadOnlyList<NaradPersistedEvent> LoadBackfillThroughBoundary(
        IAgentDefinitionStore definitionStore,
        string storageLocatorKey,
        string sessionId,
        long afterSessionSequence,
        long boundaryRevision)
    {
        var backfill = new List<NaradPersistedEvent>();
        long cursor = afterSessionSequence;
        while (true)
        {
            IReadOnlyList<NaradPersistedEvent> page = definitionStore.ListNaradSessionHistory(
                storageLocatorKey,
                sessionId,
                cursor,
                MaxHistoryWindow);
            if (page.Count == 0)
            {
                break;
            }

            foreach (NaradPersistedEvent persistedEvent in page)
            {
                if (persistedEvent.SessionSequence > boundaryRevision)
                {
                    return backfill;
                }

                backfill.Add(persistedEvent);
            }

            long lastSequence = page[^1].SessionSequence;
            if (lastSequence >= boundaryRevision || page.Count < MaxHistoryWindow)
            {
                break;
            }

            cursor = lastSequence;
        }

        return backfill;
    }

    internal static void ValidateBackfill(IReadOnlyList<NaradPersistedEvent> backfill)
    {
        foreach (NaradPersistedEvent persistedEvent in backfill)
        {
            _ = NaradStreamObservation.ValidateEventJson(persistedEvent);
            _ = NaradEventJsonParser.ParseBody(persistedEvent);
        }
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
            // Slow-subscriber disconnect during backfill; caller resumes from durable history.
        }
    }

    private static bool BelongsToSessionStream(NaradPersistedEvent persistedEvent, string sessionId)
        => string.Equals(persistedEvent.SessionId, sessionId, StringComparison.Ordinal);

    private static string? ResolveActiveRootRunId(
        IAgentDefinitionStore definitionStore,
        string storageLocatorKey,
        string sessionId)
    {
        IReadOnlyList<string> rootRunIds = definitionStore.ListNaradRootRunIdsForSession(storageLocatorKey, sessionId);
        for (int index = rootRunIds.Count - 1; index >= 0; index--)
        {
            string rootRunId = rootRunIds[index];
            if (!definitionStore.IsNaradRootRunAdmissionClosed(storageLocatorKey, rootRunId))
            {
                return rootRunId;
            }
        }

        return rootRunIds.Count > 0 ? rootRunIds[^1] : null;
    }
}
