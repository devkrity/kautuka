using Dapper;

using Microsoft.Data.Sqlite;

namespace Kautuka.Gateway.Domain.Stores;

internal sealed class ExecutionQuarantineStore(KautukaDatabase database) : IExecutionQuarantineStore
{
    private const string selectSql =
        """
        SELECT quarantine_id AS QuarantineId, entity_type AS EntityType, entity_id AS EntityId,
               reason_code AS ReasonCode, safe_diagnostic AS SafeDiagnostic, status AS Status,
               retry_count AS RetryCount, created_at AS CreatedAt, updated_at AS UpdatedAt,
               resolved_at AS ResolvedAt, audit_published_at AS AuditPublishedAt
        FROM execution_quarantine
        """;

    public ExecutionQuarantineItem Quarantine(string entityType, string entityId, string reasonCode, string safeDiagnostic)
    {
        Validate(entityType, entityId, reasonCode, safeDiagnostic);
        string now = DateTimeOffset.UtcNow.ToString("O");
        using var connection = Open();
        connection.Execute(
            """
            INSERT INTO execution_quarantine
                (quarantine_id, entity_type, entity_id, reason_code, safe_diagnostic,
                 status, retry_count, created_at, updated_at)
            VALUES ($quarantineId, $entityType, $entityId, $reasonCode, $safeDiagnostic,
                    $status, 0, $now, $now)
            ON CONFLICT(entity_type, entity_id) WHERE status IN ('open', 'retry_requested')
            DO UPDATE SET reason_code = excluded.reason_code,
                          safe_diagnostic = excluded.safe_diagnostic,
                          status = 'open', updated_at = excluded.updated_at
            """,
            new
            {
                quarantineId = $"quarantine-{Guid.NewGuid():N}",
                entityType,
                entityId,
                reasonCode,
                safeDiagnostic,
                status = ExecutionQuarantineStatuses.Open,
                now,
            });
        return connection.QuerySingle<ExecutionQuarantineItem>(
            selectSql + " WHERE entity_type = $entityType AND entity_id = $entityId AND status IN ('open', 'retry_requested')",
            new { entityType, entityId });
    }

    public IReadOnlyList<ExecutionQuarantineItem> ListActive()
    {
        using var connection = Open();
        return connection.Query<ExecutionQuarantineItem>(
            selectSql + " WHERE status IN ('open', 'retry_requested') ORDER BY created_at").AsList();
    }

    public ExecutionQuarantineItem? Get(string quarantineId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(quarantineId);
        using var connection = Open();
        return connection.QuerySingleOrDefault<ExecutionQuarantineItem>(
            selectSql + " WHERE quarantine_id = $quarantineId",
            new { quarantineId });
    }

    public bool RequestRetry(string quarantineId)
        => Transition(quarantineId, ExecutionQuarantineStatuses.RetryRequested, incrementRetry: true);

    public bool Terminate(string quarantineId)
        => Transition(quarantineId, ExecutionQuarantineStatuses.Terminated, incrementRetry: false);

    public bool Resolve(string entityType, string entityId)
    {
        string now = DateTimeOffset.UtcNow.ToString("O");
        using var connection = Open();
        return connection.Execute(
            """
            UPDATE execution_quarantine
            SET status = $resolved, resolved_at = $now, updated_at = $now
            WHERE entity_type = $entityType AND entity_id = $entityId
              AND status IN ($open, $retry)
            """,
            new
            {
                entityType,
                entityId,
                now,
                resolved = ExecutionQuarantineStatuses.Resolved,
                open = ExecutionQuarantineStatuses.Open,
                retry = ExecutionQuarantineStatuses.RetryRequested,
            }) > 0;
    }

    public void MarkAuditPublished(string quarantineId, string publishedAt)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(quarantineId);
        ArgumentException.ThrowIfNullOrWhiteSpace(publishedAt);
        using var connection = Open();
        int changed = connection.Execute(
            "UPDATE execution_quarantine SET audit_published_at = $publishedAt WHERE quarantine_id = $quarantineId AND audit_published_at IS NULL",
            new { quarantineId, publishedAt });
        if (changed is not 0 and not 1)
        {
            throw new InvalidOperationException($"Quarantine audit marker update affected {changed} rows.");
        }
    }

    private bool Transition(string quarantineId, string status, bool incrementRetry)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(quarantineId);
        using var connection = Open();
        return connection.Execute(
            """
            UPDATE execution_quarantine
            SET status = $status, retry_count = retry_count + $retryIncrement,
                resolved_at = CASE WHEN $status = 'terminated' THEN $now ELSE NULL END,
                updated_at = $now
            WHERE quarantine_id = $quarantineId AND status IN ('open', 'retry_requested')
            """,
            new
            {
                quarantineId,
                status,
                retryIncrement = incrementRetry ? 1 : 0,
                now = DateTimeOffset.UtcNow.ToString("O"),
            }) == 1;
    }

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(database.ConnectionString);
        connection.Open();
        connection.Execute("PRAGMA busy_timeout=5000;");
        return connection;
    }

    private static void Validate(string entityType, string entityId, string reasonCode, string safeDiagnostic)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(entityType);
        ArgumentException.ThrowIfNullOrWhiteSpace(entityId);
        ArgumentException.ThrowIfNullOrWhiteSpace(reasonCode);
        ArgumentException.ThrowIfNullOrWhiteSpace(safeDiagnostic);
        if (safeDiagnostic.Length > 1024)
        {
            throw new ArgumentOutOfRangeException(nameof(safeDiagnostic), "Safe diagnostics are limited to 1024 characters.");
        }
    }
}
