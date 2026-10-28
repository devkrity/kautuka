using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

using Microsoft.Data.Sqlite;

namespace Kautuka.Gateway.Domain.Execution;

internal sealed record QueuedSessionTurn(string Id, string SessionId, string TargetId, string PayloadJson, string? LeaseId, long Order = 0);

internal sealed class SessionQueueRejectedException : InvalidOperationException
{
    public SessionQueueRejectedException() : base("The message could not be queued.") { }
    public SessionQueueRejectedException(string message) : base(message) { }
    public SessionQueueRejectedException(string message, Exception innerException) : base(message, innerException) { }
}

internal sealed class SessionTurnQueueStore
{
    private readonly string connectionString;
    public SessionTurnQueueStore(KautukaDatabase database)
    {
        connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(database.DataDirectory, "channel-conversations.db"),
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false,
            DefaultTimeout = 5,
        }.ToString();
        using SqliteConnection connection = Open();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS channel_pending_turns (
                id TEXT PRIMARY KEY, session_id TEXT NOT NULL, target_id TEXT NOT NULL,
                payload_json TEXT NOT NULL, status TEXT NOT NULL, created_at INTEGER NOT NULL,
                lease_id TEXT NULL, leased_until INTEGER NULL, error TEXT NULL);
            CREATE INDEX IF NOT EXISTS idx_channel_pending_session ON channel_pending_turns(session_id, created_at, id);
            CREATE INDEX IF NOT EXISTS idx_channel_pending_status ON channel_pending_turns(status, created_at);
            """;
        command.ExecuteNonQuery();
    }
    public static string BuildId(string registrationId, string eventId)
        => "channel-turn-" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(registrationId + "\n" + eventId)))[..32];

    public bool Admit(string id, JsonElement request, JsonObject? delivery = null)
    {
        string Required(string name, string alias) => request.GetStringProperty(name, request.GetStringProperty(alias, string.Empty)) is { Length: > 0 } value
            ? value : throw new InvalidOperationException("Missing turn identity: " + name);
        string session = Required("session_id", "sessionId");
        string target = Required("target_id", "targetId");
        string owner = Required("user_id", "userId");
        JsonObject payload = delivery?.DeepClone().AsObject() ?? new JsonObject();
        payload["ownerUserId"] = owner;
        payload["message"] = request.TryGetProperty("message", out JsonElement text) ? text.GetString() : string.Empty;
        payload["request"] = JsonNode.Parse(request.GetRawText());
        return Enqueue(id, session, target, payload.ToJsonString());
    }

    public IReadOnlyList<QueuedSessionTurn> ListSession(string owner, string sessionId)
    {
        using SqliteConnection connection = Open();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT id,session_id,target_id,payload_json,created_at FROM channel_pending_turns WHERE status='pending' AND session_id=$session AND json_extract(payload_json,'$.ownerUserId')=$owner ORDER BY created_at";
        command.Parameters.AddWithValue("$session", sessionId);
        command.Parameters.AddWithValue("$owner", owner);
        using SqliteDataReader reader = command.ExecuteReader();
        List<QueuedSessionTurn> turns = [];
        while (reader.Read()) turns.Add(new(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), null, reader.GetInt64(4)));
        return turns;
    }

    public IReadOnlyList<QueuedSessionTurn> ListTarget(string owner, string targetId)
    {
        using SqliteConnection connection = Open();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT id,session_id,target_id,payload_json,created_at FROM channel_pending_turns WHERE status='pending' AND target_id=$target AND json_extract(payload_json,'$.ownerUserId')=$owner ORDER BY created_at";
        command.Parameters.AddWithValue("$target", targetId);
        command.Parameters.AddWithValue("$owner", owner);
        using SqliteDataReader reader = command.ExecuteReader();
        List<QueuedSessionTurn> turns = [];
        while (reader.Read()) turns.Add(new(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), null, reader.GetInt64(4)));
        return turns;
    }

    public int RemoveSessionWaiting(string owner, string sessionId, string? id, long through = long.MaxValue)
    {
        using SqliteConnection connection = Open();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "UPDATE channel_pending_turns SET status='cancelled',payload_json='{}' WHERE status='pending' AND session_id=$session AND json_extract(payload_json,'$.ownerUserId')=$owner AND ($id IS NULL OR id=$id) AND created_at <= $through";
        command.Parameters.AddWithValue("$session", sessionId);
        command.Parameters.AddWithValue("$owner", owner);
        command.Parameters.AddWithValue("$id", (object?)id ?? DBNull.Value);
        command.Parameters.AddWithValue("$through", through);
        return command.ExecuteNonQuery();
    }

    public bool Enqueue(string id, string sessionId, string targetId, string payloadJson)
    {
        int payloadBytes = Encoding.UTF8.GetByteCount(payloadJson);
        if (payloadBytes > 1024 * 1024) throw new SessionQueueRejectedException("This message is too large to queue. Please send a smaller message.");
        using SqliteConnection connection = Open();
        using SqliteTransaction transaction = connection.BeginTransaction();
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT COUNT(*) FROM channel_pending_turns WHERE id=$id";
        command.Parameters.AddWithValue("$id", id);
        if (Convert.ToInt32(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture) != 0) return false;
        command.CommandText = "SELECT COUNT(*) FROM channel_pending_turns WHERE status IN ('pending','leased') AND session_id=$session";
        command.Parameters.AddWithValue("$session", sessionId);
        if (Convert.ToInt32(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture) >= 100)
            throw new SessionQueueRejectedException("This session's message queue is full. Remove waiting messages with /queue before sending more.");
        command.CommandText = "SELECT COUNT(*) FROM channel_pending_turns WHERE status IN ('pending','leased')";
        if (Convert.ToInt32(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture) >= 10000)
            throw new SessionQueueRejectedException("The message queue is full. Please try again later.");
        command.CommandText = "SELECT COALESCE(SUM(length(CAST(payload_json AS BLOB))),0) FROM channel_pending_turns WHERE status IN ('pending','leased')";
        if (Convert.ToInt64(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture) + payloadBytes > 32 * 1024 * 1024)
            throw new SessionQueueRejectedException("The message queue storage is full. Please try again later.");
        command.CommandText = """
            INSERT INTO channel_pending_turns(id,session_id,target_id,payload_json,status,created_at)
            VALUES($id,$session,$target,$payload,'pending',(SELECT COALESCE(MAX(created_at),0)+1 FROM channel_pending_turns));
            """;
        command.Parameters.AddWithValue("$target", targetId);
        command.Parameters.AddWithValue("$payload", payloadJson);
        command.ExecuteNonQuery();
        transaction.Commit();
        return true;
    }

    public bool HasQueued(string sessionId)
    {
        using SqliteConnection connection = Open();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT 1 FROM channel_pending_turns WHERE session_id=$session AND status IN ('pending','leased') LIMIT 1";
        command.Parameters.AddWithValue("$session", sessionId);
        return command.ExecuteScalar() is not null;
    }

    public IReadOnlyList<QueuedSessionTurn> ListHeads(int offset = 0)
    {
        using SqliteConnection connection = Open();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            UPDATE channel_pending_turns SET status='pending',lease_id=NULL,leased_until=NULL
            WHERE status='leased' AND leased_until <= $now;
            SELECT id,session_id,target_id,payload_json FROM channel_pending_turns p
            WHERE p.status='pending' AND NOT EXISTS (
              SELECT 1 FROM channel_pending_turns older WHERE older.session_id=p.session_id
                AND older.status IN ('pending','leased') AND older.created_at < p.created_at)
            ORDER BY created_at LIMIT 64 OFFSET $offset;
            """;
        command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        command.Parameters.AddWithValue("$offset", offset);
        using SqliteDataReader reader = command.ExecuteReader();
        List<QueuedSessionTurn> jobs = [];
        while (reader.Read()) jobs.Add(new(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), null));
        return jobs;
    }

    public QueuedSessionTurn? TryLease(QueuedSessionTurn job)
    {
        using SqliteConnection connection = Open();
        using SqliteCommand command = connection.CreateCommand();
        string lease = Guid.NewGuid().ToString("N");
        command.CommandText = "UPDATE channel_pending_turns SET status='leased',lease_id=$lease,leased_until=$until WHERE id=$id AND status='pending'";
        command.Parameters.AddWithValue("$id", job.Id);
        command.Parameters.AddWithValue("$lease", lease);
        command.Parameters.AddWithValue("$until", DateTimeOffset.UtcNow.AddMinutes(2).ToUnixTimeMilliseconds());
        return command.ExecuteNonQuery() == 1 ? job with { LeaseId = lease } : null;
    }

    public void Renew(QueuedSessionTurn job) => Update(job, "leased", null, renew: true);
    public void Complete(QueuedSessionTurn job) => Update(job, "completed", null);
    public void Retry(QueuedSessionTurn job) => Update(job, "pending", null);
    public void Fail(QueuedSessionTurn job, string error) => Update(job, "failed", error);
    private void Update(QueuedSessionTurn job, string status, string? error, bool renew = false)
    {
        using SqliteConnection connection = Open();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            UPDATE channel_pending_turns SET status=$status,error=$error,leased_until=$until,
              payload_json=CASE WHEN $status IN ('completed','failed') THEN '{}' ELSE payload_json END,
              lease_id=CASE WHEN $status='leased' THEN lease_id ELSE NULL END
            WHERE id=$id AND lease_id=$lease;
            """;
        command.Parameters.AddWithValue("$id", job.Id);
        command.Parameters.AddWithValue("$lease", (object?)job.LeaseId ?? DBNull.Value);
        command.Parameters.AddWithValue("$status", status);
        command.Parameters.AddWithValue("$error", (object?)error ?? DBNull.Value);
        command.Parameters.AddWithValue("$until", renew ? DateTimeOffset.UtcNow.AddMinutes(2).ToUnixTimeMilliseconds() : DBNull.Value);
        command.ExecuteNonQuery();
    }
    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(connectionString);
        connection.Open();
        return connection;
    }
}
