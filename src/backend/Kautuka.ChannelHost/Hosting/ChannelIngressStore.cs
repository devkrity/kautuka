using Google.Protobuf;

using Kautuka.Protos;

using Microsoft.Data.Sqlite;

namespace Kautuka.ChannelHost.Hosting;

/// <summary>Durable, idempotent provider ingress and cursor store.</summary>
internal sealed class ChannelIngressStore(ChannelHostDatabase database) : IDisposable
{
    public const string GatewaySubscriberId = "gateway-ingress";
    private static readonly TimeSpan deliveryLeaseDuration = TimeSpan.FromSeconds(30);
    private readonly SemaphoreSlim signal = new(0, 1);

    public bool Enqueue(ChannelInboundEventMessage message, string? cursor = null, string? cursorScope = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(message.EventId);
        using SqliteConnection connection = database.OpenConnection();
        using SqliteTransaction transaction = connection.BeginTransaction();
        EnsureSchema(connection, transaction);
        using SqliteCommand insert = connection.CreateCommand();
        insert.Transaction = transaction;
        insert.CommandText =
            "INSERT OR IGNORE INTO channel_ingress_outbox(event_id, provider_key, attachment_id, payload, created_at_utc) VALUES($id,$provider,$attachment,$payload,$now)";
        insert.Parameters.AddWithValue("$id", message.EventId);
        insert.Parameters.AddWithValue("$provider", message.ChannelType);
        insert.Parameters.AddWithValue("$attachment", message.RegistrationId);
        insert.Parameters.AddWithValue("$payload", message.ToByteArray());
        insert.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
        bool inserted = insert.ExecuteNonQuery() == 1;
        if (inserted && !string.IsNullOrWhiteSpace(message.RegistrationId) && !string.IsNullOrWhiteSpace(message.ConversationId))
        {
            using SqliteCommand bindConversation = connection.CreateCommand();
            bindConversation.Transaction = transaction;
            bindConversation.CommandText = "INSERT OR IGNORE INTO channel_attachment_conversations(attachment_id,conversation_id,binding_source,observed_at_utc) VALUES($attachment,$conversation,'observed',$now)";
            bindConversation.Parameters.AddWithValue("$attachment", message.RegistrationId);
            bindConversation.Parameters.AddWithValue("$conversation", message.ConversationId);
            bindConversation.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
            _ = bindConversation.ExecuteNonQuery();
        }
        if (!string.IsNullOrWhiteSpace(cursor))
        {
            using SqliteCommand upsertCursor = connection.CreateCommand();
            upsertCursor.Transaction = transaction;
            upsertCursor.CommandText =
                "INSERT INTO channel_provider_cursors(provider_key, attachment_id, cursor_value, updated_at_utc) VALUES($provider,$attachment,$cursor,$now) ON CONFLICT(provider_key,attachment_id) DO UPDATE SET cursor_value=excluded.cursor_value,updated_at_utc=excluded.updated_at_utc";
            upsertCursor.Parameters.AddWithValue("$provider", message.ChannelType);
            upsertCursor.Parameters.AddWithValue("$attachment", cursorScope ?? message.RegistrationId);
            upsertCursor.Parameters.AddWithValue("$cursor", cursor);
            upsertCursor.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
            _ = upsertCursor.ExecuteNonQuery();
        }
        transaction.Commit();
        if (inserted)
        {
            // Concurrent bot admissions coalesce notifications; the durable queue owns the data.
            try { signal.Release(); }
            catch (SemaphoreFullException) { }
        }
        return inserted;
    }

    public string? GetCursor(string providerKey, string attachmentId)
    {
        using SqliteConnection connection = database.OpenConnection();
        EnsureSchema(connection);
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT cursor_value FROM channel_provider_cursors WHERE provider_key=$provider AND attachment_id=$attachment";
        command.Parameters.AddWithValue("$provider", providerKey);
        command.Parameters.AddWithValue("$attachment", attachmentId);
        return command.ExecuteScalar() as string;
    }

