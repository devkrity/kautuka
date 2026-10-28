using System.Text.Json;

using Dapper;

using Microsoft.Data.Sqlite;

namespace Kautuka.Gateway.Domain.Channels;

internal sealed partial class SqliteChannelOutboxStore : IChannelOutboxStore
{
    private const string pendingStatus = "pending";
    private const string leasedStatus = "leased";
    private const string doneStatus = "done";
    private const string failedStatus = "failed";

    private readonly string connectionString;
    private readonly object syncLock = new();

    public SqliteChannelOutboxStore(KautukaDatabase database)
    {
        connectionString = BuildConnectionString(database);
        EnsureSchema();
    }

    public ChannelOutboxEntry Enqueue(
        string channelType,
        string registrationId,
        string actionType,
        string payloadJson,
        DateTimeOffset? availableAtUtc = null,
        string? runId = null,
        string? traceParent = null,
        string? traceState = null,
        string? ownerUserId = null)
    {
        lock (syncLock)
        {
            using var connection = new SqliteConnection(connectionString);
            connection.Open();

            if (string.Equals(actionType, "session_facts", StringComparison.Ordinal))
            {
                SupersedePendingSessionFacts(connection, registrationId, payloadJson);
            }

            ChannelOutboxEntry entry = CreateEntry(
                channelType,
                registrationId,
                actionType,
                payloadJson,
                availableAtUtc,
                runId,
                traceParent,
                traceState,
                ownerUserId);
            connection.Execute(
                """
                INSERT INTO channelOutbox (
                    id, channelType, registrationId, actionType, payloadJson, status,
                    attemptCount, createdAt, updatedAt, availableAt, leaseId, leaseOwner, leasedUntil, lastError,
                    runId, traceParent, traceState, ownerUserId)
                VALUES (
                    @Id, @ChannelType, @RegistrationId, @ActionType, @PayloadJson, @Status,
                    @AttemptCount, @CreatedAt, @UpdatedAt, @AvailableAt, @LeaseId, @LeaseOwner, @LeasedUntil, @LastError,
                    @RunId, @TraceParent, @TraceState, @OwnerUserId)
                """,
                entry);

            return entry;
        }
    }

    private static void SupersedePendingSessionFacts(SqliteConnection connection, string registrationId, string payloadJson)
    {
        if (!TryReadSessionFactsDestination(payloadJson, out string? conversationId, out string? threadId, out long revision)) return;

        List<(string Id, string PayloadJson)> pending = [];
        using (SqliteCommand command = connection.CreateCommand())
        {
            command.CommandText = "SELECT id, payloadJson FROM channelOutbox WHERE registrationId=$registrationId AND actionType='session_facts' AND status='pending'";
            command.Parameters.AddWithValue("$registrationId", registrationId);
            using SqliteDataReader reader = command.ExecuteReader();
            while (reader.Read()) pending.Add((reader.GetString(0), reader.GetString(1)));
        }

        // Session facts are a revisioned snapshot for one chat thread. A newer pending
        // snapshot fully replaces older pending ones; preserve leased entries because
        // they may already be in transport delivery.
        foreach ((string id, string pendingPayload) in pending)
        {
            if (!TryReadSessionFactsDestination(pendingPayload, out string? pendingConversationId, out string? pendingThreadId, out long pendingRevision)
                || !string.Equals(conversationId, pendingConversationId, StringComparison.Ordinal)
                || !string.Equals(threadId, pendingThreadId, StringComparison.Ordinal)
                || pendingRevision >= revision) continue;

            connection.Execute("DELETE FROM channelOutbox WHERE id=@Id AND status='pending'", new { Id = id });
        }
    }

