using Dapper;

using Microsoft.Data.Sqlite;

namespace Kautuka.Gateway.Domain.Channels;

internal sealed partial class SqliteChannelOutboxStore
{
    private static string BuildConnectionString(KautukaDatabase database)
    {
        string dbPath = Path.Combine(database.DataDirectory, "channel-outbox.db");

        string? parent = Path.GetDirectoryName(dbPath);
        if (!string.IsNullOrWhiteSpace(parent))
        {
            Directory.CreateDirectory(parent);
        }

        return new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Private,
            Pooling = false,
        }.ToString();
    }

    private void EnsureSchema()
    {
        lock (syncLock)
        {
            using var connection = new SqliteConnection(connectionString);
            connection.Open();

            connection.Execute(
                """
                CREATE TABLE IF NOT EXISTS channelOutbox (
                    id TEXT PRIMARY KEY,
                    channelType TEXT NOT NULL,
                    registrationId TEXT NOT NULL,
                    actionType TEXT NOT NULL,
                    payloadJson TEXT NOT NULL,
                    status TEXT NOT NULL,
                    attemptCount INTEGER NOT NULL,
                    createdAt TEXT NOT NULL,
                    updatedAt TEXT NOT NULL,
                    availableAt TEXT NULL,
                    leaseId TEXT NULL,
                    leaseOwner TEXT NULL,
                    leasedUntil TEXT NULL,
                    lastError TEXT NULL,
                    runId TEXT NULL,
                    traceParent TEXT NULL,
                    traceState TEXT NULL,
                    ownerUserId TEXT NULL
                );

                CREATE INDEX IF NOT EXISTS idx_channel_outbox_ready
                    ON channelOutbox(status, availableAt, createdAt);

                CREATE INDEX IF NOT EXISTS idx_channel_outbox_lease
                    ON channelOutbox(status, leaseOwner, leaseId, leasedUntil);
                """);

            EnsureColumnExists(connection, "channelOutbox", "runId", "TEXT NULL");
            EnsureColumnExists(connection, "channelOutbox", "traceParent", "TEXT NULL");
            EnsureColumnExists(connection, "channelOutbox", "traceState", "TEXT NULL");
            EnsureColumnExists(connection, "channelOutbox", "ownerUserId", "TEXT NULL");
        }
    }

    private static void EnsureColumnExists(SqliteConnection connection, string tableName, string columnName, string columnDefinition)
    {
        long exists = connection.ExecuteScalar<long>(
            $"SELECT COUNT(*) FROM pragma_table_info('{tableName}') WHERE name = @ColumnName",
            new { ColumnName = columnName });

        if (exists > 0)
        {
            return;
        }

        connection.Execute($"ALTER TABLE {tableName} ADD COLUMN {columnName} {columnDefinition}");
    }

    private static ChannelOutboxEntry CreateEntry(
        string channelType,
        string registrationId,
        string actionType,
        string payloadJson,
        DateTimeOffset? availableAtUtc,
        string? runId,
        string? traceParent,
        string? traceState,
        string? ownerUserId)
    {
        string timestamp = DateTimeOffset.UtcNow.ToString("O");
        return new ChannelOutboxEntry(
            Id: Guid.NewGuid().ToString("N"),
            ChannelType: channelType,
            RegistrationId: registrationId,
            ActionType: actionType,
            PayloadJson: payloadJson,
            Status: pendingStatus,
            AttemptCount: 0,
            CreatedAt: timestamp,
            UpdatedAt: timestamp,
            AvailableAt: availableAtUtc?.ToString("O"),
            LeaseId: null,
            LeaseOwner: null,
            LeasedUntil: null,
            LastError: null,
            RunId: runId,
            TraceParent: traceParent,
            TraceState: traceState,
            OwnerUserId: ownerUserId);
    }
}