    public void SetCursor(string providerKey, string attachmentId, string cursor)
    {
        using SqliteConnection connection = database.OpenConnection();
        EnsureSchema(connection);
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "INSERT INTO channel_provider_cursors(provider_key,attachment_id,cursor_value,updated_at_utc) VALUES($provider,$attachment,$cursor,$now) ON CONFLICT(provider_key,attachment_id) DO UPDATE SET cursor_value=excluded.cursor_value,updated_at_utc=excluded.updated_at_utc";
        command.Parameters.AddWithValue("$provider", providerKey);
        command.Parameters.AddWithValue("$attachment", attachmentId);
        command.Parameters.AddWithValue("$cursor", cursor);
        command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
        _ = command.ExecuteNonQuery();
    }

    public bool Acknowledge(string subscriberId, string eventId, string deliveryLeaseId)
    {
        if (!string.Equals(subscriberId, GatewaySubscriberId, StringComparison.Ordinal)
            || string.IsNullOrWhiteSpace(deliveryLeaseId)) return false;
        using SqliteConnection connection = database.OpenConnection();
        using SqliteTransaction transaction = connection.BeginTransaction();
        EnsureSchema(connection, transaction);
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "DELETE FROM channel_ingress_outbox WHERE event_id=$event AND delivery_lease_id=$lease";
        command.Parameters.AddWithValue("$event", eventId);
        command.Parameters.AddWithValue("$lease", deliveryLeaseId);
        bool acknowledged = command.ExecuteNonQuery() == 1;
        transaction.Commit();
        return acknowledged;
    }

    public bool ContainsAdmittedMedia(string registrationId, string eventId, string mediaId)
    {
        using SqliteConnection connection = database.OpenConnection();
        EnsureSchema(connection);
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT payload FROM channel_ingress_outbox WHERE event_id=$event AND attachment_id=$attachment";
        command.Parameters.AddWithValue("$event", eventId);
        command.Parameters.AddWithValue("$attachment", registrationId);
        object? payload = command.ExecuteScalar();
        if (payload is not byte[] bytes) return false;
        ChannelInboundEventMessage message = ChannelInboundEventMessage.Parser.ParseFrom(bytes);
        return message.InboundMedia.Any(item => string.Equals(item.MediaId, mediaId, StringComparison.Ordinal));
    }

