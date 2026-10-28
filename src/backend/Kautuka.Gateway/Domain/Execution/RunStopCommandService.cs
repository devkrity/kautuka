using System.Text.Json;
using System.Text.Json.Nodes;

using Kautuka.Gateway.Domain.Hitl;

using Narad.Contracts;

namespace Kautuka.Gateway.Domain.Execution;

internal enum RunStopCommandStatus
{
    Accepted,
    AlreadyRequested,
    AlreadyTerminal,
    NotFound,
}

internal sealed record RunStopCommand(
    string AgentId,
    string ThreadId,
    string RootRunId,
    string UserId,
    string Actor,
    string Channel);

internal interface IRunStopCommandService
{
    ValueTask<RunStopCommandStatus> RequestStopAsync(
        RunStopCommand command,
        CancellationToken cancellationToken);
}

/// <summary>
/// Persists the user's stop intent before waking the in-process execution cancellation path.
/// The runtime event is the command of record; <see cref="IRunStopRegistry"/> is only a signal.
/// </summary>
internal sealed class RunStopCommandService(
    IRuntimeEventHistoryReader history,
    IRuntimeEventPublisher publisher,
    IRunStopRegistry stopSignals) : IRunStopCommandService
{
    public async ValueTask<RunStopCommandStatus> RequestStopAsync(
        RunStopCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        IReadOnlyList<NaradPersistedEvent> rootHistory = history.ReadRootHistory(command.RootRunId);
        NaradPersistedEvent? run = rootHistory.FirstOrDefault(runtimeEvent =>
            string.Equals(runtimeEvent.RunId, command.RootRunId, StringComparison.Ordinal)
            && string.Equals(runtimeEvent.EventType, NaradEventTypes.RunStarted, StringComparison.Ordinal));
        if (run is null)
        {
            IReadOnlyList<NaradPersistedEvent> sessionHistory = history.ReadSessionHistory(
                command.AgentId,
                command.ThreadId);
            run = sessionHistory.FirstOrDefault(runtimeEvent =>
                string.Equals(runtimeEvent.RunId, command.RootRunId, StringComparison.Ordinal)
                && string.Equals(runtimeEvent.SessionId, command.ThreadId, StringComparison.Ordinal)
                && string.Equals(runtimeEvent.EventType, NaradEventTypes.RunStarted, StringComparison.Ordinal));
            if (run is null)
            {
                return RunStopCommandStatus.NotFound;
            }

            string fallbackRootRunId = run.RootRunId ?? command.RootRunId;
            rootHistory = [.. sessionHistory.Where(runtimeEvent =>
                string.Equals(runtimeEvent.RootRunId, fallbackRootRunId, StringComparison.Ordinal))];
        }

        string rootRunId = run.RootRunId ?? command.RootRunId;
        if (rootHistory.Any(runtimeEvent =>
            string.Equals(runtimeEvent.RunId, command.RootRunId, StringComparison.Ordinal)
            && NaradEventTypes.IsRootRunTerminalEventType(runtimeEvent.EventType)))
        {
            return RunStopCommandStatus.AlreadyTerminal;
        }

        string eventId = $"run-stop:{command.RootRunId}";
        bool alreadyRequested = rootHistory.Any(runtimeEvent =>
            (string.Equals(runtimeEvent.EventId, eventId, StringComparison.Ordinal)
                || string.Equals(runtimeEvent.EventType, NaradEventTypes.RunStopRequested, StringComparison.Ordinal))
            && string.Equals(runtimeEvent.RunId, command.RootRunId, StringComparison.Ordinal));
        if (!alreadyRequested)
        {
            var context = BuildContext(run, command.UserId, command.Channel);
            await publisher.PublishWithIdAsync(
                eventId,
                NaradEventTypes.RunStopRequested,
                // runId/rootRunId live on the Narad envelope (NaradEventContext), not the body —
                // reserved envelope keys in the body fail NaradJournalValidation and block stop.
                JsonSerializer.SerializeToElement(new JsonObject
                {
                    ["threadId"] = command.ThreadId,
                    ["agentId"] = command.AgentId,
                    ["requestedByUserId"] = command.UserId,
                    ["actor"] = command.Actor,
                    ["channel"] = command.Channel,
                    ["reason"] = "user_requested",
                }, KautukaJsonContext.Default.JsonObject),
                context,
                cancellationToken).ConfigureAwait(false);
        }

        // This must remain after the durable append above. A request disconnect is not a stop.
        stopSignals.RegisterStop(command.RootRunId);
        return alreadyRequested ? RunStopCommandStatus.AlreadyRequested : RunStopCommandStatus.Accepted;
    }

    private static NaradEventContext BuildContext(
        NaradPersistedEvent runEvent,
        string userId,
        string channel)
    {
        string agentId = NaradStreamObservation.ReadString(runEvent, "agentId")
            ?? NaradStreamObservation.ReadBodyString(runEvent, "agentId")
            ?? "system";
        string sessionId = runEvent.SessionId;
        string runId = runEvent.RunId ?? runEvent.RootRunId
            ?? throw new InvalidOperationException($"Run event '{runEvent.EventId}' has no run id.");
        string rootRunId = runEvent.RootRunId ?? runId;
        return new NaradEventContext(
            agentId,
            sessionId,
            userId,
            runId,
            rootRunId,
            SourceChannel: channel,
            RootAgentId: agentId);
    }
}