    private static bool TryReadSessionFactsDestination(string payloadJson, out string? conversationId, out string? threadId, out long revision)
    {
        conversationId = null;
        threadId = null;
        revision = 0;
        try
        {
            using JsonDocument document = JsonDocument.Parse(payloadJson);
            JsonElement root = document.RootElement;
            if (!root.TryGetProperty("conversationId", out JsonElement conversation)
                || conversation.ValueKind != JsonValueKind.String
                || string.IsNullOrWhiteSpace(conversation.GetString())
                || !root.TryGetProperty("revision", out JsonElement revisionElement)
                || !revisionElement.TryGetInt64(out revision)) return false;
            conversationId = conversation.GetString();
            threadId = root.TryGetProperty("conversationThreadId", out JsonElement thread)
                && thread.ValueKind == JsonValueKind.String ? thread.GetString() : null;
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }


    public IReadOnlyList<ChannelOutboxEntry> LeasePending(
        string leaseOwner,
        int maxItems,
        TimeSpan leaseDuration,
        DateTimeOffset nowUtc)
    {
        int clampedMax = Math.Clamp(maxItems, 1, 100);
        DateTimeOffset leasedUntil = nowUtc.Add(leaseDuration);
        string leaseId = Guid.NewGuid().ToString("N");

        lock (syncLock)
        {
            using var connection = new SqliteConnection(connectionString);
            connection.Open();
            using SqliteTransaction tx = connection.BeginTransaction();

            // Reclaim expired leases so disconnected workers do not leave actions stuck forever.
            connection.Execute(
                """
                UPDATE channelOutbox
                SET status = @PendingStatus,
                    leaseId = NULL,
                    leaseOwner = NULL,
                    leasedUntil = NULL,
                    updatedAt = @UpdatedAt
                WHERE status = @LeasedStatus
                  AND (leasedUntil IS NULL OR leasedUntil <= @Now)
                """,
                new
                {
                    PendingStatus = pendingStatus,
                    LeasedStatus = leasedStatus,
                    UpdatedAt = nowUtc.ToString("O"),
                    Now = nowUtc.ToString("O"),
                },
                tx);

            ChannelOutboxEntry[] pending = [.. connection.Query<ChannelOutboxEntry>(
                """
                SELECT id,
                       channelType,
                       registrationId,
                       actionType,
                       payloadJson,
                       status,
                       attemptCount,
                       createdAt,
                       updatedAt,
                       availableAt,
                       leaseId,
                       leaseOwner,
                       leasedUntil,
                                             lastError,
                                             runId,
                                             traceParent,
                                             traceState,
                                             ownerUserId
                FROM channelOutbox
                WHERE status = @PendingStatus
                  AND (availableAt IS NULL OR availableAt <= @Now)
                ORDER BY CASE
                           WHEN actionType IN ('session_facts', 'queue_view', 'session_view', 'session_picker_view') THEN 1
                           ELSE 0
                         END,
                         createdAt
                LIMIT @MaxItems
                """,
                new
                {
                    PendingStatus = pendingStatus,
                    Now = nowUtc.ToString("O"),
                    MaxItems = clampedMax,
                },
                tx)];

            if (pending.Length == 0)
            {
                tx.Commit();
                return [];
            }

            string timestamp = nowUtc.ToString("O");
            int updated = 0;
            string leasedUntilText = leasedUntil.ToString("O");
            foreach (ChannelOutboxEntry item in pending)
            {
                updated += connection.Execute(
                    """
                    UPDATE channelOutbox
                    SET status = @LeasedStatus,
                        leaseId = @LeaseId,
                        leaseOwner = @LeaseOwner,
                        leasedUntil = @LeasedUntil,
                        updatedAt = @UpdatedAt
                    WHERE id = @Id
                      AND status = @PendingStatus
                    """,
                    new
                    {
                        LeasedStatus = leasedStatus,
                        LeaseId = leaseId,
                        LeaseOwner = leaseOwner,
                        LeasedUntil = leasedUntilText,
                        UpdatedAt = timestamp,
                        PendingStatus = pendingStatus,
                        Id = item.Id,
                    },
                    tx);
            }

            tx.Commit();

            if (updated <= 0)
            {
                return [];
            }

            return [.. pending.Select(item => item with
            {
                Status = leasedStatus,
                LeaseId = leaseId,
                LeaseOwner = leaseOwner,
                LeasedUntil = leasedUntilText,
                UpdatedAt = timestamp,
            })];
        }
    }

    public void Acknowledge(
        string actionId,
        string leaseOwner,
        string leaseId,
        bool success,
        string? error,
        DateTimeOffset nowUtc,
        DateTimeOffset? retryAtUtc = null,
        int maxAttempts = 10)
    {
        lock (syncLock)
        {
            using var connection = new SqliteConnection(connectionString);
            connection.Open();

            ChannelOutboxEntry? existing = connection.QueryFirstOrDefault<ChannelOutboxEntry>(
                """
                SELECT id,
                       channelType,
                       registrationId,
                       actionType,
                       payloadJson,
                       status,
                       attemptCount,
                       createdAt,
                       updatedAt,
                       availableAt,
                       leaseId,
                       leaseOwner,
                       leasedUntil,
                      lastError,
                      runId,
                      traceParent,
                      traceState,
                      ownerUserId
                FROM channelOutbox
                WHERE id = @Id
                LIMIT 1
                """,
                new { Id = actionId });

            if (existing is null
                || !string.Equals(existing.Status, leasedStatus, StringComparison.Ordinal)
                || !string.Equals(existing.LeaseOwner, leaseOwner, StringComparison.Ordinal)
                || !string.Equals(existing.LeaseId, leaseId, StringComparison.Ordinal))
            {
                return;
            }

            string updatedAt = nowUtc.ToString("O");
            if (success)
            {
                connection.Execute(
                    """
                    UPDATE channelOutbox
                    SET status = @DoneStatus,
                        updatedAt = @UpdatedAt,
                        leaseId = NULL,
                        leaseOwner = NULL,
                        leasedUntil = NULL,
                        lastError = NULL
                    WHERE id = @Id
                    """,
                    new
                    {
                        DoneStatus = doneStatus,
                        UpdatedAt = updatedAt,
                        Id = actionId,
                    });

                return;
            }

            int attempts = existing.AttemptCount + 1;
            bool exhausted = attempts >= Math.Max(1, maxAttempts);
            string nextStatus = exhausted ? failedStatus : pendingStatus;

            connection.Execute(
                """
                UPDATE channelOutbox
                SET status = @Status,
                    attemptCount = @AttemptCount,
                    updatedAt = @UpdatedAt,
                    availableAt = @AvailableAt,
                    leaseId = NULL,
                    leaseOwner = NULL,
                    leasedUntil = NULL,
                    lastError = @LastError
                WHERE id = @Id
                """,
                new
                {
                    Status = nextStatus,
                    AttemptCount = attempts,
                    UpdatedAt = updatedAt,
                    AvailableAt = exhausted ? null : retryAtUtc?.ToString("O"),
                    LastError = string.IsNullOrWhiteSpace(error) ? "channel action failed" : error,
                    Id = actionId,
                });
        }
    }
}
