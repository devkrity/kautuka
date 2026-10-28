using Kautuka.Gateway.Domain.Execution;

namespace Kautuka.Gateway.Domain.Stores;

internal interface IGatewaySessionStore
{
    GatewaySession CreateSession(string targetType, string targetId, string ownerUserId, string? originator = null);
    GatewaySession EnsureSession(string targetType, string targetId, string sessionId, string ownerUserId, string? originator = null);
    IReadOnlyList<GatewaySession> ListSessions(string targetType, string targetId, string ownerUserId, bool includeArchived = false);
    GatewaySession? UpdateSessionTitle(string sessionId, string title);
    GatewaySession? ArchiveSession(string sessionId);
    IReadOnlyList<GatewaySession> ListRecentSessions(string ownerUserId, int limit);
    IReadOnlyList<string> PruneSessions(string targetType, string targetId, string ownerUserId, int retainCount, string preserveSessionId);
    bool TryGetSession(string sessionId, out GatewaySession? session);
    bool DeleteSession(string sessionId);
    GatewaySession? CompactSession(string sessionId, string? reason = null, string? summary = null);
    GatewaySession AppendUserMessage(string sessionId, GatewayMessage userMessage, IReadOnlyList<string>? userAssetIds = null, string? userId = null);
    GatewaySession AppendAssistantMessage(string sessionId, GatewayMessage assistantMessage, string? userId = null);
    GatewayMemoriesResponse GetMemories(string targetType, string targetId, string ownerUserId);
    bool DeleteMemoryFact(string factId);
    bool UpdateMemoryFact(string factId, string title, string content, string category);
    IReadOnlyList<GatewayRun> ListRuns();
    bool TryGetRun(string runId, out GatewayRun? run);
    GatewaySession AppendMessages(string sessionId, GatewayMessage userMessage, GatewayMessage assistantMessage, IReadOnlyList<string>? userAssetIds = null, string? userId = null);
    GatewayRun AppendRun(string targetType, string targetId, string userId, string? sessionId, string input, string output, GatewayMetrics? metrics = null, EffectiveRunBehavior? effectiveBehavior = null, string? runId = null, string? error = null, string? traceId = null);
    GatewayRun CreateInteractionRun(string targetType, string targetId, string userId, string? sessionId, string interactionId, string prompt, IReadOnlyList<string> options);
    GatewayRun? UpdateInteractionRun(string interactionId, string status, Dictionary<string, object?> output, string? error = null);
}
