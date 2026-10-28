using System.Text.Json;

using Narad.Contracts;

namespace Kautuka.Gateway.Domain.Execution;

internal static class NaradAuthorityEventDraftFactory
{
    public static NaradAuthorityEventDraft Create(
        string eventId,
        string eventType,
        JsonElement body,
        NaradEventContext context,
        string? workflowId = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(eventId);
        ArgumentException.ThrowIfNullOrWhiteSpace(eventType);
        ArgumentNullException.ThrowIfNull(context);

        bool runScoped = NaradEventTypes.RequiresRunTreeScope(eventType)
            || eventType.StartsWith("message.", StringComparison.Ordinal)
            || eventType.StartsWith("reasoning.", StringComparison.Ordinal);

        // Surface agent for channel / AAAT isolation. Public streams keep agentId.
        body = NaradEventBodies.WithAgentId(body, context.AgentId);

        // Owner stamp for Approvals invalidation SSE (/api/approvals/changes?scope=mine).
        // Public wire filters strip userId before clients see events.
        if (RequiresOwnerUserIdStamp(eventType))
        {
            body = NaradEventBodies.WithOwnerUserId(body, context.UserId);
        }

        // Preserve the delivery owner on response events as well as the user turn.
        // Session origin alone cannot identify later manual or HITL turns in that session.
        if (eventType is NaradEventTypes.MessageUserRecorded or NaradEventTypes.MessageTextDelta
            or NaradEventTypes.MessageTextCompleted or NaradEventTypes.RunFinished)
        {
            body = NaradEventBodies.WithSourceChannel(body, context.SourceChannel);
        }

        return new NaradAuthorityEventDraft(
            eventId,
            eventType,
            context.SessionId,
            body,
            DateTimeOffset.UtcNow.ToString("O"),
            RootRunId: runScoped ? context.RootRunId : null,
            RunId: runScoped ? context.RunId : null,
            WorkflowId: workflowId,
            ParentSessionId: context.ParentSessionId,
            ParentRunId: context.ParentRunId,
            ParentToolCallId: context.ParentToolCallId);
    }

    public static NaradAuthorityEventDraft CreateNew(
        string eventType,
        JsonElement body,
        NaradEventContext context,
        string? workflowId = null)
        => Create(Guid.NewGuid().ToString("N"), eventType, body, context, workflowId);

    private static bool RequiresOwnerUserIdStamp(string eventType)
        => eventType is NaradEventTypes.InterruptRequested
            or NaradEventTypes.InterruptResolved
            or NaradEventTypes.RunPaused;
}
