namespace Narad.Contracts;

/// <summary>Immutable Narad event persisted in the authority journal with allocated ordering fields.</summary>
public sealed record NaradPersistedEvent(
    string EventId,
    string ProtocolVersion,
    string EventType,
    string SessionId,
    long SessionSequence,
    string RecordClass,
    string EventJson,
    string OccurredAt,
    string? RootRunId = null,
    string? RunId = null,
    long? Sequence = null,
    string? WorkflowId = null,
    long? WorkflowSequence = null);
