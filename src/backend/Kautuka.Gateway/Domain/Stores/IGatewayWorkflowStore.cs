using System.Text.Json.Nodes;

using Kautuka.Gateway.Domain.Workflows;

using Narad.Contracts;

namespace Kautuka.Gateway.Domain.Stores;

internal interface IGatewayWorkflowStore
{
    GatewayWorkflowDefinition CreateDefinition(GatewayWorkflowDefinition definition);

    bool UpdateDefinition(GatewayWorkflowDefinition definition);

    GatewayWorkflowDefinition? GetDefinition(string workflowId);

    IReadOnlyList<GatewayWorkflowDefinition> ListDefinitions(string ownerUserId, bool archived = false);

    bool SetArchived(string workflowId, string ownerUserId, bool archived);

    bool DeleteDefinitionIfNeverRun(string workflowId, string ownerUserId);

    bool HasRuns(string workflowId, string ownerUserId);

    GatewayWorkflowInstance CreateInstance(GatewayWorkflowInstance instance, IReadOnlyList<WorkflowArtifact>? artifacts = null);

    IReadOnlyList<WorkflowArtifact> ListArtifacts(string instanceId);

    GatewayWorkflowInstance? GetInstance(string instanceId);

    GatewayWorkflowInstance? GetInstanceByStepSession(string sessionId);

    IReadOnlyList<GatewayWorkflowInstance> ListInstances(string ownerUserId, string? workflowId, int limit, int offset);

    IReadOnlyList<GatewayWorkflowInstance> ListRecoverableInstances();

    bool TryRequestStop(string instanceId, string ownerUserId, DateTimeOffset nowUtc, out GatewayWorkflowInstance? instance);

    bool TrySetInstanceStatus(
        string instanceId,
        string status,
        string? currentNodeId,
        string? failureCode,
        string? error,
        bool? stopRequested,
        DateTimeOffset nowUtc,
        out GatewayWorkflowInstance? instance);

    bool TryUpdateEnvelope(string instanceId, JsonObject envelope, DateTimeOffset nowUtc, out GatewayWorkflowInstance? instance);

    bool TryUpdateStepAttempt(GatewayWorkflowStepAttempt attempt, DateTimeOffset nowUtc);

    bool TryAddStepAttempt(GatewayWorkflowStepAttempt attempt);

    bool TryClearMafCheckpoint(string instanceId, DateTimeOffset nowUtc);

    string? GetMafCheckpointJson(string instanceId);

    bool TrySaveMafCheckpointJson(string instanceId, string checkpointJson, DateTimeOffset nowUtc);

    NaradPersistedEvent AppendNaradEvent(NaradPersistedEvent persistedEvent);

    IReadOnlyList<NaradPersistedEvent> ListNaradEvents(string instanceId, long afterSessionSequence, int limit);

    long ReadNaradSessionSequence(string instanceId);
}
