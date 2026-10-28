using Dapper;

using Microsoft.Data.Sqlite;

namespace Kautuka.Gateway.Domain.Channels;

internal sealed partial class SqliteChannelConversationMapStore
{
    private static string BuildConnectionString(KautukaDatabase database)
    {
        string dbPath = Path.Combine(database.DataDirectory, "channel-conversations.db");

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
                CREATE TABLE IF NOT EXISTS channelConversationMappings (
                    id TEXT PRIMARY KEY,
                    channelType TEXT NOT NULL,
                    registrationId TEXT NOT NULL,
                    targetType TEXT NOT NULL,
                    targetId TEXT NOT NULL,
                    providerConversationId TEXT NOT NULL,
                    providerThreadId TEXT NOT NULL,
                    conversationKind TEXT NOT NULL,
                    sessionId TEXT NULL,
                    createdAt TEXT NOT NULL,
                    updatedAt TEXT NOT NULL,
                    expiresAt TEXT NULL
                );

                CREATE INDEX IF NOT EXISTS idx_channel_map_lookup
                    ON channelConversationMappings(channelType, registrationId, providerConversationId, providerThreadId);

                CREATE INDEX IF NOT EXISTS idx_channel_map_kind_target
                    ON channelConversationMappings(conversationKind, targetType, targetId);

                CREATE INDEX IF NOT EXISTS idx_channel_map_expiry
                    ON channelConversationMappings(conversationKind, expiresAt);

                -- Telegram conversations retain their session until an explicit reset.
                -- Promote existing bindings before any expiry cleanup can remove them.
                UPDATE channelConversationMappings
                SET conversationKind = 'permanent_topic', expiresAt = NULL
                WHERE channelType = 'telegram' COLLATE NOCASE
                  AND conversationKind = 'transient_thread'
                  AND providerThreadId NOT LIKE 'task:%';
                """);
        }
    }

    private static ChannelConversationMapEntry CreateEntry(
        string channelType,
        string registrationId,
        string targetType,
        string targetId,
        string providerConversationId,
        string providerThreadId,
        string conversationKind,
        string? sessionId,
        string? expiresAt)
    {
        string timestamp = DateTimeOffset.UtcNow.ToString("O");
        return new ChannelConversationMapEntry(
            Guid.NewGuid().ToString("N"),
            channelType,
            registrationId,
            targetType,
            targetId,
            providerConversationId,
            providerThreadId,
            conversationKind,
            sessionId,
            timestamp,
            timestamp,
            expiresAt);
    }

    private static void Insert(SqliteConnection connection, ChannelConversationMapEntry entry)
    {
        connection.Execute(
            """
            INSERT INTO channelConversationMappings (
                id, channelType, registrationId, targetType, targetId,
                providerConversationId, providerThreadId, conversationKind,
                sessionId, createdAt, updatedAt, expiresAt)
            VALUES (
                @Id, @ChannelType, @RegistrationId, @TargetType, @TargetId,
                @ProviderConversationId, @ProviderThreadId, @ConversationKind,
                @SessionId, @CreatedAt, @UpdatedAt, @ExpiresAt)
            """,
            entry);
    }

    private static ChannelConversationMapEntry? TryResolveInternal(
        SqliteConnection connection,
        string channelType,
        string registrationId,
        string providerConversationId,
        string providerThreadId,
        string? kind)
    {
        return connection.QueryFirstOrDefault<ChannelConversationMapEntry>(
            """
            SELECT id,
                   channelType,
                   registrationId,
                   targetType,
                   targetId,
                   providerConversationId,
                   providerThreadId,
                   conversationKind,
                   sessionId,
                   createdAt,
                   updatedAt,
                   expiresAt
            FROM channelConversationMappings
            WHERE channelType = @ChannelType
              AND registrationId = @RegistrationId
              AND providerConversationId = @ProviderConversationId
              AND providerThreadId = @ProviderThreadId
              AND (@Kind IS NULL OR conversationKind = @Kind)
            ORDER BY updatedAt DESC
            LIMIT 1
            """,
            new { ChannelType = channelType, RegistrationId = registrationId, ProviderConversationId = providerConversationId, ProviderThreadId = providerThreadId, Kind = kind });
    }

    private static ChannelConversationMapEntry? TryResolveById(SqliteConnection connection, string entryId)
    {
        return connection.QueryFirstOrDefault<ChannelConversationMapEntry>(
            """
            SELECT id,
                   channelType,
                   registrationId,
                   targetType,
                   targetId,
                   providerConversationId,
                   providerThreadId,
                   conversationKind,
                   sessionId,
                   createdAt,
                   updatedAt,
                   expiresAt
            FROM channelConversationMappings
            WHERE id = @Id
            LIMIT 1
            """,
            new { Id = entryId });
    }
}
