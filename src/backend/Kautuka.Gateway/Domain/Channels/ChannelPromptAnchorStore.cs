using Microsoft.Data.Sqlite;

namespace Kautuka.Gateway.Domain.Channels;

// Gateway owns the immutable association between a run and its visible prompt.
// Scope includes the bot identity and session so delivery never borrows another conversation's prompt.
internal sealed class ChannelPromptAnchorStore
{
    private readonly string connectionString;

    public ChannelPromptAnchorStore(KautukaDatabase database)
    {
        connectionString = new SqliteConnectionStringBuilder { DataSource = Path.Combine(database.DataDirectory, "channel-conversations.db"), Pooling = false }.ToString();
        using SqliteConnection connection = Open();
        using SqliteCommand command = connection.CreateCommand();
        using SqliteTransaction transaction = connection.BeginTransaction(deferred: false);
        command.Transaction = transaction;
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS channel_prompt_anchors (
                owner TEXT NOT NULL, registration TEXT NOT NULL, chat TEXT NOT NULL, topic TEXT NOT NULL,
                session TEXT NOT NULL, root TEXT NOT NULL, message TEXT NULL,
                PRIMARY KEY(owner,registration,chat,topic,session,root));
            """;
        command.ExecuteNonQuery();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS channel_message_sessions (
                owner TEXT NOT NULL, registration TEXT NOT NULL, chat TEXT NOT NULL, topic TEXT NOT NULL,
                message TEXT NOT NULL, session TEXT NOT NULL,
                PRIMARY KEY(owner,registration,chat,topic,message));
            """;
        command.ExecuteNonQuery();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS channel_view_sessions (
                owner TEXT NOT NULL, registration TEXT NOT NULL, chat TEXT NOT NULL, topic TEXT NOT NULL,
                view_id TEXT NOT NULL, session TEXT NOT NULL,
                PRIMARY KEY(owner,registration,chat,topic,view_id));
            """;
        command.ExecuteNonQuery();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS channel_target_picker_messages (
                owner TEXT NOT NULL, registration TEXT NOT NULL, chat TEXT NOT NULL, topic TEXT NOT NULL,
                message TEXT NOT NULL, created_at TEXT NOT NULL,
                PRIMARY KEY(owner,registration,chat,topic,message));
            """;
        command.ExecuteNonQuery();
        command.CommandText = "PRAGMA table_info(channel_prompt_anchors)";
        bool hasLabel = false;
        using (SqliteDataReader reader = command.ExecuteReader())
            while (reader.Read()) hasLabel |= reader.GetString(1) == "session_label";
        if (!hasLabel)
        {
            command.CommandText = "ALTER TABLE channel_prompt_anchors ADD COLUMN session_label TEXT NULL";
            command.ExecuteNonQuery();
        }
        transaction.Commit();
    }

    public void Save(GatewayChannelRegistration registration, string chat, string? topic, string session, string root, string? message, string? sessionLabel = null)
    {
        if (string.IsNullOrWhiteSpace(root)) return;
        using SqliteConnection connection = Open();
        using SqliteCommand command = Command(connection, registration, chat, topic, session, root);
        command.CommandText = """
            INSERT INTO channel_prompt_anchors(owner,registration,chat,topic,session,root,message,session_label)
            VALUES($owner,$registration,$chat,$topic,$session,$root,$message,$label)
            ON CONFLICT(owner,registration,chat,topic,session,root) DO UPDATE SET
                session_label=CASE WHEN channel_prompt_anchors.message IS NULL OR channel_prompt_anchors.message=excluded.message
                    THEN COALESCE(channel_prompt_anchors.session_label,excluded.session_label) ELSE channel_prompt_anchors.session_label END,
                message=COALESCE(channel_prompt_anchors.message,excluded.message);
            """;
        command.Parameters.AddWithValue("$message", (object?)message ?? DBNull.Value);
        command.Parameters.AddWithValue("$label", (object?)sessionLabel ?? DBNull.Value);
        command.ExecuteNonQuery();
        if (!string.IsNullOrWhiteSpace(message)) SaveMessageSession(registration, chat, topic, session, message);
    }

    public void SaveMessageSession(GatewayChannelRegistration registration, string chat, string? topic, string session, string message)
    {
        using SqliteConnection connection = Open();
        using SqliteCommand command = Command(connection, registration, chat, topic, session, "");
        command.CommandText = """
            INSERT INTO channel_message_sessions(owner,registration,chat,topic,message,session)
            VALUES($owner,$registration,$chat,$topic,$message,$session) ON CONFLICT DO NOTHING;
            """;
        command.Parameters.AddWithValue("$message", message);
        command.ExecuteNonQuery();
    }

    public string? ResolveMessageSession(GatewayChannelRegistration registration, string chat, string? topic, string message)
    {
        using SqliteConnection connection = Open();
        using SqliteCommand command = Command(connection, registration, chat, topic, "", "");
        command.CommandText = """
            SELECT session FROM channel_message_sessions WHERE owner=$owner AND registration=$registration AND chat=$chat AND topic=$topic AND message=$message
            UNION SELECT session FROM channel_prompt_anchors WHERE owner=$owner AND registration=$registration AND chat=$chat AND topic=$topic AND message=$message LIMIT 2;
            """;
        command.Parameters.AddWithValue("$message", message);
        using SqliteDataReader reader = command.ExecuteReader();
        if (!reader.Read()) return null;
        string session = reader.GetString(0);
        return reader.Read() ? null : session;
    }

