using System.Text.Json.Nodes;

using Kautuka.Gateway.Domain.Stores;
using Kautuka.Gateway.Domain.Workflows;

namespace Kautuka.IntegrationTests;

internal sealed class TestGatewayWorkflowStore(GatewayWorkflowInstance? instance = null) : IGatewayWorkflowStore
{
    public GatewayWorkflowInstance? Instance { get; set; } = instance;
    public int InstanceLookups { get; private set; }

    public GatewayWorkflowDefinition CreateDefinition(GatewayWorkflowDefinition definition) => throw new NotSupportedException();
    public bool UpdateDefinition(GatewayWorkflowDefinition definition) => throw new NotSupportedException();
    public GatewayWorkflowDefinition? GetDefinition(string workflowId) => throw new NotSupportedException();
    public IReadOnlyList<GatewayWorkflowDefinition> ListDefinitions(string ownerUserId, bool archived = false) => throw new NotSupportedException();
    public bool SetArchived(string workflowId, string ownerUserId, bool archived) => throw new NotSupportedException();
    public bool DeleteDefinitionIfNeverRun(string workflowId, string ownerUserId) => throw new NotSupportedException();
    public bool HasRuns(string workflowId, string ownerUserId) => throw new NotSupportedException();
    public GatewayWorkflowInstance CreateInstance(GatewayWorkflowInstance workflowInstance, IReadOnlyList<WorkflowArtifact>? artifacts = null) => throw new NotSupportedException();
    public IReadOnlyList<WorkflowArtifact> ListArtifacts(string instanceId) => [];
    public GatewayWorkflowInstance? GetInstance(string instanceId)
    {
        InstanceLookups++;
        return Instance is { } found && found.Id == instanceId ? found : null;
    }
    public GatewayWorkflowInstance? GetInstanceByStepSession(string sessionId) => null;
    public IReadOnlyList<GatewayWorkflowInstance> ListInstances(string ownerUserId, string? workflowId, int limit, int offset) => throw new NotSupportedException();
    public IReadOnlyList<GatewayWorkflowInstance> ListRecoverableInstances() => [];
    public bool TryRequestStop(string instanceId, string ownerUserId, DateTimeOffset nowUtc, out GatewayWorkflowInstance? updated) { updated = null; return false; }
    public bool TrySetInstanceStatus(string instanceId, string status, string? currentNodeId, string? failureCode, string? error, bool? stopRequested, DateTimeOffset nowUtc, out GatewayWorkflowInstance? updated) { updated = null; return false; }
    public bool TryUpdateEnvelope(string instanceId, JsonObject envelope, DateTimeOffset nowUtc, out GatewayWorkflowInstance? updated) { updated = null; return false; }
    public bool TryUpdateStepAttempt(GatewayWorkflowStepAttempt attempt, DateTimeOffset nowUtc) => false;
    public bool TryAddStepAttempt(GatewayWorkflowStepAttempt attempt) => false;
    public bool TryClearMafCheckpoint(string instanceId, DateTimeOffset nowUtc) => false;
    public string? GetMafCheckpointJson(string instanceId) => null;
    public bool TrySaveMafCheckpointJson(string instanceId, string checkpointJson, DateTimeOffset nowUtc) => false;
    public NaradPersistedEvent AppendNaradEvent(NaradPersistedEvent persistedEvent) => persistedEvent;
    public IReadOnlyList<NaradPersistedEvent> ListNaradEvents(string instanceId, long afterSessionSequence, int limit) => [];
    public long ReadNaradSessionSequence(string instanceId) => 0;
}
