namespace Kautuka.Gateway.Domain.Stores;

internal static class ExecutionQuarantineStatuses
{
    public const string Open = "open";
    public const string RetryRequested = "retry_requested";
    public const string Terminated = "terminated";
    public const string Resolved = "resolved";
}

internal sealed record ExecutionQuarantineItem(
    string QuarantineId,
    string EntityType,
    string EntityId,
    string ReasonCode,
    string SafeDiagnostic,
    string Status,
    int RetryCount,
    string CreatedAt,
    string UpdatedAt,
    string? ResolvedAt = null,
    string? AuditPublishedAt = null);

internal interface IExecutionQuarantineStore
{
    ExecutionQuarantineItem Quarantine(string entityType, string entityId, string reasonCode, string safeDiagnostic);
    ExecutionQuarantineItem? Get(string quarantineId);
    IReadOnlyList<ExecutionQuarantineItem> ListActive();
    bool RequestRetry(string quarantineId);
    bool Terminate(string quarantineId);
    bool Resolve(string entityType, string entityId);
    void MarkAuditPublished(string quarantineId, string publishedAt);
}
