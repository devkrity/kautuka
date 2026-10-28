using Kautuka.Gateway.Domain.Execution;
using Kautuka.Gateway.Domain.Stores;

namespace Kautuka.Gateway.Endpoints.Runtime;

internal sealed class WorkspaceNotifyingGatewaySessionStore(
    GatewaySessionStore inner,
    INaradWorkspaceWatchHub workspaceWatch) : IGatewaySessionStore
{
    public GatewaySession CreateSession(string targetType, string targetId, string ownerUserId, string? originator = null)
    {
        GatewaySession session = inner.CreateSession(targetType, targetId, ownerUserId, originator);
        workspaceWatch.NotifySessionCreated(session);
        return session;
    }

    public GatewaySession EnsureSession(string targetType, string targetId, string sessionId, string ownerUserId, string? originator = null)
        => inner.EnsureSession(targetType, targetId, sessionId, ownerUserId, originator);

    public IReadOnlyList<GatewaySession> ListSessions(string targetType, string targetId, string ownerUserId, bool includeArchived = false)
        => inner.ListSessions(targetType, targetId, ownerUserId, includeArchived);

    public GatewaySession? UpdateSessionTitle(string sessionId, string title)
    {
        GatewaySession? updated = inner.UpdateSessionTitle(sessionId, title);
        if (updated is not null)
        {
            workspaceWatch.NotifySessionUpdated(updated);
        }

        return updated;
    }

    public GatewaySession? ArchiveSession(string sessionId)
    {
        GatewaySession? archived = inner.ArchiveSession(sessionId);
        if (archived is not null)
        {
            workspaceWatch.NotifySessionArchived(archived);
        }

        return archived;
    }

    public IReadOnlyList<GatewaySession> ListRecentSessions(string ownerUserId, int limit)
        => inner.ListRecentSessions(ownerUserId, limit);

    public IReadOnlyList<string> PruneSessions(string targetType, string targetId, string ownerUserId, int retainCount, string preserveSessionId)
        => inner.PruneSessions(targetType, targetId, ownerUserId, retainCount, preserveSessionId);

    public bool TryGetSession(string sessionId, out GatewaySession? session)
        => inner.TryGetSession(sessionId, out session);

    public bool DeleteSession(string sessionId)
        => inner.DeleteSession(sessionId);

    public GatewaySession? CompactSession(string sessionId, string? reason = null, string? summary = null)
        => inner.CompactSession(sessionId, reason, summary);

    public GatewaySession AppendUserMessage(string sessionId, GatewayMessage userMessage, IReadOnlyList<string>? userAssetIds = null, string? userId = null)
        => inner.AppendUserMessage(sessionId, userMessage, userAssetIds, userId);

    public GatewaySession AppendAssistantMessage(string sessionId, GatewayMessage assistantMessage, string? userId = null)
        => inner.AppendAssistantMessage(sessionId, assistantMessage, userId);

    public GatewayMemoriesResponse GetMemories(string targetType, string targetId, string ownerUserId)
        => inner.GetMemories(targetType, targetId, ownerUserId);

    public bool DeleteMemoryFact(string factId)
        => inner.DeleteMemoryFact(factId);

    public bool UpdateMemoryFact(string factId, string title, string content, string category)
        => inner.UpdateMemoryFact(factId, title, content, category);

    public IReadOnlyList<GatewayRun> ListRuns()
        => inner.ListRuns();

    public bool TryGetRun(string runId, out GatewayRun? run)
        => inner.TryGetRun(runId, out run);

    public GatewaySession AppendMessages(string sessionId, GatewayMessage userMessage, GatewayMessage assistantMessage, IReadOnlyList<string>? userAssetIds = null, string? userId = null)
        => inner.AppendMessages(sessionId, userMessage, assistantMessage, userAssetIds, userId);

    public GatewayRun AppendRun(string targetType, string targetId, string userId, string? sessionId, string input, string output, GatewayMetrics? metrics = null, EffectiveRunBehavior? effectiveBehavior = null, string? runId = null, string? error = null, string? traceId = null)
        => inner.AppendRun(targetType, targetId, userId, sessionId, input, output, metrics, effectiveBehavior, runId, error, traceId);

    public GatewayRun CreateInteractionRun(string targetType, string targetId, string userId, string? sessionId, string interactionId, string prompt, IReadOnlyList<string> options)
        => inner.CreateInteractionRun(targetType, targetId, userId, sessionId, interactionId, prompt, options);

    public GatewayRun? UpdateInteractionRun(string interactionId, string status, Dictionary<string, object?> output, string? error = null)
        => inner.UpdateInteractionRun(interactionId, status, output, error);
}
