using System.Globalization;

using Microsoft.Data.Sqlite;

namespace Kautuka.Gateway.Domain.Channels;

internal enum ChannelPanelView { Queue, Summary, Sessions, Closed }

internal sealed record ChannelQueueView(long Revision, long DeliveredRevision, string? MessageId, int PageOffset = 0, ChannelPanelView View = ChannelPanelView.Queue);

internal sealed class ChannelQueueViewStore
{
    public static string RegistrationScope(GatewayChannelRegistration registration)
        => registration.Id;
    private readonly string connectionString;
    public ChannelQueueViewStore(KautukaDatabase database)
    {
        connectionString = new SqliteConnectionStringBuilder { DataSource = Path.Combine(database.DataDirectory, "channel-conversations.db"), Pooling = false }.ToString();
        using SqliteConnection connection = Open();
        using SqliteTransaction transaction = connection.BeginTransaction(deferred: false);
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS channel_queue_views (
                owner_id TEXT NOT NULL, registration_id TEXT NOT NULL, chat_id TEXT NOT NULL, topic_id TEXT NOT NULL,
                revision INTEGER NOT NULL, PRIMARY KEY(owner_id,registration_id,chat_id,topic_id));
            """;
        command.ExecuteNonQuery();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS channel_ui_actions (
                token TEXT PRIMARY KEY, owner TEXT NOT NULL, registration TEXT NOT NULL, chat TEXT NOT NULL,
                topic TEXT NOT NULL, kind TEXT NOT NULL, session TEXT NOT NULL, value TEXT NOT NULL, expires INTEGER NOT NULL);
            CREATE INDEX IF NOT EXISTS idx_channel_ui_action_expiry ON channel_ui_actions(expires);
            """;
        command.ExecuteNonQuery();
        command.CommandText = "PRAGMA table_info(channel_queue_views)";
        HashSet<string> columns = new(StringComparer.Ordinal);
        using (SqliteDataReader reader = command.ExecuteReader()) while (reader.Read()) columns.Add(reader.GetString(1));
        if (!columns.Contains("message_id"))
        {
            command.CommandText = "ALTER TABLE channel_queue_views ADD COLUMN message_id TEXT NULL";
            command.ExecuteNonQuery();
        }
        if (!columns.Contains("delivered_revision"))
        {
            command.CommandText = "ALTER TABLE channel_queue_views ADD COLUMN delivered_revision INTEGER NOT NULL DEFAULT 0";
            command.ExecuteNonQuery();
        }
        if (!columns.Contains("last_queue_signature"))
        {
            command.CommandText = "ALTER TABLE channel_queue_views ADD COLUMN last_queue_signature TEXT NULL";
            command.ExecuteNonQuery();
        }
        if (!columns.Contains("page_offset"))
        {
            command.CommandText = "ALTER TABLE channel_queue_views ADD COLUMN page_offset INTEGER NOT NULL DEFAULT 0";
            command.ExecuteNonQuery();
        }
        if (!columns.Contains("view_mode"))
        {
            command.CommandText = "ALTER TABLE channel_queue_views ADD COLUMN view_mode INTEGER NOT NULL DEFAULT 1";
            command.ExecuteNonQuery();
        }
        if (!columns.Contains("last_activity"))
        {
            command.CommandText = "ALTER TABLE channel_queue_views ADD COLUMN last_activity TEXT NOT NULL DEFAULT ''";
            command.ExecuteNonQuery();
        }
        transaction.Commit();
    }

    public string CreateAction(string owner, string registration, string chat, string? topic, string kind, string session, string value)
    {
        using SqliteConnection connection = Open();
        using SqliteCommand command = connection.CreateCommand();
        string token = Guid.NewGuid().ToString("N");
        command.CommandText = "DELETE FROM channel_ui_actions WHERE expires <= $now; INSERT INTO channel_ui_actions VALUES($token,$owner,$reg,$chat,$topic,$kind,$session,$value,$expires)";
        command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        command.Parameters.AddWithValue("$token", token);
        command.Parameters.AddWithValue("$owner", owner);
        command.Parameters.AddWithValue("$reg", registration);
        command.Parameters.AddWithValue("$chat", chat);
        command.Parameters.AddWithValue("$topic", topic ?? "root");
        command.Parameters.AddWithValue("$kind", kind);
        command.Parameters.AddWithValue("$session", session);
        command.Parameters.AddWithValue("$value", value);
        command.Parameters.AddWithValue("$expires", DateTimeOffset.UtcNow.AddDays(2).ToUnixTimeSeconds());
        command.ExecuteNonQuery();
        return "ui:" + token;
    }

    public ChannelUiAction? ReadAction(string token, string owner, string registration, string chat, string? topic)
    {
        using SqliteConnection connection = Open();
        using SqliteCommand command = Scoped(connection, owner, registration, chat, topic);
        command.CommandText = "DELETE FROM channel_ui_actions WHERE token=$token AND owner=$owner AND registration=$reg AND chat=$chat AND topic=$topic AND expires>$now RETURNING kind,session,value";
        command.Parameters.AddWithValue("$token", token);
        command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        using SqliteDataReader reader = command.ExecuteReader();
        return reader.Read() ? new(reader.GetString(0), reader.GetString(1), reader.GetString(2)) : null;
    }

