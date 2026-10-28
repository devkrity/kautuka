using System.Text.Json;

namespace Narad.Contracts;

/// <summary>Draft authority event submitted before the store assigns Narad sequences and canonical JSON.</summary>
public sealed record NaradAuthorityEventDraft(
    string EventId,
    string EventType,
    string SessionId,
    JsonElement Body,
    string OccurredAt,
    string RecordClass = NaradRecordClasses.Authority,
    string ProtocolVersion = NaradProtocolVersions.V1,
    string? RootRunId = null,
    string? RunId = null,
    string? WorkflowId = null,
    string? ParentSessionId = null,
    string? ParentRunId = null,
    string? ParentToolCallId = null);
