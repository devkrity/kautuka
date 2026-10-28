using System.Text.Json;

using Narad.Contracts;

namespace Kautuka.Gateway.RuntimePlane.Projection;

/// <summary>Parses persisted Narad authority JSON. Indexed envelope columns are authoritative; EventJson must match.</summary>
internal static class NaradEventJsonParser
{
    internal const int MaxEventJsonChars = 256 * 1024;

    public static JsonElement ParseEnvelope(NaradPersistedEvent persistedEvent)
    {
        ArgumentNullException.ThrowIfNull(persistedEvent);
        try
        {
            NaradEventJsonGuard.ValidatePersistedEventJson(persistedEvent.EventJson, "narad.event_json");
            using JsonDocument document = JsonDocument.Parse(persistedEvent.EventJson);
            JsonElement root = document.RootElement;
            ValidateEnvelopeParity(persistedEvent, root);
            return root.Clone();
        }
        catch (JsonException)
        {
            throw new NaradProjectionException("Narad event JSON is malformed.");
        }
        catch (ArgumentException)
        {
            throw new NaradProjectionException("Narad event JSON failed validation.");
        }
    }

    public static JsonElement ParseBody(NaradPersistedEvent persistedEvent)
    {
        JsonElement envelope = ParseEnvelope(persistedEvent);
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            foreach (JsonProperty property in envelope.EnumerateObject())
            {
                if (IsEnvelopeField(property.Name))
                {
                    continue;
                }

                property.WriteTo(writer);
            }

            writer.WriteEndObject();
        }

        using JsonDocument bodyDocument = JsonDocument.Parse(buffer.ToArray());
        return bodyDocument.RootElement.Clone();
    }

    public static string? String(JsonElement payload, string propertyName)
        => payload.ValueKind == JsonValueKind.Object
            && payload.TryGetProperty(propertyName, out JsonElement value)
            && value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;

    public static IReadOnlyList<string>? StringList(JsonElement payload, string propertyName)
    {
        if (payload.ValueKind != JsonValueKind.Object
            || !payload.TryGetProperty(propertyName, out JsonElement value)
            || value.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        List<string> items = [];
        foreach (JsonElement entry in value.EnumerateArray())
        {
            if (entry.ValueKind == JsonValueKind.String && entry.GetString() is string text && text.Length > 0)
            {
                items.Add(text);
            }
        }

        return items.Count == 0 ? null : items;
    }

    public static string? String(NaradPersistedEvent persistedEvent, string propertyName)
        => String(ParseEnvelope(persistedEvent), propertyName);

    public static bool IsParentConversationProjection(JsonElement root)
        => root.ValueKind == JsonValueKind.Object
            && root.TryGetProperty("sourceEventId", out JsonElement sourceEventId)
            && sourceEventId.ValueKind == JsonValueKind.String
            && !string.IsNullOrWhiteSpace(sourceEventId.GetString());

    private static void ValidateEnvelopeParity(NaradPersistedEvent persistedEvent, JsonElement root)
    {
        EnsureStringEquals(root, "type", persistedEvent.EventType);
        EnsureStringEquals(root, "protocolVersion", persistedEvent.ProtocolVersion);
        EnsureStringEquals(root, "eventId", persistedEvent.EventId);
        EnsureStringEquals(root, "timestamp", persistedEvent.OccurredAt);
        EnsureStringEquals(root, "sessionId", persistedEvent.SessionId);
        EnsureNumberEquals(root, "sessionSequence", persistedEvent.SessionSequence);

        if (!string.IsNullOrWhiteSpace(persistedEvent.RootRunId))
        {
            EnsureStringEquals(root, "rootRunId", persistedEvent.RootRunId);
            EnsureStringEquals(root, "runId", persistedEvent.RunId!);
            if (persistedEvent.Sequence.HasValue)
            {
                EnsureNumberEquals(root, "sequence", persistedEvent.Sequence.Value);
            }
        }
    }

    private static void EnsureStringEquals(JsonElement root, string name, string expected)
    {
        if (!root.TryGetProperty(name, out JsonElement value)
            || value.ValueKind != JsonValueKind.String
            || !string.Equals(value.GetString(), expected, StringComparison.Ordinal))
        {
            throw new NaradProjectionException($"Narad envelope field '{name}' does not match indexed columns.");
        }
    }

    private static void EnsureNumberEquals(JsonElement root, string name, long expected)
    {
        if (!root.TryGetProperty(name, out JsonElement value)
            || !value.TryGetInt64(out long actual)
            || actual != expected)
        {
            throw new NaradProjectionException($"Narad envelope field '{name}' does not match indexed columns.");
        }
    }

    private static bool IsEnvelopeField(string name)
        => name is "type"
            or "protocolVersion"
            or "eventId"
            or "timestamp"
            or "sessionId"
            or "sessionSequence"
            or "rootRunId"
            or "runId"
            or "sequence"
            or "workflowId"
            or "workflowSequence"
            or "recordClass";
}

internal sealed class NaradProjectionException : Exception
{
    public NaradProjectionException()
    {
    }

    public NaradProjectionException(string message)
        : base(message)
    {
    }

    public NaradProjectionException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
