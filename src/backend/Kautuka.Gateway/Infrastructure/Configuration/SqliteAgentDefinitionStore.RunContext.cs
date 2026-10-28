using System.Text.Json;

using Microsoft.Data.Sqlite;

using Narad.Contracts;

namespace Kautuka.Gateway.Infrastructure.Configuration;

internal sealed partial class SqliteAgentDefinitionStore
{
    public bool HasActiveRunForSession(string storageLocatorKey, string sessionId, string? exceptRootRunId = null)
    {
        if (!File.Exists(GetDatabasePath(storageLocatorKey))) return false;
        using SqliteConnection connection = OpenConnection(storageLocatorKey);
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT 1 FROM narad_runtime_journal n
            WHERE n.session_id = $session AND n.root_run_id IS NOT NULL AND n.run_id = n.root_run_id
              AND ($except IS NULL OR n.root_run_id <> $except)
              AND n.event_type IN ('run.started','run.resumed','run.paused','run.finished','run.error','run.cancelled')
              AND n.session_sequence = (
                SELECT MAX(j.session_sequence) FROM narad_runtime_journal j
                WHERE j.session_id = n.session_id AND j.root_run_id = n.root_run_id AND j.run_id = j.root_run_id
                  AND j.event_type IN ('run.started','run.resumed','run.paused','run.finished','run.error','run.cancelled'))
              AND n.event_type NOT IN ('run.finished','run.error','run.cancelled') LIMIT 1;
            """;
        command.Parameters.AddWithValue("$session", sessionId);
        command.Parameters.AddWithValue("$except", (object?)exceptRootRunId ?? DBNull.Value);
        return command.ExecuteScalar() is not null;
    }

    public string? FindRunUserMessage(string storageLocatorKey, string sessionId, string runId)
    {
        if (!File.Exists(GetDatabasePath(storageLocatorKey))) return null;
        using SqliteConnection connection = OpenConnection(storageLocatorKey);
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT event_json FROM narad_runtime_journal
            WHERE session_id = $session AND run_id = $run AND event_type = $type
            ORDER BY session_sequence LIMIT 1;
            """;
        command.Parameters.AddWithValue("$session", sessionId);
        command.Parameters.AddWithValue("$run", runId);
        command.Parameters.AddWithValue("$type", NaradEventTypes.MessageUserRecorded);
        if (command.ExecuteScalar() is not string json) return null;
        using JsonDocument document = JsonDocument.Parse(json);
        return document.RootElement.TryGetProperty("content", out JsonElement content) && content.ValueKind == JsonValueKind.String ? content.GetString() : null;
    }
}