    public async IAsyncEnumerable<ChannelInboundEventMessage> StreamAsync(
        string subscriberId,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (!string.Equals(subscriberId, GatewaySubscriberId, StringComparison.Ordinal))
            throw new InvalidOperationException("Only the canonical Gateway ingress subscriber is supported.");
        while (!cancellationToken.IsCancellationRequested)
        {
            List<ChannelInboundEventMessage> pending = ReadPending(128);
            if (pending.Count == 0)
            {
                await signal.WaitAsync(TimeSpan.FromSeconds(1), cancellationToken).ConfigureAwait(false);
                continue;
            }

            foreach (ChannelInboundEventMessage message in pending)
            {
                yield return message;
            }

            // Unacknowledged records deliberately replay. The Gateway's durable ingress
            // deduplication makes this at-least-once boundary idempotent.
            await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken).ConfigureAwait(false);
        }
    }

    private List<ChannelInboundEventMessage> ReadPending(int limit)
    {
        using SqliteConnection connection = database.OpenConnection();
        EnsureSchema(connection);
        using SqliteCommand command = connection.CreateCommand();
        using SqliteTransaction transaction = connection.BeginTransaction();
        EnsureSchema(connection, transaction);
        string now = DateTimeOffset.UtcNow.ToString("O");
        command.Transaction = transaction;
        command.CommandText = "SELECT sequence,payload FROM channel_ingress_outbox WHERE delivery_lease_id IS NULL OR delivery_lease_expires_at_utc <= $now ORDER BY sequence LIMIT $limit";
        command.Parameters.AddWithValue("$now", now);
        command.Parameters.AddWithValue("$limit", limit);
        using SqliteDataReader reader = command.ExecuteReader();
        List<(long Sequence, ChannelInboundEventMessage Message)> candidates = [];
        while (reader.Read()) candidates.Add((reader.GetInt64(0), ChannelInboundEventMessage.Parser.ParseFrom((byte[])reader[1])));
        reader.Close();
        List<ChannelInboundEventMessage> result = [];
        foreach ((long sequence, ChannelInboundEventMessage message) in candidates)
        {
            string leaseId = Guid.NewGuid().ToString("N");
            using SqliteCommand claim = connection.CreateCommand();
            claim.Transaction = transaction;
            claim.CommandText = "UPDATE channel_ingress_outbox SET delivery_lease_id=$lease,delivery_lease_expires_at_utc=$expires WHERE sequence=$sequence AND (delivery_lease_id IS NULL OR delivery_lease_expires_at_utc <= $now)";
            claim.Parameters.AddWithValue("$lease", leaseId);
            claim.Parameters.AddWithValue("$expires", DateTimeOffset.UtcNow.Add(deliveryLeaseDuration).ToString("O"));
            claim.Parameters.AddWithValue("$sequence", sequence);
            claim.Parameters.AddWithValue("$now", now);
            if (claim.ExecuteNonQuery() != 1) continue;
            message.DeliveryLeaseId = leaseId;
            result.Add(message);
        }
        transaction.Commit();
        return result;
    }

    public void Dispose() => signal.Dispose();

    private static void EnsureSchema(SqliteConnection connection, SqliteTransaction? transaction = null)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            CREATE TABLE IF NOT EXISTS channel_ingress_outbox(
                sequence INTEGER PRIMARY KEY AUTOINCREMENT,
                event_id TEXT NOT NULL UNIQUE,
                provider_key TEXT NOT NULL,
                attachment_id TEXT NOT NULL,
                payload BLOB NOT NULL,
                created_at_utc TEXT NOT NULL,
                delivery_lease_id TEXT NULL,
                delivery_lease_expires_at_utc TEXT NULL);
            CREATE TABLE IF NOT EXISTS channel_provider_cursors(
                provider_key TEXT NOT NULL,
                attachment_id TEXT NOT NULL,
                cursor_value TEXT NOT NULL,
                updated_at_utc TEXT NOT NULL,
                PRIMARY KEY(provider_key,attachment_id));
            CREATE INDEX IF NOT EXISTS idx_channel_ingress_pending ON channel_ingress_outbox(sequence);
            CREATE TABLE IF NOT EXISTS channel_attachment_conversations(
                attachment_id TEXT NOT NULL, conversation_id TEXT NOT NULL, binding_source TEXT NOT NULL,
                observed_at_utc TEXT NOT NULL, PRIMARY KEY(attachment_id,conversation_id));
            """;
        _ = command.ExecuteNonQuery();
        EnsureDeliveryLeaseColumns(connection, transaction);
    }

    private static void EnsureDeliveryLeaseColumns(SqliteConnection connection, SqliteTransaction? transaction)
    {
        using SqliteCommand inspect = connection.CreateCommand();
        inspect.Transaction = transaction;
        inspect.CommandText = "PRAGMA table_info(channel_ingress_outbox)";
        using SqliteDataReader reader = inspect.ExecuteReader();
        bool hasLeaseId = false;
        bool hasLeaseExpiry = false;
        while (reader.Read())
        {
            hasLeaseId |= string.Equals(reader.GetString(1), "delivery_lease_id", StringComparison.Ordinal);
            hasLeaseExpiry |= string.Equals(reader.GetString(1), "delivery_lease_expires_at_utc", StringComparison.Ordinal);
        }
        reader.Close();
        if (!hasLeaseId)
        {
            using SqliteCommand addLeaseId = connection.CreateCommand();
            addLeaseId.Transaction = transaction;
            addLeaseId.CommandText = "ALTER TABLE channel_ingress_outbox ADD COLUMN delivery_lease_id TEXT NULL";
            _ = addLeaseId.ExecuteNonQuery();
        }
        if (!hasLeaseExpiry)
        {
            using SqliteCommand addLeaseExpiry = connection.CreateCommand();
            addLeaseExpiry.Transaction = transaction;
            addLeaseExpiry.CommandText = "ALTER TABLE channel_ingress_outbox ADD COLUMN delivery_lease_expires_at_utc TEXT NULL";
            _ = addLeaseExpiry.ExecuteNonQuery();
        }
    }
}
