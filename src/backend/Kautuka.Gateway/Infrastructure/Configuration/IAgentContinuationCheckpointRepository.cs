using Kautuka.Gateway.Domain.Execution;

namespace Kautuka.Gateway.Infrastructure.Configuration;

internal interface IAgentContinuationCheckpointRepository
{
    void CreateContinuationCheckpoint(string storageLocatorKey, GatewayContinuationCheckpoint checkpoint);
    int GetNextContinuationCheckpointIndex(string storageLocatorKey, string continuationRunId);
    GatewayContinuationCheckpoint? GetContinuationCheckpointById(string storageLocatorKey, string checkpointId);
    GatewayContinuationCheckpoint? GetContinuationCheckpointByResumeTokenHash(string storageLocatorKey, string resumeTokenHash);
    void DeleteContinuationCheckpointsBySessions(string storageLocatorKey, IReadOnlyCollection<string> sessionIds);
}
