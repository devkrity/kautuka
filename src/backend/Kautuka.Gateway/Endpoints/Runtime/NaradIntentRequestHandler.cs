using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Nodes;

using Kautuka.Gateway.Domain.Execution;
using Kautuka.Gateway.Domain.Stores;

using Narad.Contracts;

namespace Kautuka.Gateway.Endpoints.Runtime;

internal interface INaradIntentRequestHandler
{
    Task<IResult> SendMessageAsync(
        string agentId,
        string sessionId,
        JsonElement body,
        GatewayUser user,
        CancellationToken cancellationToken);
}

internal sealed partial class NaradIntentRequestHandler(
    INaradIntentExecutionQueue intentQueue,
    IGatewaySessionStore sessions,
    IGatewayCatalogStore catalog,
    IThreadCommandCoordinator coordinator,
    SessionTurnQueueStore queue,
    IEnumerable<ISessionTurnProjection> projections,
    ILogger<NaradIntentRequestHandler> logger) : INaradIntentRequestHandler
{
    [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "A failed queue display refresh must not reject an already durably accepted prompt and cause duplicate resubmission.")]
    public async Task<IResult> SendMessageAsync(
        string agentId,
        string sessionId,
        JsonElement body,
        GatewayUser user,
        CancellationToken cancellationToken)
    {
        GatewayAgent? agent = NaradSessionMappings.ResolveAgent(catalog, user, agentId);
        if (agent is null)
        {
            return NaradIntentResponses.Rejected("Forbidden.", StatusCodes.Status403Forbidden);
        }

        IResult? accessError = NaradSessionMappings.ValidateSessionAccess(sessionId, agent, user, sessions, out GatewaySession? session);
        if (accessError is not null || session is null)
        {
            return accessError ?? NaradIntentResponses.Rejected("Session not found.", StatusCodes.Status404NotFound);
        }

        if (body.GetRawText().Length > NaradRequestParsing.MaxBodyLength)
        {
            return NaradIntentResponses.Rejected("Request body exceeds maximum size.");
        }

        NaradUserMessage? userMessage;
        IReadOnlyList<string> requestedAssetIds;
        try
        {
            userMessage = NaradRequestParsing.TryExtractUserMessage(body);
            requestedAssetIds = NaradRequestParsing.ExtractAssetIds(body);
        }
        catch (NaradRequestValidationException ex)
        {
            return NaradIntentResponses.Rejected(ex.Message);
        }

        string messageContent = userMessage?.Content ?? string.Empty;
        if (string.IsNullOrWhiteSpace(messageContent) && requestedAssetIds.Count == 0)
        {
            return NaradIntentResponses.Rejected("A non-empty user message or at least one attachment is required.");
        }

        // Appending a waiting prompt does not mutate the run being viewed. Its event
        // revision can advance continuously while this request is in flight.
        ThreadCommandGateResult<IResult> gate = await coordinator.ExecuteAsync(
            agent.Id,
            sessionId,
            expectedRevision: null,
            async (currentRevision, ct) =>
            {
                string runId = Guid.NewGuid().ToString("N");
                string userMessageId = NaradEventBodies.BuildUserMessageId(runId, runId);
                IReadOnlyList<string> assetIds = requestedAssetIds;
                JsonObject requestBody = new()
                {
                    ["target_type"] = "agent",
                    ["target_id"] = agent.Id,
                    ["session_id"] = sessionId,
                    ["user_id"] = user.Id,
                    ["message"] = messageContent,
                    ["source"] = "narad",
                    ["run_id"] = runId,
                    ["user_message_id"] = userMessageId,
                    ["early_lifecycle_published"] = false,
                    ["assetIds"] = new JsonArray([.. assetIds.Select(static id => JsonValue.Create(id))]),
                };

                if (!intentQueue.TryEnqueue(new NaradIntentExecutionWork(JsonSerializer.SerializeToElement(requestBody, KautukaJsonContext.Default.JsonObject))))
                {
                    return NaradIntentResponses.Rejected("Intent queue is full; retry later.", StatusCodes.Status503ServiceUnavailable);
                }

                try
                {
                    QueuedSessionTurn? turn = queue.ListSession(user.Id, sessionId).FirstOrDefault(item => item.Id == runId);
                    if (turn is not null)
                        foreach (ISessionTurnProjection projection in projections) await projection.QueueChangedAsync(turn, ct).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException) { LogQueueProjectionFailed(logger, ex.GetType().Name); }
                return NaradIntentResponses.Accepted(currentRevision, currentRevision);
            },
            cancellationToken).ConfigureAwait(false);

        return gate.Status switch
        {
            ThreadCommandStatus.Conflict => NaradIntentResponses.Conflict(gate.Detail ?? "Revision conflict."),
            ThreadCommandStatus.Accepted => gate.Value ?? NaradIntentResponses.Accepted(gate.CurrentRevision),
            _ => NaradIntentResponses.Rejected("Command rejected."),
        };
    }

    [LoggerMessage(1, LogLevel.Warning, "Accepted message queue display could not refresh. failureType={FailureType}")]
    private static partial void LogQueueProjectionFailed(ILogger logger, string failureType);
}
