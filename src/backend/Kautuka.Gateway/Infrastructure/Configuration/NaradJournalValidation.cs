using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

using Narad.Contracts;

namespace Kautuka.Gateway.Infrastructure.Configuration;

internal static partial class NaradJournalValidation
{
    internal const int MaxHistoryLimit = 500;

    /// <summary>
    /// History pages fetch one extra row to compute <c>hasMore</c>. That peek is not a public page size.
    /// </summary>
    internal const int MaxHistoryPeekLimit = MaxHistoryLimit + 1;

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

    [GeneratedRegex("^[a-z][a-z0-9]*(\\.[a-z][a-z0-9]*)+$", RegexOptions.CultureInvariant)]
    private static partial Regex EventTypePattern();

    internal static void ValidateAuthorityDraft(NaradAuthorityEventDraft draft)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(draft.EventId);
        ArgumentException.ThrowIfNullOrWhiteSpace(draft.EventType);
        ArgumentException.ThrowIfNullOrWhiteSpace(draft.SessionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(draft.OccurredAt);
        ArgumentException.ThrowIfNullOrWhiteSpace(draft.ProtocolVersion);
        NaradRecordClasses.EnsureAuthorityOnly(draft.RecordClass);
        ValidateEventType(draft.EventType);
        EnsureAuthorityJournalEventTypeAllowed(draft.EventType, isPersisted: false);
        ValidateIso8601Timestamp(draft.OccurredAt, nameof(draft));

        if (!string.Equals(draft.ProtocolVersion, NaradProtocolVersions.V1, StringComparison.Ordinal))
        {
            throw new ArgumentException($"Unsupported Narad protocol version '{draft.ProtocolVersion}'.", nameof(draft));
        }

        if (draft.Body.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
        {
            throw new ArgumentException("Narad authority event body must be a JSON value.", nameof(draft));
        }

        ValidateBodyReservedKeys(draft.Body);
        if (draft.EventType == NaradEventTypes.ToolCallFinished && !draft.Body.TryGetProperty("response", out _))
            throw new ArgumentException("Tool completion requires a response field; use explicit null when no result exists.", nameof(draft));

        bool hasRoot = !string.IsNullOrWhiteSpace(draft.RootRunId);
        bool hasRun = !string.IsNullOrWhiteSpace(draft.RunId);
        if (hasRoot != hasRun)
        {
            throw new ArgumentException(
                "Narad authority events require rootRunId and runId together, or neither.",
                nameof(draft));
        }
    }

    internal static void ValidateAuthorityPersistedEvent(NaradPersistedEvent persistedEvent)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(persistedEvent.EventId);
        ArgumentException.ThrowIfNullOrWhiteSpace(persistedEvent.ProtocolVersion);
        ArgumentException.ThrowIfNullOrWhiteSpace(persistedEvent.EventType);
        ArgumentException.ThrowIfNullOrWhiteSpace(persistedEvent.SessionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(persistedEvent.RecordClass);
        ArgumentException.ThrowIfNullOrWhiteSpace(persistedEvent.EventJson);
        ArgumentException.ThrowIfNullOrWhiteSpace(persistedEvent.OccurredAt);
        NaradRecordClasses.EnsureAuthorityOnly(persistedEvent.RecordClass);
        ValidateEventType(persistedEvent.EventType);
        EnsureAuthorityJournalEventTypeAllowed(persistedEvent.EventType, isPersisted: true);
        ValidateIso8601Timestamp(persistedEvent.OccurredAt, nameof(persistedEvent));

        if (persistedEvent.SessionSequence <= 0)
        {
            throw new InvalidDataException("Narad sessionSequence must be positive.");
        }

        bool hasRoot = !string.IsNullOrWhiteSpace(persistedEvent.RootRunId);
        bool hasRun = !string.IsNullOrWhiteSpace(persistedEvent.RunId);
        bool hasSequence = persistedEvent.Sequence.HasValue;
        if (hasRoot != hasRun || hasRoot != hasSequence)
        {
            throw new InvalidDataException(
                "Narad authority persisted events require rootRunId, runId, and sequence together, or all absent.");
        }

        if (hasSequence && persistedEvent.Sequence <= 0)
        {
            throw new InvalidDataException("Narad root sequence must be positive.");
        }

        bool hasWorkflow = !string.IsNullOrWhiteSpace(persistedEvent.WorkflowId);
        bool hasWorkflowSequence = persistedEvent.WorkflowSequence.HasValue;
        if (hasWorkflow != hasWorkflowSequence)
        {
            throw new InvalidDataException("Narad workflow persisted events require workflowId and workflowSequence together.");
        }

        if (hasWorkflowSequence && persistedEvent.WorkflowSequence <= 0)
        {
            throw new InvalidDataException("Narad workflowSequence must be positive.");
        }

        _ = NaradEventJsonGuard.ValidatePersistedEventJson(persistedEvent.EventJson, "narad_runtime_journal.event_json");
        using JsonDocument document = JsonDocument.Parse(persistedEvent.EventJson);
        if (IsParentConversationProjection(document.RootElement))
        {
            ValidateParentConversationProjection(persistedEvent, document.RootElement);
            return;
        }

        ValidateEventJsonMatchesColumns(persistedEvent);
    }

    internal static void ValidateHistoryLimit(int limit)
    {
        if (limit <= 0 || limit > MaxHistoryPeekLimit)
        {
            throw new ArgumentOutOfRangeException(nameof(limit), limit, $"History limit must be between 1 and {MaxHistoryPeekLimit}.");
        }
    }

    internal static void ValidateBodyReservedKeys(JsonElement body)
    {
        if (body.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        foreach (JsonProperty property in body.EnumerateObject())
        {
            if (reservedEnvelopeKeys.Contains(property.Name))
            {
                throw new ArgumentException(
                    $"Narad authority event body must not contain reserved envelope key '{property.Name}'.",
                    nameof(body));
            }
        }
    }

    private static void EnsureAuthorityJournalEventTypeAllowed(string eventType, bool isPersisted)
    {
        if (!NaradEventTypes.IsHydrateDerivedGraphEventType(eventType))
        {
            return;
        }

        string message =
            $"Narad hydrate-derived event type '{eventType}' must not be persisted in the authority journal.";
        if (isPersisted)
        {
            throw new InvalidDataException(message);
        }

        throw new ArgumentException(message, nameof(eventType));
    }

    private static void ValidateEventType(string eventType)
    {
        if (!EventTypePattern().IsMatch(eventType))
        {
            throw new ArgumentException(
                $"Narad event type '{eventType}' must be a nonempty dotted lowercase identifier.",
                nameof(eventType));
        }
    }

    private static void ValidateIso8601Timestamp(string timestamp, string paramName)
    {
        if (!DateTimeOffset.TryParse(timestamp, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out DateTimeOffset parsed))
        {
            throw new ArgumentException($"Narad timestamp '{timestamp}' must be ISO-8601.", paramName);
        }

        if (!string.Equals(parsed.ToString("O"), timestamp, StringComparison.Ordinal))
        {
            throw new ArgumentException($"Narad timestamp '{timestamp}' must use round-trip ISO-8601 format.", paramName);
        }
    }

    private static bool IsParentConversationProjection(JsonElement root)
        => root.ValueKind == JsonValueKind.Object
            && root.TryGetProperty("sourceEventId", out JsonElement sourceEventId)
            && sourceEventId.ValueKind == JsonValueKind.String
            && !string.IsNullOrWhiteSpace(sourceEventId.GetString());

    private static void ValidateParentConversationProjection(NaradPersistedEvent persistedEvent, JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException("Narad event_json must be a JSON object.");
        }

        if (!string.IsNullOrWhiteSpace(persistedEvent.RootRunId)
            || !string.IsNullOrWhiteSpace(persistedEvent.RunId)
            || persistedEvent.Sequence.HasValue
            || !string.IsNullOrWhiteSpace(persistedEvent.WorkflowId)
            || persistedEvent.WorkflowSequence.HasValue)
        {
            throw new InvalidDataException(
                "AAAT parent conversation projections must not index run-tree or workflow sequence columns.");
        }

        AssertJsonFieldMatches(root, "type", persistedEvent.EventType);
        AssertJsonFieldMatches(root, "protocolVersion", persistedEvent.ProtocolVersion);
        AssertJsonFieldMatches(root, "eventId", persistedEvent.EventId);
        AssertJsonFieldMatches(root, "timestamp", persistedEvent.OccurredAt);
        AssertJsonFieldMatches(root, "sessionId", persistedEvent.SessionId);
        AssertJsonLongMatches(root, "sessionSequence", persistedEvent.SessionSequence);
        if (!root.TryGetProperty("sourceEventId", out JsonElement sourceEventId)
            || sourceEventId.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(sourceEventId.GetString()))
        {
            throw new InvalidDataException("AAAT parent conversation projection is missing sourceEventId.");
        }

        if (!root.TryGetProperty("sourceSessionId", out JsonElement sourceSessionId)
            || sourceSessionId.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(sourceSessionId.GetString()))
        {
            throw new InvalidDataException("AAAT parent conversation projection is missing sourceSessionId.");
        }

        foreach (string forbidden in new[] { "sequence", "workflowId", "workflowSequence" })
        {
            if (root.TryGetProperty(forbidden, out _))
            {
                throw new InvalidDataException(
                    $"AAAT parent conversation projection must not carry '{forbidden}'.");
            }
        }
    }

    private static void ValidateEventJsonMatchesColumns(NaradPersistedEvent persistedEvent)
    {
        using JsonDocument document = JsonDocument.Parse(persistedEvent.EventJson);
        JsonElement root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException("Narad event_json must be a JSON object.");
        }

        AssertJsonFieldMatches(root, "type", persistedEvent.EventType);
        AssertJsonFieldMatches(root, "protocolVersion", persistedEvent.ProtocolVersion);
        AssertJsonFieldMatches(root, "eventId", persistedEvent.EventId);
        AssertJsonFieldMatches(root, "timestamp", persistedEvent.OccurredAt);
        AssertJsonFieldMatches(root, "sessionId", persistedEvent.SessionId);
        AssertJsonLongMatches(root, "sessionSequence", persistedEvent.SessionSequence);
        AssertOptionalJsonFieldMatches(root, "workflowId", persistedEvent.WorkflowId);
        AssertOptionalJsonLongMatches(root, "workflowSequence", persistedEvent.WorkflowSequence);
        AssertOptionalJsonFieldMatches(root, "rootRunId", persistedEvent.RootRunId);
        AssertOptionalJsonFieldMatches(root, "runId", persistedEvent.RunId);
        AssertOptionalJsonLongMatches(root, "sequence", persistedEvent.Sequence);

        foreach (string reservedKey in reservedEnvelopeKeys)
        {
            if (reservedKey is "recordClass")
            {
                continue;
            }

            bool expectedPresent = reservedKey switch
            {
                "workflowId" => !string.IsNullOrWhiteSpace(persistedEvent.WorkflowId),
                "workflowSequence" => persistedEvent.WorkflowSequence.HasValue,
                "rootRunId" => !string.IsNullOrWhiteSpace(persistedEvent.RootRunId),
                "runId" => !string.IsNullOrWhiteSpace(persistedEvent.RunId),
                "sequence" => persistedEvent.Sequence.HasValue,
                _ => true,
            };

            if (expectedPresent != root.TryGetProperty(reservedKey, out _))
            {
                throw new InvalidDataException(
                    $"Narad event_json reserved key '{reservedKey}' presence does not match indexed envelope columns for event '{persistedEvent.EventId}'.");
            }
        }
    }