    public void SaveViewSession(GatewayChannelRegistration registration, string chat, string? topic, string viewId, string session)
    {
        if (string.IsNullOrWhiteSpace(viewId) || string.IsNullOrWhiteSpace(session)) return;
        using SqliteConnection connection = Open();
        using SqliteCommand command = Command(connection, registration, chat, topic, session, "");
        command.CommandText = """
            INSERT INTO channel_view_sessions(owner,registration,chat,topic,view_id,session)
            VALUES($owner,$registration,$chat,$topic,$viewId,$session) ON CONFLICT DO NOTHING;
            """;
        command.Parameters.AddWithValue("$viewId", viewId);
        command.ExecuteNonQuery();
    }

    public string? ResolveViewSession(GatewayChannelRegistration registration, string chat, string? topic, string viewId)
    {
        using SqliteConnection connection = Open();
        using SqliteCommand command = Command(connection, registration, chat, topic, "", "");
        command.CommandText = "SELECT session FROM channel_view_sessions WHERE owner=$owner AND registration=$registration AND chat=$chat AND topic=$topic AND view_id=$viewId";
        command.Parameters.AddWithValue("$viewId", viewId);
        return command.ExecuteScalar() as string;
    }

    public void SaveTargetPickerMessage(GatewayChannelRegistration registration, string chat, string? topic, string messageId)
    {
        if (string.IsNullOrWhiteSpace(messageId)) return;
        using SqliteConnection connection = Open();
        using SqliteCommand command = Command(connection, registration, chat, topic, "", "");
        command.CommandText = """
            INSERT INTO channel_target_picker_messages(owner,registration,chat,topic,message,created_at)
            VALUES($owner,$registration,$chat,$topic,$message,$createdAt) ON CONFLICT DO NOTHING;
            """;
        command.Parameters.AddWithValue("$message", messageId);
        command.Parameters.AddWithValue("$createdAt", DateTimeOffset.UtcNow.ToString("O"));
        command.ExecuteNonQuery();
    }

    public IReadOnlyList<string> TakeTargetPickerMessages(GatewayChannelRegistration registration, string chat, string? topic)
    {
        using SqliteConnection connection = Open();
        using SqliteTransaction transaction = connection.BeginTransaction(deferred: false);
        using SqliteCommand command = Command(connection, registration, chat, topic, "", "");
        command.Transaction = transaction;
        string cutoff = DateTimeOffset.UtcNow.AddMinutes(-10).ToString("O");
        command.CommandText = "SELECT message FROM channel_target_picker_messages WHERE owner=$owner AND registration=$registration AND chat=$chat AND topic=$topic AND created_at >= $cutoff";
        command.Parameters.AddWithValue("$cutoff", cutoff);
        List<string> messages = [];
        using (SqliteDataReader reader = command.ExecuteReader())
            while (reader.Read()) messages.Add(reader.GetString(0));
        command.CommandText = "DELETE FROM channel_target_picker_messages WHERE owner=$owner AND registration=$registration AND chat=$chat AND topic=$topic";
        command.ExecuteNonQuery();
        transaction.Commit();
        return messages;
    }

    public string? ReadSessionLabel(GatewayChannelRegistration registration, string chat, string? topic, string session, string root)
    {
        using SqliteConnection connection = Open();
        using SqliteCommand command = Command(connection, registration, chat, topic, session, root);
        command.CommandText = "SELECT session_label FROM channel_prompt_anchors WHERE owner=$owner AND registration=$registration AND chat=$chat AND topic=$topic AND session=$session AND root=$root AND message IS NOT NULL";
        return command.ExecuteScalar() as string;
    }

    public (bool Expected, string? MessageId) Read(GatewayChannelRegistration registration, string chat, string? topic, string session, string root)
    {
        using SqliteConnection connection = Open();
        using SqliteCommand command = Command(connection, registration, chat, topic, session, root);
        command.CommandText = "SELECT message FROM channel_prompt_anchors WHERE owner=$owner AND registration=$registration AND chat=$chat AND topic=$topic AND session=$session AND root=$root";
        using SqliteDataReader reader = command.ExecuteReader();
        return reader.Read() ? (true, reader.IsDBNull(0) ? null : reader.GetString(0)) : (false, null);
    }

    private static SqliteCommand Command(SqliteConnection connection, GatewayChannelRegistration registration, string chat, string? topic, string session, string root)
    {
        SqliteCommand command = connection.CreateCommand();
        command.Parameters.AddWithValue("$owner", registration.OwnerUserId);
        command.Parameters.AddWithValue("$registration", ChannelQueueViewStore.RegistrationScope(registration));
        command.Parameters.AddWithValue("$chat", chat);
        command.Parameters.AddWithValue("$topic", ChannelThreadRouting.ProviderTopic(chat, topic) ?? "");
        command.Parameters.AddWithValue("$session", session);
        command.Parameters.AddWithValue("$root", root);
        return command;
    }

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(connectionString);
        connection.Open();
        return connection;
    }
}
