using System.Text.Json;

using Microsoft.Data.Sqlite;

namespace Kautuka.Gateway.Infrastructure.Configuration;

internal sealed class SqliteAgentRuntimeRunRepository(SqliteAgentStoreDatabase database) : IAgentRuntimeRunRepository
{
    public IReadOnlyList<GatewayRun> ListRuntimeRuns(string storageLocatorKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(storageLocatorKey);
        if (!database.Exists(storageLocatorKey))
        {
            return [];
        }

        using var connection = database.OpenCurrentConnection(storageLocatorKey);
        return LoadRuntimeRuns(connection);
    }

    private static List<GatewayRun> LoadRuntimeRuns(SqliteConnection connection)
    {
        List<GatewayRun> runs = [];
        using (SqliteCommand runCommand = connection.CreateCommand())
        {
            runCommand.CommandText = "SELECT run_json FROM runtime_runs ORDER BY created_at DESC;";

            using SqliteDataReader reader = runCommand.ExecuteReader();
            while (reader.Read())
            {
                GatewayRun? run = JsonSerializer.Deserialize(reader.GetString(0), KautukaJsonContext.Default.GatewayRun);
                if (run is not null)
                {
                    runs.Add(run);
                }
            }
        }
        return runs;
    }
    public void CreateRuntimeRun(string storageLocatorKey, GatewayRun run)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(storageLocatorKey);
        ArgumentNullException.ThrowIfNull(run);

        using var connection = database.OpenCurrentConnection(storageLocatorKey);
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO runtime_runs (id, user_id, target_type, target_id, session_id, interaction_id, status, run_json, created_at)
            VALUES ($id, $userId, $targetType, $targetId, $sessionId, $interactionId, $status, $runJson, $createdAt);
            """;
        AddRuntimeRunParameters(command, run);
        command.ExecuteNonQuery();
    }

    public void UpdateRuntimeRun(string storageLocatorKey, GatewayRun run)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(storageLocatorKey);
        ArgumentNullException.ThrowIfNull(run);

        using var connection = database.OpenCurrentConnection(storageLocatorKey);
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            UPDATE runtime_runs
            SET status = $status,
                run_json = $runJson
            WHERE id = $id;
            """;
        AddRuntimeRunParameters(command, run);
        if (command.ExecuteNonQuery() != 1)
        {
            throw new InvalidOperationException($"Runtime run '{run.Id}' does not exist in the authoritative store.");
        }
    }

    private static void AddRuntimeRunParameters(SqliteCommand command, GatewayRun run)
    {
        command.Parameters.AddWithValue("$id", run.Id);
        command.Parameters.AddWithValue("$userId", run.UserId);
        command.Parameters.AddWithValue("$targetType", run.TargetType);
        command.Parameters.AddWithValue("$targetId", run.TargetId);
        command.Parameters.AddWithValue("$sessionId", (object?)run.SessionId ?? DBNull.Value);
        command.Parameters.AddWithValue("$interactionId", (object?)run.InteractionId ?? DBNull.Value);
        command.Parameters.AddWithValue("$status", run.Status);
        command.Parameters.AddWithValue(
            "$runJson",
            GatewayRuntimePayloadGuard.ValidatePersistedJson(
                JsonSerializer.Serialize(run, KautukaJsonContext.Default.GatewayRun),
                "agent.runtime_runs.run_json"));
        command.Parameters.AddWithValue("$createdAt", run.StartedAt);
    }
}
