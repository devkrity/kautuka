using Narad.Contracts;

namespace Kautuka.Gateway.Domain.Execution;

internal sealed record NaradEventContext(
    string AgentId,
    string SessionId,
    string UserId,
    string RunId,
    string RootRunId,
    string? ParentRunId = null,
    string? ParentSessionId = null,
    string? ParentToolCallId = null,
    string? ContinuationRunId = null,
    string? SourceChannel = null,
    string? InteractionId = null,
    string? TraceId = null,
    string? RootAgentId = null);

internal interface IRuntimeEventPublisher
{
    ValueTask<NaradPersistedEvent> PublishAsync(
        NaradAuthorityEventDraft draft,
        NaradEventContext context,
        CancellationToken cancellationToken = default);

    ValueTask<NaradPersistedEvent> PublishWithIdAsync(
        string eventId,
        string eventType,
        System.Text.Json.JsonElement body,
        NaradEventContext context,
        CancellationToken cancellationToken = default)
        => PublishAsync(
            NaradAuthorityEventDraftFactory.Create(eventId, eventType, body, context),
            context,
            cancellationToken);
}