    public long NextQueueViewRevision(string owner, string registration, string chat, string? topic, string? signature = null, bool onlyIfChanged = false, int pageOffset = 0, ChannelPanelView view = ChannelPanelView.Queue)
    {
        using SqliteConnection connection = Open();
        using SqliteCommand command = Scoped(connection, owner, registration, chat, topic);
        command.CommandText = """
            INSERT INTO channel_queue_views(owner_id,registration_id,chat_id,topic_id,revision,last_queue_signature,page_offset,view_mode) VALUES($owner,$reg,$chat,$topic,1,COALESCE($signature,'') || ':',$offset,$view)
            ON CONFLICT(owner_id,registration_id,chat_id,topic_id) DO UPDATE SET revision=revision+1,last_queue_signature=COALESCE($signature,'') || ':' || last_activity,page_offset=$offset,view_mode=$view
            WHERE $onlyChanged=0 OR last_queue_signature IS NOT (COALESCE($signature,'') || ':' || last_activity) RETURNING revision;
            """;
        command.Parameters.AddWithValue("$signature", (object?)signature ?? DBNull.Value);
        command.Parameters.AddWithValue("$onlyChanged", onlyIfChanged ? 1 : 0);
        command.Parameters.AddWithValue("$offset", pageOffset);
        command.Parameters.AddWithValue("$view", (int)view);
        object? result = command.ExecuteScalar();
        return result is null ? 0 : Convert.ToInt64(result, CultureInfo.InvariantCulture);
    }

    // Only delivered, visible messages advance activity. Replaying the same outbox entry
    // is idempotent; panel sends themselves never call this and cannot create a refresh loop.
    public void RecordActivity(string owner, string registration, string chat, string? topic, string activityId)
    {
        using SqliteConnection connection = Open();
        using SqliteCommand command = Scoped(connection, owner, registration, chat, topic);
        command.CommandText = """
            INSERT INTO channel_queue_views(owner_id,registration_id,chat_id,topic_id,revision,last_activity)
            VALUES($owner,$reg,$chat,$topic,0,$activity)
            ON CONFLICT(owner_id,registration_id,chat_id,topic_id) DO UPDATE SET last_activity=$activity;
            """;
        command.Parameters.AddWithValue("$activity", activityId);
        command.ExecuteNonQuery();
    }

    public ChannelQueueView? Read(string owner, string registration, string chat, string? topic)
    {
        using SqliteConnection connection = Open();
        using SqliteCommand command = Scoped(connection, owner, registration, chat, topic);
        command.CommandText = "SELECT revision,delivered_revision,message_id,page_offset,view_mode FROM channel_queue_views WHERE owner_id=$owner AND registration_id=$reg AND chat_id=$chat AND topic_id=$topic";
        using SqliteDataReader reader = command.ExecuteReader();
        return reader.Read() ? new(reader.GetInt64(0), reader.GetInt64(1), reader.IsDBNull(2) ? null : reader.GetString(2), reader.GetInt32(3), (ChannelPanelView)reader.GetInt32(4)) : null;
    }

    public void Close(string owner, string registration, string chat, string? topic)
    {
        using SqliteConnection connection = Open();
        using SqliteCommand command = Scoped(connection, owner, registration, chat, topic);
        command.CommandText = "UPDATE channel_queue_views SET revision=revision+1,delivered_revision=revision+1,message_id=NULL,last_queue_signature=NULL,page_offset=0,view_mode=$closed WHERE owner_id=$owner AND registration_id=$reg AND chat_id=$chat AND topic_id=$topic";
        command.Parameters.AddWithValue("$closed", (int)ChannelPanelView.Closed);
        command.ExecuteNonQuery();
    }

    public void Delivered(string owner, string registration, string chat, string? topic, long revision, string messageId)
    {
        using SqliteConnection connection = Open();
        using SqliteCommand command = Scoped(connection, owner, registration, chat, topic);
        command.CommandText = "UPDATE channel_queue_views SET delivered_revision=$revision,message_id=$id WHERE owner_id=$owner AND registration_id=$reg AND chat_id=$chat AND topic_id=$topic AND delivered_revision <= $revision";
        command.Parameters.AddWithValue("$revision", revision);
        command.Parameters.AddWithValue("$id", messageId);
        command.ExecuteNonQuery();
    }

    private static SqliteCommand Scoped(SqliteConnection connection, string owner, string registration, string chat, string? topic)
    {
        SqliteCommand command = connection.CreateCommand();
        command.Parameters.AddWithValue("$owner", owner);
        command.Parameters.AddWithValue("$reg", registration);
        command.Parameters.AddWithValue("$chat", chat);
        command.Parameters.AddWithValue("$topic", topic ?? "root");
        return command;
    }
    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(connectionString);
        connection.Open();
        return connection;
    }
}

internal sealed record ChannelUiAction(string Kind, string SessionId, string Value);
