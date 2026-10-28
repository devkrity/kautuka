using System.Text.Json;
using System.Text.Json.Nodes;

using Kautuka.Gateway.Domain.Stores;

using Narad.Contracts;

namespace Kautuka.Gateway.Domain.Execution;

internal interface ISessionTurnSink
{
    ValueTask ObserveAsync(NaradPersistedEvent runtimeEvent, CancellationToken cancellationToken);
}

internal interface ISessionTurnProjection
{
    ValueTask<ISessionTurnSink?> PrepareAsync(QueuedSessionTurn turn, CancellationToken cancellationToken);
    ValueTask QueueChangedAsync(QueuedSessionTurn turn, CancellationToken cancellationToken);
    ValueTask FailedAsync(QueuedSessionTurn turn, CancellationToken cancellationToken);
}

internal sealed class SessionTurnExecution(
    GatewayState state,
    IGatewaySessionStore sessions,
    IChatGatewayService chat,
    IEnumerable<ISessionTurnProjection> projections,
    IRuntimeEventPublisher? publisher = null)
{
    public bool IsBusy(string target, string session, string? exceptRoot = null)
        => state.AgentDefinitionStore.HasActiveRunForSession(state.AgentDefinitionStore.GetStorageLocatorKey(target), session, exceptRoot);

    public async Task ExecuteAsync(QueuedSessionTurn turn, CancellationToken cancellationToken)
    {
        IReadOnlyList<NaradPersistedEvent> history = state.AgentDefinitionStore.ListNaradRootHistoryFull(
            state.AgentDefinitionStore.GetStorageLocatorKey(turn.TargetId), turn.Id);
        if (history.Count > 0)
        {
            NaradPersistedEvent? lifecycle = history.LastOrDefault(item => item.RunId == turn.Id && NaradEventTypes.IsRunLifecycleEventType(item.EventType));
            if (lifecycle?.EventType is NaradEventTypes.RunFinished or NaradEventTypes.RunPaused) return;
            if (lifecycle is not null && NaradEventTypes.IsRootRunTerminalEventType(lifecycle.EventType))
                throw new InvalidOperationException("The queued run previously ended with an error or cancellation.");
            throw new ChannelSessionBusyException();
        }
        JsonObject payload = JsonNode.Parse(turn.PayloadJson)!.AsObject();
        string owner = payload["ownerUserId"]!.GetValue<string>();
        if (!sessions.TryGetSession(turn.SessionId, out GatewaySession? session) || session is null
            || session.OwnerUserId != owner || session.TargetId != turn.TargetId)
            throw new InvalidOperationException("Queued session access is no longer available.");
        if (state.Agents.TryGetValue(turn.TargetId, out GatewayAgent? agent)
            && (!agent.Enabled || !GatewayOwnershipAccess.CallerCanUse(owner, agent.Scope, agent.OwnerUserId)))
            throw new InvalidOperationException("Queued agent access is no longer available.");
        List<ISessionTurnSink> sinks = [];
        foreach (ISessionTurnProjection projection in projections)
            if (await projection.PrepareAsync(turn, cancellationToken).ConfigureAwait(false) is { } sink) sinks.Add(sink);
        JsonObject request = payload["request"]!.AsObject();
        request["session_id"] = turn.SessionId;
        request["run_id"] = turn.Id;
        request["early_lifecycle_published"] = false;
        await foreach (NaradPersistedEvent runtimeEvent in chat.StreamChatAsync(
            JsonSerializer.SerializeToElement(request, KautukaJsonContext.Default.JsonObject), cancellationToken: cancellationToken).ConfigureAwait(false))
        {
            foreach (ISessionTurnSink sink in sinks) await sink.ObserveAsync(runtimeEvent, cancellationToken).ConfigureAwait(false);
            if (runtimeEvent.EventType == NaradEventTypes.RunPaused) break;
        }
    }

    public async Task QueueChangedAsync(QueuedSessionTurn turn, CancellationToken cancellationToken)
    {
        foreach (ISessionTurnProjection projection in projections)
            await projection.QueueChangedAsync(turn, cancellationToken).ConfigureAwait(false);
    }

    public async Task FailedAsync(QueuedSessionTurn turn, CancellationToken cancellationToken)
    {
        JsonObject payload = JsonNode.Parse(turn.PayloadJson)!.AsObject();
        // Admission failures need a durable browser-visible outcome too. Never rewrite an existing run.
        if (publisher is not null && payload["registrationId"] is null
            && state.AgentDefinitionStore.ListNaradRootHistoryFull(state.AgentDefinitionStore.GetStorageLocatorKey(turn.TargetId), turn.Id).Count == 0)
        {
            string owner = payload["ownerUserId"]!.GetValue<string>();
            var context = new NaradEventContext(turn.TargetId, turn.SessionId, owner, turn.Id, turn.Id);
            foreach ((string type, JsonElement body) in new (string, JsonElement)[]
            {
                (NaradEventTypes.RunStarted, NaradEventBodies.RunStarted("narad")),
                (NaradEventTypes.MessageUserRecorded, NaradEventBodies.MessageUserRecorded(NaradEventBodies.BuildUserMessageId(turn.Id, turn.Id), payload["message"]?.GetValue<string>() ?? string.Empty)),
                (NaradEventTypes.RunError, NaradEventBodies.RunError("queued_turn_failed", "This queued message could not be completed. Please check session access and send it again.", null)),
            })
                await publisher.PublishAsync(NaradAuthorityEventDraftFactory.CreateNew(type, body, context), context, cancellationToken).ConfigureAwait(false);
        }
        foreach (ISessionTurnProjection projection in projections)
            await projection.FailedAsync(turn, cancellationToken).ConfigureAwait(false);
    }
}
