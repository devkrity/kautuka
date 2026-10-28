using Dapper;

using Kautuka.Gateway.Domain.Execution;

using Microsoft.Data.Sqlite;

namespace Kautuka.Gateway.Domain.Stores;

internal sealed class GatewayContinuationStore(KautukaDatabase database, IAgentContinuationCheckpointRepository checkpointRepository) : IGatewayContinuationStore
{
    public void CreateCheckpoint(GatewayContinuationCheckpoint checkpoint)
    {
        string storageLocatorKey = ResolveStorageLocatorKey(checkpoint.AgentId)
            ?? throw new InvalidOperationException($"Agent '{checkpoint.AgentId}' does not have a registered storage locator.");

        checkpointRepository.CreateContinuationCheckpoint(storageLocatorKey, checkpoint);
    }

    public int GetNextCheckpointIndex(string continuationRunId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(continuationRunId);

        int nextCheckpointIndex = 0;
        foreach (string storageLocatorKey in ListStorageLocatorKeys())
        {
            nextCheckpointIndex = Math.Max(
                nextCheckpointIndex,
                checkpointRepository.GetNextContinuationCheckpointIndex(storageLocatorKey, continuationRunId));
        }

        return nextCheckpointIndex;
    }

    public GatewayContinuationCheckpoint? GetById(string checkpointId)
    {
        foreach (string storageLocatorKey in ListStorageLocatorKeys())
        {
            GatewayContinuationCheckpoint? checkpoint = checkpointRepository.GetContinuationCheckpointById(storageLocatorKey, checkpointId);
            if (checkpoint is not null)
            {
                return checkpoint;
            }
        }

        return null;
    }

    public GatewayContinuationCheckpoint? GetByResumeTokenHash(string resumeTokenHash)
    {
        foreach (string storageLocatorKey in ListStorageLocatorKeys())
        {
            GatewayContinuationCheckpoint? checkpoint = checkpointRepository.GetContinuationCheckpointByResumeTokenHash(storageLocatorKey, resumeTokenHash);
            if (checkpoint is not null)
            {
                return checkpoint;
            }
        }

        return null;
    }

    public void DeleteBySessions(IReadOnlyCollection<string> sessionIds)
    {
        if (sessionIds.Count == 0)
        {
            return;
        }

        string[] uniqueSessionIds = [.. sessionIds
            .Where(static id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.Ordinal)];

        if (uniqueSessionIds.Length == 0)
        {
            return;
        }

        foreach (string storageLocatorKey in ListStorageLocatorKeys())
        {
            checkpointRepository.DeleteContinuationCheckpointsBySessions(storageLocatorKey, uniqueSessionIds);
        }
    }

    private string? ResolveStorageLocatorKey(string agentId)
    {
        using var connection = new SqliteConnection(database.ConnectionString);
        connection.Open();
        return connection.QueryFirstOrDefault<string>(
            "SELECT COALESCE(NULLIF(storage_locator_key, ''), id) FROM agents WHERE id = $id LIMIT 1;",
            new { id = agentId });
    }

    private IReadOnlyList<string> ListStorageLocatorKeys()
    {
        using var connection = new SqliteConnection(database.ConnectionString);
        connection.Open();
        return [.. connection.Query<string>("SELECT COALESCE(NULLIF(storage_locator_key, ''), id) FROM agents ORDER BY rowid;")
            .Where(static storageLocatorKey => !string.IsNullOrWhiteSpace(storageLocatorKey))
            .Distinct(StringComparer.Ordinal)];
    }
}
