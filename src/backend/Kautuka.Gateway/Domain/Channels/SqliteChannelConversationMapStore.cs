using Dapper;

using Microsoft.Data.Sqlite;

namespace Kautuka.Gateway.Domain.Channels;

internal sealed partial class SqliteChannelConversationMapStore : IChannelConversationMapStore
{
    private const string permanentTopicKind = "permanent_topic";
    private const string transientThreadKind = "transient_thread";

    private readonly string connectionString;
    private readonly object syncLock = new();

    public IReadOnlyList<ChannelConversationMapEntry> ListAll()
    {
        using var connection = new SqliteConnection(connectionString);
        connection.Open();
        return connection.Query<ChannelConversationMapEntry>("SELECT * FROM channelConversationMappings WHERE expiresAt IS NULL OR expiresAt > @Now",
            new { Now = DateTimeOffset.UtcNow.ToString("O") }).ToArray();
    }

    public SqliteChannelConversationMapStore(KautukaDatabase database)
    {
        connectionString = BuildConnectionString(database);
        EnsureSchema();
    }

    public ChannelConversationMapEntry GetOrCreatePermanentTopic(
        string channelType,
        string registrationId,
        string targetType,
        string targetId,
        string providerConversationId,
        string providerTopicId,
        string? sessionId)
    {
        lock (syncLock)
        {
            using var connection = new SqliteConnection(connectionString);
            connection.Open();

            ChannelConversationMapEntry? existing = TryResolveInternal(
                connection,
                channelType,
                registrationId,
                providerConversationId,
                providerTopicId,
                kind: null);

            if (existing is not null)
            {
                if (!string.Equals(existing.TargetType, targetType, StringComparison.OrdinalIgnoreCase)
                    || !string.Equals(existing.TargetId, targetId, StringComparison.Ordinal))
                    throw new InvalidOperationException("The channel conversation is already assigned to a different target.");
                if (!string.Equals(existing.ConversationKind, permanentTopicKind, StringComparison.Ordinal) || existing.ExpiresAt is not null)
                {
                    connection.Execute("UPDATE channelConversationMappings SET conversationKind = @Kind, expiresAt = NULL WHERE id = @Id",
                        new { Kind = permanentTopicKind, existing.Id });
                    return existing with { ConversationKind = permanentTopicKind, ExpiresAt = null };
                }
                return existing;
            }

            ChannelConversationMapEntry entry = CreateEntry(
                channelType,
                registrationId,
                targetType,
                targetId,
                providerConversationId,
                providerTopicId,
                permanentTopicKind,
                sessionId,
                expiresAt: null);

            Insert(connection, entry);
            return entry;
        }
    }

    public ChannelConversationMapEntry CreateTransientThread(
        string channelType,
        string registrationId,
        string targetType,
        string targetId,
        string providerConversationId,
        string providerThreadId,
        string? sessionId,
        DateTimeOffset expiresAtUtc)
    {
        lock (syncLock)
        {
            using var connection = new SqliteConnection(connectionString);
            connection.Open();

            ChannelConversationMapEntry entry = CreateEntry(
                channelType,
                registrationId,
                targetType,
                targetId,
                providerConversationId,
                providerThreadId,
                transientThreadKind,
                sessionId,
                expiresAtUtc.ToString("O"));

            Insert(connection, entry);
            return entry;
        }
    }

    public ChannelConversationMapEntry? TryResolve(
        string channelType,
        string registrationId,
        string providerConversationId,
        string providerThreadId)
    {
        lock (syncLock)
        {
            using var connection = new SqliteConnection(connectionString);
            connection.Open();

            return TryResolveInternal(connection, channelType, registrationId, providerConversationId, providerThreadId, kind: null);
        }
    }

    public IReadOnlyList<ChannelConversationMapEntry> ListBySession(string sessionId)
    {
        lock (syncLock)
        {
            using var connection = new SqliteConnection(connectionString);
            connection.Open();
            return connection.Query<ChannelConversationMapEntry>("SELECT * FROM channelConversationMappings WHERE sessionId = @SessionId ORDER BY updatedAt DESC", new { SessionId = sessionId }).ToArray();
        }
    }

    public ChannelConversationMapEntry? TryResolveBySession(string sessionId, string channelType)
    {
        lock (syncLock)
        {
            using var connection = new SqliteConnection(connectionString);
            connection.Open();

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
                WHERE sessionId = @SessionId
                  AND channelType = @ChannelType
                ORDER BY updatedAt DESC
                LIMIT 1
                """,
                new { SessionId = sessionId, ChannelType = channelType });
        }
    }

    public ChannelConversationMapEntry? TryResolveLatestByRegistration(string channelType, string registrationId)
    {
        lock (syncLock)
        {
            using var connection = new SqliteConnection(connectionString);
            connection.Open();

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
                ORDER BY updatedAt DESC
                LIMIT 1
                """,
                new { ChannelType = channelType, RegistrationId = registrationId });
        }
    }

    public ChannelConversationMapEntry? AttachSession(
        string entryId,
        string sessionId,
        DateTimeOffset? expiresAtUtc = null)
    {
        lock (syncLock)
        {
            using var connection = new SqliteConnection(connectionString);
            connection.Open();

            string updatedAt = DateTimeOffset.UtcNow.ToString("O");
            int affected = connection.Execute(
                """
                UPDATE channelConversationMappings
                SET sessionId = @SessionId,
                    updatedAt = @UpdatedAt,
                    expiresAt = COALESCE(@ExpiresAt, expiresAt)
                WHERE id = @Id
                """,
                new { SessionId = sessionId, UpdatedAt = updatedAt, ExpiresAt = expiresAtUtc?.ToString("O"), Id = entryId });

            if (affected == 0)
                return null;

            return TryResolveById(connection, entryId);
        }
    }

    public int CleanupExpiredTransientThreads(DateTimeOffset nowUtc)
    {
        lock (syncLock)
        {
            using var connection = new SqliteConnection(connectionString);
            connection.Open();

            return connection.Execute(
                """
                DELETE FROM channelConversationMappings
                WHERE conversationKind = @Kind
                  AND expiresAt IS NOT NULL
                  AND expiresAt <= @Now
                """,
                new { Kind = transientThreadKind, Now = nowUtc.ToString("O") });
        }
    }

}
