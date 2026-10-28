using Kautuka.Gateway.Domain.Hitl;

namespace Kautuka.Gateway.Domain.Stores;

internal interface IGatewayApprovalStore
{
    /// <summary>Persists a new tool approval request. The decision is null (pending).</summary>
    void CreateRequest(GatewayToolApprovalRequest request);

    /// <summary>Records a decision (approved/denied) on an existing request.</summary>
    bool Resolve(
        string requestId,
        bool approved,
        string? reason,
        string? decidedBy,
        string resolutionSource,
        ApprovalContinuationWork? continuation = null);

    bool Expire(string requestId, DateTimeOffset nowUtc, string reason) => false;

    bool Resolve(
        string requestId,
        bool approved,
        string? reason,
        string? decidedBy,
        string resolutionSource,
        ApprovalContinuationWork? continuation,
        ApprovalResolutionEventWork? resolutionEvent)
        => Resolve(requestId, approved, reason, decidedBy, resolutionSource, continuation);

    bool Resolve(
        string requestId,
        string decision,
        string? reason,
        string? decidedBy,
        string resolutionSource,
        ApprovalContinuationWork? continuation,
        ApprovalResolutionEventWork? resolutionEvent)
        => Resolve(
            requestId,
            string.Equals(decision, "approved", StringComparison.OrdinalIgnoreCase),
            reason,
            decidedBy,
            resolutionSource,
            continuation,
            resolutionEvent);

    bool Expire(
        string requestId,
        DateTimeOffset nowUtc,
        string reason,
        ApprovalContinuationWork? continuation,
        ApprovalResolutionEventWork? resolutionEvent)
        => Expire(requestId, nowUtc, reason);

    void SetChannelTetherMode(string requestId, ChannelHitlTetherMode mode);

    /// <summary>
    /// Returns all pending approval requests visible to the caller.
    /// When <paramref name="adminAll"/> is true and the caller is an admin, returns all requests.
    /// Otherwise scoped to <paramref name="userId"/>.
    /// </summary>
    IReadOnlyList<GatewayToolApprovalRequest> ListPending(string? userId, bool adminAll);

    /// <summary>Returns pending in-run HITL requests whose expiry clock has elapsed.</summary>
    IReadOnlyList<GatewayToolApprovalRequest> ListDueForExpiry(DateTimeOffset nowUtc) => [];

    /// <summary>
    /// Extends a pending HITL request once after expiry. Returns false when a retry was already scheduled
    /// or the row is no longer pending/due.
    /// </summary>
    bool TryScheduleHitlRetry(string requestId, DateTimeOffset newExpiresAtUtc, DateTimeOffset nowUtc) => false;

    /// <summary>Returns a single request by ID, or null if not found.</summary>
    GatewayToolApprovalRequest? GetById(string requestId);

    /// <summary>
    /// Returns the most-recently decided approval for a given run + tool identity,
    /// or null if none exists. Used for logical-key replay when MAF re-mints a
    /// fresh RequestId on checkpoint resume.
    /// </summary>
    GatewayToolApprovalRequest? GetDecidedByRunAndTool(string runId, string toolCallId, string toolName);

    /// <summary>
    /// Returns the most-recently decided approval for a given run + tool name + arguments,
    /// or null if none exists. This is resilient when both RequestId and ToolCallId are
    /// re-minted during checkpoint replay.
    /// </summary>
    GatewayToolApprovalRequest? GetDecidedByRunToolAndArguments(string runId, string toolName, string? argumentsJson);

    /// <summary>Returns all requests for a given session, ordered by requested_at.</summary>
    IReadOnlyList<GatewayToolApprovalRequest> ListBySession(string sessionId);

    /// <summary>Returns requests created inside agent-tool child runs for the given outer session.</summary>
    IReadOnlyList<GatewayToolApprovalRequest> ListByOuterSession(string outerSessionId) => [];

    /// <summary>
    /// Returns resolved approval requests (approved or denied), ordered by decided_at descending.
    /// When <paramref name="adminAll"/> is true, returns all; otherwise scoped to <paramref name="userId"/>.
    /// </summary>
    IReadOnlyList<GatewayToolApprovalRequest> ListHistory(string? userId, bool adminAll, int limit, int offset);

    /// <summary>
    /// Aggregates human approve/deny counts. Excludes auto_approved, expired, and stopped.
    /// </summary>
    IReadOnlyList<HitlApprovalStatRow> ListHitlStats(HitlApprovalStatsQuery query) => [];

    /// <summary>Recent human denials for a function, newest first.</summary>
    IReadOnlyList<GatewayToolApprovalRequest> ListDeniedHistory(HitlApprovalStatsQuery query, int limit) => [];
}
