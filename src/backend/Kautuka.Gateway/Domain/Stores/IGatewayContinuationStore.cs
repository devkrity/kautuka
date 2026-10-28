using Kautuka.Gateway.Domain.Execution;

namespace Kautuka.Gateway.Domain.Stores;

internal interface IGatewayContinuationStore
{
    void CreateCheckpoint(GatewayContinuationCheckpoint checkpoint);

    int GetNextCheckpointIndex(string continuationRunId);

    GatewayContinuationCheckpoint? GetById(string checkpointId);

    GatewayContinuationCheckpoint? GetByResumeTokenHash(string resumeTokenHash);

    void DeleteBySessions(IReadOnlyCollection<string> sessionIds);
}