    private static void AssertJsonFieldMatches(JsonElement root, string propertyName, string expected)
    {
        if (!root.TryGetProperty(propertyName, out JsonElement actual)
            || actual.ValueKind != JsonValueKind.String
            || !string.Equals(actual.GetString(), expected, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"Narad event_json property '{propertyName}' does not match indexed envelope column value.");
        }
    }

    private static void AssertJsonLongMatches(JsonElement root, string propertyName, long expected)
    {
        if (!root.TryGetProperty(propertyName, out JsonElement actual)
            || !actual.TryGetInt64(out long actualValue)
            || actualValue != expected)
        {
            throw new InvalidDataException(
                $"Narad event_json property '{propertyName}' does not match indexed envelope column value.");
        }
    }

    private static void AssertOptionalJsonFieldMatches(JsonElement root, string propertyName, string? expected)
    {
        bool shouldExist = !string.IsNullOrWhiteSpace(expected);
        if (shouldExist)
        {
            AssertJsonFieldMatches(root, propertyName, expected!);
            return;
        }

        if (root.TryGetProperty(propertyName, out _))
        {
            throw new InvalidDataException(
                $"Narad event_json property '{propertyName}' must be absent when the indexed envelope column is null.");
        }
    }

    private static void AssertOptionalJsonLongMatches(JsonElement root, string propertyName, long? expected)
    {
        if (expected.HasValue)
        {
            AssertJsonLongMatches(root, propertyName, expected.Value);
            return;
        }

        if (root.TryGetProperty(propertyName, out _))
        {
            throw new InvalidDataException(
                $"Narad event_json property '{propertyName}' must be absent when the indexed envelope column is null.");
        }
    }
}
