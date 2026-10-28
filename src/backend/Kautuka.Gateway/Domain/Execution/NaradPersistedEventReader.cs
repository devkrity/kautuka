using System.Text.Json;

using Narad.Contracts;

namespace Kautuka.Gateway.Domain.Execution;

internal static class NaradPersistedEventReader
{
    public static JsonElement ParseBody(NaradPersistedEvent persistedEvent)
    {
        using JsonDocument document = JsonDocument.Parse(persistedEvent.EventJson);
        JsonElement root = document.RootElement;
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            foreach (JsonProperty property in root.EnumerateObject())
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

    public static string? String(NaradPersistedEvent persistedEvent, string propertyName)
    {
        using JsonDocument document = JsonDocument.Parse(persistedEvent.EventJson);
        return String(document.RootElement, propertyName);
    }

    public static bool TryGetUsage(NaradPersistedEvent persistedEvent, out long promptTokens, out long completionTokens)
    {
        promptTokens = 0;
        completionTokens = 0;
        using JsonDocument document = JsonDocument.Parse(persistedEvent.EventJson);
        if (!document.RootElement.TryGetProperty("usage", out JsonElement usage)
            || usage.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        if (usage.TryGetProperty("promptTokens", out JsonElement prompt) && prompt.TryGetInt64(out long parsedPrompt))
        {
            promptTokens = parsedPrompt;
        }

        if (usage.TryGetProperty("completionTokens", out JsonElement completion) && completion.TryGetInt64(out long parsedCompletion))
        {
            completionTokens = parsedCompletion;
        }

        return promptTokens > 0 || completionTokens > 0;
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
