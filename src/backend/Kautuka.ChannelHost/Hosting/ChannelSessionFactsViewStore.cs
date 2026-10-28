using System.Collections.Concurrent;
using System.Globalization;

using Microsoft.Data.Sqlite;

namespace Kautuka.ChannelHost.Hosting;

internal sealed class ChannelSessionFactsViewStore
{
    private readonly string connectionString;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> gates = new(StringComparer.Ordinal);

    public ChannelSessionFactsViewStore(ChannelHostDatabase database)
    {
        connectionString = new SqliteConnectionStringBuilder(database.ConnectionString) { Pooling = false }.ToString();
        Initialize();
    }

    public async ValueTask<IDisposable> EnterAsync(string owner, string attachment, string chat, string? topic, CancellationToken cancellationToken)
    {
        string key = Key(owner, attachment, chat, topic);
        SemaphoreSlim gate = gates.GetOrAdd(key, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        return new GateLease(gate);
    }

    public ViewState? Read(string owner, string attachment, string chat, string? topic)
    {
        using SqliteConnection connection = Open();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT revision,message_id,view_mode,expires_at FROM channel_session_fact_views WHERE owner_id=$owner AND attachment_id=$attachment AND chat_id=$chat AND topic_id=$topic";
        Bind(command, owner, attachment, chat, topic);
        using SqliteDataReader reader = command.ExecuteReader();
        return reader.Read() ? new ViewState(reader.GetInt64(0), reader.IsDBNull(1) ? null : reader.GetString(1),
            reader.GetString(2), reader.IsDBNull(3) ? null : reader.GetString(3)) : null;
    }

    public void Delivered(string owner, string attachment, string chat, string? topic, long revision, string messageId,
        string viewMode = "summary", DateTimeOffset? expiresAtUtc = null)
    {
        using SqliteConnection connection = Open();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO channel_session_fact_views(owner_id,attachment_id,chat_id,topic_id,revision,message_id,view_mode,expires_at)
            VALUES($owner,$attachment,$chat,$topic,$revision,$message,$view,$expires)
            ON CONFLICT(owner_id,attachment_id,chat_id,topic_id) DO UPDATE SET revision=excluded.revision,message_id=excluded.message_id,view_mode=excluded.view_mode,expires_at=excluded.expires_at
            WHERE excluded.revision >= channel_session_fact_views.revision;
            """;
        Bind(command, owner, attachment, chat, topic);
        command.Parameters.AddWithValue("$revision", revision);
        command.Parameters.AddWithValue("$message", messageId);
        command.Parameters.AddWithValue("$view", viewMode);
        command.Parameters.AddWithValue("$expires", expiresAtUtc?.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture) ?? (object)DBNull.Value);
        command.ExecuteNonQuery();
    }

    public IReadOnlyList<ExpiredQueueCard> ListExpiredQueueCards(DateTimeOffset now)
    {
        using SqliteConnection connection = Open();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT owner_id,attachment_id,chat_id,topic_id,revision,message_id,expires_at FROM channel_session_fact_views WHERE view_mode='queue' AND expires_at <= $now AND message_id IS NOT NULL AND message_id <> ''";
        command.Parameters.AddWithValue("$now", now.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
        using SqliteDataReader reader = command.ExecuteReader();
        List<ExpiredQueueCard> rows = [];
        while (reader.Read()) rows.Add(new ExpiredQueueCard(reader.GetString(0), reader.GetString(1), reader.GetString(2),
            reader.GetString(3), reader.GetInt64(4), reader.GetString(5), reader.GetString(6)));
        return rows;
    }

    public void QueueDeletion(string owner, string attachment, string chat, string? topic, string messageId)
    {
        using SqliteConnection connection = Open();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO channel_session_fact_cleanup(owner_id,attachment_id,chat_id,topic_id,message_id,created_at)
            VALUES($owner,$attachment,$chat,$topic,$message,$created)
            ON CONFLICT(owner_id,attachment_id,chat_id,topic_id,message_id) DO NOTHING;
            """;
        Bind(command, owner, attachment, chat, topic);
        command.Parameters.AddWithValue("$message", messageId);
        command.Parameters.AddWithValue("$created", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
        command.ExecuteNonQuery();
    }

    public IReadOnlyList<PendingCardDeletion> ListPendingDeletions(int limit = 100)
    {
        using SqliteConnection connection = Open();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "DELETE FROM channel_session_fact_cleanup WHERE created_at < $cutoff";
        command.Parameters.AddWithValue("$cutoff", DateTimeOffset.UtcNow.AddDays(-30).ToString("O", CultureInfo.InvariantCulture));
        command.ExecuteNonQuery();
        command.Parameters.Clear();
        command.CommandText = """
            SELECT owner_id,attachment_id,chat_id,topic_id,message_id
            FROM channel_session_fact_cleanup ORDER BY created_at LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 500));
        using SqliteDataReader reader = command.ExecuteReader();
        List<PendingCardDeletion> rows = [];
        while (reader.Read()) rows.Add(new PendingCardDeletion(reader.GetString(0), reader.GetString(1),
            reader.GetString(2), reader.GetString(3), reader.GetString(4)));
        return rows;
    }

    public void CompleteDeletion(string owner, string attachment, string chat, string? topic, string messageId)
    {
        using SqliteConnection connection = Open();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "DELETE FROM channel_session_fact_cleanup WHERE owner_id=$owner AND attachment_id=$attachment AND chat_id=$chat AND topic_id=$topic AND message_id=$message";
        Bind(command, owner, attachment, chat, topic);
        command.Parameters.AddWithValue("$message", messageId);
        command.ExecuteNonQuery();
    }

    private SqliteConnection Open()
    {
        SqliteConnection connection = new(connectionString);
        connection.Open();
        return connection;
    }

    private void Initialize()
    {
        using SqliteConnection connection = Open();
        using SqliteTransaction transaction = connection.BeginTransaction(deferred: false);
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS channel_session_fact_views(
                owner_id TEXT NOT NULL,
                attachment_id TEXT NOT NULL,
                chat_id TEXT NOT NULL,
                topic_id TEXT NOT NULL,
                revision INTEGER NOT NULL,
                message_id TEXT NULL,
                view_mode TEXT NOT NULL DEFAULT 'summary',
                expires_at TEXT NULL,
                PRIMARY KEY(owner_id,attachment_id,chat_id,topic_id));
            """;
        command.ExecuteNonQuery();
        HashSet<string> columns = new(StringComparer.Ordinal);
        command.CommandText = "PRAGMA table_info(channel_session_fact_views)";
        using (SqliteDataReader reader = command.ExecuteReader()) while (reader.Read()) columns.Add(reader.GetString(1));
        if (!columns.Contains("view_mode"))
        {
            command.CommandText = "ALTER TABLE channel_session_fact_views ADD COLUMN view_mode TEXT NOT NULL DEFAULT 'summary'";
            command.ExecuteNonQuery();
        }
        if (!columns.Contains("expires_at"))
        {
            command.CommandText = "ALTER TABLE channel_session_fact_views ADD COLUMN expires_at TEXT NULL";
            command.ExecuteNonQuery();
        }
        command.CommandText = "CREATE INDEX IF NOT EXISTS idx_channel_session_fact_views_expiry ON channel_session_fact_views(view_mode,expires_at)";
        command.ExecuteNonQuery();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS channel_session_fact_cleanup(
                owner_id TEXT NOT NULL, attachment_id TEXT NOT NULL, chat_id TEXT NOT NULL, topic_id TEXT NOT NULL,
                message_id TEXT NOT NULL, created_at TEXT NOT NULL,
                PRIMARY KEY(owner_id,attachment_id,chat_id,topic_id,message_id));
            CREATE INDEX IF NOT EXISTS idx_channel_session_fact_cleanup_created ON channel_session_fact_cleanup(created_at);
            """;
        command.ExecuteNonQuery();
        transaction.Commit();
    }

    private static void Bind(SqliteCommand command, string owner, string attachment, string chat, string? topic)
    {
        command.Parameters.AddWithValue("$owner", owner);
        command.Parameters.AddWithValue("$attachment", attachment);
        command.Parameters.AddWithValue("$chat", chat);
        command.Parameters.AddWithValue("$topic", topic ?? "root");
    }

    private static string Key(string owner, string attachment, string chat, string? topic) => string.Join('\0', owner, attachment, chat, topic ?? "root");

    internal sealed record ViewState(long Revision, string? MessageId, string ViewMode, string? ExpiresAtUtc);
    internal sealed record ExpiredQueueCard(string Owner, string AttachmentId, string ChatId, string TopicId,
        long Revision, string MessageId, string ExpiresAtUtc);
    internal sealed record PendingCardDeletion(string Owner, string AttachmentId, string ChatId, string TopicId, string MessageId);

    private sealed class GateLease(SemaphoreSlim gate) : IDisposable
    {
        private int disposed;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) == 0) gate.Release();
        }
    }
}
