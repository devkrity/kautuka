using System.Text.Json.Nodes;

using Kautuka.Gateway.Domain.Execution;
using Kautuka.Gateway.Domain.Stores;

using Narad.Contracts;

namespace Kautuka.Gateway.Domain.Workflows;

internal interface IWorkflowNaradPublisher
{
    NaradPersistedEvent Publish(
        GatewayWorkflowInstance instance,
        string eventType,
        JsonObject body);
}

internal sealed class WorkflowNaradPublisher(
    IGatewayWorkflowStore workflowStore,
    IWorkflowNaradStreamHub streamHub,
    IRuntimeEventStreamHub runtimeStreamHub) : IWorkflowNaradPublisher
{
    private static readonly HashSet<string> reservedEnvelopeKeys = new(StringComparer.Ordinal)
    {
        "type",
        "protocolVersion",
        "eventId",
        "timestamp",
        "sessionId",
        "sessionSequence",
        "workflowId",
        "workflowSequence",
        "rootRunId",
        "runId",
        "sequence",
        "recordClass",
    };

    public NaradPersistedEvent Publish(GatewayWorkflowInstance instance, string eventType, JsonObject body)
    {
        ArgumentNullException.ThrowIfNull(instance);
        ArgumentException.ThrowIfNullOrWhiteSpace(eventType);
        ArgumentNullException.ThrowIfNull(body);

        foreach (KeyValuePair<string, JsonNode?> pair in body)
        {
            if (reservedEnvelopeKeys.Contains(pair.Key))
            {
                throw new InvalidOperationException(
                    $"Workflow Narad event body must not contain reserved envelope key '{pair.Key}'.");
            }
        }

        string occurredAt = DateTimeOffset.UtcNow.ToString("O");
        string eventId = Guid.NewGuid().ToString("N");
        var envelope = new JsonObject
        {
            ["type"] = eventType,
            ["protocolVersion"] = NaradProtocolVersions.V1,
            ["eventId"] = eventId,
            ["timestamp"] = occurredAt,
            ["sessionId"] = instance.Id,
            ["sessionSequence"] = 0,
            ["workflowId"] = instance.Id,
            ["workflowSequence"] = 0,
        };
        foreach (KeyValuePair<string, JsonNode?> pair in body)
        {
            if (!envelope.ContainsKey(pair.Key))
            {
                envelope[pair.Key] = pair.Value?.DeepClone();
            }
        }

        var draft = new NaradPersistedEvent(
            eventId,
            NaradProtocolVersions.V1,
            eventType,
            instance.Id,
            SessionSequence: 0,
            NaradRecordClasses.Authority,
            envelope.ToJsonString(),
            occurredAt,
            WorkflowId: instance.Id,
            WorkflowSequence: 0);
        NaradPersistedEvent persisted = workflowStore.AppendNaradEvent(draft);
        streamHub.Publish(persisted);
        runtimeStreamHub.Publish(persisted);
        return persisted;
    }
}
